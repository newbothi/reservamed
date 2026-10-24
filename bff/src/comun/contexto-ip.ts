import { AsyncLocalStorage } from 'node:async_hooks';

/**
 * Hallazgo A4. La IP del cliente se toma de req.ip, que Express calcula confiando SOLO en los
 * proxies de TRUSTED_PROXIES (NGINX). Nunca se reenvía la cabecera X-Forwarded-For que manda
 * el navegador: el BFF envía a la API una cabecera nueva con un único valor.
 */
const almacen = new AsyncLocalStorage<{ ip?: string }>();

/** Tipos mínimos de Express (evita depender de @types/express). */
type Req = { ip?: string };

export function middlewareIp(req: Req, _res: unknown, next: () => void): void {
  almacen.run({ ip: normalizarIp(req.ip) }, next);
}

export function ipActual(): string | undefined {
  return almacen.getStore()?.ip;
}

/** "::ffff:203.0.113.5" → "203.0.113.5"; descarta valores que no sean IP. */
export function normalizarIp(ip?: string): string | undefined {
  if (!ip) return undefined;
  const limpia = ip.startsWith('::ffff:') ? ip.slice(7) : ip;
  return /^[0-9a-fA-F:.]+$/.test(limpia) ? limpia : undefined;
}

/**
 * Valor para app.set('trust proxy'). Por defecto solo loopback (desarrollo local con Vite).
 * En Docker: TRUSTED_PROXIES=172.30.1.10 (IP fija de NGINX).
 */
export function proxiesConfiables(env = process.env.TRUSTED_PROXIES): string[] {
  const extra = (env ?? '')
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean);
  return ['loopback', ...extra];
}
