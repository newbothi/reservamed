import { Body, Controller, ForbiddenException, HttpCode, NotFoundException, Post, Req, Res } from '@nestjs/common';
import { z } from 'zod';
import { GestorSesion, Req as Peticion, Res as Respuesta } from '../comun/gestor-sesion.service';
import { ZodPipe } from '../comun/util';
import { firmarLectura, leerConfigLectores } from './lector-huella';

const LecturaSchema = z.object({
  desafio: z.object({
    nonce: z.string().min(10).max(100),
    citaId: z.number().int().positive(),
    rut: z.string().min(3).max(15),
    sedeId: z.number().int().positive(),
  }),
  resultado: z.enum(['COINCIDE', 'NO_COINCIDE']),
});

/**
 * POST /bff/bo/lector/leer: el "dedo en el lector" del simulador. Solo para personal de
 * recepción o administración con sesión en el backoffice. Devuelve el comprobante firmado que
 * luego se adjunta al emitir el bono; la API decide si vale.
 */
@Controller('bo/lector')
export class LectorController {
  private readonly cfg = leerConfigLectores();

  constructor(private readonly sesion: GestorSesion) {}

  @Post('leer')
  @HttpCode(200)
  async leer(
    @Body(new ZodPipe(LecturaSchema)) body: z.infer<typeof LecturaSchema>,
    @Req() req: Peticion,
    @Res({ passthrough: true }) res: Respuesta,
  ) {
    if (!this.cfg) throw new NotFoundException({ status: 404, title: 'El lector simulado no está habilitado.' });
    const usuario = await this.sesion.usuario(req, res, 'backoffice');
    if (usuario.rol !== 'RECEPCION' && usuario.rol !== 'ADMIN')
      throw new ForbiddenException({ status: 403, title: 'Solo recepción usa el lector de huella.' });
    if (!this.cfg.porSede.has(body.desafio.sedeId))
      throw new NotFoundException({ status: 404, title: 'No hay lector instalado en esa sede.' });
    // Se simulan ~1,2 s de captura, como un lector real.
    await new Promise((r) => setTimeout(r, 1200));
    return { comprobante: firmarLectura(this.cfg, body.desafio, body.resultado) };
  }
}
