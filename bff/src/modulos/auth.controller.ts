import { Body, Controller, HttpCode, Post } from '@nestjs/common';
import { z } from 'zod';
import { CoreClient } from '../comun/core-client.service';
import { Sesion } from '../comun/tipos';
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
    password: z.string().min(8, 'Mínimo 8 caracteres'),
    financiadorId: z.number().int().positive('Seleccione su previsión'),
    planSaludId: z.number().int().positive().nullable().optional(),
    tramoFonasa: z.enum(['A', 'B', 'C', 'D']).nullable().optional(),
    esCarga: z.boolean().default(false),
    afiliadoRut: z.string().nullable().optional(),
    afiliadoNombre: z.string().nullable().optional(),
  })
  .refine((r) => !r.esCarga || (r.afiliadoRut && rutValido(r.afiliadoRut)), {
    message: 'Ingrese un RUT válido del titular del plan',
    path: ['afiliadoRut'],
  });

@Controller('auth')
export class AuthController {
  constructor(private readonly core: CoreClient) {}

  @Post('login')
  @HttpCode(200)
  login(@Body(new ZodPipe(LoginSchema)) body: z.infer<typeof LoginSchema>) {
    return this.core.post<Sesion>('/api/auth/login', body);
  }

  @Post('registro')
  async registro(@Body(new ZodPipe(RegistroSchema)) body: z.infer<typeof RegistroSchema>) {
    return this.core.post<Sesion>('/api/auth/registro', {
      ...body,
      telefono: body.telefono ? body.telefono.replace(/\s/g, '') : null,
    });
  }
}
