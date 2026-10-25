"""Convierte reportes de ZAP y Trivy en anotaciones de GitHub y falla según el gate de F1.

  python scripts/ci/anotar.py zap reporte.json      → falla si hay alertas de riesgo Alto
  python scripts/ci/anotar.py trivy reporte.json     → falla si hay vulnerabilidades CRITICAL
  F5 (job de seguridad bloqueante):
  python scripts/ci/anotar.py semgrep semgrep.json   → falla con cualquier hallazgo (ERROR o WARNING)
  python scripts/ci/anotar.py trivyfs trivy-fs.json  → falla con vulnerabilidades, configuraciones o secretos HIGH/CRITICAL
  python scripts/ci/anotar.py npm audit-*.json       → falla con vulnerabilidades high/critical
  python scripts/ci/anotar.py pipaudit pip-audit.json
  python scripts/ci/anotar.py dotnet vulnerables.json (dotnet list package --vulnerable --format json)
"""
import json
import sys
from pathlib import Path

RIESGO = {"0": "Info", "1": "Bajo", "2": "Medio", "3": "Alto"}


def esc(texto: str) -> str:
    return texto.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


def zap(ruta: Path) -> int:
    if not ruta.is_file():
        print(f"::error title=ZAP::No se generó {ruta} (ver la anotación con la salida de ZAP)")
        return 1
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
    if not ruta.is_file():
        print(f"::error title=Trivy::No se generó {ruta}")
        return 1
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


def trx(ruta: Path) -> int:
    """Pruebas .NET fallidas (archivo .trx) como anotaciones: nombre, mensaje y primeras líneas de la pila."""
    import xml.etree.ElementTree as ET

    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    raiz = ET.parse(ruta).getroot()
    fallidas = 0
    for r in raiz.iterfind(".//t:UnitTestResult", ns):
        if r.get("outcome") != "Failed":
            continue
        fallidas += 1
        msg = r.findtext(".//t:ErrorInfo/t:Message", default="", namespaces=ns).strip()
        pila = r.findtext(".//t:ErrorInfo/t:StackTrace", default="", namespaces=ns).strip().splitlines()[:6]
        if fallidas <= 25:
            print(f"::error title=Prueba fallida: {esc(r.get('testName', '')[-120:])}::{esc(msg[:1500] + chr(10) + chr(10).join(pila))}")
    c = raiz.find(".//t:ResultSummary/t:Counters", ns)
    if c is not None:
        print(f"::notice title=Pruebas {ruta.stem}::{c.get('passed')} pasaron, {c.get('failed')} fallaron de {c.get('total')}")
    return 1 if fallidas else 0


def gitleaks(ruta: Path) -> int:
    """Hallazgos de gitleaks (reporte JSON con --redact): regla, archivo, línea y commit; nunca el secreto."""
    hallazgos = json.loads(ruta.read_text() or "[]") if ruta.exists() else []
    for h in hallazgos[:25]:
        print(
            f"::error file={h.get('File')},line={h.get('StartLine')},title=gitleaks {esc(h.get('RuleID', ''))}::"
            f"{esc(h.get('File', ''))}:{h.get('StartLine')} commit {str(h.get('Commit', ''))[:8]} ({esc(h.get('Description', ''))})"
        )
    return 1 if hallazgos else 0


def semgrep(ruta: Path) -> int:
    datos = json.loads(ruta.read_text()) if ruta.is_file() else {"results": [], "errors": [{"message": "sin reporte"}]}
    res = [r for r in datos.get("results", []) if r.get("extra", {}).get("severity") in ("ERROR", "WARNING")]
    for r in res[:40]:
        print(f"::error file={r['path']},line={r['start']['line']},title=Semgrep {esc(r['check_id'].split('.')[-1])}::"
              f"{esc(r['extra'].get('message', '')[:300])}")
    lista = [f"{r['path']}:{r['start']['line']} {r['check_id'].split('.')[-1]}" for r in res]
    print(f"::notice title=Semgrep::{len(res)} hallazgos, {len(datos.get('errors', []))} errores de análisis%0A{esc(chr(10).join(lista))}")
    return 1 if res else 0


