import { useRef, useState } from 'react';
import { Link, Navigate, useLocation } from 'react-router-dom';
import { api, clp, fechaLarga, hora, pruebaAntiBots } from '../api';
import type { CupoElegido } from './Buscar';

interface LineaValorizada {
  codigo: string;
  descripcion: string;
  valor: number;
  bonificacion: number;
  otraBonificacion: number;
  copago: number;
}

export interface PagoIniciado {
  token: string;
  url: string;
  monto: number;
  valorizacion: {
    esParticular: boolean;
    aviso: string | null;
    financiador: string;
    planCodigo: string | null;
    lineas: LineaValorizada[];
    totalValor: number;
    totalBonificacion: number;
    totalCopago: number;
  };
}

type Modalidad = 'ONLINE' | 'EN_CLINICA';

export default function Reservar() {
  const { state } = useLocation();
  const cupo: CupoElegido | null =
    (state as CupoElegido | null) ?? JSON.parse(sessionStorage.getItem('rm_cupo') ?? 'null');
  const [modalidad, setModalidad] = useState<Modalidad>('ONLINE');
  const [enviando, setEnviando] = useState(false);
  const [error, setError] = useState('');
  const [reservada, setReservada] = useState<{ citaId: number; estado: string } | null>(null);
  const [pago, setPago] = useState<PagoIniciado | null>(null);

  if (!cupo) return <Navigate to="/" replace />;

  const confirmar = async () => {
    setEnviando(true);
    setError('');
    try {
      const pow = await pruebaAntiBots();
      const r = await api<{ citaId: number; estado: string }>('/citas', {
        cuerpo: { profesionalId: cupo.profesionalId, sedeId: cupo.sedeId, inicio: cupo.inicio, modalidadPago: modalidad, pow },
      });
      sessionStorage.removeItem('rm_cupo');
      setReservada(r);
      if (modalidad === 'ONLINE') {
        setPago(await api<PagoIniciado>('/pagos/webpay/iniciar', { cuerpo: { citaId: r.citaId } }));
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setEnviando(false);
    }
  };

  if (pago) return <IrAWebpay pago={pago} />;

  if (reservada) {
    return (
      <section className="tarjeta centrada">
        <h1>{reservada.estado === 'RESERVADA' ? '¡Hora reservada!' : 'Reserva creada'}</h1>
        {reservada.estado === 'RESERVADA' ? (
          <p>
            Le enviamos la confirmación por correo. El día de la atención, llegue 15 minutos antes con su cédula:
            en admisión se emite el bono con su huella y paga el copago.
          </p>
        ) : (
          <p>{error || 'No se pudo iniciar el pago. Puede reintentarlo desde Mis citas antes de que expire la reserva.'}</p>
        )}
        <Link className="boton" to="/mis-citas">Ver mis citas</Link>
      </section>
    );
  }

  return (
    <section className="tarjeta centrada">
      <h1>Confirme su reserva</h1>
      <dl className="resumen">
        <dt>Profesional</dt><dd>{cupo.profesional}</dd>
        <dt>Especialidad</dt><dd>{cupo.especialidad}</dd>
        <dt>Sede</dt><dd>{cupo.sede}</dd>
        <dt>Fecha</dt><dd>{fechaLarga(cupo.inicio)} a las {hora(cupo.inicio)}</dd>
        <dt>Valor particular</dt><dd>{clp(cupo.valorParticular)}</dd>
      </dl>

      <fieldset className="opciones">
        <legend>¿Cómo quiere pagar?</legend>
        <label className={modalidad === 'ONLINE' ? 'opcion activa' : 'opcion'}>
          <input type="radio" checked={modalidad === 'ONLINE'} onChange={() => setModalidad('ONLINE')} />
          <div>
            <strong>Pagar ahora con Webpay</strong>
            <span>Se emite el bono web con su previsión y paga sólo el copago. Tiene 10 minutos para completar el pago.</span>
          </div>
        </label>
        <label className={modalidad === 'EN_CLINICA' ? 'opcion activa' : 'opcion'}>
          <input type="radio" checked={modalidad === 'EN_CLINICA'} onChange={() => setModalidad('EN_CLINICA')} />
          <div>
            <strong>Pagar en la clínica</strong>
            <span>El día de la atención se emite el bono con su huella en admisión y paga con débito, crédito o efectivo.</span>
          </div>
        </label>
      </fieldset>

      {error && <p className="error">{error}</p>}
      <div className="acciones">
        <Link className="boton sec" to="/">Volver</Link>
        <button onClick={confirmar} disabled={enviando}>{enviando ? 'Reservando…' : 'Confirmar reserva'}</button>
      </div>
    </section>
  );
}

/** Muestra la valorización del bono y envía el formulario POST con token_ws, como exige Webpay. */
export function IrAWebpay({ pago }: { pago: PagoIniciado }) {
  const form = useRef<HTMLFormElement>(null);
  const v = pago.valorizacion;
  return (
    <section className="tarjeta centrada">
      <h1>Pago del bono</h1>
      {v.aviso && <p className="aviso">{v.aviso}</p>}
      <p className="sutil">{v.esParticular ? 'Atención particular' : `${v.financiador} · plan ${v.planCodigo}`}</p>
      <table className="tabla">
        <thead>
          <tr><th>Prestación</th><th>Valor</th><th>Bonificación</th><th>Copago</th></tr>
        </thead>
        <tbody>
          {v.lineas.map((l) => (
            <tr key={l.codigo}>
              <td>{l.codigo} {l.descripcion}</td>
              <td>{clp(l.valor)}</td>
              <td>{clp(l.bonificacion + l.otraBonificacion)}</td>
              <td><strong>{clp(l.copago)}</strong></td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="total">A pagar: {clp(pago.monto)}</p>
      <form ref={form} method="post" action={pago.url}>
        <input type="hidden" name="token_ws" value={pago.token} />
        <button type="submit">Ir a pagar con Webpay</button>
      </form>
    </section>
  );
}
