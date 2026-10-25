# ReservaMed — checklist OWASP ASVS 4.0.3, nivel 2

Autoevaluación al cierre de la fase 5a de seguridad (no es una auditoría de terceros).
Cada requisito tiene un estado y su evidencia en el repositorio:

- **Cumple**: implementado y, cuando se puede, probado automáticamente (CI o `scripts/verificar-fase*.ps1`).
- **No aplica**: la aplicación no tiene esa funcionalidad.
- **Riesgo aceptado**: no se cumple por una limitación del despliegue; se documenta la razón, la mitigación y lo que haría falta en producción.

`scripts/verificar-fase5a.ps1` comprueba que ningún requisito quede sin evaluar ni como brecha.

## V1 Arquitectura, diseño y modelado de amenazas

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V1.1.2 | Modelado de amenazas en el diseño | Cumple | Estudio de seguridad: modelo de amenazas (4 zonas de confianza, 11 componentes) |
| V1.2.1 | Cuentas de sistema operativo únicas y de bajo privilegio por componente | Cumple | Contenedores sin root, solo lectura y sin capacidades (M4, `verificar-fase1.ps1`) |
| V1.2.2 | Comunicaciones entre componentes autenticadas | Cumple | Usuario SQL y de RabbitMQ por servicio (A5), API key al ml-service, JWT con sesión a la API |
| V1.4.1 | Control de acceso en puntos confiables del servidor | Cumple | `PoliticaAcceso` en la API Core; el BFF y el navegador no deciden (F3) |
| V1.6.1 | Política de gestión de claves criptográficas | Cumple | `scripts/configurar-secretos.ps1`: claves aleatorias por instalación, fuera de git (C1) |
| V1.8.1 | Datos sensibles identificados y clasificados | Cumple | Estudio (Ley 19.628 / 20.584 / 21.719); columnas enmascaradas en `db/07_privacidad.sql` |
| V1.9.1 | Comunicación cifrada entre componentes | Cumple | F5: todo el tráfico interno va cifrado y verificado contra la CA local: NGINX → BFF → API (HTTPS), API → ml-service (HTTPS), SQL Server con `forceencryption` y certificado fijado, AMQPS y consola de RabbitMQ por TLS. `TlsInterno` impide arrancar la API sin esto; `verificar-fase5a.ps1` |

## V2 Autenticación

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V2.1.1 | Contraseñas de al menos 12 caracteres | Cumple | `PoliticaContrasenas` (M2), prueba de integración |
| V2.1.2 | Se permiten contraseñas de 64 caracteres o más | Cumple | Máximo 128 caracteres |
| V2.1.3 | Sin truncar la contraseña | Cumple | `Contrasenas`: SHA-384 de la contraseña completa antes de BCrypt (formato `$rm2$`); los hashes antiguos se migran al ingresar. Prueba `Contrasenas_largas_cuentan_completas_mas_alla_de_72_bytes` |
| V2.1.4 | Cualquier carácter Unicode imprimible | Cumple | Sin listas de caracteres permitidos |
| V2.1.5 | El usuario puede cambiar su contraseña | Cumple | `POST /api/cuenta/clave` y "Mi cuenta" en portal y backoffice (`CuentaTests`) |
| V2.1.6 | Cambio de contraseña exige la actual | Cumple | Exige la contraseña actual (422 `CLAVE_ACTUAL`, cuenta para el bloqueo) y la política completa para la nueva |
| V2.1.7 | Se rechazan contraseñas filtradas | Cumple | HIBP con k-anonimato (`VerificadorHibp`) |
| V2.1.8 | Medidor de fortaleza | Cumple | Portal: `fortaleza()` en el registro |
| V2.1.9 | Sin reglas de composición | Cumple | Solo largo, lista común, datos personales y filtraciones |
| V2.1.10 | Sin rotación periódica obligatoria | Cumple | No existe vencimiento de contraseñas |
| V2.1.11 | Se permite pegar y usar gestores de contraseñas | Cumple | Campos `autocomplete` correctos, sin bloquear pegar |
| V2.1.12 | Opción de ver la contraseña escrita | Cumple | `CampoClave`: botón Ver/Ocultar en login, registro y cambio de contraseña (portal y backoffice) |
| V2.2.1 | Controles contra automatización (fuerza bruta) | Cumple | Límite por IP en NGINX y API, bloqueo progresivo por cuenta (A2) |
| V2.2.2 | Autenticadores débiles (SMS, correo) solo como secundarios | Cumple | Personal con TOTP; el correo solo verifica la cuenta |
| V2.2.3 | Aviso al usuario cuando cambian sus credenciales | Cumple | Correo al cambiar la contraseña, al restablecer el TOTP y al solicitar la eliminación de la cuenta |
| V2.4.1 | Contraseñas con hash adaptativo y sal | Cumple | BCrypt (costo 11) |
| V2.4.2 | Sal de al menos 32 bits | Cumple | BCrypt genera sal de 128 bits |
| V2.5.1 | Recuperación de cuenta segura | No aplica | No hay recuperación en línea; se gestiona en la clínica |
| V2.7.1 | OTP por SMS u otro canal no seguro | No aplica | No se usan OTP fuera de banda |
| V2.8.1 | OTP con vida limitada | Cumple | TOTP de 30 s con tolerancia de ±1 paso |
| V2.8.3 | Algoritmos aprobados para OTP | Cumple | RFC 6238 HMAC, vectores oficiales en `IdentidadTests` |
| V2.8.4 | Un OTP se usa una sola vez | Cumple | `TotpUltimoPaso` (anti-repetición), prueba de integración |
| V2.8.5 | OTP reutilizado se registra y rechaza | Cumple | `TOTP_FALLIDO` en la auditoría y cuenta para el bloqueo |
| V2.8.6 | Autenticador perdido se puede revocar | Cumple | `POST /api/usuarios/{id}/restablecer-totp` (admin), revoca sesiones |
| V2.10.1 | Secretos de servicio no están en el código | Cumple | C1, gitleaks en CI |
| V2.10.2 | Sin credenciales por defecto en cuentas de servicio | Cumple | `configurar-secretos.ps1`; la app no arranca con valores publicados |
| V2.10.4 | Secretos guardados fuera del código fuente | Cumple | `.env`, user-secrets y `bff/.env` (en `.gitignore`) |

