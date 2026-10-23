/** Tipos que devuelve la API Core (camelCase, fechas locales de Chile sin zona). */

export interface Cupo {
  profesionalId: number;
  profesional: string;
  especialidadId: number;
  especialidad: string;
  sedeId: number;
  sede: string;
  prestacionId: number;
  prestacion: string;
  valorParticular: number;
  inicio: string;
  fin: string;
}

export interface CitaResumen {
  id: number;
  fechaHoraInicio: string;
  fechaHoraFin: string;
  estado: string;
  canal: string;
  expiraEn: string | null;
  profesionalId: number;
  profesional: string;
  especialidad: string;
  sedeId: number;
  sede: string;
  direccionSede: string;
  prestacion: string;
  folioBono: number | null;
  montoPagado: number | null;
  medioPago: string | null;
}

export interface Sesion {
  token: string;
  expiraUtc: string;
  rol: string;
  nombre: string;
  pacienteId: number | null;
}

export interface LineaValorizada {
  codigo: string;
  descripcion: string;
  cantidad: number;
  valor: number;
  bonificacion: number;
  otraBonificacion: number;
  copago: number;
}

export interface Valorizacion {
  esParticular: boolean;
  aviso: string | null;
  financiador: string;
  planCodigo: string | null;
  lineas: LineaValorizada[];
  totalValor: number;
  totalBonificacion: number;
  totalOtraBonificacion: number;
  totalCopago: number;
}
