import { Body, Controller, Get, HttpCode, HttpException, Post, Req, Res } from '@nestjs/common';
import { z } from 'zod';
import { CoreClient } from '../comun/core-client.service';
import { GestorSesion, Req as Peticion, Res as Respuesta, SesionEmitida } from '../comun/gestor-sesion.service';
import { ServicioPow } from '../comun/pow.service';
import { rutValido, ZodPipe } from '../comun/util';

const LoginSchema = z.object({
  email: z.string().trim().toLowerCase().email('Correo no válido'),
  password: z.string().min(1, 'Ingrese su contraseña'),
});

const RegistroSchema = z
  .object({
    rut: z.string().refine(rutValido, 'RUT no válido'),
    nombres: z.string().trim().min(2, 'Ingrese sus nombres'),
    apellidoPaterno: z.string().trim().min(2, 'Ingrese su apellido paterno'),
    apellidoMaterno: z.string().trim().optional(),
    fechaNacimiento: z.string().regex(/^\d{4}-\d{2}-\d{2}$/, 'Fecha no válida'),
    sexo: z.enum(['M', 'F', 'X']),
    email: z.string().trim().toLowerCase().email('Correo no válido'),
    telefono: z.string().regex(/^\+?56\s?9\s?\d{4}\s?\d{4}$/, 'Teléfono chileno: +56 9 XXXX XXXX').optional().or(z.literal('')),
    comuna: z.string().optional(),
    password: z.string().min(12, 'Mínimo 12 caracteres').max(128),
    financiadorId: z.number().int().positive('Seleccione su previsión'),
    planSaludId: z.number().int().positive().nullable().optional(),
    tramoFonasa: z.enum(['A', 'B', 'C', 'D']).nullable().optional(),
    esCarga: z.boolean().default(false),
    afiliadoRut: z.string().nullable().optional(),
    afiliadoNombre: z.string().nullable().optional(),
    aceptaPrivacidad: z.literal(true, { errorMap: () => ({ message: 'Debe aceptar la política de privacidad' }) }),
    pow: z.unknown(),
  })
  .refine((r) => !r.esCarga || (r.afiliadoRut && rutValido(r.afiliadoRut)), {
    message: 'Ingrese un RUT válido del titular del plan',
    path: ['afiliadoRut'],
  });

export interface ResultadoLogin {
  estado: 'OK' | 'TOTP' | 'ENROLAR_TOTP';
  sesion: SesionEmitida | null;
  desafio: string | null;
  secretoTotp: string | null;
  uriTotp: string | null;
}

/**
 * Sesión del portal de pacientes. Los tokens quedan en la cookie sellada (A1): el navegador
 * solo recibe nombre y rol para mostrar la interfaz.
 */
@Controller('auth')
export class AuthController {
  constructor(
    private readonly core: CoreClient,
    private readonly sesion: GestorSesion,
    private readonly pow: ServicioPow,
  ) {}

  @Post('login')
  @HttpCode(200)
  async login(@Body(new ZodPipe(LoginSchema)) body: z.infer<typeof LoginSchema>, @Res({ passthrough: true }) res: Respuesta) {
    const r = await this.core.post<ResultadoLogin>('/api/auth/login', { ...body, cliente: 'PORTAL' });
    if (r.estado !== 'OK' || !r.sesion) throw new HttpException({ status: 403, title: 'Ingrese por el backoffice.' }, 403);
    return { usuario: this.sesion.guardar(res, 'portal', r.sesion).usuario };
  }

  /** Respuesta neutra de la API (202) haya o no cuenta previa (B4). Exige prueba anti-bots (A8). */
  @Post('registro')
  @HttpCode(202)
  async registro(@Body(new ZodPipe(RegistroSchema)) body: z.infer<typeof RegistroSchema>) {
    this.pow.exigir(body.pow);
    const { pow: _pow, ...datos } = body;
    return this.core.post<{ mensaje: string }>('/api/auth/registro', {
      ...datos,
      telefono: datos.telefono ? datos.telefono.replace(/\s/g, '') : null,
    });
  }

  @Post('verificar-correo')
  @HttpCode(200)
  verificar(@Body(new ZodPipe(z.object({ token: z.string().min(20).max(100) }))) body: { token: string }) {
    return this.core.post<{ mensaje: string }>('/api/auth/verificar-correo', body);
  }

  @Get('sesion')
  async actual(@Req() req: Peticion, @Res({ passthrough: true }) res: Respuesta) {
    return { usuario: await this.sesion.usuario(req, res, 'portal') };
  }

  @Post('salir')
  @HttpCode(204)
  async salir(@Req() req: Peticion, @Res({ passthrough: true }) res: Respuesta) {
    await cerrarSesion(this.core, this.sesion, req, res, 'portal');
  }
}

/** Revoca la sesión en la API (si sigue viva) y borra la cookie. Nunca falla hacia el usuario. */
export async function cerrarSesion(core: CoreClient, sesion: GestorSesion, req: Peticion, res: Respuesta, app: 'portal' | 'backoffice') {
  const d = sesion.leer(req, app);
  if (d) {
    try {
      await core.post('/api/auth/cerrar', {}, `Bearer ${d.access}`);
    } catch {
      /* el access pudo vencer: la sesión expira sola */
    }
  }
  sesion.borrar(res, app);
}
