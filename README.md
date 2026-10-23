# ReservaMed

Sistema de reserva de horas médicas con **admisión, bono electrónico y pago**, modelado como funciona en una clínica chilena: reserva web o presencial, valorización con Fonasa/Isapre vía I-Med, firma con huella, copago en POS o Webpay, notificaciones y auditoría. Incluye un **módulo de machine learning en Python** que estima el riesgo de inasistencia y la demanda por especialidad.

> Proyecto de portafolio. Todos los nombres, RUT y montos de los datos demo son ficticios. Las integraciones externas (I-Med, Transbank, HIS) están simuladas detrás de interfaces.

## Arquitectura

```
                       NGINX  (:80)
              ┌──────────┴──────────┐
              ▼                     ▼
      React + TS (portal)    React + TS (backoffice)
         /                       /backoffice/
              │                     │
              ▼                     │
      BFF NestJS  (/bff)            │
              │                     │
              └──────────┬──────────┘
                         ▼
               API Core ASP.NET Core (/api, /fhir)
          (Agenda · Citas · Admisión · Pagos · Auditoría)
          │              │          │          │
     SQL Server       RabbitMQ      │     Integraciones (mock)
     (Dapper)         │      │      │     ├─ I-Med (bonos Fonasa/Isapre)
                Worker .NET  │      │     ├─ POS Transbank (caja)
                (notif., HIS)│      │     ├─ Webpay Plus (online)
                             ▼      ▼     └─ FHIR R4 (Patient, Appointment)
                    ml-service (Python · FastAPI · scikit-learn)
                    riesgo de inasistencia · demanda semanal
```

| Carpeta | Qué es |
|---|---|
| `db/` | Scripts SQL Server: esquema + datos demo |
| `api-core/` | Solución .NET 10 (`ReservaMed.slnx`): `Api`, `Worker`, `Contracts` |
| `bff/` | Backend for Frontend en NestJS + Zod (sólo para el portal) |
| `ml-service/` | Machine learning en Python: FastAPI, scikit-learn, consumidor RabbitMQ, datos sintéticos |
| `portal/` | Portal de pacientes (React + TS + Vite) |
| `backoffice/` | Recepción, médicos y administración (React + TS + Vite) |
| `nginx/` | Proxy inverso y servidor de los frontends |

### Por qué cada pieza

- **API Core como monolito modular.** Agenda, citas, admisión y pagos comparten transacciones (reservar un cupo, emitir bono y cobrar deben ser consistentes). Separarlos en microservicios agregaría coordinación distribuida sin beneficio a esta escala.
- **BFF para el portal.** Adapta los datos para el frontend (agrupa cupos por día y profesional, calcula `puedeAnular` / `puedePagar`), cachea catálogos y valida con Zod. El backoffice no lo necesita y habla directo con la API.
- **Outbox + RabbitMQ.** El evento se guarda en la misma transacción que el cambio de estado. Si RabbitMQ está caído, los eventos esperan en la tabla `Outbox`; nada se pierde.
- **Integraciones detrás de interfaces** (`IEmisorBonos`, `ITerminalPos`, `IPasarelaWebpay`). Cambiar un mock por un cliente real no toca la lógica de negocio.

## Cómo funciona el flujo chileno

### Pago en la clínica (el caso del bono con huella)

1. El paciente reserva en el portal o recepción lo agenda (presencial o teléfono). Estado `RESERVADA`.
2. El día de la atención, recepción busca la cita y **valoriza**: se consulta al financiador cuánto bonifica según el plan y el **convenio** de la Isapre/Fonasa con esa sede.
3. El beneficiario pone la **huella** (simulada) y se **emite el bono**: folio, código de auditoría `BONO-XXXX-XXXX-XXXX`, afiliado y beneficiario, institución, médico tratante, derivador. Sólo es válido el día de emisión.
4. Se **cobra el copago** en el POS (débito/crédito, con código de autorización, n° de operación, terminal) o en efectivo.
5. La cita pasa a `ADMITIDA` y se imprime el comprobante con el mismo formato que el bono real.

`Valor = Bonificación financiador + Otra bonificación + Copago`. La base de datos lo exige con un `CHECK`.

### Pago online

