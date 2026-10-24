import { createHash, createHmac, randomBytes, timingSafeEqual } from 'node:crypto';

/**
 * Hallazgo A8: prueba de trabajo (proof-of-work) contra bots en registro y reserva, al estilo de
 * ALTCHA. El servidor entrega un desafío firmado; el navegador busca un número tal que
 * SHA-256(sal + número) empiece con N bits en cero (≈1 s en un PC). Para una persona es casi
 * invisible; para un bot que quiere crear miles de cuentas o reservas, cada intento cuesta CPU.
 * Sin terceros (Google/Cloudflare), sin rastreo y compatible con la CSP estricta.
 */
export interface Desafio {
  sal: string;          // aleatorio + "." + expiración (epoch ms)
  dificultad: number;   // bits en cero exigidos
  firma: string;        // HMAC(sal|dificultad): el cliente no puede bajarla
}

export interface Solucion extends Desafio {
  numero: number;
}

const VIGENCIA_MS = 5 * 60_000;

export function crearDesafio(clave: Buffer, dificultad: number, ahora = Date.now()): Desafio {
  const sal = `${randomBytes(12).toString('hex')}.${ahora + VIGENCIA_MS}`;
  return { sal, dificultad, firma: firmar(clave, sal, dificultad) };
}

function firmar(clave: Buffer, sal: string, dificultad: number): string {
  return createHmac('sha256', clave).update(`${sal}|${dificultad}`).digest('hex');
}

export function bitsEnCero(hash: Buffer): number {
  let n = 0;
  for (const byte of hash) {
    if (byte === 0) { n += 8; continue; }
    return n + Math.clz32(byte) - 24;
  }
  return n;
}

export const hashSolucion = (sal: string, numero: number) => createHash('sha256').update(`${sal}${numero}`).digest();

/** Registro de soluciones ya usadas (una solución sirve una sola vez). */
export class SolucionesUsadas {
  private readonly usadas = new Map<string, number>();

  marcar(sal: string, expira: number): boolean {
    const ahora = Date.now();
    for (const [k, v] of this.usadas) if (v < ahora) this.usadas.delete(k);
    if (this.usadas.has(sal)) return false;
    this.usadas.set(sal, expira);
    return true;
  }
}

export function verificarSolucion(
  s: Partial<Solucion> | undefined,
  clave: Buffer,
  dificultadMinima: number,
  usadas: SolucionesUsadas,
  ahora = Date.now(),
): string | null {
  if (!s || typeof s.sal !== 'string' || typeof s.firma !== 'string' || !Number.isSafeInteger(s.numero) || typeof s.dificultad !== 'number')
    return 'Falta la verificación anti-bots.';
  if (s.dificultad < dificultadMinima) return 'Verificación anti-bots inválida.';
  const esperada = Buffer.from(firmar(clave, s.sal, s.dificultad), 'hex');
  const recibida = Buffer.from(s.firma, 'hex');
  if (recibida.length !== esperada.length || !timingSafeEqual(recibida, esperada)) return 'Verificación anti-bots inválida.';
  const expira = Number(s.sal.split('.')[1]);
  if (!Number.isFinite(expira) || expira < ahora) return 'La verificación anti-bots expiró. Intente de nuevo.';
  if (bitsEnCero(hashSolucion(s.sal, s.numero as number)) < s.dificultad) return 'Verificación anti-bots inválida.';
  if (!usadas.marcar(s.sal, expira)) return 'Esta verificación ya se usó. Intente de nuevo.';
  return null;
}

/** Resuelve un desafío (lo usan las pruebas y los scripts; el navegador tiene su propia versión). */
export function resolver(d: Desafio): Solucion {
  for (let numero = 0; ; numero++) if (bitsEnCero(hashSolucion(d.sal, numero)) >= d.dificultad) return { ...d, numero };
}
