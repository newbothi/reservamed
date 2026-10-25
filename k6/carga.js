// F5b: prueba de carga con k6 contra el borde (NGINX → BFF → API → SQL Server).
//   docker run --rm --network reservamed_app --add-host reservamed.localhost:172.30.1.10 -v ./k6:/k6 \
//     -v ./certs/ca.pem:/ca.pem:ro -e SSL_CERT_FILE=/ca.pem -e BASE_URL=https://reservamed.localhost:8443 grafana/k6:1.3.0 run /k6/carga.js
//   (scripts/verificar-fase5b.ps1 lo hace en el PC; NGINX valida el Host y el certificado es de la CA local)
// Escenarios:
//   navegacion — pacientes buscando horas (catálogo y disponibilidad) a ritmo constante, por debajo
//                del límite por IP de NGINX (20/s): mide latencia y errores.
//   rafaga     — 80 solicitudes casi simultáneas desde una IP: el límite de NGINX debe responder 429
//                (protección ante abuso) sin errores 5xx.
import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'https://localhost';
const limitadas = new Counter('rafaga_429');
const errores5xx = new Rate('errores_5xx');

export const options = {
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
  scenarios: {
    navegacion: {
      executor: 'constant-arrival-rate', rate: 12, timeUnit: '1s', duration: '60s',
      preAllocatedVUs: 20, maxVUs: 40, exec: 'navegar',
    },
    rafaga: {
      executor: 'shared-iterations', vus: 40, iterations: 80, startTime: '65s', maxDuration: '20s', exec: 'rafaga',
    },
  },
  thresholds: {
    'http_req_failed{scenario:navegacion}': ['rate<0.01'],
    'http_req_duration{scenario:navegacion}': ['p(95)<800', 'p(99)<1500'],
    'checks{scenario:navegacion}': ['rate>0.99'],
    rafaga_429: ['count>0'],
    errores_5xx: ['rate==0'],
  },
};

const hoy = new Date().toISOString().slice(0, 10);
const enUnaSemana = new Date(Date.now() + 6 * 86400000).toISOString().slice(0, 10);

export function navegar() {
  const r = Math.random() < 0.3
    ? http.get(`${BASE}/bff/catalogo`, { tags: { nombre: 'catalogo' } })
    : http.get(`${BASE}/bff/agenda/disponibilidad?especialidadId=${1 + Math.floor(Math.random() * 5)}&desde=${hoy}&hasta=${enUnaSemana}`,
        { tags: { nombre: 'disponibilidad' } });
  errores5xx.add(r.status >= 500);
  check(r, { 'respuesta 200': (x) => x.status === 200, 'JSON': (x) => (x.headers['Content-Type'] || '').includes('json') });
}

export function rafaga() {
  const r = http.get(`${BASE}/bff/catalogo`, { tags: { nombre: 'rafaga' } });
  errores5xx.add(r.status >= 500);
  if (r.status === 429) limitadas.add(1);
  check(r, { '200 o 429': (x) => x.status === 200 || x.status === 429 });
}

export function handleSummary(datos) {
  const m = datos.metrics;
  const d = m['http_req_duration{scenario:navegacion}'] || m.http_req_duration;
  const p95 = d && d.values['p(95)'] !== undefined ? d.values['p(95)'].toFixed(0) : '?';
  const p99 = d && d.values['p(99)'] !== undefined ? d.values['p(99)'].toFixed(0) : '?';
  const fallas = m['http_req_failed{scenario:navegacion}'];
  const tasa = fallas ? (fallas.values.rate * 100).toFixed(2) : '?';
  const rps = m.http_reqs ? m.http_reqs.values.rate.toFixed(1) : '?';
  const n429 = m.rafaga_429 ? m.rafaga_429.values.count : 0;
  const malos = Object.entries(datos.metrics).filter(([, v]) => v.thresholds && Object.values(v.thresholds).some((t) => !t.ok)).map(([k]) => k);
  const linea = `navegación: p95 ${p95} ms, p99 ${p99} ms, errores ${tasa}%, ${rps} req/s; ráfaga: ${n429} respuestas 429`;
  const salida = (malos.length ? `::error title=k6::Umbrales no cumplidos: ${malos.join(', ')} — ${linea}` : `::notice title=k6::${linea}`) + '\n';
  return { stdout: salida, '/k6/resultado.json': JSON.stringify({ p95, p99, tasaErrores: tasa, rps, rafaga429: n429, umbralesFallidos: malos }, null, 2) };
}
