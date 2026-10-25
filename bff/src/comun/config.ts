import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Configuración del BFF por variables de entorno. En local, bff/.env (lo genera
 * scripts/configurar-secretos.ps1, está en .gitignore) se carga sin pisar variables existentes.
 */
export function cargarEnv(ruta = join(__dirname, '..', '..', '.env')): void {
  if (!existsSync(ruta)) return;
  for (const linea of readFileSync(ruta, 'utf8').split(/\r?\n/)) {
    const m = /^\s*([A-Z_][A-Z0-9_]*)\s*=\s*(.*)\s*$/.exec(linea);
    if (m && process.env[m[1]] === undefined) process.env[m[1]] = m[2].replace(/^['"]|['"]$/g, '');
  }
}

/**
 * F5 (ASVS V6.4.1): en Docker los secretos son archivos en /run/secrets (Docker secrets) con el
 * nombre de la variable (BFF_SESSION_KEY, HUELLA_KEY). Tienen prioridad sobre el entorno.
 */
export function cargarSecretosArchivo(carpeta = '/run/secrets', env: NodeJS.ProcessEnv = process.env): string[] {
  if (!existsSync(carpeta)) return [];
  const cargados: string[] = [];
  for (const nombre of readdirSync(carpeta)) {
    if (!/^[A-Z_][A-Z0-9_]*$/.test(nombre)) continue;
    env[nombre] = readFileSync(join(carpeta, nombre), 'utf8').trim();
    cargados.push(nombre);
  }
  return cargados;
}

/** F5 (V9.2.2): certificado y clave del HTTPS interno (NGINX → BFF). Sin ellos, HTTP (desarrollo). */
export function leerTls(env: NodeJS.ProcessEnv = process.env): { cert: Buffer; key: Buffer } | undefined {
  const cert = env.BFF_TLS_CERT;
  const key = env.BFF_TLS_KEY;
  if (!cert || !key) {
    if (env.NODE_ENV === 'production') throw new Error('Defina BFF_TLS_CERT y BFF_TLS_KEY: en producción el BFF solo atiende por HTTPS.');
    return undefined;
  }
  return { cert: readFileSync(cert), key: readFileSync(key) };
}

export interface ConfigBff {
  claveSesion: Buffer;          // AES-256-GCM de la cookie de sesión
  clavePow: Buffer;             // HMAC de los desafíos anti-bots
  origenes: string[];           // Origin permitidos en peticiones que cambian estado (CSRF)
  dificultadPow: number;        // bits en cero exigidos al hash
  produccion: boolean;
}

const VALORES_PUBLICADOS = new Set(['CAMBIAR', 'dev-ml-key-cambiar']);

export function leerConfig(env: NodeJS.ProcessEnv = process.env): ConfigBff {
  const texto = env.BFF_SESSION_KEY ?? '';
  let clave: Buffer;
  try {
    clave = Buffer.from(texto.replace(/-/g, '+').replace(/_/g, '/'), 'base64');
  } catch {
    clave = Buffer.alloc(0);
  }
  if (clave.length < 32 || VALORES_PUBLICADOS.has(texto) || texto.toUpperCase().startsWith('CAMBIAR')) {
    throw new Error(
      'BFF_SESSION_KEY falta o es muy corta (base64 de al menos 32 bytes). ' +
        'Ejecute scripts/configurar-secretos.ps1 (local) o defínala en .env (Docker).',
    );
  }
  const { createHash } = require('node:crypto') as typeof import('node:crypto');
  return {
    claveSesion: createHash('sha256').update(clave).update('sesion').digest(),
    clavePow: createHash('sha256').update(clave).update('pow').digest(),
    origenes: (env.BFF_ORIGENES ?? 'https://reservamed.localhost,https://localhost,http://localhost:5173,http://localhost:5174')
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean),
    dificultadPow: Math.min(24, Math.max(8, Number(env.BFF_POW_DIFICULTAD ?? 16))),
    produccion: env.NODE_ENV === 'production',
  };
}
