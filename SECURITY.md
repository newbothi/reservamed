# Seguridad

ReservaMed es un proyecto de portafolio: todos los datos son ficticios y las cuentas demo
(clave `Demo1234!`) solo existen en la instalación local que crea `docker compose`.
Ningún secreto vive en el repositorio: cada instalación genera los suyos con
`scripts/configurar-secretos.ps1` (en `.env` y `secretos/`, ambos fuera de git).

## Reportar una vulnerabilidad

Use **Security → Report a vulnerability** en este repositorio (reporte privado de GitHub).
No abra un issue público con los detalles. Respondo dentro de 7 días.

## Cómo se revisa

- Estudio de seguridad de la versión 1 y reconstrucción en seis fases: [`CHANGELOG.md`](CHANGELOG.md).
- Autoevaluación OWASP ASVS 4.0.3 nivel 2: [`docs/asvs-nivel-2.md`](docs/asvs-nivel-2.md).
- Cada cambio pasa por CI: gitleaks sobre todo el historial, Semgrep, Trivy, `npm audit`,
  `pip-audit`, paquetes NuGet vulnerables, ZAP, pruebas de autorización por rol y E2E.
