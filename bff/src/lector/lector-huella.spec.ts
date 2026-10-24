import { createHmac } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { claveLector, firmarLectura, leerConfigLectores } from './lector-huella';

const CLAVE = 'pruebas-lectores-huella-Hq7Zt2Wm9Kc4Xv8Lp3Rb';
const cfg = leerConfigLectores({ HUELLA_KEY: CLAVE })!;
const desafio = { nonce: 'n0nce-de-prueba-123', citaId: 42, rut: '30123456-7', sedeId: 1 };

describe('C3: simulador del lector de huella', () => {
  it('firma la lectura con la clave del lector de la sede', () => {
    const c = firmarLectura(cfg, desafio, 'COINCIDE', 1_800_000_000_000);
    const [v, payload, firma] = c.split('.');
    expect(v).toBe('v1');
    const lectura = JSON.parse(Buffer.from(payload, 'base64url').toString('utf8'));
    expect(lectura).toEqual({ lector: 'LECTOR-PROV-01', sedeId: 1, citaId: 42, rut: '30123456-7', nonce: desafio.nonce, emitido: 1_800_000_000, resultado: 'COINCIDE' });
    const esperada = createHmac('sha256', claveLector(CLAVE, 'LECTOR-PROV-01')).update(`v1.${payload}`).digest('base64url');
    expect(firma).toBe(esperada);
  });

  it('cada sede firma con su propio lector', () => {
    const c = firmarLectura(cfg, { ...desafio, sedeId: 2 }, 'COINCIDE');
    expect(JSON.parse(Buffer.from(c.split('.')[1], 'base64url').toString()).lector).toBe('LECTOR-LC-01');
    expect(() => firmarLectura(cfg, { ...desafio, sedeId: 99 }, 'COINCIDE')).toThrow();
  });

  it('sin clave (o con LECTOR_SIMULADO=false) el simulador queda apagado', () => {
    expect(leerConfigLectores({})).toBeNull();
    expect(leerConfigLectores({ HUELLA_KEY: 'corta' })).toBeNull();
    expect(leerConfigLectores({ HUELLA_KEY: CLAVE, LECTOR_SIMULADO: 'false' })).toBeNull();
  });
});
