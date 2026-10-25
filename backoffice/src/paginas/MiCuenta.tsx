import { FormEvent, useCallback, useEffect, useState } from 'react';
import { api, ErrorApi } from '../api';
import { CampoClave } from '../CampoClave';

interface SesionActiva { id: string; cliente: string; creadaEn: string; ultimoUso: string; ip: string | null; actual: boolean }

const local = (utc: string) =>
  new Date(utc.endsWith('Z') ? utc : `${utc}Z`).toLocaleString('es-CL', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

/** Personal: cambiar su contraseña y ver/cerrar sus sesiones (V2.1.6, V3.3.4). */
export default function MiCuenta() {
  const [f, setF] = useState({ actual: '', nueva: '', repetir: '' });
  const [mensaje, setMensaje] = useState<{ tipo: 'error' | 'exito'; texto: string } | null>(null);
  const [sesiones, setSesiones] = useState<SesionActiva[]>([]);

  const cargar = useCallback(() => {
    api<SesionActiva[]>('/api/cuenta/sesiones').then(setSesiones).catch((e) => setMensaje({ tipo: 'error', texto: e.message }));
  }, []);
  useEffect(cargar, [cargar]);

  const cambiar = async (e: FormEvent) => {
    e.preventDefault();
    if (f.nueva !== f.repetir) return setMensaje({ tipo: 'error', texto: 'Las contraseñas nuevas no coinciden.' });
    try {
      const r = await api<{ sesionesCerradas: number }>('/api/cuenta/clave', { cuerpo: { actual: f.actual, nueva: f.nueva } });
      setF({ actual: '', nueva: '', repetir: '' });
      setMensaje({ tipo: 'exito', texto: `Contraseña cambiada. Sesiones cerradas en otros equipos: ${r.sesionesCerradas}.` });
      cargar();
    } catch (err) {
      setMensaje({ tipo: 'error', texto: (err as ErrorApi).message });
    }
  };

  const cerrar = async (id: string) => {
    await api(`/api/cuenta/sesiones/${id}/cerrar`, { cuerpo: {} }).catch((e) => setMensaje({ tipo: 'error', texto: e.message }));
    cargar();
  };

  return (
    <section>
      <h1>Mi cuenta</h1>
      <form className="tarjeta formulario" onSubmit={cambiar}>
        <h2>Cambiar contraseña</h2>
        <label>Contraseña actual<CampoClave value={f.actual} onChange={(e) => setF({ ...f, actual: e.target.value })} autoComplete="current-password" required /></label>
        <label>Contraseña nueva<CampoClave value={f.nueva} onChange={(e) => setF({ ...f, nueva: e.target.value })} minLength={12} maxLength={128} autoComplete="new-password" required /></label>
        <label>Repita la contraseña nueva<CampoClave value={f.repetir} onChange={(e) => setF({ ...f, repetir: e.target.value })} autoComplete="new-password" required /></label>
        {mensaje && <p className={mensaje.tipo} role="status">{mensaje.texto}</p>}
        <button type="submit">Cambiar contraseña</button>
      </form>
      <div className="tarjeta">
        <h2>Sesiones abiertas</h2>
        <table className="tabla">
          <thead><tr><th>Aplicación</th><th>Inicio</th><th>Último uso</th><th>IP</th><th></th></tr></thead>
          <tbody>
            {sesiones.map((s) => (
              <tr key={s.id}>
                <td>{s.cliente === 'BACKOFFICE' ? 'Backoffice' : 'Portal'}</td>
                <td>{local(s.creadaEn)}</td>
                <td>{local(s.ultimoUso)}</td>
                <td>{s.ip ?? '—'}</td>
                <td>{s.actual ? <span className="badge ok">Esta sesión</span> : <button className="secundario" onClick={() => cerrar(s.id)}>Cerrar</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
