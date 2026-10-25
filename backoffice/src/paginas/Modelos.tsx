import { useCallback, useEffect, useState } from 'react';
import { api, ErrorApi } from '../api';

interface Coeficiente { variable: string; clave: string; coeficiente: number; oddsRatio: number }

interface Modelo {
  version: string;
  algoritmo: string;
  entrenadoEn: string;
  muestras: number;
  tasaBase: number;
  auc: number;
  precisionAlto: number;
  recallAlto: number;
  umbralMedio: number;
  umbralAlto: number;
  datosSinteticos: boolean;
  activo: boolean;
  coeficientes?: Coeficiente[];
  citasRecalculadas?: number;
  brier?: number | null;
  brierBase?: number | null;
  observaciones?: string | null;
}

interface Demanda {
  especialidad: string;
  capacidadSemanal: number;
  promedioUltimas8: number;
  tendenciaSemanal: number;
  errorMedioAbsoluto: number;
  semanas: { semana: string; citasEstimadas: number; ocupacion: number | null }[];
}

const pct = (n: number) => `${(n * 100).toFixed(1)}%`;
const fechaCorta = (s: string) => `${s.slice(8, 10)}/${s.slice(5, 7)}`;

/** Clasificación de ocupación: siempre con texto, nunca sólo color. */
function estadoOcupacion(o: number | null) {
  if (o === null) return { texto: 'sin cupos', clase: 'neutro' };
  if (o >= 0.95) return { texto: '▲ saturada', clase: 'error' };
  if (o >= 0.8) return { texto: '△ alta', clase: 'alerta' };
  if (o < 0.4) return { texto: '▽ baja', clase: 'info' };
  return { texto: 'normal', clase: 'neutro' };
}

