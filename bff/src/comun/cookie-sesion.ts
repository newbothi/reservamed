import { createCipheriv, createDecipheriv, randomBytes } from 'node:crypto';

/**
 * Hallazgo A1. Los tokens nunca llegan al navegador: viven sellados (AES-256-GCM) dentro de una
 * cookie HttpOnly, Secure y SameSite=Strict. JavaScript de la página no puede leerla, así que un
 * XSS no puede robar la sesión, y el sello impide modificarla.
 */
export type App = 'portal' | 'backoffice';

export interface UsuarioSesion {
  id: number;
  rol: string;
  nombre: string;
  email: string;
  pacienteId: number | null;
  profesionalId: number | null;
  sedeId: number | null;
}

export interface DatosSesion {
  access: string;
  accessExpira: number;      // epoch ms
  refresh: string;
  sesionExpira: number;      // epoch ms
  usuario: UsuarioSesion;
}

/** Prefijo __Host-: exige Secure, Path=/ y sin Domain (no la puede pisar un subdominio). */
export const NOMBRE_COOKIE: Record<App, string> = { portal: '__Host-rm_p', backoffice: '__Host-rm_b' };
export const NOMBRE_DESAFIO = '__Host-rm_2fa';

export function sellar(datos: unknown, clave: Buffer): string {
  const iv = randomBytes(12);
  const c = createCipheriv('aes-256-gcm', clave, iv);
  const cifrado = Buffer.concat([c.update(JSON.stringify(datos), 'utf8'), c.final()]);
  return Buffer.concat([iv, c.getAuthTag(), cifrado]).toString('base64url');
}

export function abrir<T>(sello: string | undefined, clave: Buffer): T | null {
  if (!sello) return null;
  try {
    const b = Buffer.from(sello, 'base64url');
    if (b.length < 29) return null;
    const d = createDecipheriv('aes-256-gcm', clave, b.subarray(0, 12));
    d.setAuthTag(b.subarray(12, 28));
    return JSON.parse(Buffer.concat([d.update(b.subarray(28)), d.final()]).toString('utf8')) as T;
  } catch {
    return null;   // alterada, de otra clave o corrupta: como si no existiera
  }
}

export function leerCookies(cabecera: string | undefined): Record<string, string> {
  const r: Record<string, string> = {};
  for (const parte of (cabecera ?? '').split(';')) {
    const i = parte.indexOf('=');
    if (i > 0) r[parte.slice(0, i).trim()] = decodeURIComponent(parte.slice(i + 1).trim());
  }
  return r;
}

export function cookie(nombre: string, valor: string, maxAgeSeg: number): string {
  return `${nombre}=${valor}; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=${Math.max(0, Math.floor(maxAgeSeg))}`;
}

export const borrarCookie = (nombre: string) => cookie(nombre, '', 0);
