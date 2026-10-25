import { Link } from 'react-router-dom';

/** ASVS V8.3.3: qué datos se tratan, para qué, cuánto tiempo y cómo ejercer los derechos. */
export default function Privacidad() {
  return (
    <article className="tarjeta legal">
      <h1>Política de privacidad</h1>
      <p className="sutil">ReservaMed es un proyecto de portafolio: todos los datos que muestra son ficticios.</p>

      <h2>Quién trata sus datos</h2>
      <p>La clínica que opera ReservaMed es la responsable de los datos personales que usted entrega al crear su cuenta y al reservar horas.</p>

      <h2>Qué datos y para qué</h2>
      <ul>
        <li><strong>Identificación y contacto</strong> (RUT, nombre, fecha de nacimiento, sexo, correo, celular, comuna): identificarlo, avisarle de sus horas y emitir sus bonos.</li>
        <li><strong>Previsión</strong> (Fonasa o isapre, plan, titular si es carga): calcular el copago y emitir el bono con su financiador.</li>
        <li><strong>Horas y pagos</strong>: la agenda, el cobro y el comprobante. De la tarjeta solo se guardan los últimos 4 dígitos; el pago lo procesa Webpay.</li>
        <li><strong>Seguridad</strong> (inicios de sesión, IP, registro de accesos): proteger su cuenta y saber quién vio su información.</li>
        <li><strong>Predicción de inasistencia</strong>: un modelo estadístico estima, para el personal de la clínica, la probabilidad de que una hora no se use. Usa edad, previsión e historial de asistencia; no toma decisiones sobre usted por sí solo.</li>
      </ul>

      <h2>Quién más los recibe</h2>
      <p>Solo su financiador de salud (para el bono) y el procesador de pagos. No vendemos ni cedemos datos para publicidad.</p>

      <h2>Cuánto tiempo</h2>
      <ul>
        <li>La ficha clínica y la información de las atenciones se conservan el plazo que exige la normativa de salud (Ley 20.584 y su reglamento), aunque usted cierre su cuenta.</li>
        <li>El registro de avisos enviados se borra a los 90 días.</li>
        <li>El registro de accesos se conserva para auditoría y no se puede modificar.</li>
      </ul>

      <h2>Sus derechos</h2>
      <p>
        Puede acceder a sus datos, pedir que se corrijan, oponerse a su uso o pedir su eliminación (Ley 19.628 y Ley 21.719).
        En <Link to="/mi-cuenta">Mi cuenta</Link> puede descargar todos sus datos, ver quién accedió a su ficha y solicitar el cierre de su cuenta.
        Para corregir datos de identificación o de previsión, acérquese a la recepción de la clínica.
      </p>

      <h2>Seguridad</h2>
      <p>Los datos viajan y se guardan cifrados, el acceso del personal está limitado a lo que su función necesita y cada acceso queda registrado.</p>
    </article>
  );
}
