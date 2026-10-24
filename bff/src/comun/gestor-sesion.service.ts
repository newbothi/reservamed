import { createHash } from 'node:crypto';
import { HttpException, Injectable, Logger, UnauthorizedException } from '@nestjs/common';
import { leerConfig } from './config';
import { abrir, App, borrarCookie, cookie, DatosSesion, leerCookies, NOMBRE_COOKIE, sellar, UsuarioSesion } from './cookie-sesion';
import { CoreClient } from './core-client.service';

/** Lo que entrega la API al iniciar sesión o renovar (POST /api/auth/...). */
export interface SesionEmitida {
  accessToken: string;
  accessExpiraUtc: string;
  refreshToken: string;
  sesionExpiraUtc: string;
  usuario: UsuarioSesion;
}

/** Tipos mínimos de Express que se usan (evita depender de @types/express). */
export interface Req { headers: Record<string, string | string[] | undefined>; }
export interface Res { append(campo: string, valor: string): unknown; }

const RENOVAR_ANTES_MS = 60_000;

@Injectable()
export class GestorSesion {
  private readonly log = new Logger(GestorSesion.name);
  private readonly cfg = leerConfig();
  /** Renovaciones en curso por refresh token: varias peticiones a la vez comparten una sola. */
  private readonly renovando = new Map<string, Promise<DatosSesion | null>>();

  constructor(private readonly core: CoreClient) {}

  leer(req: Req, app: App): DatosSesion | null {
    const cookies = leerCookies(req.headers.cookie as string | undefined);
    const d = abrir<DatosSesion>(cookies[NOMBRE_COOKIE[app]], this.cfg.claveSesion, NOMBRE_COOKIE[app]);
    return d && d.sesionExpira > Date.now() ? d : null;
  }

  guardar(res: Res, app: App, s: SesionEmitida): DatosSesion {
    const datos: DatosSesion = {
      access: s.accessToken,
      accessExpira: Date.parse(s.accessExpiraUtc),
      refresh: s.refreshToken,
      sesionExpira: Date.parse(s.sesionExpiraUtc),
      usuario: s.usuario,
    };
    res.append('Set-Cookie', cookie(NOMBRE_COOKIE[app], sellar(datos, this.cfg.claveSesion, NOMBRE_COOKIE[app]), (datos.sesionExpira - Date.now()) / 1000));
    return datos;
  }

  borrar(res: Res, app: App): void {
    res.append('Set-Cookie', borrarCookie(NOMBRE_COOKIE[app]));
  }

  /** "Bearer ..." vigente para llamar a la API; renueva si hace falta. 401 si no hay sesión. */
  async token(req: Req, res: Res, app: App): Promise<string> {
    return `Bearer ${(await this.exigir(req, res, app)).access}`;
  }

  async usuario(req: Req, res: Res, app: App): Promise<UsuarioSesion> {
    return (await this.exigir(req, res, app)).usuario;
  }

  private async exigir(req: Req, res: Res, app: App): Promise<DatosSesion> {
    const d = this.leer(req, app);
    if (!d) throw new UnauthorizedException({ status: 401, title: 'Inicie sesión.' });
    if (d.accessExpira - Date.now() > RENOVAR_ANTES_MS) return d;

    const clave = createHash('sha256').update(d.refresh).digest('base64');
    let promesa = this.renovando.get(clave);
    if (!promesa) {
      promesa = this.renovar(d.refresh);
      this.renovando.set(clave, promesa);
      // Se recuerda 30 s: peticiones que lleguen con la cookie vieja reciben el mismo resultado.
      promesa.finally(() => setTimeout(() => this.renovando.delete(clave), 30_000).unref());
    }
    const nueva = await promesa;
    if (!nueva) {
      this.borrar(res, app);
      throw new UnauthorizedException({ status: 401, title: 'Su sesión expiró. Ingrese nuevamente.' });
    }
    res.append('Set-Cookie', cookie(NOMBRE_COOKIE[app], sellar(nueva, this.cfg.claveSesion, NOMBRE_COOKIE[app]), (nueva.sesionExpira - Date.now()) / 1000));
    return nueva;
  }

  private async renovar(refresh: string): Promise<DatosSesion | null> {
    try {
      const s = await this.core.post<SesionEmitida>('/api/auth/renovar', { refreshToken: refresh });
      return {
        access: s.accessToken,
        accessExpira: Date.parse(s.accessExpiraUtc),
        refresh: s.refreshToken,
        sesionExpira: Date.parse(s.sesionExpiraUtc),
        usuario: s.usuario,
      };
    } catch (e) {
      if (e instanceof HttpException && e.getStatus() === 401) return null;
      this.log.warn(`No se pudo renovar la sesión: ${(e as Error).message}`);
      throw e;
    }
  }
}
