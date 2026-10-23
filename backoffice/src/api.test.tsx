import { describe, expect, it } from 'vitest';
import { fecha, formatearRut, rutBono } from './api';

describe('formatos del backoffice', () => {
  it('formatea RUT para el buscador', () => expect(formatearRut('11223344k')).toBe('11.223.344-K'));

  it('rellena el RUT con ceros como en el bono', () => expect(rutBono('19876321-7')).toBe('0019876321-7'));

  it('muestra fechas en formato chileno', () => expect(fecha('2026-10-26T08:30:00')).toBe('26-10-2026'));
});
