import { useCallback, useEffect, useState } from 'react';
import { api, clp, ESTADOS, fechaHora } from '../api';
import { IrAWebpay, PagoIniciado } from './Reservar';

interface Cita {
  id: number;
  fechaHoraInicio: string;
  estado: string;
  profesional: string;
  especialidad: string;
  sede: string;
  direccionSede: string;
  folioBono: number | null;
  montoPagado: number | null;
  medioPago: string | null;
  puedePagar: boolean;
  puedeAnular: boolean;
  segundosParaPagar: number | null;
}

function Estado({ estado }: { estado: string }) {
  const e = ESTADOS[estado] ?? { texto: estado, tono: 'neutro' };
  return <span className={`badge ${e.tono}`}>{e.texto}</span>;
}

function Cuenta({ segundos }: { segundos: number }) {
  const [s, setS] = useState(segundos);
  useEffect(() => {
    const t = setInterval(() => setS((x) => Math.max(0, x - 1)), 1000);
    return () => clearInterval(t);
  }, []);
  return <span className="cuenta">{Math.floor(s / 60)}:{String(s % 60).padStart(2, '0')}</span>;
}

export default function MisCitas() {
  const [datos, setDatos] = useState<{ proximas: Cita[]; historial: Cita[] } | null>(null);
  const [error, setError] = useState('');
  const [pago, setPago] = useState<PagoIniciado | null>(null);

  const cargar = useCallback(() => {
    api<{ proximas: Cita[]; historial: Cita[] }>('/citas/mias').then(setDatos).catch((e) => setError(e.message));
  }, []);
  useEffect(cargar, [cargar]);

  const anular = async (c: Cita) => {
    if (!confirm(`¿Anular su hora de ${c.especialidad} del ${fechaHora(c.fechaHoraInicio)}?`)) return;
    try {
      await api(`/citas/${c.id}/cancelar`, { cuerpo: { motivo: 'Anulada por el paciente desde el portal' } });
      cargar();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const pagar = async (c: Cita) => {
    try {
      setPago(await api<PagoIniciado>('/pagos/webpay/iniciar', { cuerpo: { citaId: c.id } }));
    } catch (e) {
      setError((e as Error).message);
    }
  };

  if (pago) return <IrAWebpay pago={pago} />;

  return (
    <section>
      <h1>Mis citas</h1>
      {error && <p className="error">{error}</p>}
      {!datos && !error && <p className="vacio">Cargando…</p>}
      {datos?.proximas.length === 0 && <p className="vacio">No tiene horas próximas.</p>}

      {datos?.proximas.map((c) => (
        <article key={c.id} className="tarjeta cita">
          <div>
            <Estado estado={c.estado} />
            <h3>{c.especialidad} · {c.profesional}</h3>
            <p className="fecha">{fechaHora(c.fechaHoraInicio)}</p>
            <p className="sutil">{c.sede} — {c.direccionSede}</p>
            {c.folioBono && <p className="sutil">Bono folio {c.folioBono} · pagado {clp(c.montoPagado)}</p>}
            {c.estado === 'RESERVADA' && (
              <p className="sutil">Pago en la clínica: llegue 15 minutos antes con su cédula para emitir el bono.</p>
            )}
          </div>
          <div className="acciones-cita">
            {c.puedePagar && c.segundosParaPagar !== null && (
              <>
                <span className="sutil">Expira en <Cuenta segundos={c.segundosParaPagar} /></span>
                <button onClick={() => pagar(c)}>Pagar ahora</button>
              </>
            )}
            {c.puedeAnular && <button className="sec" onClick={() => anular(c)}>Anular</button>}
          </div>
        </article>
      ))}

      {datos && datos.historial.length > 0 && (
        <>
          <h2>Historial</h2>
          <table className="tabla">
            <thead>
              <tr><th>Fecha</th><th>Especialidad</th><th>Profesional</th><th>Estado</th><th>Pagado</th></tr>
            </thead>
            <tbody>
              {datos.historial.map((c) => (
                <tr key={c.id}>
                  <td>{fechaHora(c.fechaHoraInicio)}</td>
                  <td>{c.especialidad}</td>
                  <td>{c.profesional}</td>
                  <td><Estado estado={c.estado} /></td>
                  <td>{c.montoPagado ? clp(c.montoPagado) : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  );
}
