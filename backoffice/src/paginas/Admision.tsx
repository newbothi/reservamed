import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, clp, Estado, fecha, formatearRut, hora } from '../api';
import { Bono, ComprobanteBono, Pago, Voucher } from './Comprobante';

interface CitaAdmision {
  citaId: number;
  estado: string;
  fechaHoraInicio: string;
  sede: string;
  profesional: string;
  especialidad: string;
  prestacion: string;
  pacienteRut: string;
  pacienteNombre: string;
  financiador: string | null;
  planCodigo: string | null;
  tramoFonasa: string | null;
  esCarga: boolean;
  esParticular: boolean;
  avisoParticular: string | null;
  numeroConvenio: string | null;
  bono: Bono | null;
  pagos: Pago[];
}

interface Valorizacion {
  esParticular: boolean;
  aviso: string | null;
  financiador: string;
  planCodigo: string | null;
  afiliadoNombre: string | null;
  lineas: { codigo: string; descripcion: string; valor: number; bonificacion: number; otraBonificacion: number; copago: number }[];
  totalValor: number;
  totalBonificacion: number;
  totalCopago: number;
}

type Huella = 'sin-leer' | 'leyendo' | 'ok' | 'fallida';

/** Desafío de un solo uso de la API para que el lector firme ESTA lectura (C3). */
interface DesafioHuella {
  nonce: string;
  citaId: number;
  rut: string;
  sedeId: number;
  expiraUtc: string;
}

