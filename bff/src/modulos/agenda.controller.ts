import { BadRequestException, Controller, Get, Query } from '@nestjs/common';
import { CoreClient } from '../comun/core-client.service';
import { Cupo } from '../comun/tipos';
import { soloFecha } from '../comun/util';

interface HorasProfesional {
  profesionalId: number;
  profesional: string;
  especialidad: string;
  sedeId: number;
  sede: string;
  valorParticular: number;
  horas: { inicio: string; hora: string }[];
}

interface DiaDisponible {
  fecha: string;
  profesionales: HorasProfesional[];
}

/**
 * La API Core devuelve una lista plana de cupos. El portal necesita verlos
 * agrupados por día y por profesional: esa adaptación es el trabajo típico de un BFF.
 */
@Controller('agenda')
export class AgendaController {
  constructor(private readonly core: CoreClient) {}

  @Get('disponibilidad')
  async disponibilidad(
    @Query('especialidadId') especialidadId?: string,
    @Query('sedeId') sedeId?: string,
    @Query('desde') desde?: string,
    @Query('hasta') hasta?: string,
  ): Promise<DiaDisponible[]> {
    if (!especialidadId || !desde || !hasta) throw new BadRequestException({ status: 400, title: 'Faltan filtros' });

    const qs = new URLSearchParams({ especialidadId, desde, hasta });
    if (sedeId) qs.set('sedeId', sedeId);
    const cupos = await this.core.get<Cupo[]>(`/api/agenda/disponibilidad?${qs}`);

    const dias = new Map<string, Map<string, HorasProfesional>>();
    for (const c of cupos) {
      const fecha = soloFecha(c.inicio);
      const clave = `${c.profesionalId}-${c.sedeId}`;
      if (!dias.has(fecha)) dias.set(fecha, new Map());
      const porProf = dias.get(fecha)!;
      if (!porProf.has(clave)) {
        porProf.set(clave, {
          profesionalId: c.profesionalId,
          profesional: c.profesional,
          especialidad: c.especialidad,
          sedeId: c.sedeId,
          sede: c.sede,
          valorParticular: c.valorParticular,
          horas: [],
        });
      }
      porProf.get(clave)!.horas.push({ inicio: c.inicio, hora: c.inicio.slice(11, 16) });
    }

    return [...dias.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([fecha, profs]) => ({ fecha, profesionales: [...profs.values()] }));
  }
}