def trivyfs(ruta: Path) -> int:
    datos = json.loads(ruta.read_text()) if ruta.is_file() else {}
    malos = []
    for r in datos.get("Results", []) or []:
        for v in r.get("Vulnerabilities", []) or []:
            malos.append(f"{v.get('Severity')} {v.get('VulnerabilityID')} {v.get('PkgName')} {v.get('InstalledVersion')}→{v.get('FixedVersion')} ({r.get('Target')})")
        for m in r.get("Misconfigurations", []) or []:
            if m.get("Status") == "FAIL":
                malos.append(f"{m.get('Severity')} {m.get('ID')} {m.get('Title')} ({r.get('Target')})")
        for x in r.get("Secrets", []) or []:
            malos.append(f"{x.get('Severity')} secreto {x.get('RuleID')} ({r.get('Target')}:{x.get('StartLine')})")
    for m in malos[:40]:
        print(f"::error title=Trivy fs::{esc(m)}")
    print(f"::notice title=Trivy fs::{len(malos)} hallazgos HIGH/CRITICAL" if ruta.is_file() else "::error title=Trivy fs::sin reporte")
    return 1 if malos or not ruta.is_file() else 0


def npm(ruta: Path) -> int:
    datos = json.loads(ruta.read_text() or "{}") if ruta.is_file() else {}
    malos = [f"{n} ({v.get('severity')}): {', '.join(str(x.get('title', x)) if isinstance(x, dict) else str(x) for x in v.get('via', []))[:200]}"
             for n, v in (datos.get("vulnerabilities") or {}).items() if v.get("severity") in ("high", "critical")]
    for m in malos:
        print(f"::error title=npm audit {esc(ruta.stem)}::{esc(m)}")
    print(f"::notice title=npm audit {esc(ruta.stem)}::{len(malos)} vulnerabilidades high/critical")
    return 1 if malos or not ruta.is_file() else 0


def pipaudit(ruta: Path) -> int:
    datos = json.loads(ruta.read_text()) if ruta.is_file() else {"dependencies": []}
    malos = [f"{d['name']} {d['version']}: {v['id']} → {', '.join(v.get('fix_versions', []))}"
             for d in datos.get("dependencies", []) for v in d.get("vulns", [])]
    for m in malos:
        print(f"::error title=pip-audit::{esc(m)}")
    print(f"::notice title=pip-audit::{len(malos)} vulnerabilidades")
    return 1 if malos or not ruta.is_file() else 0


def dotnet(ruta: Path) -> int:
    datos = json.loads(ruta.read_text()) if ruta.is_file() else {}
    malos = []
    for p in datos.get("projects", []):
        for fw in p.get("frameworks", []):
            for pkg in fw.get("topLevelPackages", []) + fw.get("transitivePackages", []):
                for v in pkg.get("vulnerabilities", []):
                    malos.append(f"{Path(p.get('path', '')).name}: {pkg.get('id')} {pkg.get('resolvedVersion')} ({v.get('severity')}) {v.get('advisoryurl')}")
    for m in sorted(set(malos)):
        print(f"::error title=NuGet vulnerable::{esc(m)}")
    print(f"::notice title=NuGet::{len(set(malos))} paquetes vulnerables%0A{esc(chr(10).join(sorted(set(malos))))}")
    return 1 if malos or not ruta.is_file() else 0


if __name__ == "__main__":
    tipo, *rutas = sys.argv[1:]
    fn = {"zap": zap, "trivy": trivy, "trx": trx, "gitleaks": gitleaks, "semgrep": semgrep, "trivyfs": trivyfs,
          "npm": npm, "pipaudit": pipaudit, "dotnet": dotnet}[tipo]
    sys.exit(max((fn(Path(r)) for r in rutas), default=0))
