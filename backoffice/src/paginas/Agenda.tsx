import { useCallback, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, clp, Estado, hora, hoyChile, leerSesion } from '../api';

interface CitaAgenda {
  id: number;
  fechaHoraInicio: string;
  estado: string;
  canal: string;
  pacienteRut: string;
  pacienteNombre: string;
  prevision: string | null;
  profesional: string;
  especialidad: string;
  sede: string;
  folioBono: number | null;
  montoPagado: number | null;
  riesgoProbabilidad: number | null;
  riesgoNivel: 'BAJO' | 'MEDIO' | 'ALTO' | null;
  riesgoFactores: string | null;
}

const TONO_RIESGO = { ALTO: 'error', MEDIO: 'alerta', BAJO: 'neutro' } as const;

/** Riesgo de inasistencia calculado por el ml-service. Texto + color, nunca sólo color. */
function Riesgo({ c }: { c: CitaAgenda }) {
  if (!c.riesgoNivel || c.riesgoProbabilidad === null) return <span className="sutil">—</span>;
  let factores: string[] = [];
  try { factores = JSON.parse(c.riesgoFactores ?? '[]'); } catch { /* sin factores */ }
  const texto = c.riesgoNivel.charAt(0) + c.riesgoNivel.slice(1).toLowerCase();
  return (
    <span className={`badge riesgo ${TONO_RIESGO[c.riesgoNivel]}`}
          title={factores.length ? `Factores: ${factores.join(' · ')}` : 'Probabilidad estimada de inasistencia'}>
      {texto} {Math.round(c.riesgoProbabilidad * 100)}%
    </span>
  );
}

interface Sede { id: number; nombre: string }

export default function Agenda() {
  const sesion = leerSesion()!;
  const [fecha, setFecha] = useState(hoyChile());
  const [sedes, setSedes] = useState<Sede[]>([]);
  const [sedeId, setSedeId] = useState(sesion.sedeId ? String(sesion.sedeId) : '');
  const [citas, setCitas] = useState<CitaAgenda[] | null>(null);
  const [error, setError] = useState('');
  const navegar = useNavigate();
  const esMedico = sesion.rol === 'MEDICO';

  useEffect(() => {
    api<Sede[]>('/api/catalogo/sedes').then(setSedes).catch(() => {});
  }, []);

  const cargar = useCallback(() => {
    const qs = new URLSearchParams({ fecha });
    if (sedeId) qs.set('sedeId', sedeId);
    api<CitaAgenda[]>(`/api/agenda/dia?${qs}`).then(setCitas).catch((e) => setError(e.message));
  }, [fecha, sedeId]);

  useEffect(() => {
    cargar();
    const t = setInterval(cargar, 30_000); // la agenda de recepción se refresca sola
    return () => clearInterval(t);
  }, [cargar]);

  const cerrar = async (id: number, accion: 'atendida' | 'no-asistio') => {
    try {
      await api(`/api/citas/${id}/${accion}`, { cuerpo: {} });
      cargar();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const activas = citas?.filter((c) => c.estado !== 'CANCELADA') ?? [];
  const resumen = {
    altoRiesgo: activas.filter((c) => c.riesgoNivel === 'ALTO' && ['RESERVADA', 'CONFIRMADA'].includes(c.estado)).length,
    total: activas.length,
    admitidas: activas.filter((c) => ['ADMITIDA', 'ATENDIDA'].includes(c.estado)).length,
    recaudado: activas.reduce((s, c) => s + (c.montoPagado ?? 0), 0),
  };

  return (
    <section>
      <h1>{esMedico ? 'Mi agenda' : 'Agenda del día'}</h1>
      <div className="barra">
        <label>Fecha<input type="date" value={fecha} onChange={(e) => setFecha(e.target.value)} /></label>
        {/* A3: recepción trabaja solo en su sede (la API también lo exige); solo admin elige. */}
        {sesion.rol === 'ADMIN' && (
          <label>Sede
            <select value={sedeId} onChange={(e) => setSedeId(e.target.value)}>
              <option value="">Todas</option>
              {sedes.map((s) => <option key={s.id} value={s.id}>{s.nombre}</option>)}
            </select>
          </label>
        )}
        {sesion.rol === 'RECEPCION' && (
          <span className="sutil">Sede {sedes.find((s) => s.id === sesion.sedeId)?.nombre ?? ''}</span>
        )}
        <span className="sutil">
          {resumen.total} citas · {resumen.admitidas} admitidas{!esMedico && ` · recaudado ${clp(resumen.recaudado)}`}
          {resumen.altoRiesgo > 0 && ` · ${resumen.altoRiesgo} con riesgo alto de inasistencia`}
        </span>
      </div>
      {error && <p className="error">{error}</p>}

      <table className="tabla">
        <thead>
          <tr>
            <th>Hora</th><th>Paciente</th><th>RUT</th><th>Previsión</th>
            {!esMedico && <th>Profesional</th>}
            <th>Estado</th><th title="Riesgo de inasistencia (modelo de ML)">Riesgo</th><th>Bono</th><th></th>
          </tr>
        </thead>
        <tbody>
          {citas?.length === 0 && <tr><td colSpan={9} className="vacio">Sin citas para este día.</td></tr>}
          {citas?.map((c) => (
            <tr key={c.id} className={!esMedico ? 'clic' : ''} onClick={() => !esMedico && navegar(`/admision/${c.id}`)}>
              <td><strong>{hora(c.fechaHoraInicio)}</strong></td>
              <td>{c.pacienteNombre}</td>
              <td>{c.pacienteRut}</td>
              <td>{c.prevision ?? 'Sin previsión'}</td>
              {!esMedico && <td>{c.profesional}<br /><span className="sutil">{c.especialidad} · {c.canal}</span></td>}
              <td><Estado estado={c.estado} /></td>
              <td>{['RESERVADA', 'CONFIRMADA', 'ADMITIDA', 'ATENDIDA', 'NO_ASISTIO'].includes(c.estado) ? <Riesgo c={c} /> : '—'}</td>
              <td>{c.folioBono ?? '—'}</td>
              <td onClick={(e) => e.stopPropagation()}>
                {esMedico && c.estado === 'ADMITIDA' && <button onClick={() => cerrar(c.id, 'atendida')}>Atendida</button>}
                {esMedico && ['RESERVADA', 'CONFIRMADA'].includes(c.estado) && (
                  <button className="sec" onClick={() => cerrar(c.id, 'no-asistio')}>No asistió</button>
                )}
                {!esMedico && ['RESERVADA', 'CONFIRMADA'].includes(c.estado) && (
                  <button onClick={() => navegar(`/admision/${c.id}`)}>Admitir</button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
