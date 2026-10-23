import { describe, expect, it } from 'vitest';
import { formatearRut, hora, sumarDias } from './api';

describe('formatearRut', () => {
  it('agrega puntos y guion mientras se escribe', () => {
    expect(formatearRut('198763217')).toBe('19.876.321-7');
    expect(formatearRut('11223344k')).toBe('11.223.344-K');
  });

  it('ignora caracteres que no son dígitos ni K', () => expect(formatearRut('19a876b321-7')).toBe('19.876.321-7'));

  it('deja tal cual entradas de un carácter', () => expect(formatearRut('1')).toBe('1'));
});

describe('utilidades de fecha', () => {
  it('suma días cruzando fin de mes', () => expect(sumarDias('2026-10-30', 3)).toBe('2026-11-02'));
  it('extrae la hora', () => expect(hora('2026-10-26T08:30:00')).toBe('08:30'));
});
