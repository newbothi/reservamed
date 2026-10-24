/**
 * Cliente del backoffice. Fase 2 (A1/A2): ya no habla directo con la API ni guarda tokens.
 * Todo pasa por el BFF en /bff/bo, que guarda la sesión en una cookie HttpOnly sellada y exige
 * la cabecera X-ReservaMed contra CSRF. El navegador solo conoce quién es el usuario.
 */

export interface Sesion {
  id: number;
  rol: 'RECEPCION' | 'MEDICO' | 'ADMIN' | 'PACIENTE';
  nombre: string;
  email: string;
  profesionalId: number | null;
  sedeId: number | null;
}

/** Usuario de la sesión actual, en memoria (la fuente de verdad es la cookie del BFF). */
let actual: Sesion | null = null;
export const leerSesion = () => actual;
export const fijarSesion = (s: Sesion | null) => {
  actual = s;
};

export class ErrorApi extends Error {
  constructor(public status: number, mensaje: string, public codigo?: string) {
    super(mensaje);
  }
}

export const EVENTO_SESION_TERMINADA = 'rm:bo-sesion-terminada';

/** `ruta` es la de la API ("/api/agenda/...") o una del BFF ("/auth/..."). */
export async function api<T>(ruta: string, opciones: { metodo?: string; cuerpo?: unknown } = {}): Promise<T> {
  const resp = await fetch(`/bff/bo${ruta}`, {
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
    throw new ErrorApi(resp.status, datos?.errores?.[0]?.mensaje ?? datos?.title ?? `Error ${resp.status}`, datos?.codigo);
  }
  return datos as T;
}

/* ---------- Formatos ---------- */
const pesos = new Intl.NumberFormat('es-CL', { style: 'currency', currency: 'CLP', maximumFractionDigits: 0 });
export const clp = (n: number | null | undefined) => pesos.format(n ?? 0);
export const hora = (s: string) => s.slice(11, 16);
export const fecha = (s: string) => `${s.slice(8, 10)}-${s.slice(5, 7)}-${s.slice(0, 4)}`;
export const hoyChile = () => new Intl.DateTimeFormat('sv-SE', { timeZone: 'America/Santiago' }).format(new Date());

export function formatearRut(entrada: string): string {
  const limpio = entrada.replace(/[^\dkK]/g, '').toUpperCase();
  if (limpio.length < 2) return limpio;
  return `${limpio.slice(0, -1).replace(/\B(?=(\d{3})+(?!\d))/g, '.')}-${limpio.slice(-1)}`;
}

/** "12345678-9" → "0012345678-9", como se ven los RUT en el bono. */
export const rutBono = (rut: string) => rut.padStart(12, '0');

export const ESTADOS: Record<string, { texto: string; tono: string }> = {
  PENDIENTE_PAGO: { texto: 'Pendiente pago web', tono: 'alerta' },
  RESERVADA: { texto: 'Reservada', tono: 'info' },
  CONFIRMADA: { texto: 'Pagada online', tono: 'ok' },
  ADMITIDA: { texto: 'Admitida', tono: 'ok' },
  ATENDIDA: { texto: 'Atendida', tono: 'neutro' },
  CANCELADA: { texto: 'Anulada', tono: 'error' },
  EXPIRADA: { texto: 'Expirada', tono: 'error' },
  NO_ASISTIO: { texto: 'No asistió', tono: 'error' },
};

export function Estado({ estado }: { estado: string }) {
  const e = ESTADOS[estado] ?? { texto: estado, tono: 'neutro' };
  return <span className={`badge ${e.tono}`}>{e.texto}</span>;
}
