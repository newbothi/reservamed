import { clp, fecha, rutBono } from '../api';

export interface Bono {
  id: number;
  folio: number;
  financiador: string;
  numeroConvenio: string;
  planCodigo: string | null;
  beneficiarioRut: string;
  beneficiarioNombre: string;
  beneficiarioFechaNacimiento: string;
  beneficiarioSexo: string;
  afiliadoRut: string;
  afiliadoNombre: string;
  rutInstitucion: string;
  nombreInstitucion: string;
  rutProfesional: string;
  nombreProfesional: string;
  derivadorRut: string | null;
  canal: string;
  tipoFirma: string;
  codigoAuditoria: string;
  rutEmisor: string;
  fechaEmision: string;
  validoHasta: string;
  estado: string;
  totalValor: number;
  totalBonificacion: number;
  totalOtraBonificacion: number;
  totalCopago: number;
  detalles: { codigo: string; descripcion: string; cantidad: number; valor: number; bonificacion: number; otraBonificacion: number; copago: number }[];
}

export interface Pago {
  id: number;
  medio: string;
  monto: number;
  estado: string;
  codigoAutorizacion: string | null;
  numeroOperacion: string | null;
  numeroUnico: string | null;
  terminal: string | null;
  ultimosDigitos: string | null;
  cuotas: number | null;
  buyOrder: string | null;
  fechaPago: string | null;
}

function edad(nacimiento: string, a: string) {
  const n = new Date(nacimiento), h = new Date(a);
  let e = h.getFullYear() - n.getFullYear();
  if (h.getMonth() < n.getMonth() || (h.getMonth() === n.getMonth() && h.getDate() < n.getDate())) e--;
  return e;
}

/** Réplica del "Bono de atención ambulatoria" que se imprime en admisión. */
export function ComprobanteBono({ bono, pago }: { bono: Bono; pago?: Pago }) {
  const b = bono;
  return (
    <>
      <div className="bono">
        <div className="enc">
          <strong>{b.financiador}</strong>
          <h3>BONO DE ATENCIÓN AMBULATORIA</h3>
          <span className="folio">{b.folio}</span>
        </div>
        <div className="meta">
          <span>Convenio: {b.numeroConvenio} - {b.nombreInstitucion}</span>
          <span>Fecha emisión: {b.fechaEmision.slice(0, 10)} &nbsp; Hora: {b.fechaEmision.slice(11, 19)}</span>
        </div>
        <p style={{ textAlign: 'right', fontSize: 11 }}>VÁLIDO SÓLO PARA ESTA FECHA DE EMISIÓN</p>

        <h4>DATOS DE BENEFICIARIO</h4>
        <p>Afiliado: {rutBono(b.afiliadoRut)} {b.afiliadoNombre}</p>
        <p>
          Beneficiario: {rutBono(b.beneficiarioRut)} {b.beneficiarioNombre} / Edad:{edad(b.beneficiarioFechaNacimiento, b.fechaEmision)} / Sexo:{b.beneficiarioSexo}
        </p>
        <p>Plan: {b.planCodigo ?? '—'}</p>

        <h4>DATOS DE PRESTADOR</h4>
        <p>
          Profesional / Institución {rutBono(b.rutInstitucion)} {b.nombreInstitucion} / Médico tratante: {rutBono(b.rutProfesional)} {b.nombreProfesional}
        </p>

        <table>
          <thead>
            <tr><th>Prestación</th><th></th><th>Valor</th><th>Bonificación financiador</th><th>Otra bonificación</th><th>Copago</th></tr>
          </thead>
          <tbody>
            {b.detalles.map((d) => (
              <tr key={d.codigo}>
                <td>{d.codigo}</td><td>{d.cantidad} {d.descripcion}</td>
                <td>{clp(d.valor)}</td><td>{clp(d.bonificacion)}</td><td>{clp(d.otraBonificacion)}</td><td>{clp(d.copago)}</td>
              </tr>
            ))}
            <tr>
              <td></td><td style={{ textAlign: 'right' }}>Totales</td>
              <td>{clp(b.totalValor)}</td><td>{clp(b.totalBonificacion)}</td><td>{clp(b.totalOtraBonificacion)}</td><td>{clp(b.totalCopago)}</td>
            </tr>
          </tbody>
        </table>
        <p className="apagar">A Pagar: {clp(b.totalCopago)}</p>

        <p>MEDIOS DE PAGO: {pago ? `${pago.medio.replace('_', ' ')} ${clp(pago.monto)}` : 'pendiente'}</p>
        <p>Emisor: {rutBono(b.rutEmisor)} | Derivado por: {b.derivadorRut ? rutBono(b.derivadorRut) : '0000000000-0'}</p>
        <div className="firma">
          Firmado electrónicamente ({b.tipoFirma === 'HUELLA' ? 'huella' : 'clave web'}) por {b.beneficiarioNombre} | Auditoría {b.codigoAuditoria}
        </div>
      </div>
      {pago && pago.medio !== 'EFECTIVO' && <Voucher pago={pago} />}
    </>
  );
}

/** Voucher estilo Transbank (POS o Webpay). */
export function Voucher({ pago, titulo }: { pago: Pago; titulo?: string }) {
  const f = pago.fechaPago ?? '';
  const tipo = pago.medio === 'POS_CREDITO' ? 'VENTA CREDITO' : pago.medio === 'WEBPAY' ? 'VENTA WEBPAY' : pago.medio === 'EFECTIVO' ? 'PAGO EFECTIVO' : 'VENTA DEBITO';
  return (
    <div className="voucher">
      {titulo && `${titulo}\n`}
      {`${tipo}\nReservaMed\n------------------------------\nFECHA ${f ? fecha(f) : ''}  HORA ${f.slice(11, 19)}\n`}
      {pago.terminal && `TERMINAL ${pago.terminal}\n`}
      {pago.ultimosDigitos && `NUMERO DE TARJETA ************${pago.ultimosDigitos}\n`}
      {pago.cuotas ? `CUOTAS ${pago.cuotas}\n` : ''}
      {`MONTO TOTAL ${clp(pago.monto)}\n`}
      {pago.numeroOperacion && `NUMERO DE OPERACION ${pago.numeroOperacion}\n`}
      {pago.codigoAutorizacion && `CODIGO DE AUTORIZACION ${pago.codigoAutorizacion}\n`}
      {pago.numeroUnico && `NUMERO UNICO ${pago.numeroUnico}\n`}
      {pago.buyOrder && `ORDEN DE COMPRA ${pago.buyOrder}\n`}
      {pago.medio !== 'EFECTIVO' && '------------------------------\nACEPTO PAGAR SEGUN CONTRATO CON EMISOR'}
    </div>
  );
}
