import { randomBytes } from 'node:crypto';
import { describe, expect, it, vi } from 'vitest';
import { leerConfig } from './config';
import { abrir, cookie, leerCookies, NOMBRE_COOKIE, sellar } from './cookie-sesion';
import { revisarCsrf } from './csrf';
import { GestorSesion } from './gestor-sesion.service';
import { rutaProxyPermitida } from './ruta-proxy';
import { bitsEnCero, crearDesafio, resolver, SolucionesUsadas, verificarSolucion } from './pow';

const CLAVE = randomBytes(32);

describe('A1: cookie de sesión sellada', () => {
  it('sella y abre; el contenido no es legible', () => {
    const sello = sellar({ access: 'secreto-jwt', n: 1 }, CLAVE);
    expect(sello).not.toContain('secreto-jwt');
    expect(abrir<{ access: string }>(sello, CLAVE)?.access).toBe('secreto-jwt');
  });

  it('una cookie alterada o de otra clave se ignora', () => {
    const sello = sellar({ rol: 'PACIENTE' }, CLAVE);
    const alterado = sello.slice(0, -2) + (sello.endsWith('A') ? 'BB' : 'AA');
    expect(abrir(alterado, CLAVE)).toBeNull();
    expect(abrir(sello, randomBytes(32))).toBeNull();
    expect(abrir('basura', CLAVE)).toBeNull();
  });

  it('la cookie es HttpOnly, Secure, SameSite=Strict y __Host-', () => {
    const c = cookie(NOMBRE_COOKIE.portal, 'x', 60);
    expect(c).toMatch(/^__Host-rm_p=x; Path=\/; HttpOnly; Secure; SameSite=Strict; Max-Age=60$/);
    expect(leerCookies('a=1; __Host-rm_p=valor%3D; b=2')['__Host-rm_p']).toBe('valor=');
  });
});

describe('Configuración del BFF', () => {
  it('exige BFF_SESSION_KEY de al menos 32 bytes', () => {
    expect(() => leerConfig({})).toThrow(/BFF_SESSION_KEY/);
    expect(() => leerConfig({ BFF_SESSION_KEY: Buffer.alloc(16).toString('base64') })).toThrow();
    expect(() => leerConfig({ BFF_SESSION_KEY: 'CAMBIAR-' + 'x'.repeat(40) })).toThrow();
    const cfg = leerConfig({ BFF_SESSION_KEY: randomBytes(32).toString('base64url') });
    expect(cfg.claveSesion.length).toBe(32);
    expect(cfg.claveSesion.equals(cfg.clavePow)).toBe(false);   // claves derivadas distintas
  });
});

describe('A8: prueba de trabajo anti-bots', () => {
  it('una solución válida se acepta una sola vez', () => {
    const usadas = new SolucionesUsadas();
    const s = resolver(crearDesafio(CLAVE, 10));
    expect(bitsEnCero(Buffer.from([0, 0x0f]))).toBe(12);
    expect(verificarSolucion(s, CLAVE, 10, usadas)).toBeNull();
    expect(verificarSolucion(s, CLAVE, 10, usadas)).toMatch(/ya se usó/);
  });

  it('no se puede bajar la dificultad ni inventar la firma', () => {
    const usadas = new SolucionesUsadas();
    const facil = resolver(crearDesafio(CLAVE, 4));
    expect(verificarSolucion(facil, CLAVE, 10, usadas)).toMatch(/inválida/);
    const d = crearDesafio(CLAVE, 10);
    expect(verificarSolucion({ ...resolver(d), firma: 'ab'.repeat(32) }, CLAVE, 10, usadas)).toMatch(/inválida/);
  });

  it('un desafío vencido o sin solución se rechaza', () => {
    const usadas = new SolucionesUsadas();
    const viejo = resolver(crearDesafio(CLAVE, 8, Date.now() - 10 * 60_000));
    expect(verificarSolucion(viejo, CLAVE, 8, usadas)).toMatch(/expiró/);
    expect(verificarSolucion(undefined, CLAVE, 8, usadas)).toMatch(/Falta/);
  });
});

