import { describe, expect, it } from 'vitest';
import { destinoSeguro, fortaleza } from './api';
import { bitsEnCero, resolver, sha256 } from './pow';

/** Referencia: Web Crypto (el mismo SHA-256 del navegador y de Node). */
const referencia = async (t: string) =>
  Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(t))), (b) => b.toString(16).padStart(2, '0')).join('');

const hex = (h: Uint32Array) => Array.from(h, (x) => (x >>> 0).toString(16).padStart(8, '0')).join('');

describe('B1: ?volver= solo acepta rutas internas', () => {
  it.each(['/mis-citas', '/reservar?x=1', '/pago/retorno?token_ws=abc'])('acepta %s', (r) => expect(destinoSeguro(r)).toBe(r));
  it.each(['//evil.example', '/\\evil.example', 'https://evil.example', 'javascript:alert(1)', 'mis-citas', '/a\nb', ' /x', ''])(
    'rechaza %j',
    (r) => expect(destinoSeguro(r)).toBe('/mis-citas'),
  );
  it('sin parámetro usa el destino por defecto', () => expect(destinoSeguro(null)).toBe('/mis-citas'));
});

describe('medidor de contraseña', () => {
  it('exige 12 caracteres', () => expect(fortaleza('Corta1!').nivel).toBe(0));
  it('no permite el correo dentro de la clave', () => expect(fortaleza('martina-2026-segura', 'martina@demo.cl').nivel).toBe(0));
  it('una frase larga y variada es fuerte', () => expect(fortaleza('Tres-perros-azules-2026').nivel).toBe(3));
});

describe('prueba de trabajo del navegador', () => {
  it.each(['', 'abc', 'a'.repeat(55), 'a'.repeat(56), 'a'.repeat(64), 'sal.123' + 98765])('SHA-256 coincide con Web Crypto (%#)', async (t) =>
    expect(hex(sha256(t))).toBe(await referencia(t)),
  );

  it('encuentra una solución que el BFF acepta', async () => {
    const d = { sal: 'abcdef.9999999999999', dificultad: 12, firma: 'x' };
    const s = await resolver(d);
    expect(bitsEnCero(sha256(d.sal + s.numero))).toBeGreaterThanOrEqual(12);
    expect((await referencia(d.sal + s.numero)).slice(0, 3)).toBe('000');
  });
});