## V3 Sesiones

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V3.1.1 | Sin tokens de sesión en la URL | Cumple | Cookie `__Host-` sellada; A9 sin datos en URL |
| V3.2.1 | Token nuevo al autenticarse | Cumple | Sesión nueva en cada login (`ServicioSesiones.CrearAsync`) |
| V3.2.2 | Tokens con al menos 64 bits de entropía | Cumple | Refresh de 256 bits, sid GUID aleatorio |
| V3.2.3 | Tokens guardados de forma segura en el navegador | Cumple | Cookie HttpOnly sellada con AES-GCM; el navegador no ve tokens (F2) |
| V3.3.1 | Cerrar sesión invalida la sesión | Cumple | Gate F2: la copia de la cookie da 401 tras salir |
| V3.3.2 | Vida máxima de sesión | Cumple | Acceso 15 min, sesión máxima 12 h (personal) y 7 días (pacientes) |
| V3.3.3 | Cerrar las demás sesiones al cambiar credenciales | Cumple | Cambiar la contraseña revoca todas las demás sesiones (`RevocarTodasAsync` con `excepto`) |
| V3.3.4 | Ver y cerrar sesiones activas | Cumple | `GET /api/cuenta/sesiones` y cierre individual; solo las propias (404 para sesiones ajenas) |
| V3.4.1 | Cookie con atributo Secure | Cumple | `verificar-fase2.ps1` |
| V3.4.2 | Cookie HttpOnly | Cumple | `verificar-fase2.ps1` |
| V3.4.3 | Cookie SameSite | Cumple | SameSite=Strict |
| V3.4.4 | Prefijo `__Host-` | Cumple | `__Host-rm_p`, `__Host-rm_b`, `__Host-rm_2fa` |
| V3.4.5 | Path de la cookie acotado | Cumple | `Path=/` exigido por `__Host-` |
| V3.5.2 | Sin secretos estáticos como sesión | Cumple | Sesiones revocables, no API keys |
| V3.5.3 | Tokens sin estado firmados y validados | Cumple | JWT HS256 de 15 min + sid validado en cada petición |
| V3.7.1 | Reautenticación antes de acciones sensibles | Cumple | `[RequiereReautenticacion]`: administrar usuarios exige TOTP confirmado en los últimos 5 minutos en esa misma sesión (403 `REAUTENTICAR`) |

