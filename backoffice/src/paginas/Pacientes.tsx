import { FormEvent, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, ErrorApi, Estado, fecha, formatearRut, hora, hoyChile, leerSesion } from '../api';

interface Prevision {
  financiador: string;
  tipo: string;
  planCodigo: string | null;
  planNombre: string | null;
  tramoFonasa: string | null;
  esCarga: boolean;
  afiliadoRut: string | null;
  afiliadoNombre: string | null;
}

interface Paciente {
  id: number;
  rut: string;
  nombreCompleto: string;
  fechaNacimiento: string;
  edad: number;
  sexo: string;
  email: string;
  telefono: string | null;
  prevision: Prevision | null;
  proximasCitas: { id: number; fechaHoraInicio: string; estado: string; profesional: string; especialidad: string; sede: string; folioBono: number | null }[];
}

interface Financiador { id: number; nombre: string; tipo: string; planes: { id: number; nombre: string }[] }
interface Cupo { profesionalId: number; profesional: string; sedeId: number; sede: string; inicio: string }

export default function Pacientes() {
  const [rut, setRut] = useState('');
  const [paciente, setPaciente] = useState<Paciente | null>(null);
  const [noExiste, setNoExiste] = useState(false);
  const [error, setError] = useState('');
  const [financiadores, setFinanciadores] = useState<Financiador[]>([]);

  useEffect(() => {
    api<Financiador[]>('/api/catalogo/financiadores').then(setFinanciadores).catch(() => {});
  }, []);

  const buscar = async (e?: FormEvent, rutBuscado = rut) => {
    e?.preventDefault();
    setError('');
    setNoExiste(false);
    setPaciente(null);
    try {
      // A9: el RUT viaja en el cuerpo, nunca en la URL (no queda en logs ni en el historial).
      setPaciente(await api<Paciente>('/api/admision/pacientes/buscar', { cuerpo: { rut: rutBuscado.replace(/\./g, '') } }));
    } catch (err) {
      if ((err as ErrorApi).status === 404) setNoExiste(true);
      else setError((err as Error).message);
    }
  };

  return (
    <section>
      <h1>Pacientes</h1>
      <form className="barra" onSubmit={buscar}>
        <label>RUT del paciente<input value={rut} onChange={(e) => setRut(formatearRut(e.target.value))} placeholder="12.345.678-9" autoFocus /></label>
        <button type="submit">Buscar</button>
      </form>
      {error && <p className="error">{error}</p>}

      {noExiste && <NuevoPaciente rut={rut} alCrear={() => buscar(undefined, rut)} />}

      {paciente && (
        <>
          <div className="tarjeta">
            <h2 style={{ marginTop: 0, textTransform: 'none' }}>{paciente.nombreCompleto}</h2>
            <div className="datos-paciente">
              <div><span>RUT</span>{paciente.rut}</div>
              <div><span>Nacimiento</span>{fecha(paciente.fechaNacimiento)} ({paciente.edad} años)</div>
              <div><span>Sexo</span>{paciente.sexo}</div>
              <div><span>Contacto</span>{paciente.email}<br />{paciente.telefono}</div>
              <div>
                <span>Previsión</span>
                {paciente.prevision
                  ? `${paciente.prevision.financiador}${paciente.prevision.tramoFonasa ? ` tramo ${paciente.prevision.tramoFonasa}` : ''}${paciente.prevision.planNombre && !paciente.prevision.tramoFonasa ? ` · ${paciente.prevision.planNombre}` : ''}`
                  : 'Sin previsión registrada'}
              </div>
              {paciente.prevision?.esCarga && (
                <div><span>Titular del plan</span>{paciente.prevision.afiliadoRut} {paciente.prevision.afiliadoNombre}</div>
              )}
            </div>
          </div>

          <EditarPrevision pacienteId={paciente.id} financiadores={financiadores} alGuardar={() => buscar()} />
          <ReservaPresencial pacienteId={paciente.id} alReservar={() => buscar()} />

          <h2>Próximas citas</h2>
          <table className="tabla">
            <thead><tr><th>Fecha</th><th>Especialidad</th><th>Profesional</th><th>Sede</th><th>Estado</th><th></th></tr></thead>
            <tbody>
              {paciente.proximasCitas.length === 0 && <tr><td colSpan={6} className="vacio">Sin citas próximas.</td></tr>}
              {paciente.proximasCitas.map((c) => (
                <tr key={c.id}>
                  <td>{fecha(c.fechaHoraInicio)} {hora(c.fechaHoraInicio)}</td>
                  <td>{c.especialidad}</td><td>{c.profesional}</td><td>{c.sede}</td>
                  <td><Estado estado={c.estado} /></td>
                  <td><Link to={`/admision/${c.id}`}>Abrir</Link></td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  );
}

function NuevoPaciente({ rut, alCrear }: { rut: string; alCrear: () => void }) {
  const [f, setF] = useState({ nombres: '', apellidoPaterno: '', apellidoMaterno: '', fechaNacimiento: '', sexo: 'F', email: '', telefono: '', comuna: '' });
  const [error, setError] = useState('');
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });

  const crear = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await api('/api/admision/pacientes', { cuerpo: { ...f, rut } });
      alCrear();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <form className="tarjeta formulario" onSubmit={crear}>
      <p className="aviso">No existe un paciente con RUT {rut}. Regístrelo:</p>
      <div className="grilla">
        <label>Nombres<input value={f.nombres} onChange={set('nombres')} required /></label>
        <label>Apellido paterno<input value={f.apellidoPaterno} onChange={set('apellidoPaterno')} required /></label>
        <label>Apellido materno<input value={f.apellidoMaterno} onChange={set('apellidoMaterno')} /></label>
        <label>Fecha de nacimiento<input type="date" value={f.fechaNacimiento} onChange={set('fechaNacimiento')} required /></label>
        <label>Sexo<select value={f.sexo} onChange={set('sexo')}><option value="F">F</option><option value="M">M</option><option value="X">X</option></select></label>
        <label>Correo<input type="email" value={f.email} onChange={set('email')} required /></label>
        <label>Teléfono<input value={f.telefono} onChange={set('telefono')} /></label>
        <label>Comuna<input value={f.comuna} onChange={set('comuna')} /></label>
      </div>
      {error && <p className="error">{error}</p>}
      <button type="submit">Registrar paciente</button>
    </form>
  );
}

function EditarPrevision({ pacienteId, financiadores, alGuardar }: { pacienteId: number; financiadores: Financiador[]; alGuardar: () => void }) {
  const [abierto, setAbierto] = useState(false);
  const [f, setF] = useState({ financiadorId: '', planSaludId: '', tramoFonasa: '', esCarga: false, afiliadoRut: '', afiliadoNombre: '' });
  const [error, setError] = useState('');
  const fin = financiadores.find((x) => String(x.id) === f.financiadorId);

  if (!abierto) return <button className="sec" style={{ marginBottom: '.9rem' }} onClick={() => setAbierto(true)}>Actualizar previsión</button>;

  const guardar = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await api(`/api/admision/pacientes/${pacienteId}/prevision`, {
        metodo: 'PUT',
        cuerpo: {
          financiadorId: Number(f.financiadorId),
          planSaludId: f.planSaludId ? Number(f.planSaludId) : null,
          tramoFonasa: f.tramoFonasa || null,
          esCarga: f.esCarga,
          afiliadoRut: f.esCarga ? f.afiliadoRut : null,
          afiliadoNombre: f.esCarga ? f.afiliadoNombre : null,
        },
      });
      setAbierto(false);
      alGuardar();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <form className="tarjeta formulario" onSubmit={guardar}>
      <div className="grilla">
        <label>Previsión
          <select value={f.financiadorId} onChange={(e) => setF({ ...f, financiadorId: e.target.value, planSaludId: '', tramoFonasa: '' })} required>
            <option value="">Seleccione…</option>
            {financiadores.map((x) => <option key={x.id} value={x.id}>{x.nombre}</option>)}
          </select>
        </label>
        {fin?.tipo === 'ISAPRE' && (
          <label>Plan<select value={f.planSaludId} onChange={(e) => setF({ ...f, planSaludId: e.target.value })} required>
            <option value="">Seleccione…</option>
            {fin.planes.map((p) => <option key={p.id} value={p.id}>{p.nombre}</option>)}
          </select></label>
        )}
        {fin?.tipo === 'FONASA' && (
          <label>Tramo<select value={f.tramoFonasa} onChange={(e) => setF({ ...f, tramoFonasa: e.target.value })} required>
            <option value="">Seleccione…</option>
            {['A', 'B', 'C', 'D'].map((t) => <option key={t}>{t}</option>)}
          </select></label>
        )}
      </div>
      {fin && fin.tipo !== 'PARTICULAR' && (
        <label className="check"><input type="checkbox" checked={f.esCarga} onChange={(e) => setF({ ...f, esCarga: e.target.checked })} />Es carga de otro titular</label>
      )}
      {f.esCarga && (
        <div className="grilla">
          <label>RUT titular<input value={f.afiliadoRut} onChange={(e) => setF({ ...f, afiliadoRut: formatearRut(e.target.value) })} required /></label>
          <label>Nombre titular<input value={f.afiliadoNombre} onChange={(e) => setF({ ...f, afiliadoNombre: e.target.value })} required /></label>
        </div>
      )}
      {error && <p className="error">{error}</p>}
      <div className="fila"><button type="submit">Guardar previsión</button><button type="button" className="sec" onClick={() => setAbierto(false)}>Cancelar</button></div>
    </form>
  );
}

/** Reserva hecha por recepción (paciente presencial o por teléfono). Siempre se paga en caja. */
function ReservaPresencial({ pacienteId, alReservar }: { pacienteId: number; alReservar: () => void }) {
  const sesion = leerSesion()!;
  const [abierto, setAbierto] = useState(false);
  const [especialidades, setEspecialidades] = useState<{ id: number; nombre: string }[]>([]);
  const [especialidadId, setEspecialidadId] = useState('');
  const [dia, setDia] = useState(hoyChile());
  const [canal, setCanal] = useState('PRESENCIAL');
  const [cupos, setCupos] = useState<Cupo[] | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    if (abierto) api<{ id: number; nombre: string }[]>('/api/catalogo/especialidades').then(setEspecialidades).catch(() => {});
  }, [abierto]);

  useEffect(() => {
    if (!especialidadId) return;
    const qs = new URLSearchParams({ especialidadId, desde: dia, hasta: dia });
    if (sesion.sedeId) qs.set('sedeId', String(sesion.sedeId));
    api<Cupo[]>(`/api/agenda/disponibilidad?${qs}`).then(setCupos).catch((e) => setError(e.message));
  }, [especialidadId, dia, sesion.sedeId]);

  if (!abierto) return <button className="sec" style={{ marginLeft: '.6rem' }} onClick={() => setAbierto(true)}>Reservar hora</button>;

  const reservar = async (c: Cupo) => {
    try {
      await api('/api/citas', {
        cuerpo: { profesionalId: c.profesionalId, sedeId: c.sedeId, inicio: c.inicio, modalidadPago: 'EN_CLINICA', pacienteId, canal },
      });
      setAbierto(false);
      alReservar();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <div className="tarjeta">
      <div className="barra">
        <label>Especialidad
          <select value={especialidadId} onChange={(e) => setEspecialidadId(e.target.value)}>
            <option value="">Seleccione…</option>
            {especialidades.map((e) => <option key={e.id} value={e.id}>{e.nombre}</option>)}
          </select>
        </label>
        <label>Día<input type="date" value={dia} onChange={(e) => setDia(e.target.value)} /></label>
        <label>Canal
          <select value={canal} onChange={(e) => setCanal(e.target.value)}>
            <option value="PRESENCIAL">Presencial</option><option value="TELEFONO">Teléfono</option>
          </select>
        </label>
        <button className="sec" onClick={() => setAbierto(false)}>Cerrar</button>
      </div>
      {error && <p className="error">{error}</p>}
      {cupos?.length === 0 && <p className="vacio">Sin horas ese día.</p>}
      {Object.entries((cupos ?? []).reduce<Record<string, Cupo[]>>((acc, c) => {
        (acc[`${c.profesional} · ${c.sede}`] ??= []).push(c);
        return acc;
      }, {})).map(([prof, lista]) => (
        <div key={prof} style={{ marginBottom: '.6rem' }}>
          <strong>{prof}</strong>
          <div className="chips">
            {lista.map((c) => <button key={c.inicio} className="hora" onClick={() => reservar(c)}>{hora(c.inicio)}</button>)}
          </div>
        </div>
      ))}
    </div>
  );
}
