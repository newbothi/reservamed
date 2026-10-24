import 'reflect-metadata';
import { LectorController } from './lector/lector.controller';
import { cargarEnv, leerConfig } from './comun/config';
cargarEnv();
import { Controller, Get, Module } from '@nestjs/common';
import { APP_GUARD, NestFactory } from '@nestjs/core';
import { ThrottlerGuard, ThrottlerModule } from '@nestjs/throttler';
import { middlewareIp, proxiesConfiables } from './comun/contexto-ip';
import { middlewareCsrf } from './comun/csrf';
import { GestorSesion } from './comun/gestor-sesion.service';
import { ServicioPow } from './comun/pow.service';
import { BackofficeController } from './modulos/backoffice.controller';
import { PowController } from './modulos/pow.controller';
import { CoreClient } from './comun/core-client.service';
import { AgendaController } from './modulos/agenda.controller';
import { AuthController } from './modulos/auth.controller';
import { CatalogoController } from './modulos/catalogo.controller';
import { CitasController, PagosController } from './modulos/citas.controller';

@Controller('health')
class HealthController {
  @Get()
  ok() {
    return { estado: 'ok', servicio: 'reservamed-bff' };
  }
}

@Module({
  imports: [
    // Límite de peticiones por IP: protege login y reservas de abuso (bots que acaparan horas).
    ThrottlerModule.forRoot([{ ttl: 60_000, limit: 120 }]),
  ],
  controllers: [HealthController, AuthController, BackofficeController, LectorController, PowController, CatalogoController, AgendaController, CitasController, PagosController],
  providers: [CoreClient, GestorSesion, ServicioPow, { provide: APP_GUARD, useClass: ThrottlerGuard }],
})
class AppModule {}

async function bootstrap() {
  // Falla al arrancar si falta BFF_SESSION_KEY (mismo criterio que C1 en la API).
  const cfg = leerConfig();
  const app = await NestFactory.create(AppModule);
  app.setGlobalPrefix('bff');
  app.enableShutdownHooks();
  // A4: X-Forwarded-For solo vale si viene de NGINX (TRUSTED_PROXIES); el resto se ignora.
  const express = app.getHttpAdapter().getInstance();
  express.set('trust proxy', proxiesConfiables());
  express.disable('x-powered-by');
  app.use(middlewareIp);
  // CSRF: la sesión ahora viaja en cookie (F2), así que toda acción exige cabecera propia y origen permitido.
  app.use(middlewareCsrf(cfg.origenes));
  // F4 (ASVS 8.2.1): las respuestas traen datos de pacientes: ni el navegador ni un proxy las guardan.
  app.use((_req: unknown, res: { setHeader(n: string, v: string): void }, next: () => void) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('Pragma', 'no-cache');
    next();
  });

  const puerto = Number(process.env.PORT ?? 3000);
  await app.listen(puerto);
  console.log(`BFF escuchando en http://localhost:${puerto}/bff (API Core: ${process.env.API_CORE_URL ?? 'http://localhost:5080'})`);
}

bootstrap();
