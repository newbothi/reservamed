/** Cliente de la API Core. El backoffice no pasa por el BFF: habla directo con /api. */

export interface Sesion {
  token: string;
  expiraUtc: string;
  rol: 'RECEPCION' | 'MEDICO' | 'ADMIN' | 'PACIENTE';
  nombre: string;
  profesionalId: number | null;
  sedeId: number | null;
}

const CLAVE = 'rm_bo_sesion';

export function leerSesion(): Sesion | null {
  try {
    const s = JSON.parse(localStorage.getItem(CLAVE) ?? 'null') as Sesion | null;
    return s && Date.parse(s.expiraUtc) > Date.now() ? s : null;
  } catch {
    return null;
  }
}

export function guardarSesion(s: Sesion | null) {
  try {
    if (s) localStorage.setItem(CLAVE, JSON.stringify(s));
    else localStorage.removeItem(CLAVE);
  } catch {
    /* sin almacenamiento */
  }
}

export class ErrorApi extends Error {
  constructor(public status: number, mensaje: string, public codigo?: string) {
    super(mensaje);
  }
}

export async function api<T>(ruta: string, opciones: { metodo?: string; cuerpo?: unknown } = {}): Promise<T> {
  const sesion = leerSesion();
  const resp = await fetch(ruta, {
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
    if (resp.status === 401) {
      guardarSesion(null);
      if (!ruta.endsWith('/login')) location.reload();
    }
    throw new ErrorApi(resp.status, datos?.title ?? `Error ${resp.status}`, datos?.codigo);
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