describe('CSRF', () => {
  const origenes = ['https://reservamed.localhost'];
  const req = (method: string, headers: Record<string, string>) => ({ method, headers });

  it('las lecturas no exigen nada', () => expect(revisarCsrf(req('GET', {}), origenes)).toBeNull());

  it('las acciones exigen la cabecera propia', () => {
    expect(revisarCsrf(req('POST', {}), origenes)).toMatch(/cabecera/);
    expect(revisarCsrf(req('POST', { 'x-reservamed': '1', origin: 'https://reservamed.localhost' }), origenes)).toBeNull();
  });

  it('rechaza otro origen o una petición cross-site', () => {
    expect(revisarCsrf(req('POST', { 'x-reservamed': '1', origin: 'https://evil.example' }), origenes)).toMatch(/origen/);
    expect(revisarCsrf(req('DELETE', { 'x-reservamed': '1', 'sec-fetch-site': 'cross-site' }), origenes)).toMatch(/otro sitio/);
  });
});

describe('Renovación de la sesión en el BFF', () => {
  process.env.BFF_SESSION_KEY = randomBytes(32).toString('base64');

  function sesionQueVence(gestor: GestorSesion) {
    const cookies: string[] = [];
    const res = { append: (_: string, v: string) => cookies.push(v) };
    gestor.guardar(res, 'portal', {
      accessToken: 'viejo',
      accessExpiraUtc: new Date(Date.now() + 10_000).toISOString(),   // vence en 10 s: hay que renovar
      refreshToken: 'refresh-1',
      sesionExpiraUtc: new Date(Date.now() + 3_600_000).toISOString(),
      usuario: { id: 1, rol: 'PACIENTE', nombre: 'Ana', email: 'a@b.cl', pacienteId: 1, profesionalId: null, sedeId: null },
    });
    const valor = cookies[0].split(';')[0];
    return { headers: { cookie: valor } };
  }

  it('peticiones simultáneas comparten una sola renovación (no disparan la detección de reuso)', async () => {
    const post = vi.fn(async () => {
      await new Promise((r) => setTimeout(r, 20));
      return {
        accessToken: 'nuevo', accessExpiraUtc: new Date(Date.now() + 900_000).toISOString(),
        refreshToken: 'refresh-2', sesionExpiraUtc: new Date(Date.now() + 3_600_000).toISOString(),
        usuario: { id: 1, rol: 'PACIENTE', nombre: 'Ana', email: 'a@b.cl', pacienteId: 1, profesionalId: null, sedeId: null },
      };
    });
    const gestor = new GestorSesion({ post } as never);
    const req = sesionQueVence(gestor);
    const res = { append: vi.fn() };

    const tokens = await Promise.all([1, 2, 3, 4].map(() => gestor.token(req, res, 'portal')));

    expect(post).toHaveBeenCalledTimes(1);
    expect(new Set(tokens)).toEqual(new Set(['Bearer nuevo']));
  });

  it('si la API rechaza la renovación, se borra la cookie y responde 401', async () => {
    const { UnauthorizedException } = await import('@nestjs/common');
    const gestor = new GestorSesion({ post: vi.fn(async () => { throw new UnauthorizedException(); }) } as never);
    const req = sesionQueVence(gestor);
    const cookies: string[] = [];

    await expect(gestor.token(req, { append: (_: string, v: string) => cookies.push(v) }, 'portal')).rejects.toThrow();
    expect(cookies.some((c) => c.startsWith('__Host-rm_p=;') && c.includes('Max-Age=0'))).toBe(true);
  });
});

describe('proxy del backoffice', () => {
  it('reenvía rutas /api normales con su consulta', () => {
    expect(rutaProxyPermitida('/bff/bo/api/agenda/dia?fecha=2026-10-24')).toBe('/api/agenda/dia?fecha=2026-10-24');
    expect(rutaProxyPermitida('/bff/bo/api/ml/modelo/entrenar')).toBe('/api/ml/modelo/entrenar');
    expect(rutaProxyPermitida('/bff/bo/fhir/Patient/12')).toBe('/fhir/Patient/12');
  });
  it.each([
    '/bff/bo/api/auth/renovar',
    '/bff/bo/api/AUTH/cerrar',
    '/bff/bo/api/../api/auth/renovar',
    '/bff/bo/api/x/../../health',
    '/bff/bo/api/%61uth/renovar',
    '/bff/bo/api/%2e%2e/auth',
    '/bff/bo/api//auth/renovar',
    '/bff/bo/api\\auth',
    '/bff/bo/otra',
  ])('rechaza %s', (u) => expect(rutaProxyPermitida(u)).toBeNull());
});
