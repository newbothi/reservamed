/**
 * Cliente del BFF. Todas las llamadas del portal pasan por /bff.
 * Fase 2 (A1): el navegador ya no guarda ningún token. La sesión vive en una cookie HttpOnly
 * sellada por el BFF; JavaScript no puede leerla, así que un XSS no puede robarla.
 * La cabecera X-ReservaMed acompaña cada solicitud: el BFF la exige contra CSRF.
 */
import { resolver, type Desafio, type Solucion } from './pow';

export interface Usuario {
  id: number;
  rol: string;
  nombre: string;
  email: string;
  pacienteId: number | null;
}

export class ErrorApi extends Error {
  constructor(
    public status: number,
    mensaje: string,
    public detalles?: { campo: string; mensaje: string }[],
    public codigo?: string,
  ) {
    super(mensaje);
  }
}

/** Avisa a la interfaz que la sesión terminó (venció, se revocó o se cerró en otra pestaña). */
export const EVENTO_SESION_TERMINADA = 'rm:sesion-terminada';

export async function api<T>(ruta: string, opciones: { metodo?: string; cuerpo?: unknown } = {}): Promise<T> {
  const resp = await fetch(`/bff${ruta}`, {
    method: opciones.metodo ?? (opciones.cuerpo !== undefined ? 'POST' : 'GET'),
    credentials: 'same-origin',
    headers: {
      'X-ReservaMed': '1',
      ...(opciones.cuerpo !== undefined ? { 'Content-Type': 'application/json' } : {}),
    },
    body: opciones.cuerpo !== undefined ? JSON.stringify(opciones.cuerpo) : undefined,
  });
  if (resp.status === 204) return undefined as T;
  const datos = await resp.json().catch(() => ({}));
  if (!resp.ok) {
    if (resp.status === 401 && !ruta.startsWith('/auth/')) window.dispatchEvent(new Event(EVENTO_SESION_TERMINADA));
    const detalle = datos?.errores?.[0]?.mensaje;
    throw new ErrorApi(resp.status, detalle ?? datos?.title ?? 'Ocurrió un error', datos?.errores, datos?.codigo);
  }
  return datos as T;
}

/** Pide un desafío anti-bots al BFF y lo resuelve en el navegador (≈1 s). */
export async function pruebaAntiBots(): Promise<Solucion> {
  return resolver(await api<Desafio>('/pow'));
}

/**
 * Hallazgo B1: el parámetro ?volver= solo acepta rutas internas del portal. Se rechaza
 * "//otro.sitio", "https://…", "/\\otro" y cualquier esquema, para no servir de redirección abierta.
 */
export function destinoSeguro(volver: string | null, porDefecto = '/mis-citas'): string {
  if (!volver) return porDefecto;
  if (!/^\/(?![/\\])[A-Za-z0-9\-._~%!$&'()*+,;=:@/?]*$/.test(volver)) return porDefecto;
  return volver;
}

/* ---------- Formato chileno ---------- */

const pesos = new Intl.NumberFormat('es-CL', { style: 'currency', currency: 'CLP', maximumFractionDigits: 0 });
export const clp = (n: number | null | undefined) => pesos.format(n ?? 0);

/** Las fechas llegan como hora local de Chile sin zona ("2026-10-09T10:20:00"). */
const comoLocal = (s: string) => new Date(s.length === 10 ? `${s}T00:00:00` : s);

export const fechaLarga = (s: string) =>
  comoLocal(s).toLocaleDateString('es-CL', { weekday: 'long', day: 'numeric', month: 'long' });

export const fechaHora = (s: string) =>
  comoLocal(s).toLocaleString('es-CL', { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

export const hora = (s: string) => s.slice(11, 16);

/** Fecha de hoy en Chile como YYYY-MM-DD. */
export function hoyChile(): string {
  return new Intl.DateTimeFormat('sv-SE', { timeZone: 'America/Santiago' }).format(new Date());
}

export function sumarDias(fecha: string, dias: number): string {
  const d = new Date(`${fecha}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + dias);
  return d.toISOString().slice(0, 10);
}

export const ESTADOS: Record<string, { texto: string; tono: string }> = {
  PENDIENTE_PAGO: { texto: 'Pendiente de pago', tono: 'alerta' },
  RESERVADA: { texto: 'Reservada', tono: 'info' },
  CONFIRMADA: { texto: 'Confirmada y pagada', tono: 'ok' },
  ADMITIDA: { texto: 'Admitida', tono: 'ok' },
  ATENDIDA: { texto: 'Atendida', tono: 'neutro' },
  CANCELADA: { texto: 'Anulada', tono: 'error' },
  EXPIRADA: { texto: 'Expirada', tono: 'error' },
  NO_ASISTIO: { texto: 'No asistió', tono: 'error' },
};

/** Valida y formatea RUT en el cliente (la validación definitiva la hace el servidor). */
export function formatearRut(entrada: string): string {
  const limpio = entrada.replace(/[^\dkK]/g, '').toUpperCase();
  if (limpio.length < 2) return limpio;
  const cuerpo = limpio.slice(0, -1).replace(/\B(?=(\d{3})+(?!\d))/g, '.');
  return `${cuerpo}-${limpio.slice(-1)}`;
}

/**
 * Indicador de fortaleza (solo orientativo; la política real la aplica la API, M2):
 * largo, variedad y que no contenga el correo.
 */
export function fortaleza(clave: string, email = ''): { nivel: 0 | 1 | 2 | 3; texto: string } {
  if (clave.length < 12) return { nivel: 0, texto: `Faltan ${12 - clave.length} caracteres (mínimo 12)` };
  const local = email.split('@')[0]?.toLowerCase();
  if (local && local.length >= 3 && clave.toLowerCase().includes(local)) return { nivel: 0, texto: 'No use su correo en la contraseña' };
  const clases = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter((r) => r.test(clave)).length;
  const puntos = (clave.length >= 16 ? 2 : 1) + (clases >= 3 ? 1 : 0);
  if (puntos >= 3) return { nivel: 3, texto: 'Fuerte' };
  if (puntos === 2) return { nivel: 2, texto: 'Aceptable' };
  return { nivel: 1, texto: 'Débil: alargue la frase o combine letras, números y símbolos' };
}
