"""Convierte reportes de ZAP y Trivy en anotaciones de GitHub y falla según el gate de F1.

  python scripts/ci/anotar.py zap reporte.json      → falla si hay alertas de riesgo Alto
  python scripts/ci/anotar.py trivy reporte.json     → falla si hay vulnerabilidades CRITICAL
"""
import json
import sys
from pathlib import Path

RIESGO = {"0": "Info", "1": "Bajo", "2": "Medio", "3": "Alto"}


def esc(texto: str) -> str:
    return texto.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


def zap(ruta: Path) -> int:
    datos = json.loads(ruta.read_text())
    altas = 0
    resumen = []
    for sitio in datos.get("site", []):
        for a in sitio.get("alerts", []):
            riesgo = RIESGO.get(str(a.get("riskcode")), "?")
            url = (a.get("instances") or [{}])[0].get("uri", "")
            linea = f"[{riesgo}] {a.get('alert')} ({a.get('pluginid')}) x{a.get('count')} — {url}"
            resumen.append(linea)
            if riesgo == "Alto":
                altas += 1
                print(f"::error title=ZAP alto: {esc(a.get('alert', ''))}::{esc(linea)}")
    titulo = f"ZAP {ruta.stem}: {altas} altas, {len(resumen)} alertas"
    print(f"::notice title={esc(titulo)}::{esc(chr(10).join(resumen) or 'Sin alertas')}")
    return 1 if altas else 0


def trivy(ruta: Path) -> int:
    datos = json.loads(ruta.read_text())
    criticas = []
    for r in datos.get("Results", []) or []:
        for v in r.get("Vulnerabilities", []) or []:
            if v.get("Severity") == "CRITICAL":
                criticas.append(f"{v.get('VulnerabilityID')} {v.get('PkgName')} {v.get('InstalledVersion')}→{v.get('FixedVersion')} ({r.get('Target')})")
    imagen = datos.get("ArtifactName", ruta.stem)
    for c in criticas[:8]:
        print(f"::error title=Trivy CRITICAL {esc(imagen)}::{esc(c)}")
    print(f"::notice title=Trivy {esc(imagen)}::{len(criticas)} vulnerabilidades críticas con corrección disponible")
    return 1 if criticas else 0


if __name__ == "__main__":
    tipo, *rutas = sys.argv[1:]
    fn = {"zap": zap, "trivy": trivy}[tipo]
    sys.exit(max((fn(Path(r)) for r in rutas), default=0))
