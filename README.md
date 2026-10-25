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
              ▼                     ▼
      BFF NestJS  (/bff)     (/bff/bo: login con TOTP + proxy)
              │   sesión en cookie HttpOnly sellada
              └──────────┬──────────┘
                         ▼
               API Core ASP.NET Core (interna: /api, /fhir)
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
| `bff/` | Backend for Frontend en NestJS + Zod: sesión de portal y backoffice, CSRF, prueba anti-bots |
| `ml-service/` | Machine learning en Python: FastAPI, scikit-learn, consumidor RabbitMQ, datos sintéticos |
| `portal/` | Portal de pacientes (React + TS + Vite) |
| `backoffice/` | Recepción, médicos y administración (React + TS + Vite) |
| `nginx/` | Proxy inverso y servidor de los frontends |

### Por qué cada pieza

- **API Core como monolito modular.** Agenda, citas, admisión y pagos comparten transacciones (reservar un cupo, emitir bono y cobrar deben ser consistentes). Separarlos en microservicios agregaría coordinación distribuida sin beneficio a esta escala.
- **BFF como única puerta.** Adapta los datos para el portal (agrupa cupos por día y profesional, calcula `puedeAnular` / `puedePagar`), cachea catálogos y valida con Zod. Desde la fase 2 de seguridad también guarda la sesión de portal y backoffice en una cookie `HttpOnly` sellada: el navegador nunca ve un token, y la API Core ya no se publica.
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

