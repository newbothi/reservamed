import { useCallback, useEffect, useState } from 'react';
import { api } from '../api';

interface Alerta {
  id: number;
  regla: string;
  severidad: 'MEDIA' | 'ALTA';
  clave: string;
  conteo: number;
  detalle: string;
  desdeEn: string;
  creadaEn: string;
  revisadaEn: string | null;
  revisadaPor: string | null;
}

const fecha = (s: string) => s.replace('T', ' ').slice(0, 16);

/** ASVS V11.1.7: alertas de actividad inusual detectadas sobre la auditoría. */
export default function Seguridad() {
  const [pendientes, setPendientes] = useState(true);
  const [alertas, setAlertas] = useState<Alerta[]>([]);
  const [error, setError] = useState('');

  const cargar = useCallback(() => {
    api<Alerta[]>(`/api/seguridad/alertas?pendientes=${pendientes}`).then(setAlertas).catch((e) => setError(e.message));
  }, [pendientes]);
  useEffect(cargar, [cargar]);

  const revisar = async (id: number) => {
    try {
      await api(`/api/seguridad/alertas/${id}/revisada`, { cuerpo: {} });
      cargar();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const altas = alertas.filter((a) => a.severidad === 'ALTA' && !a.revisadaEn).length;

  return (
    <section>
      <h1>Alertas de seguridad</h1>
      <div className="barra">
        <label className="check">
          <input type="checkbox" checked={pendientes} onChange={(e) => setPendientes(e.target.checked)} /> Solo pendientes
        </label>
        {altas > 0 && <span className="badge error">{altas} de severidad alta sin revisar</span>}
      </div>
      {error && <p className="error">{error}</p>}
      {alertas.length === 0 ? <p className="vacio">Sin alertas{pendientes ? ' pendientes' : ''}.</p> : (
        <table className="tabla">
          <thead><tr><th>Detectada</th><th>Severidad</th><th>Regla</th><th>Detalle</th><th>Estado</th></tr></thead>
          <tbody>
            {alertas.map((a) => (
              <tr key={a.id}>
                <td>{fecha(a.creadaEn)}</td>
                <td><span className={`badge ${a.severidad === 'ALTA' ? 'error' : 'alerta'}`}>{a.severidad}</span></td>
                <td><code>{a.regla}</code></td>
                <td>{a.detalle}</td>
                <td>{a.revisadaEn
                  ? <span className="sutil">Revisada {fecha(a.revisadaEn)}{a.revisadaPor ? ` por ${a.revisadaPor}` : ''}</span>
                  : <button className="secundario" onClick={() => revisar(a.id)}>Marcar revisada</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p className="sutil">Cada alerta también se envía por correo a los administradores. Los umbrales se configuran en la sección "Alertas" de la API.</p>
    </section>
  );
}
