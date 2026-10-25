"""
F5b: genera docs/ml/revision-modelo.md con el desempeño y la equidad del modelo por grupo,
usando el historial SINTÉTICO (mismo simulador que la demo). Sin base de datos.
    python -m scripts.revision_modelo
"""
from __future__ import annotations

from pathlib import Path

from app import modelo
from app.revision import disparidad, evaluar
from tests.test_modelo import dataset_con_grupos

GRUPOS = {"Sexo": "Sexo", "Prevision": "Previsión (no es variable del modelo)", "TramoEdad": "Tramo de edad", "Sede": "Sede"}


def tabla(nombre: str, grupo: dict) -> str:
    filas = [f"| {nombre} | Citas | Inasistencia real | Prob. media | % ALTO | Falsos positivos | Cobertura | AUC |",
             "|---|---|---|---|---|---|---|---|"]
    for k, m in grupo.items():
        filas.append(f"| {k} | {m['citas']} | {m['tasaReal']:.1%} | {m['probMedia']:.1%} | {m['pctAlto']:.1%} | "
                     f"{m['falsosPositivos']:.1%} | {m['cobertura']:.1%} | {m['auc']:.3f} |")
    return "\n".join(filas)


def main() -> None:
    df = dataset_con_grupos(meses=18)
    r = modelo.entrenar(df)
    e = evaluar(df, list(GRUPOS))
    partes = [
        "# Revisión del modelo de inasistencia (F5b)",
        "",
        f"Generado con `python -m scripts.revision_modelo` sobre {len(df):,} citas **sintéticas** ".replace(",", ".")
        + "(18 meses, semilla fija). Validación temporal: entrena con el 80 % más antiguo y evalúa con el 20 % más reciente. "
        + "La ficha del modelo (`ficha-modelo.md`) explica cómo leer estas tablas y las decisiones tomadas.",
        "",
        "## Desempeño global",
        "",
        "| Métrica | Valor | Mínimo para activarse |",
        "|---|---|---|",
        f"| AUC | {r.auc:.3f} | {modelo.AUC_MINIMO:.2f} |",
        f"| Brier (error de las probabilidades) | {r.brier:.4f} | menor que la tasa base ({r.brier_base:.4f}) |",
        f"| Precisión del nivel ALTO | {r.precision_alto:.1%} | mayor que la tasa base ({r.tasa_base:.1%}) |",
        f"| Cobertura del nivel ALTO | {r.recall_alto:.1%} | — |",
        "",
        "## Calibración por decil",
        "",
        "| Decil | Probabilidad media | Inasistencia real | Citas |",
        "|---|---|---|---|",
        *[f"| {c['decil']} | {c['probabilidadMedia']:.1%} | {c['inasistenciaReal']:.1%} | {c['citas']} |" for c in r.calibracion or []],
        "",
        "## Equidad por grupo",
        "",
        "*Falsos positivos*: citas a las que el paciente sí asistió pero quedaron en ALTO (reciben un recordatorio "
        "o una confirmación de más). *Cobertura*: inasistencias reales que quedaron en ALTO.",
    ]
    resumen = []
    for col, titulo in GRUPOS.items():
        g = e["grupos"][col]
        partes += ["", f"### {titulo}", "", tabla(titulo, g)]
        razon, dif_fp = disparidad(g, "pctAlto")[0], disparidad(g, "falsosPositivos")[1]
        resumen.append(f"| {titulo} | {razon:.2f} | {dif_fp:.1%} |")
    partes += ["", "## Resumen de disparidad", "", "| Grupo | Razón de % ALTO (máx/mín) | Diferencia de falsos positivos |",
               "|---|---|---|", *resumen]
    salida = Path(__file__).resolve().parents[2] / "docs" / "ml" / "revision-modelo.md"
    salida.parent.mkdir(parents=True, exist_ok=True)
    salida.write_text("\n".join(partes) + "\n", encoding="utf-8")
    print(f"Escrito {salida}")


if __name__ == "__main__":
    main()