## V4 Control de acceso

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V4.1.1 | Reglas aplicadas en el servidor | Cumple | `[Authorize]` + `PoliticaAcceso` |
| V4.1.2 | Atributos de usuario no manipulables | Cumple | Sede y profesional salen del token (A3) |
| V4.1.3 | Mínimo privilegio | Cumple | Roles por función; usuarios SQL por servicio |
| V4.1.5 | Falla de forma segura | Cumple | Lo ajeno responde igual que lo inexistente (404 SIN_ACCESO) |
| V4.2.1 | Protección contra IDOR | Cumple | Matriz rol × endpoint al 100 % en CI (F3) |
| V4.2.2 | Protección CSRF | Cumple | Cookie SameSite=Strict + cabecera `X-ReservaMed` + Origin (F2) |
| V4.3.1 | MFA en interfaces administrativas | Cumple | TOTP obligatorio para todo el personal |
| V4.3.2 | Sin listado de directorios ni metadatos expuestos | Cumple | NGINX bloquea `/.` (`.git`, `.env`) |
| V4.3.3 | Separación de funciones | Cumple | Recepción, médico y admin con permisos distintos; recepción por sede |

## V5 Validación, sanitización y codificación

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V5.1.1 | Defensa contra contaminación de parámetros | Cumple | Model binding tipado de ASP.NET Core |
| V5.1.3 | Toda entrada validada con listas permitidas | Cumple | FluentValidation (M3) y Zod en el BFF |
| V5.1.4 | Datos estructurados tipados | Cumple | DTOs con tipos y enums |
| V5.2.1 | HTML no confiable sanitizado | Cumple | React escapa; sin `dangerouslySetInnerHTML` |
| V5.3.1 | Codificación de salida según contexto | Cumple | React y JSON |
| V5.3.3 | Protección contra XSS | Cumple | React + CSP estricta `script-src 'self'` |
| V5.3.4 | Consultas parametrizadas | Cumple | Dapper con parámetros en todas las consultas |
| V5.3.10 | Inyección XPath / XML | No aplica | No se procesa XML |
| V5.5.1 | Objetos serializados con integridad | Cumple | Cookies AES-GCM con AAD; modelos ML firmados (M5) |
| V5.5.3 | Sin deserializar datos no confiables | Cumple | Solo JSON; `joblib.load` solo tras verificar la firma |

## V6 Criptografía

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V6.1.1 | Datos personales regulados cifrados en reposo | Cumple | TDE AES-256 en ReservaMed (M7) |
| V6.1.2 | Datos de salud cifrados en reposo | Cumple | TDE; respaldos cifrados con certificado |
| V6.1.3 | Datos financieros cifrados en reposo | Cumple | TDE; solo se guardan los 4 últimos dígitos de tarjetas |
| V6.2.1 | Los módulos criptográficos fallan de forma segura | Cumple | AES-GCM autenticado, sin oráculos de relleno |
| V6.2.2 | Algoritmos probados | Cumple | AES-GCM, HMAC-SHA256, SHA-256, BCrypt (.NET y Node estándar) |
| V6.2.5 | Sin modos inseguros | Cumple | Sin ECB ni CBC sin MAC |
| V6.2.6 | Nonces únicos | Cumple | IV aleatorio de 96 bits por operación |
| V6.3.1 | Números aleatorios con generador seguro | Cumple | `RandomNumberGenerator` / `crypto.randomBytes` |
| V6.4.1 | Gestor de secretos (vault) | Cumple | Docker secrets (`secretos/`, archivos en `/run/secrets`): las claves de la API, Worker, BFF y ml-service ya no están en variables de entorno ni en `docker inspect`. Excepción documentada: la clave inicial de `sa` y del administrador de RabbitMQ (sus imágenes solo la aceptan por variable; ningún servicio de la app las usa) |
| V6.4.2 | El material de claves no se expone a la aplicación | Riesgo aceptado | Sin HSM ni vault externo en este despliegue local: la aplicación lee sus claves desde archivos de solo lectura. Mitigación: una clave por propósito y por instalación, contenedores sin shell ni root, TDE con certificado en SQL Server. En producción: Azure Key Vault con claves no exportables |

