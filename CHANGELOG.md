# Cambios

## v2.0.0 — reconstrucción de seguridad

La versión 2 rehace ReservaMed a partir del estudio de seguridad de la versión 1: 27 hallazgos
(5 críticos, 9 altos, 9 medios y 4 bajos), corregidos y verificados en seis fases. Cada fase tuvo
un gate automático en CI y una verificación en el PC (`scripts/verificar-fase*.ps1`).

### F0 — Base y CI
- **C1** Ningún secreto en el código ni en el repositorio: claves aleatorias por instalación
  (`scripts/configurar-secretos.ps1`) y la API no arranca con valores publicados.
- CI con compilación, pruebas, cobertura, gitleaks sobre todo el historial y Dependabot.

### F1 — Borde e infraestructura
- **C4** HTTPS con TLS 1.2/1.3, HSTS y redirección; **M8** CSP estricta y cabeceras de seguridad.
- **C2** Webpay real (integración de Transbank); el simulador solo existe en desarrollo.
- **A4** Solo NGINX y el BFF informan la IP del cliente; **A5** red interna sin puertos y un usuario
  por servicio en SQL Server y RabbitMQ; **M9** AMQPS; **M4** contenedores sin root, de solo lectura.

### F2 — Identidad y sesión
- **A1** Tokens solo en el BFF, en cookie `__Host-` sellada; refresh rotativo y revocación inmediata.
- **A2** TOTP obligatorio para el personal y bloqueo progresivo; **A7** activación por correo;
  **A8** prueba anti-bots propia; **M1 M2 B4** contraseñas largas y no filtradas, sin enumerar cuentas; **B1** redirección `?volver=` solo a rutas internas.

### F3 — Autorización y dominio
- **C3** Bono solo con lectura de huella firmada por el lector de la sede.
- **C5 A3** Política de acceso por objeto (médico, sede, paciente) y matriz rol × endpoint en CI.
- **A6** Webpay validado e idempotente; **M3** validación de entrada; **B3** reserva atómica.

### F4 — Datos y privacidad
- **A9** Eventos y logs sin datos personales; **M6** auditoría inmutable encadenada.
- **M7** TDE, enmascaramiento dinámico y respaldos cifrados; **M5 B2** ml-service endurecido y sin
  detalles internos; checklist OWASP ASVS 4.0.3 nivel 2.

### F5a — Cierre del checklist ASVS nivel 2 (18 brechas)
- Mi cuenta: cambio de contraseña con aviso y cierre de sesiones, sesiones abiertas, contraseña visible.
- Contraseñas sin truncar (SHA-384 + BCrypt), reautenticación TOTP para administrar usuarios.
- Derechos del paciente (Ley 21.719): política de privacidad, exportación de datos y cierre de cuenta.
- Alertas de seguridad con umbrales y correo; SBOM CycloneDX.
- TLS en todo el tráfico interno y Docker secrets. Resultado: 136 cumple, 8 no aplica, 1 riesgo aceptado.

### F5b — Calidad y release
- Job de seguridad **bloqueante**: Semgrep, Trivy, npm audit, pip-audit y paquetes NuGet vulnerables;
  dependencias actualizadas (zod 4, FastAPI/Starlette, ASP.NET 10.0.13) y acciones de CI fijadas por SHA.
- Pruebas de punta a punta con Playwright y accesibilidad WCAG 2.1 AA con axe.
- Prueba de carga con k6 (latencia p95, errores y límite por IP).
- Revisión del modelo de ML: calibración, gate de calidad para activar modelos y equidad por grupo
  ([ficha del modelo](docs/ml/ficha-modelo.md)).
