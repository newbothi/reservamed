import { ArgumentMetadata, BadRequestException, PipeTransform } from '@nestjs/common';
import { z, ZodTypeAny } from 'zod';

/** Valida el cuerpo de la petición con un esquema Zod antes de llegar al controlador. */
export class ZodPipe<T extends ZodTypeAny> implements PipeTransform {
  constructor(private readonly esquema: T) {}

  transform(valor: unknown, _meta: ArgumentMetadata): z.infer<T> {
    const r = this.esquema.safeParse(valor);
    if (!r.success) {
      throw new BadRequestException({
        status: 400,
        title: 'Datos inválidos',
        errores: r.error.issues.map((i) => ({ campo: i.path.join('.'), mensaje: i.message })),
      });
    }
    return r.data;
  }
}

/** Valida RUT chileno con módulo 11 (el mismo algoritmo que usa la API Core). */
export function rutValido(entrada: string): boolean {
  const limpio = entrada.replace(/[.\s]/g, '').toUpperCase();
  const m = /^(\d{7,8})-?([\dK])$/.exec(limpio);
  if (!m) return false;
  let suma = 0;
  let mult = 2;
  for (const d of m[1].split('').reverse()) {
    suma += Number(d) * mult;
    mult = mult === 7 ? 2 : mult + 1;
  }
  const resto = 11 - (suma % 11);
  const dv = resto === 11 ? '0' : resto === 10 ? 'K' : String(resto);
  return dv === m[2];
}

const ZONA = 'America/Santiago';

/** Fecha y hora actual de Chile como string "YYYY-MM-DDTHH:mm:ss" (sin zona, igual que la API). */
export function ahoraChile(): string {
  return new Intl.DateTimeFormat('sv-SE', {
    timeZone: ZONA,
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit',
    hour12: false,
  }).format(new Date()).replace(' ', 'T');
}

/** Convierte una fecha-hora local (sin zona) a milisegundos comparables. */
export function ms(fechaLocal: string): number {
  return Date.parse(fechaLocal.length === 10 ? `${fechaLocal}T00:00:00Z` : `${fechaLocal}Z`);
}

/** "2026-10-09T10:20:00" → "2026-10-09" */
export const soloFecha = (fechaLocal: string) => fechaLocal.slice(0, 10);