## V7 Errores y registro

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V7.1.1 | No se registran credenciales ni tokens | Cumple | NGINX sin query string; barrido en `verificar-fase4.ps1` |
| V7.1.2 | No se registran otros datos sensibles | Cumple | A9: logs enmascarados; barrido de RUT y correos (gate F4) |
| V7.1.3 | Se registran los eventos de seguridad | Cumple | Login, TOTP, bloqueos, accesos denegados, cambios de datos |
| V7.1.4 | Cada evento con contexto (quién, cuándo, desde dónde) | Cumple | Auditoría: usuario, rol, IP real (A4), fecha, recurso |
| V7.2.1 | Se registran todas las decisiones de autenticación | Cumple | LOGIN, LOGIN_FALLIDO, TOTP_FALLIDO, LOGIN_BLOQUEADO |
| V7.2.2 | Se registran las fallas de control de acceso | Cumple | ACCESO_DENEGADO por rol y por objeto (M6) |
| V7.3.1 | Registro protegido contra inyección | Cumple | Logging estructurado con plantillas |
| V7.3.3 | Registros de seguridad protegidos contra modificación | Cumple | Auditoría de solo inserción y hashes encadenados (M6) |
| V7.3.4 | Hora sincronizada | Cumple | Reloj del host; auditoría en hora de Chile |
| V7.4.1 | Mensajes de error genéricos | Cumple | ProblemDetails sin pila (B2, `verificar-fase4.ps1`) |
| V7.4.2 | Manejo de excepciones uniforme | Cumple | `ManejadorErrores` |
| V7.4.3 | Manejador de último recurso | Cumple | `UseExceptionHandler` |

## V8 Protección de datos

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V8.1.1 | Datos sensibles no quedan en cachés del servidor | Cumple | Sin cachés de respuestas con datos personales |
| V8.2.1 | Cabeceras anti-caché en datos sensibles | Cumple | BFF: `Cache-Control: no-store` |
| V8.2.2 | Sin datos sensibles en el almacenamiento del navegador | Cumple | Sin tokens ni datos personales en localStorage (F2) |
| V8.2.3 | Datos del cliente borrados al cerrar sesión | Cumple | Cookie eliminada y sesión revocada |
| V8.3.1 | Datos sensibles en el cuerpo, no en la URL | Cumple | Búsqueda por RUT con POST (A9) |
| V8.3.2 | El usuario puede exportar y borrar sus datos | Cumple | `GET /api/cuenta/datos` (JSON con datos, horas, bonos, pagos y accesos a la ficha) y `POST /api/cuenta/eliminacion` (cierra la cuenta, borra el contacto, ficha en retención legal). Quedan en `SolicitudesDatos` |
| V8.3.3 | Información clara sobre el tratamiento de datos | Cumple | Página `/privacidad` y aceptación obligatoria al registrarse (`Usuarios.PrivacidadAceptadaEn`, validado en BFF y API) |
| V8.3.4 | Datos sensibles identificados con política de uso | Cumple | Estudio de seguridad, sección de cumplimiento |
| V8.3.5 | Acceso a datos sensibles auditado | Cumple | VER_PACIENTE, VER_CITA, VER_RIESGO, FHIR_* (M6) |
| V8.3.6 | Datos sensibles borrados de la memoria | No aplica | Runtimes administrados (.NET, Node, Python) |
| V8.3.7 | Datos sensibles con cifrado autenticado | Cumple | Secretos TOTP y cookies con AES-GCM |
| V8.3.8 | Retención y borrado de datos | Cumple | Notificaciones 90 días, Outbox 7 días |

## V9 Comunicaciones

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V9.1.1 | TLS para toda conexión de clientes | Cumple | Solo HTTPS; HTTP redirige (C4) |
| V9.1.2 | Suites de cifrado fuertes | Cumple | Configuración Mozilla intermedia en NGINX |
| V9.1.3 | Solo TLS 1.2 y 1.3 | Cumple | `verificar-fase1.ps1` rechaza TLS 1.0/1.1 |
| V9.2.1 | Certificados confiables en conexiones de servidor | Cumple | Ningún cliente usa `TrustServerCertificate=True`: la API y el Worker fijan `certs/sqlserver.crt`; ml-service, BFF y NGINX validan contra `certs/ca.pem` |
| V9.2.2 | TLS en todas las conexiones internas, incluida la base | Cumple | Igual que V1.9.1; SQL Server rechaza conexiones sin cifrar (`infra/sqlserver/mssql.conf`) |
| V9.2.3 | Conexiones a sistemas externos autenticadas | Cumple | Transbank y HIBP por HTTPS validado |

## V10 Código malicioso

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V10.1.1 | Análisis de código | Cumple | Semgrep y Trivy del repositorio en CI (informativo hasta F5) |
| V10.3.1 | Actualizaciones automáticas con integridad | No aplica | No hay actualización automática |
| V10.3.2 | Integridad de recursos de terceros (SRI) | No aplica | Sin scripts de CDN; todo se sirve desde el propio sitio |