1. El paciente elige "Pagar ahora". La cita queda `PENDIENTE_PAGO` con **10 minutos** de reserva temporal.
2. Se valoriza el bono web y se crea la transacción Webpay. El navegador hace `POST` del `token_ws` a la página de pago (simulada).
3. Al volver, se confirma la transacción. Si está aprobada se emite el bono (firma `CLAVE_WEB`) y la cita queda `CONFIRMADA`.
4. Si la reserva expiró mientras el usuario pagaba, el cargo se **reversa** automáticamente.

### Reglas de negocio incluidas

- Un cupo no se puede reservar dos veces: **índice único filtrado** `(ProfesionalId, FechaHoraInicio) WHERE OcupaCupo = 1`. Si dos personas reservan a la vez, la segunda recibe un 409.
- **Bloqueo optimista** (`ROWVERSION`) en cada cambio de estado de la cita.
- Disponibilidad calculada desde horarios semanales, menos citas activas y **bloqueos** (vacaciones).
- Máximo 3 horas pendientes por paciente en la web; no dos horas el mismo día con el mismo profesional.
- Anulación web hasta 2 horas antes. Si se pagó online, se anula el bono y se reversa el pago.
- **Fonasa tramo A** no puede usar Modalidad Libre Elección: la valorización se rechaza.
- **Carga vs. titular:** el bono muestra afiliado (titular del plan) y beneficiario (quien se atiende) por separado.
- Sin convenio entre la previsión y la sede → atención particular.
- Recepción sólo admite pacientes de su propia sede.
- Expiración de reservas no pagadas y recordatorios 24 h antes (jobs en segundo plano).
- RUT validado con módulo 11 en API, BFF y frontends. Hora oficial `America/Santiago`. Montos en CLP sin decimales.
- **Auditoría** de quién vio o modificó cada dato (Ley 20.584 de derechos del paciente y Ley 19.628 de datos personales).

## Machine learning (`ml-service`)

Un servicio en **Python** separado del resto. No reemplaza nada: se suma a la arquitectura igual que el Worker.

| Modelo | Algoritmo | Para qué se usa en el sistema |
|---|---|---|
| **Riesgo de inasistencia** por cita | Regresión logística | La agenda del día muestra el riesgo (Bajo / Medio / Alto con %). El Worker envía SMS de recordatorio sólo a riesgo medio y alto, y a los de riesgo alto les pide confirmar. |
| **Demanda semanal** por especialidad | Regresión lineal (tendencia + estacionalidad anual) | El admin ve la ocupación estimada de las próximas 4 semanas contra los cupos disponibles, para abrir o cerrar bloques de agenda. |

### Cómo se integra

- **Por eventos.** El ml-service escucha `cita.reservada` y `cita.confirmada` en RabbitMQ, calcula el riesgo y lo guarda en `dbo.PrediccionesCita`. La reserva nunca espera al modelo.
- **Por HTTP**, sólo desde la API Core (`/api/ml/...`), que aplica autenticación, roles y auditoría. El ml-service no se publica hacia afuera y exige `X-Api-Key`.
- **Si Python está caído**, nada se rompe: reservar, pagar y admitir siguen funcionando, la agenda muestra "—" en el riesgo y el Worker envía SMS a todos.
- La predicción vive en una **tabla aparte** de `Citas` para no tocar su `ROWVERSION` y no provocar conflictos de concurrencia en admisión.

### Variables del modelo de inasistencia

Días de anticipación de la reserva, franja horaria, día de la semana, canal, si pagó online, especialidad, sede, edad, citas previas y tasa de inasistencia previa (suavizada para pacientes nuevos). La misma consulta SQL y la misma función de preparación se usan para entrenar y para predecir.

**Decisión ética:** no se usan el tipo de previsión ni el tramo Fonasa, porque son proxies de nivel socioeconómico. El riesgo sólo sirve para recordar más o gestionar sobrecupos, **nunca para negar una hora**.

### Evaluación

