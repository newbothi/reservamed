import { describe, expect, it } from 'vitest';
import { ms, rutValido, soloFecha } from './util';

describe('rutValido (módulo 11)', () => {
  it.each(['19876321-7', '19.876.321-7', '198763217', '11223344-K', '11.223.344-k', '9876543-3'])(
    'acepta %s',
    (rut) => expect(rutValido(rut)).toBe(true),
  );

  it.each(['19876321-8', '11223344-0', '123-4', 'abc', ''])('rechaza %s', (rut) =>
    expect(rutValido(rut)).toBe(false),
  );
});

describe('fechas locales sin zona', () => {
  it('compara fechas y fechas-hora en la misma escala', () => {
    expect(ms('2026-10-26T08:30:00') - ms('2026-10-26')).toBe(8.5 * 3_600_000);
  });

  it('extrae la fecha', () => expect(soloFecha('2026-10-26T08:30:00')).toBe('2026-10-26'));
});