Requiere [mkcert](https://github.com/FiloSottile/mkcert) para el HTTPS local (`winget install FiloSottile.mkcert`).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\generar-certificados.ps1   # una vez: CA local + certificados
powershell -ExecutionPolicy Bypass -File scripts\configurar-secretos.ps1    # una vez: claves aleatorias (.env y secretos\)
docker compose up --build
```

- Portal: https://reservamed.localhost
- Backoffice: https://reservamed.localhost/backoffice/

`docker compose` se niega a arrancar si falta un secreto en `.env` o en `secretos\`, y NGINX no arranca sin certificados.
Si viene de una versión anterior, vuelva a ejecutar los dos scripts: reutilizan sus claves y certificados
y solo agregan lo nuevo (certificados internos y la carpeta `secretos\`).
La primera vez, `db-init` crea la base y los usuarios por servicio, y `ml-bootstrap` genera el historial
sintético y entrena el primer modelo (uno o dos minutos).

**Qué cambia respecto de la versión 1 (fase 1 de seguridad):**

| | |
|---|---|
| HTTPS | TLS 1.2/1.3, `http://` redirige a `https://`, HSTS y CSP estricta |
| Pago online | Webpay Plus **real, ambiente de integración** de Transbank (requiere Internet). Tarjeta de prueba `4051 8856 0044 6623`, CVV `123`, RUT `11.111.111-1`, clave `123`. El Webpay simulado solo existe en desarrollo local |
| Red | SQL Server y RabbitMQ están en una red interna, sin puertos en su PC |
| Usuarios | Cada servicio entra a SQL Server y RabbitMQ con su propio usuario y permisos mínimos; `sa` solo lo usa `db-init` |
| RabbitMQ | Solo AMQPS (TLS) en el puerto 5671 |
| Contenedores | Sin root, sistema de archivos de solo lectura, sin capacidades, imágenes fijadas por digest |

**Fase 2 de seguridad (identidad y sesión):**

| | |
|---|---|
| Sesión | Tokens solo en el BFF, dentro de una cookie `__Host-` HttpOnly, Secure y SameSite=Strict. Token de acceso de 15 minutos con *refresh* rotativo; cerrar sesión o desactivar un usuario revoca la sesión en el acto |
| Personal | Contraseña + código TOTP (Google/Microsoft Authenticator, 1Password…). **El primer ingreso de cada cuenta muestra un QR para enrolar el autenticador.** Un administrador puede reiniciarlo desde la API (`/api/usuarios/{id}/restablecer-totp`) |
| Bloqueo | 5 intentos fallidos bloquean la cuenta 1, 2, 4… hasta 60 minutos, además del límite por IP |
| Registro | Contraseña de 12+ caracteres que no esté filtrada (Have I Been Pwned, k-anonimato) ni contenga su nombre o correo; activación por correo; respuesta idéntica exista o no la cuenta; prueba anti-bots propia (sin Google ni Cloudflare) en registro y reservas; máximo 2 reservas sin pagar |
| Correo | Los correos de prueba llegan a **Mailpit**: http://127.0.0.1:8025 (solo desde este PC) |

**Fase 3 de seguridad (autorización y dominio):**

| | |
|---|---|
| Acceso por objeto | Una sola política decide quién ve cada cita y paciente: recepción solo su sede (aunque cambie `sedeId`), el médico solo sus citas y los pacientes que atiende (también en FHIR y en el riesgo de ML), el paciente solo lo suyo. Lo ajeno responde igual que lo inexistente |
| Huella | El bono ya no se emite porque el navegador diga "huella verificada": la API entrega un desafío de un solo uso, el **lector de la sede firma la lectura** y la API valida firma, sede, cita, RUT y hora. En esta demo el lector es un simulador del BFF (botones *Leer huella* / *Simular fallo*) |
| Webpay | La confirmación compara monto, orden de compra y estado con lo registrado; si no coincide, reversa. Confirmar dos veces (doble clic, recarga) cobra y confirma una sola vez |
| Validación | Cada solicitud se valida antes de la lógica (FluentValidation): datos inválidos responden 400 con el campo, nunca un error 500 |
| Reservas | La regla "una hora por día con el mismo profesional" se aplica de forma atómica, también con reservas simultáneas |

**Fase 4 de seguridad (datos y privacidad):**

| | |
|---|---|
| Eventos y logs | Los eventos (Outbox y RabbitMQ) llevan solo el id de la cita; el Worker consulta lo necesario con permisos por columna. Logs y registro de envíos con correo y teléfono enmascarados; NGINX no guarda query strings; el RUT se busca por POST, nunca en la URL. Retención: notificaciones 90 días, Outbox 7 días |
| Auditoría | Solo inserción (permisos + trigger) y cadena de hashes SHA-256: `GET /api/auditoria/integridad` detecta filas alteradas o borradas. Si la auditoría falla, log crítico y `/health` en *Unhealthy*. También se auditan lecturas y accesos denegados |
| Base de datos | TDE (AES-256) y enmascaramiento dinámico del RUT y la fecha de nacimiento para las cuentas no clínicas. Respaldo cifrado: `scripts\respaldar-base.ps1` (guarde también `SQL_TDE_PASS`) |
| ml-service | API key comparada en tiempo constante, modelos firmados (HMAC) antes de cargarlos y sin `/docs` en producción |
| ASVS | Autoevaluación OWASP ASVS 4.0.3 nivel 2 en [`docs/asvs-nivel-2.md`](docs/asvs-nivel-2.md); las brechas quedan asignadas a la fase 5 |

**Fase 5a de seguridad (cierre del checklist ASVS nivel 2):**

| | |
|---|---|
| Mi cuenta | Portal y backoffice: cambiar la contraseña (pide la actual, avisa por correo y cierra las demás sesiones), ver y cerrar sesiones abiertas, botón para ver la contraseña. Las contraseñas largas cuentan completas (antes BCrypt cortaba a 72 bytes) |
| Datos del paciente | Política de privacidad aceptada al registrarse; descarga de todos sus datos y de quién accedió a su ficha; solicitud de cierre de cuenta (la ficha clínica se conserva el plazo legal) |
| Personal | Administrar usuarios pide de nuevo el código TOTP (válido 5 minutos en esa sesión). Pantallas **Usuarios** y **Seguridad** en el backoffice |
| Alertas | Fuerza bruta, cuentas probadas desde una IP, bloqueos masivos, accesos denegados y consulta masiva de pacientes generan alertas (umbrales en `Alertas` de `appsettings.json`) con correo a los administradores |
| TLS interno | NGINX → BFF → API, API → ml-service, SQL Server (cifrado obligatorio, certificado fijado) y la consola de RabbitMQ van por TLS con la CA local |
| Secretos | Docker secrets (`secretos\`, en `.gitignore`): las claves ya no están en variables de entorno ni en `docker inspect` |
| SBOM | Inventario CycloneDX de cada imagen en CI |
| ASVS | 145 requisitos: 136 cumple, 8 no aplica, 1 riesgo aceptado (HSM, documentado), 0 brechas |

**Fase 5b (calidad y release v2.0):**

| | |
|---|---|
| Seguridad en CI | Bloquea el merge: Semgrep (SAST), Trivy (dependencias, Dockerfiles, IaC y secretos), `npm audit`, `pip-audit` y paquetes NuGet vulnerables. Las acciones de GitHub están fijadas por SHA |
| E2E | Playwright (`e2e/`): reserva y anulación en el portal, Mi cuenta, privacidad, ingreso con TOTP y administración de usuarios en el backoffice |
| Accesibilidad | axe-core en las pantallas principales: WCAG 2.1 AA sin violaciones serias ni críticas |
| Carga | k6 (`k6/carga.js`): búsqueda de horas a ritmo constante con p95 < 800 ms y < 1 % de errores; una ráfaga debe topar el límite por IP de NGINX |
| Machine learning | Un modelo nuevo solo se activa si supera la calidad mínima (AUC y calibración); revisión de equidad por sexo, previsión, edad y sede en la [ficha del modelo](docs/ml/ficha-modelo.md) |
| Cambios | Resumen de las seis fases en [`CHANGELOG.md`](CHANGELOG.md) |

La **matriz rol × endpoint** (cada endpoint con anónimo, dos pacientes, recepción de cada sede, médico y admin) corre en CI y falla si aparece un endpoint sin caso.

Verificación en su PC: `powershell -ExecutionPolicy Bypass -File scripts\verificar-fase2.ps1`, `scripts\verificar-fase3.ps1`, `scripts\verificar-fase4.ps1`, `scripts\verificar-fase5a.ps1` y `scripts\verificar-fase5b.ps1` (esta última necesita Node.js 22 para las pruebas E2E) (crea un paciente ficticio, reservas que anula al final, y usa la cuenta `qa@reservamed.cl`, cuyo autenticador guarda en `certs\qa-totp.txt`).

Para inspeccionar la base o la consola de RabbitMQ (solo en `127.0.0.1`):

```powershell
docker compose -f docker-compose.yml -f docker-compose.depuracion.yml up -d
```

SQL Server en `localhost,1433` (usuario `sa`, clave `SA_PASSWORD` del `.env`); RabbitMQ en https://localhost:15671
(usuario `reservamed`, clave `RABBIT_PASS`).

## Levantar en local (Visual Studio + Node)

0. **Secretos (una vez):** desde la raíz ejecute `powershell -ExecutionPolicy Bypass -File scripts\configurar-secretos.ps1`. Genera claves aleatorias y las guarda en *user-secrets* de .NET (`Jwt:Key`, `Ml:ApiKey`, `Seguridad:TotpClave`, `Huella:Clave`), en `ml-service/.env`, `bff/.env` y `.env`. Si lo había ejecutado en una fase anterior, vuelva a ejecutarlo (conserva las claves existentes y agrega las nuevas). Ninguno de esos archivos se sube a git. Sin este paso la API no arranca y le dice qué falta.
1. **Base de datos:** ejecute `db/01_schema.sql`, `db/02_seed.sql`, `db/03_ml.sql` y `db/05_identidad.sql` y `db/06_qa_roles.sql` en SSMS contra su instancia local (ambos se pueden ejecutar sobre una base existente). En local los correos de activación no se envían: el enlace aparece en el registro de la API. La cadena de conexión por defecto usa `Server=localhost;Trusted_Connection=True`.
2. **RabbitMQ (opcional):** `docker run -d -p 5672:5672 -p 15672:15672 rabbitmq:4-management-alpine`. Sin RabbitMQ todo funciona igual; sólo no salen notificaciones hasta que el broker esté disponible.
3. **API Core:** abra `api-core/ReservaMed.slnx` en Visual Studio 2026 y ejecute `ReservaMed.Api` (http://localhost:5080). Opcionalmente ejecute también `ReservaMed.Worker`.
4. **BFF:** `cd bff && npm install && npm run build && npm start` (puerto 3000; lee `bff/.env`). Portal y backoffice en desarrollo también pasan por él.
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
| `borde` | levanta el compose completo con HTTPS; verifica TLS, cabeceras, puertos cerrados, usuarios por servicio y contenedores sin root; `verificar-fase2.ps1` (sesión, TOTP, CSRF, registro con Mailpit, IP real en la auditoría y que una cookie robada o revocada no sirva); ZAP baseline sin alertas altas; Trivy sin vulnerabilidades críticas | sí |
| `seguridad` | Trivy del repositorio, Semgrep, `npm audit`, `pip-audit`, paquetes NuGet vulnerables | informativo hasta F5 |

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

Las cuentas del personal piden enrolar un autenticador TOTP en su primer ingreso al backoffice.
`qa@reservamed.cl`, `qa.recepcion@reservamed.cl`, `qa.recepcion.lc@reservamed.cl` y `qa.medico@reservamed.cl` son cuentas reservadas a las verificaciones automáticas (enrolan su propio TOTP).

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
| POST | `/api/cuenta/clave` · GET `/api/cuenta/sesiones` · POST `/api/cuenta/sesiones/{id}/cerrar` | cualquier usuario |
| POST | `/api/cuenta/reautenticar` | personal |
| GET | `/api/cuenta/datos` · POST `/api/cuenta/eliminacion` | paciente |
| GET | `/api/usuarios` · POST `/api/usuarios/{id}/{desactivar,activar,restablecer-totp}` (con TOTP reciente) | admin |
| GET | `/api/seguridad/alertas` · POST `/api/seguridad/alertas/{id}/revisada` | admin |

En desarrollo, el documento OpenAPI está en `/openapi/v1.json`.

## Pendiente / ideas para extender

- Tests de integración (Testcontainers con SQL Server y RabbitMQ), sobre todo la concurrencia de reservas.
- Sobrecupos y lista de espera cuando se libera una hora.
- Boleta electrónica (SII) del copago.
- Validar el recurso FHIR contra el perfil oficial HL7 Chile CL Core.
- Sobrecupos automáticos en horarios con varias citas de riesgo alto.
- Calibración de probabilidades y monitoreo de deriva del modelo con datos reales.
