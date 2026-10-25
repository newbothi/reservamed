"""
F5b — revisión del modelo de inasistencia: desempeño y equidad por grupo.

Se evalúa sobre la partición temporal de prueba (el 20 % más reciente), igual que en el entrenamiento.
Por cada grupo (sexo, previsión, tramo de edad, sede) se calcula:
  - tasa real de inasistencia y probabilidad media predicha (calibración por grupo),
  - % de citas marcadas ALTO,
  - tasa de falsos positivos: citas a las que SÍ asistieron pero quedaron en ALTO (la "molestia"
    del modelo: un recordatorio o una confirmación extra que no hacía falta),
  - cobertura: de las inasistencias reales, cuántas quedaron en ALTO,
  - AUC dentro del grupo.
La previsión y el sexo NO son variables del modelo: se miden para comprobar que no se cuelan por proxies.
"""
from __future__ import annotations

import numpy as np
import pandas as pd
from sklearn.metrics import roc_auc_score

from .features import preparar
from .modelo import construir_pipeline


def tramo_edad(edad: float) -> str:
    return "0 a 14" if edad < 15 else "15 a 39" if edad < 40 else "40 a 64" if edad < 65 else "65 o más"


def evaluar(df: pd.DataFrame, grupos: list[str]) -> dict:
    """df: columnas crudas de features.SQL_BASE + NoAsistio + las columnas de grupo."""
    df = df.sort_values("FechaHoraInicio").reset_index(drop=True)
    corte = int(len(df) * 0.8)
    entreno, prueba = df.iloc[:corte], df.iloc[corte:].copy()
    pipe = construir_pipeline().fit(preparar(entreno), entreno["NoAsistio"].astype(int))
    prueba["p"] = pipe.predict_proba(preparar(prueba))[:, 1]
    umbral_alto = float(np.quantile(prueba["p"], 0.80))
    prueba["alto"] = prueba["p"] >= umbral_alto

    def metricas(g: pd.DataFrame) -> dict:
        y = g["NoAsistio"].astype(int)
        asistieron = g[y == 0]
        faltaron = g[y == 1]
        return {
            "citas": int(len(g)),
            "tasaReal": round(float(y.mean()), 3),
            "probMedia": round(float(g["p"].mean()), 3),
            "pctAlto": round(float(g["alto"].mean()), 3),
            "falsosPositivos": round(float(asistieron["alto"].mean()), 3) if len(asistieron) else None,
            "cobertura": round(float(faltaron["alto"].mean()), 3) if len(faltaron) else None,
            "auc": round(float(roc_auc_score(y, g["p"])), 3) if y.nunique() == 2 else None,
        }

    resultado = {"global": metricas(prueba), "umbralAlto": round(umbral_alto, 4), "grupos": {}}
    for col in grupos:
        resultado["grupos"][col] = {str(k): metricas(g) for k, g in prueba.groupby(col) if len(g) >= 100}
    return resultado


def disparidad(grupo: dict, metrica: str) -> tuple[float, float]:
    """(razón máx/mín, diferencia máx-mín) de una métrica entre los subgrupos."""
    valores = [m[metrica] for m in grupo.values() if m.get(metrica) is not None]
    if len(valores) < 2:
        return 1.0, 0.0
    return (max(valores) / min(valores) if min(valores) > 0 else float("inf")), max(valores) - min(valores)