- **Validación temporal:** entrena con el 80 % más antiguo y evalúa con el 20 % más reciente, como se usará en la realidad.
- **Métricas:** AUC, precisión y cobertura del nivel ALTO (el 20 % de citas más riesgosas). Con el historial sintético se obtiene AUC ≈ 0,74.
- **Explicable:** la página *Machine learning* del backoffice muestra los coeficientes y odds ratios, y cada predicción guarda las 3 variables que más subieron el riesgo.
- Cada entrenamiento queda registrado en `dbo.ModelosML` con sus métricas. Sólo uno está activo.

### Datos sintéticos

La base demo no trae historial, así que `scripts/generar_datos.py` simula 18 meses de citas pasadas (~20 mil) con patrones conocidos de inasistencia: más inasistencia con mucha anticipación, a primera hora y los lunes; mucho menos si se pagó por adelantado; y una propensión propia de cada paciente. El modelo tiene que **redescubrir** esos patrones. Los pacientes sintéticos usan correos `@sintetico.cl` y se borran con `--limpiar`. El panel avisa cuando un modelo se entrenó con datos sintéticos.

### Ejecutar en local

```powershell
cd ml-service
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements-dev.txt

# ML_DB y ML_API_KEY quedan en ml-service/.env al ejecutar scripts/configurar-secretos.ps1
# (driver por defecto: "ODBC Driver 18 for SQL Server"; cámbielo con -DriverOdbc)

python -m scripts.generar_datos      # historial sintético (una vez)
python -m scripts.entrenar           # entrena y deja el modelo activo
uvicorn app.main:app --port 5090     # API + consumidor de RabbitMQ (8000 suele estar reservado en Windows)
python -m pytest -q                  # pruebas (no necesitan base de datos)
ruff check .                         # lint y reglas de seguridad (bandit)
```

Para ver qué drivers ODBC tiene instalados: `Get-OdbcDriver | Select Name` en PowerShell. Antes, ejecute `db/03_ml.sql` en SSMS.

## Levantar con Docker

```powershell
pwsh scripts/configurar-secretos.ps1   # o: powershell -File scripts\configurar-secretos.ps1
docker compose up --build
```

`docker compose` se niega a arrancar si falta algún secreto en `.env`; no hay valores por defecto.

- Portal: http://localhost
- Backoffice: http://localhost/backoffice/
- RabbitMQ: http://localhost:15672 (usuario `reservamed`, clave `RABBIT_PASS` del `.env`)
- SQL Server: `localhost,1433` (usuario `sa`, clave del `.env`)

La primera vez, el contenedor `db-init` crea la base y carga los datos demo, y el `ml-service` genera el historial sintético y entrena el primer modelo (tarda uno o dos minutos).

## Levantar en local (Visual Studio + Node)

