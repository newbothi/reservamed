import 'reflect-metadata';
import { Controller, Get, Module } from '@nestjs/common';
import { APP_GUARD, NestFactory } from '@nestjs/core';
import { ThrottlerGuard, ThrottlerModule } from '@nestjs/throttler';
import { middlewareIp, proxiesConfiables } from './comun/contexto-ip';
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
  controllers: [HealthController, AuthController, CatalogoController, AgendaController, CitasController, PagosController],
  providers: [CoreClient, { provide: APP_GUARD, useClass: ThrottlerGuard }],
})
class AppModule {}

async function bootstrap() {
  const app = await NestFactory.create(AppModule);
  app.setGlobalPrefix('bff');
  app.enableShutdownHooks();
  // A4: X-Forwarded-For solo vale si viene de NGINX (TRUSTED_PROXIES); el resto se ignora.
  const express = app.getHttpAdapter().getInstance();
  express.set('trust proxy', proxiesConfiables());
  express.disable('x-powered-by');
  app.use(middlewareIp);
  if (process.env.NODE_ENV !== 'production') app.enableCors({ origin: ['http://localhost:5173'] });

  const puerto = Number(process.env.PORT ?? 3000);
  await app.listen(puerto);
  console.log(`BFF escuchando en http://localhost:${puerto}/bff (API Core: ${process.env.API_CORE_URL ?? 'http://localhost:5080'})`);
}

bootstrap();
