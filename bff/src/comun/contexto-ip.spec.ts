import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { afterEach, describe, expect, it, vi } from 'vitest';
// eslint-disable-next-line @typescript-eslint/no-require-imports
const express = require('express');
import { CoreClient } from './core-client.service';
import { ipActual, middlewareIp, normalizarIp, proxiesConfiables } from './contexto-ip';

/** Levanta un Express real con la configuración del BFF y devuelve la IP que ve el BFF. */
async function ipVista(trustProxy: string[], xff?: string): Promise<string | undefined> {
  const app = express();
  app.set('trust proxy', trustProxy);
  app.use(middlewareIp);
  app.get('/', (_req: unknown, res: { json: (v: unknown) => void }) => res.json({ ip: ipActual() ?? null }));
  const server = createServer(app);
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  try {
    const { port } = server.address() as AddressInfo;
    const resp = await fetch(`http://127.0.0.1:${port}/`, { headers: xff ? { 'X-Forwarded-For': xff } : {} });
    return (await resp.json()).ip ?? undefined;
  } finally {
    server.close();
  }
}

describe('A4: IP del cliente en el BFF', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('ignora X-Forwarded-For si la conexión no viene de un proxy confiable', async () => {
    // Aquí la conexión viene de 127.0.0.1 y solo se confía en 172.30.1.10 (NGINX).
    expect(await ipVista(['172.30.1.10'], '203.0.113.66')).toBe('127.0.0.1');
  });

  it('acepta la IP que informa un proxy confiable', async () => {
    expect(await ipVista(proxiesConfiables(''), '198.51.100.7')).toBe('198.51.100.7');
  });

  it('con varias IPs en la cabecera toma la que agregó el proxy confiable, no la del atacante', async () => {
    expect(await ipVista(['loopback'], '203.0.113.66, 198.51.100.7')).toBe('198.51.100.7');
  });

  it('TRUSTED_PROXIES agrega IPs a loopback', () => {
    expect(proxiesConfiables('172.30.1.10, 10.0.0.2')).toEqual(['loopback', '172.30.1.10', '10.0.0.2']);
    expect(proxiesConfiables(undefined)).toEqual(['loopback']);
  });

  it('normaliza IPv4 mapeada y descarta basura', () => {
    expect(normalizarIp('::ffff:198.51.100.7')).toBe('198.51.100.7');
    expect(normalizarIp('<script>')).toBeUndefined();
    expect(normalizarIp(undefined)).toBeUndefined();
  });

  it('CoreClient envía a la API la IP del contexto y no otra', async () => {
    const llamadas: Record<string, string>[] = [];
    vi.stubGlobal('fetch', vi.fn(async (_url: string, init: { headers: Record<string, string> }) => {
      llamadas.push(init.headers);
      return new Response('{}', { status: 200 });
    }));
    const core = new CoreClient();

    await new Promise<void>((resolve, reject) =>
      middlewareIp({ ip: '::ffff:198.51.100.7' }, undefined, () => core.get('/api/x').then(() => resolve(), reject)),
    );
    await core.get('/api/fuera-de-peticion');

    expect(llamadas[0]['X-Forwarded-For']).toBe('198.51.100.7');
    expect(llamadas[1]['X-Forwarded-For']).toBeUndefined();
  });
});