export default function Modelos() {
  const [modelo, setModelo] = useState<Modelo | null>(null);
  const [historial, setHistorial] = useState<Modelo[]>([]);
  const [demanda, setDemanda] = useState<Demanda[] | null>(null);
  const [aviso, setAviso] = useState('');
  const [error, setError] = useState('');
  const [entrenando, setEntrenando] = useState(false);

  const cargar = useCallback(() => {
    setError('');
    api<Modelo>('/api/ml/modelo')
      .then((m) => { setModelo(m); setAviso(''); })
      .catch((e: ErrorApi) => {
        setModelo(null);
        if (e.status === 404) setAviso('Todavía no hay un modelo entrenado. Genere el historial sintético y entrene (ver README).');
        else setError(e.message);
      });
    api<Modelo[]>('/api/ml/modelos').then(setHistorial).catch(() => setHistorial([]));
    api<Demanda[]>('/api/ml/demanda?semanas=4').then(setDemanda).catch(() => setDemanda(null));
  }, []);
  useEffect(cargar, [cargar]);

  const entrenar = async () => {
    setEntrenando(true);
    setError('');
    try {
      const m = await api<Modelo>('/api/ml/modelo/entrenar', { cuerpo: {} });
      setAviso(`Modelo ${m.version} entrenado. Se recalculó el riesgo de ${m.citasRecalculadas ?? 0} citas próximas.`);
      cargar();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setEntrenando(false);
    }
  };

  const coef = (modelo?.coeficientes ?? []).slice(0, 12);
  const maxAbs = Math.max(0.01, ...coef.map((c) => Math.abs(c.coeficiente)));

  return (
    <section className="viz-root">
      <div className="fila" style={{ justifyContent: 'space-between' }}>
        <h1 style={{ margin: 0 }}>Modelos de machine learning</h1>
        <button onClick={entrenar} disabled={entrenando}>{entrenando ? 'Entrenando…' : 'Reentrenar modelo'}</button>
      </div>
      <p className="sutil">
        Riesgo de inasistencia con regresión logística y demanda semanal con regresión lineal (Python · scikit-learn).
        El riesgo se usa para priorizar recordatorios y gestionar sobrecupos, nunca para negar una hora.
      </p>
      {error && <p className="error">{error}</p>}
      {aviso && <p className="aviso">{aviso}</p>}

      {modelo && (
        <>
          {modelo.datosSinteticos && (
            <p className="aviso">Este modelo se entrenó con historial <strong>sintético</strong> (datos de demostración).</p>
          )}
          <div className="tiles">
            <div className="tile"><span>AUC</span><strong>{modelo.auc.toFixed(3)}</strong>
              <small>0,5 = azar · 1,0 = perfecto</small></div>
            <div className="tile"><span>Inasistencia histórica</span><strong>{pct(modelo.tasaBase)}</strong>
              <small>{modelo.muestras.toLocaleString('es-CL')} citas</small></div>
            <div className="tile"><span>Precisión nivel ALTO</span><strong>{pct(modelo.precisionAlto)}</strong>
              <small>de las citas marcadas ALTO, cuántas faltaron</small></div>
            <div className="tile"><span>Cobertura nivel ALTO</span><strong>{pct(modelo.recallAlto)}</strong>
              <small>de las inasistencias, cuántas detectó</small></div>
            {modelo.brier != null && modelo.brierBase != null && (
              <div className="tile"><span>Calibración (Brier)</span><strong>{modelo.brier.toFixed(4)}</strong>
                <small>menor es mejor · sin modelo: {modelo.brierBase.toFixed(4)}</small></div>
            )}
          </div>
          <p className="sutil">
            Versión {modelo.version} · entrenado {modelo.entrenadoEn.replace('T', ' ')} ·
            umbrales: MEDIO ≥ {pct(modelo.umbralMedio)}, ALTO ≥ {pct(modelo.umbralAlto)}
          </p>

          <h2>¿Qué variables pesan más?</h2>
          <p className="sutil">
            Coeficientes de la regresión logística (variables numéricas estandarizadas).
            <span className="leyenda"><i className="m-sube" /> aumenta el riesgo</span>
            <span className="leyenda"><i className="m-baja" /> reduce el riesgo</span>
          </p>
          <table className="tabla coef">
            <thead><tr><th>Variable</th><th className="col-barra">Efecto</th><th>Coef.</th><th>Odds ×</th></tr></thead>
            <tbody>
              {coef.map((c) => {
                const ancho = (Math.abs(c.coeficiente) / maxAbs) * 50;
                const sube = c.coeficiente > 0;
                return (
                  <tr key={c.clave} title={`${c.variable}: multiplica las chances de no asistir por ${c.oddsRatio}`}>
                    <td>{c.variable}</td>
                    <td className="col-barra">
                      <div className="pista">
                        <div className="cero" />
                        <div className={sube ? 'barra sube' : 'barra baja'}
                             style={sube ? { left: '50%', width: `${ancho}%` } : { right: '50%', width: `${ancho}%` }} />
                      </div>
                    </td>
                    <td className="num">{c.coeficiente > 0 ? '+' : ''}{c.coeficiente.toFixed(2)}</td>
                    <td className="num">{c.oddsRatio.toFixed(2)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </>
      )}

      <h2>Demanda estimada próximas 4 semanas</h2>
      {!demanda && <p className="vacio">Sin pronóstico (se necesitan al menos 16 semanas de historial).</p>}
      {demanda && demanda.length > 0 && (
        <table className="tabla demanda">
          <thead>
            <tr>
              <th>Especialidad</th><th className="num">Cupos/sem</th><th className="num">Prom. 8 sem</th>
              <th className="num">Tendencia</th>
              {demanda[0].semanas.map((s) => <th key={s.semana}>Sem. {fechaCorta(s.semana)}</th>)}
            </tr>
          </thead>
          <tbody>
            {demanda.map((d) => (
              <tr key={d.especialidad}>
                <td>{d.especialidad}<br /><span className="sutil">error ±{d.errorMedioAbsoluto}</span></td>
                <td className="num">{d.capacidadSemanal}</td>
                <td className="num">{d.promedioUltimas8}</td>
                <td className="num">{d.tendenciaSemanal > 0 ? '+' : ''}{d.tendenciaSemanal.toFixed(2)}/sem</td>
                {d.semanas.map((s) => {
                  const e = estadoOcupacion(s.ocupacion);
                  return (
                    <td key={s.semana} title={`${s.citasEstimadas} citas estimadas de ${d.capacidadSemanal} cupos`}>
                      <div className="ocupacion">
                        <div className="pista-h"><div className="relleno" style={{ width: `${Math.min(100, (s.ocupacion ?? 0) * 100)}%` }} /></div>
                        <span className="num">{s.ocupacion !== null ? `${Math.round(s.ocupacion * 100)}%` : '—'}</span>
                      </div>
                      <span className={`badge ${e.clase}`}>{e.texto}</span>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {historial.length > 0 && (
        <>
          <h2>Historial de entrenamientos</h2>
          <table className="tabla">
            <thead><tr><th>Versión</th><th>Fecha</th><th className="num">Citas</th><th className="num">AUC</th><th className="num">Precisión ALTO</th><th>Estado</th></tr></thead>
            <tbody>
              {historial.map((h) => (
                <tr key={h.version}>
                  <td><code>{h.version}</code></td>
                  <td>{h.entrenadoEn.replace('T', ' ')}</td>
                  <td className="num">{h.muestras.toLocaleString('es-CL')}</td>
                  <td className="num">{h.auc.toFixed(3)}</td>
                  <td className="num">{pct(h.precisionAlto)}</td>
                  <td>{h.activo ? <span className="badge ok">Activo</span>
                    : h.observaciones ? <span className="badge alerta" title={h.observaciones}>Rechazado: {h.observaciones}</span>
                    : <span className="badge neutro">Anterior</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  );
}
