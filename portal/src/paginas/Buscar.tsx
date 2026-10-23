import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, clp, fechaLarga, hoyChile, sumarDias } from '../api';
import { useSesion } from '../sesion';

export interface Catalogo {
  especialidades: { id: number; nombre: string }[];
  sedes: { id: number; nombre: string; direccion: string; comuna: string }[];
  financiadores: { id: number; codigo: string; nombre: string; tipo: string; planes: { id: number; codigo: string; nombre: string }[] }[];
}

export interface HorasProfesional {
  profesionalId: number;
  profesional: string;
  especialidad: string;
  sedeId: number;
  sede: string;
  valorParticular: number;
  horas: { inicio: string; hora: string }[];
}

interface Dia {
  fecha: string;
  profesionales: HorasProfesional[];
}

export interface CupoElegido {
  profesionalId: number;
  profesional: string;
  especialidad: string;
  sedeId: number;
  sede: string;
  valorParticular: number;
  inicio: string;
}

export default function Buscar() {
  const [catalogo, setCatalogo] = useState<Catalogo | null>(null);
  const [especialidadId, setEspecialidadId] = useState('');
  const [sedeId, setSedeId] = useState('');
  const [desde, setDesde] = useState(hoyChile());
  const [dias, setDias] = useState<Dia[] | null>(null);
  const [cargando, setCargando] = useState(false);
  const [error, setError] = useState('');
  const { sesion } = useSesion();
  const navegar = useNavigate();

  useEffect(() => {
    api<Catalogo>('/catalogo').then(setCatalogo).catch((e) => setError(e.message));
  }, []);

  useEffect(() => {
    if (!especialidadId) return;
    setCargando(true);
    setError('');
    const qs = new URLSearchParams({ especialidadId, desde, hasta: sumarDias(desde, 6) });
    if (sedeId) qs.set('sedeId', sedeId);
    api<Dia[]>(`/agenda/disponibilidad?${qs}`)
      .then(setDias)
      .catch((e) => setError(e.message))
      .finally(() => setCargando(false));
  }, [especialidadId, sedeId, desde]);

  const elegir = (p: HorasProfesional, inicio: string) => {
    const cupo: CupoElegido = { ...p, inicio };
    if (!sesion) {
      sessionStorage.setItem('rm_cupo', JSON.stringify(cupo));
      navegar('/login?volver=/reservar');
      return;
    }
    navegar('/reservar', { state: cupo });
  };

  const hoy = hoyChile();

  return (
    <section>
      <div className="hero">
        <h1>Reserve su hora médica</h1>
        <p>Elija especialidad, sede y horario. Puede pagar su bono online o en la clínica.</p>
      </div>

      <div className="filtros tarjeta">
        <label>
          Especialidad
          <select value={especialidadId} onChange={(e) => setEspecialidadId(e.target.value)}>
            <option value="">Seleccione…</option>
            {catalogo?.especialidades.map((e) => (
              <option key={e.id} value={e.id}>{e.nombre}</option>
            ))}
          </select>
        </label>
        <label>
          Sede
          <select value={sedeId} onChange={(e) => setSedeId(e.target.value)}>
            <option value="">Todas las sedes</option>
            {catalogo?.sedes.map((s) => (
              <option key={s.id} value={s.id}>{s.nombre}</option>
            ))}
          </select>
        </label>
        <div className="semana">
          <button className="sec" disabled={desde <= hoy} onClick={() => setDesde(sumarDias(desde, -7) < hoy ? hoy : sumarDias(desde, -7))}>
            ← Anterior
          </button>
          <span>{fechaLarga(desde)} – {fechaLarga(sumarDias(desde, 6))}</span>
          <button className="sec" onClick={() => setDesde(sumarDias(desde, 7))}>Siguiente →</button>
        </div>
      </div>

      {error && <p className="error">{error}</p>}
      {!especialidadId && <p className="vacio">Seleccione una especialidad para ver las horas disponibles.</p>}
      {cargando && <p className="vacio">Buscando horas…</p>}
      {!cargando && dias?.length === 0 && especialidadId && (
        <p className="vacio">No hay horas disponibles esta semana. Pruebe la semana siguiente u otra sede.</p>
      )}

      {!cargando && dias?.map((d) => (
        <div key={d.fecha} className="dia">
          <h2>{fechaLarga(d.fecha)}</h2>
          {d.profesionales.map((p) => (
            <div key={`${p.profesionalId}-${p.sedeId}`} className="tarjeta profesional">
              <div className="prof-cabecera">
                <div>
                  <strong>{p.profesional}</strong>
                  <span className="sutil">{p.especialidad} · {p.sede}</span>
                </div>
                <span className="sutil">Particular {clp(p.valorParticular)}</span>
              </div>
              <div className="horas">
                {p.horas.map((h) => (
                  <button key={h.inicio} className="hora" onClick={() => elegir(p, h.inicio)}>{h.hora}</button>
                ))}
              </div>
            </div>
          ))}
        </div>
      ))}
    </section>
  );
}
