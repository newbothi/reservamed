import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { cargarSecretosArchivo, leerTls } from './config';

describe('F5: secretos y TLS interno', () => {
  it('lee los secretos de /run/secrets con prioridad sobre el entorno', () => {
    const dir = mkdtempSync(join(tmpdir(), 'secretos-'));
    try {
      writeFileSync(join(dir, 'BFF_SESSION_KEY'), 'desde-archivo\n');
      writeFileSync(join(dir, 'no-es-variable'), 'x');
      const env: NodeJS.ProcessEnv = { BFF_SESSION_KEY: 'desde-entorno' };
      expect(cargarSecretosArchivo(dir, env)).toEqual(['BFF_SESSION_KEY']);
      expect(env.BFF_SESSION_KEY).toBe('desde-archivo');
      expect(env['no-es-variable']).toBeUndefined();
    } finally {
      rmSync(dir, { recursive: true });
    }
  });

  it('en producción exige certificado para el HTTPS interno', () => {
    expect(() => leerTls({ NODE_ENV: 'production' })).toThrow(/BFF_TLS_CERT/);
    expect(leerTls({ NODE_ENV: 'development' })).toBeUndefined();
  });
});
