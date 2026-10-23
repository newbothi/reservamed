import { useEffect, useState } from 'react';
import { api } from '../api';

interface Registro {
  id: number;
  fecha: string;
  email: string | null;
  rol: string | null;
  accion: string;
  recurso: string;
  recursoId: string | null;
  ip: string | null;
  detalle: string | null;
}

const ACCIONES = ['', 'LOGIN_FALLIDO', 'VER_PACIENTE', 'VER_CITA', 'VER_AGENDA', 'RESERVAR_CITA', 'CANCELAR_CITA',
  'EMITIR_BONO', 'ANULAR_BONO', 'COBRAR', 'ADMITIR', 'PAGO_WEBPAY', 'ACTUALIZAR_PREVISION', 'FHIR_PATIENT', 'FHIR_APPOINTMENT', 'ENTRENAR_MODELO'];

/** Quién vio o modificó qué dato: requisito de confidencialidad de la información de salud. */
export default function Auditoria() {
  const [accion, setAccion] = useState('');
  const [email, setEmail] = useState('');
  const [registros, setRegistros] = useState<Registro[]>([]);
  const [error, setError] = useState('');

  useEffect(() => {
    const qs = new URLSearchParams();
    if (accion) qs.set('accion', accion);
    if (email) qs.set('email', email);
    api<Registro[]>(`/api/auditoria?${qs}`).then(setRegistros).catch((e) => setError(e.message));
  }, [accion, email]);

  return (
    <section>
      <h1>Auditoría de accesos</h1>
      <div className="barra">
        <label>Acción
          <select value={accion} onChange={(e) => setAccion(e.target.value)}>
            {ACCIONES.map((a) => <option key={a} value={a}>{a || 'Todas'}</option>)}
          </select>
        </label>
        <label>Usuario<input value={email} onChange={(e) => setEmail(e.target.value)} placeholder="correo contiene…" /></label>
      </div>
      {error && <p className="error">{error}</p>}
      <table className="tabla">
        <thead><tr><th>Fecha</th><th>Usuario</th><th>Rol</th><th>Acción</th><th>Recurso</th><th>IP</th><th>Detalle</th></tr></thead>
        <tbody>
          {registros.map((r) => (
            <tr key={r.id}>
              <td>{r.fecha.replace('T', ' ')}</td>
              <td>{r.email ?? '—'}</td>
              <td>{r.rol ?? '—'}</td>
              <td><code>{r.accion}</code></td>
              <td>{r.recurso} {r.recursoId}</td>
              <td>{r.ip}</td>
              <td className="sutil">{r.detalle}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