## V11 Lógica de negocio

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V11.1.1 | Pasos del negocio en orden | Cumple | Estados de la cita; bono antes de cobrar |
| V11.1.2 | Límites realistas por usuario | Cumple | Máximo 2 reservas sin pagar y 3 futuras (A8) |
| V11.1.4 | Controles contra automatización | Cumple | Prueba de trabajo en registro y reservas (A8) |
| V11.1.5 | Límites y validaciones del negocio | Cumple | Webpay: monto, orden y estado (A6) |
| V11.1.6 | Sin condiciones de carrera (TOCTOU) | Cumple | Reserva atómica (B3) y confirmación idempotente (A6) |
| V11.1.7 | Monitoreo de actividad inusual | Cumple | `ServicioAlertas`: reglas sobre la auditoría (fuerza bruta por IP, cuentas probadas, bloqueos masivos, accesos denegados, consulta masiva de pacientes); pantalla Seguridad del backoffice |
| V11.1.8 | Alertas configurables ante ataques automatizados | Cumple | Umbrales configurables (sección `Alertas`), una alerta por regla y ventana, correo a los administradores activos (`AlertasTests`) |

## V12 Archivos y recursos

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V12.1.1 | Tamaño máximo de archivos subidos | No aplica | No hay carga de archivos |
| V12.3.1 | Rutas de archivo desde datos del usuario | No aplica | No se arman rutas con datos del usuario |

## V13 API y servicios web

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V13.1.1 | Mismo parser y codificación en todos los componentes | Cumple | JSON UTF-8 de punta a punta |
| V13.1.3 | Las URL no exponen datos sensibles | Cumple | A9 |
| V13.1.4 | Autorización por URI y por recurso | Cumple | Matriz rol × endpoint (F3) |
| V13.1.5 | Se rechazan tipos de contenido inesperados | Cumple | `[ApiController]` responde 415 |
| V13.2.1 | Solo los métodos HTTP necesarios | Cumple | Rutas con verbo explícito; 405 en el resto |
| V13.2.2 | Validación de esquema JSON | Cumple | FluentValidation y Zod |
| V13.2.3 | APIs con cookie protegidas contra CSRF | Cumple | V4.2.2 |
| V13.2.5 | Se verifica el Content-Type | Cumple | `application/json` exigido |

## V14 Configuración

| ID | Requisito | Estado | Evidencia |
|---|---|---|---|
| V14.1.1 | Construcción segura y repetible | Cumple | CI con jobs bloqueantes y `docker compose` |
| V14.2.1 | Componentes actualizados | Cumple | Trivy, `npm audit`, `pip-audit`, NuGet vulnerables en CI |
| V14.2.2 | Funciones innecesarias eliminadas | Cumple | Sin OpenAPI ni `/docs` en producción (B2, M5) |
| V14.2.3 | Recursos locales, sin CDN | Cumple | Todo empaquetado por Vite |
| V14.2.4 | Componentes de fuentes confiables | Cumple | Imágenes fijadas por digest (M4) |
| V14.2.5 | Inventario de componentes (SBOM) | Cumple | SBOM CycloneDX de cada imagen y del código fuente en CI (artefacto `reportes-borde`, paso que falla si un SBOM viene vacío) |
| V14.3.2 | Sin modo depuración en producción | Cumple | `ASPNETCORE_ENVIRONMENT=Production`, Webpay simulado deshabilitado (C2) |
| V14.3.3 | Las cabeceras no exponen versiones | Cumple | Sin `Server` con versión ni `X-Powered-By` (M8) |
| V14.4.1 | Content-Type con charset | Cumple | JSON y HTML en UTF-8 |
| V14.4.3 | Content-Security-Policy | Cumple | `default-src 'self'` (M8) |
| V14.4.4 | X-Content-Type-Options: nosniff | Cumple | M8 |
| V14.4.5 | Strict-Transport-Security | Cumple | M8 |
| V14.4.6 | Referrer-Policy | Cumple | M8 |
| V14.4.7 | Protección contra framing | Cumple | `frame-ancestors 'none'` y `X-Frame-Options: DENY` |
| V14.5.1 | Solo métodos HTTP permitidos | Cumple | V13.2.1 |
| V14.5.3 | CORS con lista permitida | Cumple | Sin CORS en producción; solo desarrollo |
