import { HttpException, Injectable, Logger, ServiceUnavailableException } from '@nestjs/common';
import { ipActual } from './contexto-ip';

type Metodo = 'GET' | 'POST' | 'PUT' | 'DELETE';

/**
 * Cliente HTTP hacia la API Core (ASP.NET Core).
 * Reenvía el JWT del usuario y traduce los ProblemDetails de la API a errores HTTP de Nest,
 * conservando el mismo status y mensaje para que el portal los muestre tal cual.
 */
@Injectable()
export class CoreClient {
  private readonly log = new Logger(CoreClient.name);
  private readonly base = (process.env.API_CORE_URL ?? 'http://localhost:5080').replace(/\/$/, '');

  async llamar<T>(metodo: Metodo, ruta: string, opciones: { token?: string; cuerpo?: unknown } = {}): Promise<T> {
    const headers: Record<string, string> = { Accept: 'application/json' };
    if (opciones.cuerpo !== undefined) headers['Content-Type'] = 'application/json';
    if (opciones.token) headers.Authorization = opciones.token;
    // A4: IP calculada por Express (solo confía en NGINX), nunca la cabecera del navegador.
    const ip = ipActual();
    if (ip) headers['X-Forwarded-For'] = ip;

    let resp: Response;
    try {
      resp = await fetch(`${this.base}${ruta}`, {
        method: metodo,
        headers,
        body: opciones.cuerpo !== undefined ? JSON.stringify(opciones.cuerpo) : undefined,
        signal: AbortSignal.timeout(15_000),
      });
    } catch (e) {
      this.log.error(`API Core no responde: ${metodo} ${ruta} (${(e as Error).message})`);
      throw new ServiceUnavailableException({ status: 503, title: 'El servicio no está disponible. Intente en unos minutos.' });
    }

    if (resp.status === 204) return undefined as T;

    const texto = await resp.text();
    const datos = texto ? JSON.parse(texto) : undefined;
    if (!resp.ok) {
      // ProblemDetails: { status, title, codigo }
      throw new HttpException(
        { status: resp.status, title: datos?.title ?? 'Error', codigo: datos?.codigo },
        resp.status,
      );
    }
    return datos as T;
  }

  get<T>(ruta: string, token?: string) {
    return this.llamar<T>('GET', ruta, { token });
  }

  post<T>(ruta: string, cuerpo: unknown, token?: string) {
    return this.llamar<T>('POST', ruta, { cuerpo, token });
  }
}
