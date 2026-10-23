import { Body, Controller, Get, Headers, HttpCode, Param, ParseIntPipe, Post, UnauthorizedException } from '@nestjs/common';
import { z } from 'zod';
import { CoreClient } from '../comun/core-client.service';
import { CitaResumen, Valorizacion } from '../comun/tipos';
import { ahoraChile, ms, ZodPipe } from '../comun/util';

const ReservarSchema = z.object({
  profesionalId: z.number().int().positive(),
  sedeId: z.number().int().positive(),
  inicio: z.string().regex(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/),
  modalidadPago: z.enum(['ONLINE', 'EN_CLINICA']),
});

const CancelarSchema = z.object({ motivo: z.string().max(200).optional() });

const HORAS_MIN_ANULAR = Number(process.env.HORAS_MINIMAS_ANULAR ?? 2);

function exigirToken(auth?: string): string {
  if (!auth?.startsWith('Bearer ')) throw new UnauthorizedException({ status: 401, title: 'Inicie sesión' });
  return auth;
}

@Controller('citas')
export class CitasController {
  constructor(private readonly core: CoreClient) {}

  /**
   * Mis citas, separadas en próximas e historial y con las acciones permitidas ya calculadas,
   * para que el frontend no tenga que replicar reglas de negocio.
   */
  @Get('mias')
  async mias(@Headers('authorization') auth?: string) {
    const citas = await this.core.get<CitaResumen[]>('/api/citas/mias', exigirToken(auth));
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
  reservar(
    @Body(new ZodPipe(ReservarSchema)) body: z.infer<typeof ReservarSchema>,
    @Headers('authorization') auth?: string,
    @Headers('x-forwarded-for') ip?: string,
  ) {
    const inicio = body.inicio.length === 16 ? `${body.inicio}:00` : body.inicio;
    return this.core.post<{ citaId: number; estado: string; inicio: string; expiraEn: string | null }>(
      '/api/citas',
      { ...body, inicio },
      exigirToken(auth),
      ip,
    );
  }

  @Post(':id/cancelar')
  @HttpCode(204)
  async cancelar(
    @Param('id', ParseIntPipe) id: number,
    @Body(new ZodPipe(CancelarSchema)) body: z.infer<typeof CancelarSchema>,
    @Headers('authorization') auth?: string,
  ) {
    await this.core.post(`/api/citas/${id}/cancelar`, { motivo: body.motivo ?? null }, exigirToken(auth));
  }
}

@Controller('pagos')
export class PagosController {
  constructor(private readonly core: CoreClient) {}

  /** Crea la transacción Webpay y devuelve la URL + token para que el navegador haga el POST. */
  @Post('webpay/iniciar')
  @HttpCode(200)
  iniciar(@Body(new ZodPipe(z.object({ citaId: z.number().int().positive() }))) body: { citaId: number },
          @Headers('authorization') auth?: string) {
    return this.core.post<{ token: string; url: string; monto: number; valorizacion: Valorizacion }>(
      '/api/pagos/webpay/iniciar', body, exigirToken(auth));
  }

  /** Se llama desde la página de retorno con el token_ws (o TBK_TOKEN si el usuario anuló). */
  @Post('webpay/confirmar')
  @HttpCode(200)
  confirmar(@Body(new ZodPipe(z.object({ token: z.string().min(10) }))) body: { token: string },
            @Headers('authorization') auth?: string) {
    return this.core.post('/api/pagos/webpay/confirmar', body, exigirToken(auth));
  }
}
