import { useCallback, useEffect, useState } from 'react';
import { api } from '../api';
import { useReautenticacion } from '../Reautenticar';

interface Usuario {
  id: number;
  email: string;
  rol: string;
  nombreVisible: string;
  activo: boolean;
  emailVerificado: boolean;
  totpActivo: boolean;
  bloqueadoHasta: string | null;
  sesionesActivas: number;
}

/** Administración de cuentas. Cada acción pide confirmar el TOTP (V3.7.1). */
export default function Usuarios() {
  const [rol, setRol] = useState('');
  const [lista, setLista] = useState<Usuario[]>([]);
  const [error, setError] = useState('');
  const [aviso, setAviso] = useState('');
  const { ejecutar, dialogo } = useReautenticacion();

  const cargar = useCallback(() => {
    api<Usuario[]>(`/api/usuarios${rol ? `?rol=${rol}` : ''}`).then(setLista).catch((e) => setError(e.message));
  }, [rol]);
  useEffect(cargar, [cargar]);

  const accion = (u: Usuario, ruta: string, texto: string) =>
    ejecutar(async () => {
      await api(`/api/usuarios/${u.id}/${ruta}`, { cuerpo: {} });
      setAviso(`${texto}: ${u.email}`);
      cargar();
    }).catch((e) => setError(e.message));

  return (
    <section>
      <h1>Usuarios</h1>
      <div className="barra">
        <label>Rol
          <select value={rol} onChange={(e) => setRol(e.target.value)}>
            {['', 'PACIENTE', 'RECEPCION', 'MEDICO', 'ADMIN'].map((r) => <option key={r} value={r}>{r || 'Todos'}</option>)}
          </select>
        </label>
      </div>
      {error && <p className="error">{error}</p>}
      {aviso && <p className="exito" role="status">{aviso}</p>}
      <table className="tabla">
        <thead><tr><th>Usuario</th><th>Rol</th><th>Estado</th><th>Sesiones</th><th>Acciones</th></tr></thead>
        <tbody>
          {lista.map((u) => (
            <tr key={u.id}>
              <td>{u.nombreVisible}<br /><span className="sutil">{u.email}</span></td>
              <td>{u.rol}</td>
              <td>
                {u.activo ? <span className="badge ok">Activo</span> : <span className="badge neutro">Desactivado</span>}{' '}
                {u.bloqueadoHasta && <span className="badge alerta">Bloqueado</span>}
                {u.rol !== 'PACIENTE' && !u.totpActivo && <span className="badge alerta">Sin TOTP</span>}
              </td>
              <td>{u.sesionesActivas}</td>
              <td className="acciones-fila">
                {u.activo
                  ? <button className="secundario" onClick={() => accion(u, 'desactivar', 'Cuenta desactivada')}>Desactivar</button>
                  : <button className="secundario" onClick={() => accion(u, 'activar', 'Cuenta activada')}>Activar</button>}
                {u.rol !== 'PACIENTE' && u.totpActivo &&
                  <button className="secundario" onClick={() => accion(u, 'restablecer-totp', 'Autenticador restablecido')}>Restablecer TOTP</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {dialogo}
    </section>
  );
}