0. **Secretos (una vez):** desde la raíz ejecute `powershell -ExecutionPolicy Bypass -File scripts\configurar-secretos.ps1`. Genera claves aleatorias y las guarda en *user-secrets* de .NET (`Jwt:Key`, `Ml:ApiKey`), en `ml-service/.env` y en `.env`. Ninguno de esos archivos se sube a git. Sin este paso la API no arranca y le dice qué falta.
1. **Base de datos:** ejecute `db/01_schema.sql`, `db/02_seed.sql` y `db/03_ml.sql` en SSMS contra su instancia local. La cadena de conexión por defecto usa `Server=localhost;Trusted_Connection=True`.
2. **RabbitMQ (opcional):** `docker run -d -p 5672:5672 -p 15672:15672 rabbitmq:4-management-alpine`. Sin RabbitMQ todo funciona igual; sólo no salen notificaciones hasta que el broker esté disponible.
3. **API Core:** abra `api-core/ReservaMed.slnx` en Visual Studio 2026 y ejecute `ReservaMed.Api` (http://localhost:5080). Opcionalmente ejecute también `ReservaMed.Worker`.
4. **BFF:** `cd bff && npm install && npm run build && npm start` (puerto 3000).
5. **Portal:** `cd portal && npm install && npm run dev` → http://localhost:5173
6. **Backoffice:** `cd backoffice && npm install && npm run dev` → http://localhost:5174/backoffice/
7. **ML (opcional):** ver la sección *Machine learning → Ejecutar en local*. Sin él, todo funciona y el riesgo aparece como "—".

## Calidad y seguridad

Cada push y pull request ejecuta `.github/workflows/ci.yml`:

| Job | Qué verifica | Bloquea |
|---|---|---|
| `dotnet` | compila la solución, pruebas unitarias e integración (SQL Server real con Testcontainers), cobertura | sí |
| `node` | `tsc`/build y Vitest de BFF, portal y backoffice | sí |
| `python` | ruff (incluye reglas bandit) y pytest con cobertura | sí |
| `secretos` | `configurar-secretos.ps1` genera `.env` válidos y distintos en cada ejecución | sí |
| `gitleaks` | ningún secreto en el historial de git | sí |
| `seguridad` | Trivy, Semgrep, `npm audit`, `pip-audit`, paquetes NuGet vulnerables | informativo en F0 |

Para correr las pruebas en local (opcional): `dotnet test api-core/ReservaMed.slnx` (las de integración necesitan Docker), `npm test` en cada proyecto Node y `python -m pytest` en `ml-service`.

## Usuarios demo (clave `Demo1234!`)

| Usuario | Rol | Para probar |
|---|---|---|
| `ignacio@demo.cl` | Paciente | Colmena, **carga** de su madre (afiliado distinto del beneficiario) |
| `martina@demo.cl` | Paciente | Fonasa tramo C (MLE) |
| `benjamin@demo.cl` | Paciente | Fonasa tramo A → rechazo en la valorización |
| `sofia@demo.cl` | Paciente | Particular |
| `recepcion@reservamed.cl` | Recepción | Sede Providencia |
| `recepcion.lc@reservamed.cl` | Recepción | Sede Las Condes |
| `dra.soto@reservamed.cl` | Médico | Gastroenterología, ve sólo su agenda |
| `admin@reservamed.cl` | Admin | Todo + auditoría + bloqueos de agenda |

**Pruebas rápidas**

- Ignacio reserva Gastroenterología en Providencia: valor $36.110, bonificación $16.350, copago $19.760.
- En el POS o en Webpay, una tarjeta terminada en `0000` es rechazada.
- Reserve con pago online y no pague: a los 10 minutos la hora vuelve a quedar libre.
- Abra la misma hora en dos navegadores y confirme en ambos: el segundo recibe un 409 porque el cupo ya fue tomado (el índice único filtrado protege incluso si ambos confirman en el mismo milisegundo).

## Endpoints principales (API Core)

| Método | Ruta | Rol |
|---|---|---|
| POST | `/api/auth/login`, `/api/auth/registro` | público |
| GET | `/api/catalogo/{especialidades,sedes,profesionales,financiadores}` | público |
| GET | `/api/agenda/disponibilidad` | público |
| GET | `/api/agenda/dia` | recepción, médico, admin |
| POST | `/api/agenda/bloqueos` | admin |
| POST | `/api/citas` · GET `/api/citas/mias` · POST `/api/citas/{id}/cancelar` | paciente / recepción |
| POST | `/api/citas/{id}/atendida`, `/no-asistio` | médico |
| GET | `/api/admision/pacientes/{rut}` | recepción |
| POST | `/api/admision/citas/{id}/{valorizar,bono,cobrar,admitir}` | recepción |
| POST | `/api/pagos/webpay/{iniciar,confirmar}` | paciente |
| GET | `/fhir/{Patient,Practitioner,Appointment}/{id}` | personal clínico |
| GET | `/api/auditoria` | admin |
| GET | `/api/ml/modelo`, `/api/ml/modelos`, `/api/ml/demanda` · POST `/api/ml/modelo/entrenar` | admin |
| GET | `/api/ml/citas/{id}/riesgo` | personal clínico |

En desarrollo, el documento OpenAPI está en `/openapi/v1.json`.

## Pendiente / ideas para extender

- Tests de integración (Testcontainers con SQL Server y RabbitMQ), sobre todo la concurrencia de reservas.
- Sobrecupos y lista de espera cuando se libera una hora.
- Boleta electrónica (SII) del copago.
- Token en cookie `httpOnly` en lugar de `localStorage`.
- Validar el recurso FHIR contra el perfil oficial HL7 Chile CL Core.
- Sobrecupos automáticos en horarios con varias citas de riesgo alto.
- Calibración de probabilidades y monitoreo de deriva del modelo con datos reales.
