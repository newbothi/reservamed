import { FormEvent, useCallback, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, ErrorApi, fortaleza } from '../api';
import { CampoClave } from '../CampoClave';
import { useSesion } from '../sesion';

interface SesionActiva {
  id: string;
  cliente: string;
  creadaEn: string;
  ultimoUso: string;
  ip: string | null;
  actual: boolean;
}

/** Las horas de las sesiones vienen en UTC: se muestran en la hora local del navegador. */
const local = (utc: string) =>
  new Date(utc.endsWith('Z') ? utc : `${utc}Z`).toLocaleString('es-CL', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

function CambiarClave() {
  const { sesion } = useSesion();
  const [f, setF] = useState({ actual: '', nueva: '', repetir: '' });
  const [error, setError] = useState('');
  const [ok, setOk] = useState('');
  const fuerza = fortaleza(f.nueva, sesion?.email);

  const enviar = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setOk('');
    if (f.nueva !== f.repetir) return setError('Las contraseñas nuevas no coinciden.');
    try {
      const r = await api<{ sesionesCerradas: number }>('/cuenta/clave', { cuerpo: { actual: f.actual, nueva: f.nueva } });
      setF({ actual: '', nueva: '', repetir: '' });
      setOk(r.sesionesCerradas > 0
        ? `Contraseña cambiada. Cerramos ${r.sesionesCerradas} sesión(es) abiertas en otros equipos.`
        : 'Contraseña cambiada.');
    } catch (err) {
      setError((err as ErrorApi).message);
    }
  };

  return (
    <form className="tarjeta formulario" onSubmit={enviar}>
      <h2>Cambiar contraseña</h2>
      <label>Contraseña actual<CampoClave value={f.actual} onChange={(e) => setF({ ...f, actual: e.target.value })} autoComplete="current-password" required /></label>
      <label>Contraseña nueva
        <CampoClave value={f.nueva} onChange={(e) => setF({ ...f, nueva: e.target.value })} minLength={12} maxLength={128} autoComplete="new-password" required />
        {f.nueva && <span className={`medidor n${fuerza.nivel}`}>{fuerza.texto}</span>}
      </label>
      <label>Repita la contraseña nueva<CampoClave value={f.repetir} onChange={(e) => setF({ ...f, repetir: e.target.value })} autoComplete="new-password" required /></label>
      {error && <p className="error" role="alert">{error}</p>}
      {ok && <p className="exito" role="status">{ok}</p>}
      <button type="submit">Cambiar contraseña</button>
      <p className="sutil">Le avisaremos por correo y se cerrarán sus sesiones en otros equipos.</p>
    </form>
  );
}

function Sesiones() {
  const [lista, setLista] = useState<SesionActiva[] | null>(null);
  const [error, setError] = useState('');
  const cargar = useCallback(() => {
    api<SesionActiva[]>('/cuenta/sesiones').then(setLista).catch((e: Error) => setError(e.message));
  }, []);
  useEffect(cargar, [cargar]);

  const cerrar = async (id: string) => {
    try {
      await api(`/cuenta/sesiones/${id}/cerrar`, { cuerpo: {} });
      cargar();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <section className="tarjeta">
      <h2>Sesiones abiertas</h2>
      {error && <p className="error">{error}</p>}
      {!lista ? <p className="sutil">Cargando…</p> : (
        <table className="tabla">
          <thead><tr><th>Inicio</th><th>Último uso</th><th>IP</th><th></th></tr></thead>
          <tbody>
            {lista.map((s) => (
              <tr key={s.id}>
                <td>{local(s.creadaEn)}</td>
                <td>{local(s.ultimoUso)}</td>
                <td>{s.ip ?? '—'}</td>
                <td>{s.actual ? <span className="badge ok">Este equipo</span> : <button className="secundario" onClick={() => cerrar(s.id)}>Cerrar</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p className="sutil">Si ve una sesión que no reconoce, ciérrela y cambie su contraseña.</p>
    </section>
  );
}

function MisDatos() {
  const [error, setError] = useState('');
  const descargar = async () => {
    setError('');
    try {
      const datos = await api<unknown>('/cuenta/datos');
      const url = URL.createObjectURL(new Blob([JSON.stringify(datos, null, 2)], { type: 'application/json' }));
      const a = Object.assign(document.createElement('a'), { href: url, download: `mis-datos-reservamed-${new Date().toISOString().slice(0, 10)}.json` });
      a.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      setError((e as Error).message);
    }
  };
  return (
    <section className="tarjeta">
      <h2>Mis datos</h2>
      <p>Descargue todos sus datos personales, sus horas, bonos y pagos, y la lista de quién accedió a su ficha (por rol y fecha).</p>
      {error && <p className="error">{error}</p>}
      <button onClick={descargar}>Descargar mis datos (JSON)</button>
    </section>
  );
}

function CerrarCuenta() {
  const [f, setF] = useState({ clave: '', confirmacion: '' });
  const [error, setError] = useState('');
  const { entrar } = useSesion();
  const navegar = useNavigate();

  const enviar = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    try {
      await api('/cuenta/eliminacion', { cuerpo: f });
      entrar(null);
      navegar('/', { replace: true });
    } catch (err) {
      setError((err as ErrorApi).message);
    }
  };

  return (
    <form className="tarjeta formulario peligro" onSubmit={enviar}>
      <h2>Cerrar mi cuenta y eliminar mis datos</h2>
      <p>Se cierra su cuenta de inmediato y se eliminan sus datos de contacto. La ficha clínica se conserva por el plazo legal y luego se elimina. Antes debe anular sus horas futuras.</p>
      <label>Contraseña<CampoClave value={f.clave} onChange={(e) => setF({ ...f, clave: e.target.value })} autoComplete="current-password" required /></label>
      <label>Escriba ELIMINAR para confirmar<input value={f.confirmacion} onChange={(e) => setF({ ...f, confirmacion: e.target.value })} required /></label>
      {error && <p className="error" role="alert">{error}</p>}
      <button type="submit" className="rojo" disabled={f.confirmacion !== 'ELIMINAR'}>Cerrar mi cuenta</button>
    </form>
  );
}

export default function MiCuenta() {
  return (
    <>
      <h1>Mi cuenta</h1>
      <CambiarClave />
      <Sesiones />
      <MisDatos />
      <CerrarCuenta />
    </>
  );
}
