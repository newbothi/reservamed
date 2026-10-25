import { Body, Controller, Get, HttpCode, Param, ParseUUIDPipe, Post, Req, Res } from '@nestjs/common';
import { z } from 'zod';
import { CoreClient } from '../comun/core-client.service';
import { GestorSesion, Req as Peticion, Res as Respuesta } from '../comun/gestor-sesion.service';
import { ZodPipe } from '../comun/util';

const CambiarClaveSchema = z.object({
  actual: z.string().min(1, 'Ingrese su contraseña actual').max(128),
  nueva: z.string().min(12, 'Mínimo 12 caracteres').max(128),
});

const EliminacionSchema = z.object({
  clave: z.string().min(1, 'Ingrese su contraseña').max(128),
  confirmacion: z.literal('ELIMINAR', { errorMap: () => ({ message: 'Escriba ELIMINAR para confirmar' }) }),
});

export interface SesionActiva {
  id: string;
  cliente: string;
  creadaEn: string;
  ultimoUso: string;
  expiraEn: string;
  ip: string | null;
  actual: boolean;
}

/**
 * Fase 5 (ASVS V2.1.6, V3.3.4, V8.3.2): "Mi cuenta" del portal de pacientes.
 * El backoffice llega a /api/cuenta por su proxy (/bff/bo/api/cuenta/...).
 */
@Controller('cuenta')
export class CuentaController {
  constructor(private readonly core: CoreClient, private readonly sesion: GestorSesion) {}

  @Post('clave')
  @HttpCode(200)
  async cambiarClave(
    @Body(new ZodPipe(CambiarClaveSchema)) body: z.infer<typeof CambiarClaveSchema>,
    @Req() req: Peticion,
    @Res({ passthrough: true }) res: Respuesta,
  ) {
    return this.core.post<{ sesionesCerradas: number }>('/api/cuenta/clave', body, await this.sesion.token(req, res, 'portal'));
  }

  @Get('sesiones')
  async sesiones(@Req() req: Peticion, @Res({ passthrough: true }) res: Respuesta) {
    return this.core.get<SesionActiva[]>('/api/cuenta/sesiones', await this.sesion.token(req, res, 'portal'));
  }

  @Post('sesiones/:id/cerrar')
  @HttpCode(204)
  async cerrarSesion(
    @Param('id', new ParseUUIDPipe()) id: string,
    @Req() req: Peticion,
    @Res({ passthrough: true }) res: Respuesta,
  ) {
    await this.core.post(`/api/cuenta/sesiones/${id}/cerrar`, {}, await this.sesion.token(req, res, 'portal'));
  }

  /** Descarga de "mis datos" (Ley 21.719): JSON con nombre de archivo. */
  @Get('datos')
  async datos(@Req() req: Peticion, @Res({ passthrough: true }) res: Respuesta) {
    const datos = await this.core.get<unknown>('/api/cuenta/datos', await this.sesion.token(req, res, 'portal'));
    const fecha = new Date().toISOString().slice(0, 10).replace(/-/g, '');
    res.append('Content-Disposition', `attachment; filename="mis-datos-reservamed-${fecha}.json"`);
    res.append('Cache-Control', 'no-store');
    return datos;
  }

  /** La API cierra la cuenta y revoca todas las sesiones: aquí se borra también la cookie. */
  @Post('eliminacion')
  @HttpCode(204)
  async eliminacion(
    @Body(new ZodPipe(EliminacionSchema)) body: z.infer<typeof EliminacionSchema>,
    @Req() req: Peticion,
    @Res({ passthrough: true }) res: Respuesta,
  ) {
    await this.core.post('/api/cuenta/eliminacion', body, await this.sesion.token(req, res, 'portal'));
    this.sesion.borrar(res, 'portal');
  }
}
