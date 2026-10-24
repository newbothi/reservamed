import { Body, Controller, Get, HttpCode, Param, ParseIntPipe, Post, Req, Res } from '@nestjs/common';
import { z } from 'zod';
import { CoreClient } from '../comun/core-client.service';
import { GestorSesion, Req as Peticion, Res as Respuesta } from '../comun/gestor-sesion.service';
import { ServicioPow } from '../comun/pow.service';
import { CitaResumen, Valorizacion } from '../comun/tipos';
import { ahoraChile, ms, ZodPipe } from '../comun/util';

const ReservarSchema = z.object({
  profesionalId: z.number().int().positive(),
  sedeId: z.number().int().positive(),
  inicio: z.string().regex(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/),
  modalidadPago: z.enum(['ONLINE', 'EN_CLINICA']),
  pow: z.unknown(),
});

const CancelarSchema = z.object({ motivo: z.string().max(200).optional() });

const HORAS_MIN_ANULAR = Number(process.env.HORAS_MINIMAS_ANULAR ?? 2);


@Controller('citas')
export class CitasController {
  constructor(private readonly core: CoreClient, private readonly sesion: GestorSesion, private readonly pow: ServicioPow) {}

  /**
   * Mis citas, separadas en próximas e historial y con las acciones permitidas ya calculadas,
   * para que el frontend no tenga que replicar reglas de negocio.
   */
  @Get('mias')
  async mias(@Req() req: Peticion, @Res({ passthrough: true }) res: Respuesta) {
    const citas = await this.core.get<CitaResumen[]>('/api/citas/mias', await this.sesion.token(req, res, 'portal'));
    const ahora = ms(ahoraChile());
    const activas = ['PENDIENTE_PAGO', 'RESERVADA', 'CONFIRMADA', 'ADMITIDA'];

    const enriquecidas = citas.map((c) => {
      const faltan = ms(c.fechaHoraInicio) - ahora;
      return {
        ...c,
        puedePagar: c.estado === 'PENDIENTE_PAGO' && !!c.expiraEn && ms(c.expiraEn) > ahora,
        puedeAnular: ['PENDIENTE_PAGO', 'RESERVADA', 'CONFIRMADA'].includes(c.estado) && faltan >= HORAS_MIN_ANULAR * 3_600_000,
        segundosParaPagar: c.estado === 'PENDIENTE_PAGO' && c.expiraEn ? Math.max(0, Math.floor((ms(c.expiraEn) - ahora) / 1000)) : null,
      };
    });

    return {
      proximas: enriquecidas
        .filter((c) => activas.includes(c.estado) && ms(c.fechaHoraFin) > ahora)
        .sort((a, b) => a.fechaHoraInicio.localeCompare(b.fechaHoraInicio)),
      historial: enriquecidas.filter((c) => !(activas.includes(c.estado) && ms(c.fechaHoraFin) > ahora)),
    };
  }

  @Post()
  async reservar(
    @Body(new ZodPipe(ReservarSchema)) body: z.infer<typeof ReservarSchema>,
    @Req() req: Peticion,
    @Res({ passthrough: true }) res: Respuesta,
  ) {
    const token = await this.sesion.token(req, res, 'portal');
    this.pow.exigir(body.pow);   // A8: cada reserva cuesta una prueba de trabajo
    const { pow: _pow, ...datos } = body;
    const inicio = datos.inicio.length === 16 ? `${datos.inicio}:00` : datos.inicio;
    return this.core.post<{ citaId: number; estado: string; inicio: string; expiraEn: string | null }>(
      '/api/citas',
      { ...datos, inicio },
      token,
    );
  }

  @Post(':id/cancelar')
  @HttpCode(204)
  async cancelar(
    @Param('id', ParseIntPipe) id: number,
    @Body(new ZodPipe(CancelarSchema)) body: z.infer<typeof CancelarSchema>,
    @Req() req: Peticion,
    @Res({ passthrough: true }) res: Respuesta,
  ) {
    await this.core.post(`/api/citas/${id}/cancelar`, { motivo: body.motivo ?? null }, await this.sesion.token(req, res, 'portal'));
  }
}

@Controller('pagos')
export class PagosController {
  constructor(private readonly core: CoreClient, private readonly sesion: GestorSesion) {}

  /** Crea la transacción Webpay y devuelve la URL + token para que el navegador haga el POST. */
  @Post('webpay/iniciar')
  @HttpCode(200)
  async iniciar(@Body(new ZodPipe(z.object({ citaId: z.number().int().positive() }))) body: { citaId: number },
                @Req() req: Peticion, @Res({ passthrough: true }) res: Respuesta) {
    return this.core.post<{ token: string; url: string; monto: number; valorizacion: Valorizacion }>(
      '/api/pagos/webpay/iniciar', body, await this.sesion.token(req, res, 'portal'));
  }

  /** Se llama desde la página de retorno con el token_ws (o TBK_TOKEN si el usuario anuló). */
  @Post('webpay/confirmar')
  @HttpCode(200)
  async confirmar(@Body(new ZodPipe(z.object({ token: z.string().min(10) }))) body: { token: string },
                  @Req() req: Peticion, @Res({ passthrough: true }) res: Respuesta) {
    return this.core.post('/api/pagos/webpay/confirmar', body, await this.sesion.token(req, res, 'portal'));
  }
}
