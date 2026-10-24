"""
Configuración por variables de entorno (mismos nombres en Docker y en local).

En local, si existe ml-service/.env (lo genera scripts/configurar-secretos.ps1 y está en
.gitignore), se carga antes de leer las variables. Las variables ya definidas en el
entorno tienen prioridad.

Secretos (hallazgo C1): ML_API_KEY no tiene valor por defecto. Sin ella, el servicio
rechaza todas las peticiones HTTP (falla cerrado).
"""
import os
from dataclasses import dataclass, field
from pathlib import Path

VALORES_COMPROMETIDOS = frozenset({
    "CAMBIAR-esta-clave-de-desarrollo-ReservaMed-2026-minimo-32-bytes",
    "dev-ml-key-cambiar",
    "ReservaMed_2026!",
})
LARGO_MINIMO_API_KEY = 24


def cargar_env(ruta: Path) -> None:
    """Lector mínimo de archivos .env (CLAVE=valor). No sobrescribe variables existentes."""
    if not ruta.is_file():
        return
    for linea in ruta.read_text(encoding="utf-8").splitlines():
        linea = linea.strip()
        if not linea or linea.startswith("#") or "=" not in linea:
            continue
        clave, valor = linea.split("=", 1)
        os.environ.setdefault(clave.strip(), valor.strip().strip('"').strip("'"))


cargar_env(Path(__file__).resolve().parent.parent / ".env")


def _bool(nombre: str, defecto: str) -> bool:
    return os.getenv(nombre, defecto).strip().lower() in ("1", "true", "si", "yes")


def api_key_valida(clave: str) -> bool:
    return (
        len(clave) >= LARGO_MINIMO_API_KEY
        and clave not in VALORES_COMPROMETIDOS
        and not clave.upper().startswith("CAMBIAR")
    )


@dataclass(frozen=True)
class Config:
    # En Windows local normalmente es "ODBC Driver 17 for SQL Server" o 18 (ver README).
    db: str = field(default_factory=lambda: os.getenv(
        "ML_DB",
        "DRIVER={ODBC Driver 18 for SQL Server};SERVER=localhost;DATABASE=ReservaMed;"
        "Trusted_Connection=yes;TrustServerCertificate=yes",
    ))
    rabbit_url: str = field(default_factory=lambda: os.getenv("ML_RABBIT_URL", "amqp://guest:guest@localhost/"))
    # M9: con amqps:// se valida el certificado del broker contra esta CA (certs/ca.pem).
    rabbit_ca: str = field(default_factory=lambda: os.getenv("ML_RABBIT_CA", ""))
    api_key: str = field(default_factory=lambda: os.getenv("ML_API_KEY", ""))
    carpeta_modelos: str = field(default_factory=lambda: os.getenv("ML_MODELOS", "modelos"))
    # Al iniciar: si no hay historial genera datos sintéticos y entrena (útil para la demo en Docker).
    bootstrap: bool = field(default_factory=lambda: _bool("ML_BOOTSTRAP", "false"))
    consumir_eventos: bool = field(default_factory=lambda: _bool("ML_CONSUMIR_EVENTOS", "true"))
    zona: str = "America/Santiago"
    # B2/M5: la documentación interactiva (/docs, /openapi.json) solo existe en desarrollo.
    entorno: str = field(default_factory=lambda: os.getenv("ML_ENTORNO", "produccion").strip().lower())

    @property
    def desarrollo(self) -> bool:
        return self.entorno in ("desarrollo", "development", "dev")


config = Config()
