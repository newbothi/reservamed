import { createHmac } from 'node:crypto';

/**
 * Simulador del lector biométrico de cada sede (hallazgo C3).
 *
 * En una clínica real el lector (o el servicio de I-Med) captura la huella y FIRMA la lectura
 * con una clave que vive en el dispositivo. Aquí el BFF hace ese papel: la clave del lector
 * (HUELLA_KEY) está en el servidor, nunca en el navegador, así que el navegador no puede
 * fabricar ni alterar un comprobante. La API valida la firma, la sede del lector, la cita, el
 * RUT, la hora y que el desafío no se haya usado.
 *
 * Formato (igual que VerificadorHuella en la API):
 *   v1.<base64url(JSON lectura)>.<base64url(HMAC-SHA256(claveLector, "v1." + payload))>
 *   claveLector = HMAC-SHA256(HUELLA_KEY, "lector:" + id)
 */
export interface DesafioHuella {
  nonce: string;
  citaId: number;
  rut: string;
  sedeId: number;
}

export type ResultadoLectura = 'COINCIDE' | 'NO_COINCIDE';

export interface ConfigLectores {
  clave: string;
  /** sedeId → id del lector instalado en esa sede */
  porSede: Map<number, string>;
}

const b64url = (b: Buffer) => b.toString('base64url');

export function claveLector(maestra: string, lectorId: string): Buffer {
  return createHmac('sha256', Buffer.from(maestra, 'utf8')).update(`lector:${lectorId}`, 'utf8').digest();
}

export function firmarLectura(
  cfg: ConfigLectores,
  d: DesafioHuella,
  resultado: ResultadoLectura,
  ahora = Date.now(),
): string {
  const lector = cfg.porSede.get(d.sedeId);
  if (!lector) throw new Error(`No hay lector configurado para la sede ${d.sedeId}`);
  const lectura = {
    lector,
    sedeId: d.sedeId,
    citaId: d.citaId,
    rut: d.rut,
    nonce: d.nonce,
    emitido: Math.floor(ahora / 1000),
    resultado,
  };
  const payload = b64url(Buffer.from(JSON.stringify(lectura), 'utf8'));
  const firma = createHmac('sha256', claveLector(cfg.clave, lector)).update(`v1.${payload}`, 'utf8').digest();
  return `v1.${payload}.${b64url(firma)}`;
}

/** HUELLA_KEY (>= 32 caracteres) y HUELLA_LECTORES ("1:LECTOR-PROV-01,2:LECTOR-LC-01"). Sin clave: simulador apagado. */
export function leerConfigLectores(env: NodeJS.ProcessEnv = process.env): ConfigLectores | null {
  const clave = env.HUELLA_KEY ?? '';
  if (clave.length < 32 || env.LECTOR_SIMULADO === 'false') return null;
  const porSede = new Map<number, string>();
  for (const par of (env.HUELLA_LECTORES ?? '1:LECTOR-PROV-01,2:LECTOR-LC-01').split(',')) {
    const [sede, id] = par.split(':').map((s) => s.trim());
    if (Number.isInteger(Number(sede)) && id) porSede.set(Number(sede), id);
  }
  return { clave, porSede };
}
