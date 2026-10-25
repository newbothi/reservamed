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
from urllib.parse import quote, urlsplit, urlunsplit

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


def cargar_secretos_archivo(carpeta: Path = Path("/run/secrets")) -> list[str]:
    """F5 (ASVS V6.4.1): Docker secrets. Cada archivo se llama como la variable; tiene prioridad."""
    if not carpeta.is_dir():
        return []
    cargados = []
    for archivo in sorted(carpeta.iterdir()):
        if archivo.is_file() and archivo.name.isupper() and archivo.name.replace("_", "").isalnum():
            os.environ[archivo.name] = archivo.read_text(encoding="utf-8").strip()
            cargados.append(archivo.name)
    return cargados


cargar_secretos_archivo()
cargar_env(Path(__file__).resolve().parent.parent / ".env")


def con_clave_odbc(cadena: str, clave: str) -> str:
    """Agrega PWD a la cadena ODBC (la contraseña llega aparte, como secreto). Llaves escapadas."""
    if not clave or "PWD=" in cadena.upper():
        return cadena
    return cadena.rstrip(";") + ";PWD={" + clave.replace("}", "}}") + "};"


def con_clave_amqp(url: str, clave: str) -> str:
    """Inserta la contraseña en amqps://usuario@host/vhost (la URL configurada no la incluye)."""
    if not clave:
        return url
    partes = urlsplit(url)
    if partes.password or not partes.username:
        return url
    host = partes.hostname + (f":{partes.port}" if partes.port else "")
    return urlunsplit((partes.scheme, f"{partes.username}:{quote(clave, safe='')}@{host}", partes.path, partes.query, partes.fragment))


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
    db: str = field(default_factory=lambda: con_clave_odbc(os.getenv(
        "ML_DB",
        "DRIVER={ODBC Driver 18 for SQL Server};SERVER=localhost;DATABASE=ReservaMed;"
        "Trusted_Connection=yes;TrustServerCertificate=yes",
    ), os.getenv("ML_DB_PASS", "")))
    rabbit_url: str = field(default_factory=lambda: con_clave_amqp(
        os.getenv("ML_RABBIT_URL", "amqp://guest:guest@localhost/"), os.getenv("ML_RABBIT_PASS", "")))
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
