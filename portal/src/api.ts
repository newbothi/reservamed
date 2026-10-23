/** Cliente del BFF. Todas las llamadas del portal pasan por /bff. */

export interface Sesion {
  token: string;
  expiraUtc: string;
  rol: string;
  nombre: string;
  pacienteId: number | null;
}

const CLAVE = 'rm_sesion';

export function leerSesion(): Sesion | null {
  try {
    const s = JSON.parse(localStorage.getItem(CLAVE) ?? 'null') as Sesion | null;
    if (!s || Date.parse(s.expiraUtc) < Date.now()) return null;
    return s;
  } catch {
    return null;
  }
}

export function guardarSesion(s: Sesion | null) {
  try {
    if (s) localStorage.setItem(CLAVE, JSON.stringify(s));
    else localStorage.removeItem(CLAVE);
  } catch {
    /* almacenamiento no disponible */
  }
}

export class ErrorApi extends Error {
  constructor(public status: number, mensaje: string, public detalles?: { campo: string; mensaje: string }[]) {
    super(mensaje);
  }
}

export async function api<T>(ruta: string, opciones: { metodo?: string; cuerpo?: unknown } = {}): Promise<T> {
  const sesion = leerSesion();
  const resp = await fetch(`/bff${ruta}`, {
    method: opciones.metodo ?? (opciones.cuerpo !== undefined ? 'POST' : 'GET'),
    headers: {
      ...(opciones.cuerpo !== undefined ? { 'Content-Type': 'application/json' } : {}),
      ...(sesion ? { Authorization: `Bearer ${sesion.token}` } : {}),
    },
    body: opciones.cuerpo !== undefined ? JSON.stringify(opciones.cuerpo) : undefined,
  });
  if (resp.status === 204) return undefined as T;
  const datos = await resp.json().catch(() => ({}));
  if (!resp.ok) {
    if (resp.status === 401 && sesion) guardarSesion(null);
    const detalle = datos?.errores?.[0]?.mensaje;
    throw new ErrorApi(resp.status, detalle ?? datos?.title ?? 'Ocurrió un error', datos?.errores);
  }
  return datos as T;
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
