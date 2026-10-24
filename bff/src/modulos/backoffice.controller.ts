import { All, Body, Controller, Get, HttpCode, HttpException, Post, Req, Res, UnauthorizedException } from '@nestjs/common';
import { z } from 'zod';
import { leerConfig } from '../comun/config';
import { abrir, borrarCookie, cookie, leerCookies, NOMBRE_DESAFIO, sellar } from '../comun/cookie-sesion';
import { CoreClient } from '../comun/core-client.service';
import { GestorSesion, Req as Peticion, Res as Respuesta } from '../comun/gestor-sesion.service';
import { ZodPipe } from '../comun/util';
import { cerrarSesion, ResultadoLogin } from './auth.controller';
import { rutaProxyPermitida } from '../comun/ruta-proxy';

const LoginSchema = z.object({
  email: z.string().trim().toLowerCase().email('Correo no válido'),
  password: z.string().min(1, 'Ingrese su contraseña').max(200),
});

const TotpSchema = z.object({ codigo: z.string().trim().regex(/^\d{6}$/, 'El código tiene 6 dígitos') });

interface DesafioGuardado {
  desafio: string;
  estado: 'TOTP' | 'ENROLAR_TOTP';
  expira: number;
}

/**
 * Sesión del backoffice (decisión 3 del estudio: también pasa por el BFF). Flujo del personal:
 * contraseña → código TOTP (o enrolamiento en el primer ingreso). El desafío intermedio también
 * queda en una cookie sellada, así que el navegador nunca tiene un token.
 */
@Controller('bo')
export class BackofficeController {
  private readonly cfg = leerConfig();

  constructor(private readonly core: CoreClient, private readonly sesion: GestorSesion) {}

  @Post('auth/login')
  @HttpCode(200)
  async login(@Body(new ZodPipe(LoginSchema)) body: z.infer<typeof LoginSchema>, @Res({ passthrough: true }) res: Respuesta) {
    const r = await this.core.post<ResultadoLogin>('/api/auth/login', { ...body, cliente: 'BACKOFFICE' });
    if (r.estado === 'OK' && r.sesion) return { estado: 'OK', usuario: this.sesion.guardar(res, 'backoffice', r.sesion).usuario };

    const guardado: DesafioGuardado = { desafio: r.desafio!, estado: r.estado as DesafioGuardado['estado'], expira: Date.now() + 5 * 60_000 };
    res.append('Set-Cookie', cookie(NOMBRE_DESAFIO, sellar(guardado, this.cfg.claveSesion, NOMBRE_DESAFIO), 5 * 60));
    // En el enrolamiento el secreto se muestra una sola vez para escanearlo con el autenticador.
    return { estado: r.estado, secretoTotp: r.secretoTotp, uriTotp: r.uriTotp };
  }

  @Post('auth/totp')
  @HttpCode(200)
  async totp(@Body(new ZodPipe(TotpSchema)) body: z.infer<typeof TotpSchema>, @Req() req: Peticion, @Res({ passthrough: true }) res: Respuesta) {
    const d = abrir<DesafioGuardado>(leerCookies(req.headers.cookie as string | undefined)[NOMBRE_DESAFIO], this.cfg.claveSesion, NOMBRE_DESAFIO);
    if (!d || d.expira < Date.now()) throw new UnauthorizedException({ status: 401, title: 'La verificación expiró. Ingrese nuevamente.', codigo: 'DESAFIO' });

    try {
      const r = await this.core.post<ResultadoLogin>('/api/auth/totp', { desafio: d.desafio, codigo: body.codigo });
      res.append('Set-Cookie', borrarCookie(NOMBRE_DESAFIO));
      return { estado: 'OK', usuario: this.sesion.guardar(res, 'backoffice', r.sesion!).usuario };
    } catch (e) {
      // Un desafío vencido o bloqueado no se puede reintentar: se borra y se vuelve al inicio.
      if (e instanceof HttpException && e.getStatus() !== 401) res.append('Set-Cookie', borrarCookie(NOMBRE_DESAFIO));
      throw e;
    }
  }

  @Get('auth/sesion')
  async actual(@Req() req: Peticion, @Res({ passthrough: true }) res: Respuesta) {
    return { usuario: await this.sesion.usuario(req, res, 'backoffice') };
  }

  @Post('auth/salir')
  @HttpCode(204)
  async salir(@Req() req: Peticion, @Res({ passthrough: true }) res: Respuesta) {
    await cerrarSesion(this.core, this.sesion, req, res, 'backoffice');
  }

  /**
   * Proxy autenticado hacia la API Core para el backoffice: /bff/bo/api/... → /api/...
   * NGINX ya no publica /api/ (F2): toda petición del navegador pasa por aquí con la cookie.
   */
  @All(['api/*ruta', 'fhir/*ruta'])
  async proxy(
    @Req() req: Peticion & { method: string; originalUrl: string; body?: unknown },
    @Res() res: Respuesta & { status(c: number): { type(t: string): { send(b: Buffer): unknown } } },
  ) {
    const destino = rutaProxyPermitida(req.originalUrl);
    if (!destino) throw new HttpException({ status: 404, title: 'No encontrado' }, 404);

    const token = await this.sesion.token(req, res, 'backoffice');
    const r = await this.core.reenviar(req.method, destino, token, req.body);
    res.status(r.status).type(r.tipo).send(r.cuerpo);
  }
}

