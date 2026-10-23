import { useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, clp } from '../api';

interface PagoConfirmado {
  aprobado: boolean;
  mensaje: string;
  citaId: number;
  estadoCita: string;
  monto: number;
  codigoAutorizacion: string | null;
  ultimosDigitos: string | null;
  folioBono: number | null;
}

/**
 * Webpay redirige aquí con ?token_ws=... (hubo intento de pago)
 * o con ?TBK_TOKEN=... (el usuario anuló en el formulario de pago).
 */
export default function RetornoPago() {
  const [params] = useSearchParams();
  const [resultado, setResultado] = useState<PagoConfirmado | null>(null);
  const [error, setError] = useState('');
  const enviado = useRef(false);

  useEffect(() => {
    if (enviado.current) return; // evita doble confirmación en modo estricto de React
    enviado.current = true;
    const token = params.get('token_ws') ?? params.get('TBK_TOKEN');
    if (!token) {
      setError('No se recibió la respuesta de Webpay.');
      return;
    }
    api<PagoConfirmado>('/pagos/webpay/confirmar', { cuerpo: { token } })
      .then(setResultado)
      .catch((e) => setError(e.message));
  }, [params]);

  if (error)
    return (
      <section className="tarjeta centrada">
        <h1>No pudimos confirmar el pago</h1>
        <p className="error">{error}</p>
        <Link className="boton" to="/mis-citas">Ir a mis citas</Link>
      </section>
    );

  if (!resultado) return <p className="vacio">Confirmando su pago con Webpay…</p>;

  return (
    <section className="tarjeta centrada">
      <h1>{resultado.aprobado ? 'Pago aprobado' : 'Pago no realizado'}</h1>
      <p className={resultado.aprobado ? 'exito' : 'aviso'}>{resultado.mensaje}</p>
      {resultado.aprobado && (
        <dl className="resumen">
          <dt>Monto pagado</dt><dd>{clp(resultado.monto)}</dd>
          <dt>Código de autorización</dt><dd>{resultado.codigoAutorizacion}</dd>
          <dt>Tarjeta</dt><dd>**** **** **** {resultado.ultimosDigitos}</dd>
          {resultado.folioBono && (<><dt>Folio bono</dt><dd>{resultado.folioBono}</dd></>)}
        </dl>
      )}
      <Link className="boton" to="/mis-citas">Ver mis citas</Link>
    </section>
  );
}