export default function Admision() {
  const { id } = useParams();
  const [cita, setCita] = useState<CitaAdmision | null>(null);
  const [valorizacion, setValorizacion] = useState<Valorizacion | null>(null);
  const [huella, setHuella] = useState<Huella>('sin-leer');
  // Comprobante firmado por el lector: el navegador solo lo transporta, no puede fabricarlo.
  const [comprobante, setComprobante] = useState<string | null>(null);
  const [derivador, setDerivador] = useState('');
  const [medio, setMedio] = useState<'POS_DEBITO' | 'POS_CREDITO' | 'EFECTIVO'>('POS_DEBITO');
  const [ultimos, setUltimos] = useState('');
  const [cuotas, setCuotas] = useState(1);
  const [ocupado, setOcupado] = useState('');
  const [error, setError] = useState('');

  const cargar = useCallback(() => {
    api<CitaAdmision>(`/api/admision/citas/${id}`).then(setCita).catch((e) => setError(e.message));
  }, [id]);
  useEffect(cargar, [cargar]);

  const accion = async (nombre: string, fn: () => Promise<unknown>) => {
    setOcupado(nombre);
    setError('');
    try {
      await fn();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setOcupado('');
    }
  };

  const valorizar = () =>
    accion('valorizar', async () => setValorizacion(await api<Valorizacion>(`/api/admision/citas/${id}/valorizar`, { cuerpo: {} })));

  /**
   * C3: 1) la API entrega un desafío para esta cita y este beneficiario; 2) el lector de la sede
   * (simulado en el BFF) captura la huella y firma la lectura; 3) el bono se emite con ese
   * comprobante y la API valida la firma. "Simular fallo" lee una huella que no coincide.
   */
  const leerHuella = async (exito: boolean) => {
    setHuella('leyendo');
    setComprobante(null);
    setError('');
    try {
      const desafio = await api<DesafioHuella>(`/api/admision/citas/${id}/huella/desafio`, { cuerpo: {} });
      const lectura = await api<{ comprobante: string }>('/lector/leer', {
        cuerpo: { desafio, resultado: exito ? 'COINCIDE' : 'NO_COINCIDE' },
      });
      setComprobante(lectura.comprobante);
      setHuella(exito ? 'ok' : 'fallida');
    } catch (e) {
      setHuella('sin-leer');
      setError((e as Error).message);
    }
  };

  const emitirBono = () =>
    accion('bono', async () => {
      try {
        await api(`/api/admision/citas/${id}/bono`, {
          cuerpo: { comprobanteHuella: comprobante, derivadorRut: derivador || null },
        });
        cargar();
      } finally {
        // Cada lectura sirve una sola vez: si algo falla, hay que volver a leer.
        setComprobante(null);
        setHuella('sin-leer');
      }
    });

  const anularBono = () =>
    accion('anular', async () => {
      if (!confirm('¿Anular el bono emitido?')) return;
      await api(`/api/admision/citas/${id}/bono/anular`, { cuerpo: {} });
      setHuella('sin-leer');
      setComprobante(null);
      cargar();
    });

  const cobrar = () =>
    accion('cobrar', async () => {
      setCita(await api<CitaAdmision>(`/api/admision/citas/${id}/cobrar`, {
        cuerpo: { medio, ultimosDigitos: ultimos || null, cuotas },
      }));
    });

  const admitirPagada = () =>
    accion('admitir', async () => setCita(await api<CitaAdmision>(`/api/admision/citas/${id}/admitir`, { cuerpo: {} })));

  if (!cita) return error ? <p className="error">{error}</p> : <p className="vacio">Cargando…</p>;

  const pagoOk = cita.pagos.find((p) => p.estado === 'AUTORIZADO');
  const bonoEmitido = cita.bono?.estado === 'EMITIDO';
  const terminada = ['ADMITIDA', 'ATENDIDA'].includes(cita.estado);
  const montoACobrar = cita.esParticular ? valorizacion?.totalValor : cita.bono?.totalCopago;

  return (
    <section>
      <p className="no-imprimir"><Link to="/">← Volver a la agenda</Link></p>

      <div className="tarjeta no-imprimir">
        <div className="fila" style={{ justifyContent: 'space-between' }}>
          <h1 style={{ margin: 0 }}>{cita.pacienteNombre}</h1>
          <Estado estado={cita.estado} />
        </div>
        <div className="datos-paciente">
          <div><span>RUT</span>{cita.pacienteRut}</div>
          <div><span>Previsión</span>{cita.financiador ?? 'Sin previsión'}{cita.tramoFonasa ? ` tramo ${cita.tramoFonasa}` : ''}{cita.planCodigo && cita.tramoFonasa == null ? ` · ${cita.planCodigo}` : ''}{cita.esCarga ? ' (carga)' : ''}</div>
          <div><span>Atención</span>{fecha(cita.fechaHoraInicio)} {hora(cita.fechaHoraInicio)} · {cita.sede}</div>
          <div><span>Profesional</span>{cita.profesional} · {cita.especialidad}</div>
          <div><span>Prestación</span>{cita.prestacion}</div>
          {cita.numeroConvenio && <div><span>Convenio</span>{cita.numeroConvenio}</div>}
        </div>
        {cita.avisoParticular && <p className="aviso">{cita.avisoParticular}</p>}
      </div>

      {error && <p className="error no-imprimir">{error}</p>}

      {cita.estado === 'CONFIRMADA' && (
        <div className="tarjeta no-imprimir">
          <p className="exito">El paciente pagó online (bono web folio {cita.bono?.folio}). Verifique su cédula y admítalo.</p>
          <button onClick={admitirPagada} disabled={!!ocupado}>{ocupado === 'admitir' ? 'Admitiendo…' : 'Admitir paciente'}</button>
        </div>
      )}

      {cita.estado === 'RESERVADA' && (
        <div className="pasos no-imprimir">
          {/* Paso 1: valorizar */}
          <div className={`tarjeta paso ${valorizacion ? 'hecho' : ''}`}>
            <h2><span className="num">1</span>Valorizar con {cita.esParticular ? 'arancel particular' : cita.financiador}</h2>
            <button className="sec" onClick={valorizar} disabled={!!ocupado}>
              {ocupado === 'valorizar' ? 'Consultando I-Med…' : 'Consultar cobertura'}
            </button>
            {valorizacion && (
              <table className="tabla" style={{ marginTop: '.8rem' }}>
                <thead><tr><th>Prestación</th><th>Valor</th><th>Bonificación</th><th>Copago</th></tr></thead>
                <tbody>
                  {valorizacion.lineas.map((l) => (
                    <tr key={l.codigo}>
                      <td>{l.codigo} {l.descripcion}</td><td>{clp(l.valor)}</td>
                      <td>{clp(l.bonificacion + l.otraBonificacion)}</td><td><strong>{clp(l.copago)}</strong></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>

          {/* Paso 2: bono con huella (no aplica a particulares) */}
          {!cita.esParticular && (
            <div className={`tarjeta paso ${cita.bono ? 'hecho' : ''} ${!valorizacion && !cita.bono ? 'bloqueado' : ''}`}>
              <h2><span className="num">2</span>Emitir bono electrónico</h2>
              {cita.bono ? (
                <div className="fila">
                  <p>Bono folio <strong>{cita.bono.folio}</strong> emitido · copago {clp(cita.bono.totalCopago)}</p>
                  {bonoEmitido && <button className="sec" onClick={anularBono} disabled={!!ocupado}>Anular bono</button>}
                </div>
              ) : (
                <>
                  <div className="huella">
                    <span className="icono">{huella === 'ok' ? '✅' : huella === 'fallida' ? '❌' : '🖐️'}</span>
                    <div style={{ flex: 1 }}>
                      <strong>Lector biométrico (simulado)</strong>
                      <div className="sutil">
                        {huella === 'sin-leer' && 'Pida al beneficiario que ponga el dedo en el lector.'}
                        {huella === 'leyendo' && 'Leyendo huella…'}
                        {huella === 'ok' && 'Lectura firmada por el lector de la sede. Emita el bono.'}
                        {huella === 'fallida' && 'El lector no reconoció la huella. Repita la lectura.'}
                      </div>
                    </div>
                    <button className="sec" onClick={() => leerHuella(true)} disabled={!valorizacion || huella === 'leyendo'}>Leer huella</button>
                    <button className="link" onClick={() => leerHuella(false)} disabled={!valorizacion}>Simular fallo</button>
                  </div>
                  <div className="fila" style={{ marginTop: '.8rem' }}>
                    <label>RUT médico derivador (opcional)
                      <input value={derivador} onChange={(e) => setDerivador(formatearRut(e.target.value))} placeholder="Si viene derivado" />
                    </label>
                    <button onClick={emitirBono} disabled={!valorizacion || !comprobante || huella === 'leyendo' || !!ocupado}>
                      {ocupado === 'bono' ? 'Emitiendo…' : 'Emitir bono'}
                    </button>
                  </div>
                </>
              )}
            </div>
          )}

          {/* Paso 3: cobrar */}
          <div className={`tarjeta paso ${(cita.esParticular ? !valorizacion : !bonoEmitido) ? 'bloqueado' : ''}`}>
            <h2><span className="num">{cita.esParticular ? 2 : 3}</span>Cobrar {montoACobrar !== undefined ? clp(montoACobrar) : ''} y admitir</h2>
            <div className="fila">
              <label>Medio de pago
                <select value={medio} onChange={(e) => setMedio(e.target.value as typeof medio)}>
                  <option value="POS_DEBITO">Débito (POS)</option>
                  <option value="POS_CREDITO">Crédito (POS)</option>
                  <option value="EFECTIVO">Efectivo</option>
                </select>
              </label>
              {medio !== 'EFECTIVO' && (
                <label>Últimos 4 dígitos
                  <input value={ultimos} onChange={(e) => setUltimos(e.target.value.replace(/\D/g, '').slice(0, 4))} placeholder="1088" />
                </label>
              )}
              {medio === 'POS_CREDITO' && (
                <label>Cuotas
                  <select value={cuotas} onChange={(e) => setCuotas(Number(e.target.value))}>
                    {[1, 2, 3, 6, 12].map((n) => <option key={n} value={n}>{n}</option>)}
                  </select>
                </label>
              )}
              <button onClick={cobrar} disabled={(cita.esParticular ? !valorizacion : !bonoEmitido) || !!ocupado}>
                {ocupado === 'cobrar' ? 'Esperando POS…' : 'Cobrar'}
              </button>
            </div>
            <p className="sutil">Prueba: tarjeta terminada en 0000 es rechazada por el emisor.</p>
          </div>
        </div>
      )}

      {terminada && (
        <>
          <div className="fila no-imprimir" style={{ justifyContent: 'space-between', margin: '1rem 0' }}>
            <p className="exito" style={{ margin: 0 }}>Paciente admitido. Entregue el comprobante.</p>
            <button onClick={() => window.print()}>Imprimir comprobante</button>
          </div>
          {cita.bono ? (
            <ComprobanteBono bono={cita.bono} pago={pagoOk} />
          ) : (
            pagoOk && <Voucher pago={pagoOk} titulo="ATENCIÓN PARTICULAR" />
          )}
        </>
      )}
    </section>
  );
}
