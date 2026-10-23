"""
Modelo de inasistencia: regresión logística con scikit-learn.

Se eligió regresión logística (y no un modelo de caja negra) porque:
- entrega una probabilidad, no sólo un sí/no;
- sus coeficientes se pueden mostrar y explicar a la clínica;
- con pocas decenas de miles de citas rinde casi igual que modelos más complejos.
"""
from dataclasses import dataclass

import numpy as np
import pandas as pd
from sklearn.compose import ColumnTransformer
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import precision_score, recall_score, roc_auc_score
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import OneHotEncoder, StandardScaler

from .features import BINARIAS, CATEGORICAS, NUMERICAS, etiqueta, preparar

MIN_MUESTRAS = 500


@dataclass
class ResultadoEntrenamiento:
    pipeline: Pipeline
    muestras: int
    tasa_base: float
    auc: float
    precision_alto: float
    recall_alto: float
    umbral_medio: float
    umbral_alto: float
    coeficientes: list[dict]


def construir_pipeline() -> Pipeline:
    pre = ColumnTransformer(
        [
            ("num", StandardScaler(), NUMERICAS),
            ("cat", OneHotEncoder(handle_unknown="ignore", sparse_output=False), CATEGORICAS),
            ("bin", "passthrough", BINARIAS),
        ]
    )
    return Pipeline([("pre", pre), ("clf", LogisticRegression(max_iter=2000, C=1.0))])


def entrenar(df: pd.DataFrame) -> ResultadoEntrenamiento:
    """
    Validación temporal: entrena con el 80 % más antiguo y evalúa con el 20 % más reciente,
    que es como se usará el modelo (predecir el futuro con el pasado).
    """
    if len(df) < MIN_MUESTRAS:
        raise ValueError(f"Se necesitan al menos {MIN_MUESTRAS} citas con desenlace; hay {len(df)}.")
    y_total = df["NoAsistio"].astype(int)
    if y_total.nunique() < 2:
        raise ValueError("Los datos no tienen citas atendidas y no asistidas a la vez.")

    df = df.sort_values("FechaHoraInicio").reset_index(drop=True)
    corte = int(len(df) * 0.8)
    entreno, prueba = df.iloc[:corte], df.iloc[corte:]

    modelo_eval = construir_pipeline().fit(preparar(entreno), entreno["NoAsistio"].astype(int))
    y = prueba["NoAsistio"].astype(int).to_numpy()
    p = modelo_eval.predict_proba(preparar(prueba))[:, 1]

    # Niveles de riesgo por cuantiles: ALTO = 20 % de citas más riesgosas, MEDIO = siguiente 30 %.
    umbral_medio = float(np.quantile(p, 0.50))
    umbral_alto = float(np.quantile(p, 0.80))
    alto = p >= umbral_alto

    # Modelo final: con todos los datos.
    final = construir_pipeline().fit(preparar(df), df["NoAsistio"].astype(int))

    return ResultadoEntrenamiento(
        pipeline=final,
        muestras=len(df),
        tasa_base=float(df["NoAsistio"].mean()),
        auc=float(roc_auc_score(y, p)),
        precision_alto=float(precision_score(y, alto, zero_division=0)),
        recall_alto=float(recall_score(y, alto, zero_division=0)),
        umbral_medio=umbral_medio,
        umbral_alto=umbral_alto,
        coeficientes=coeficientes(final),
    )


def coeficientes(pipeline: Pipeline) -> list[dict]:
    """Coeficientes ordenados por impacto. odds ratio = cuánto multiplica las chances de no asistir."""
    nombres = pipeline.named_steps["pre"].get_feature_names_out()
    coef = pipeline.named_steps["clf"].coef_[0]
    filas = [
        {"variable": etiqueta(n), "clave": n, "coeficiente": round(float(c), 4), "oddsRatio": round(float(np.exp(c)), 3)}
        for n, c in zip(nombres, coef, strict=True)
    ]
    return sorted(filas, key=lambda f: abs(f["coeficiente"]), reverse=True)


def nivel(prob: float, umbral_medio: float, umbral_alto: float) -> str:
    if prob >= umbral_alto:
        return "ALTO"
    if prob >= umbral_medio:
        return "MEDIO"
    return "BAJO"


def predecir(pipeline: Pipeline, crudo: pd.DataFrame) -> tuple[float, list[str]]:
    """Probabilidad de inasistencia y las variables que más la subieron para ESTA cita."""
    x = preparar(crudo)
    prob = float(pipeline.predict_proba(x)[0, 1])

    transformado = pipeline.named_steps["pre"].transform(x)[0]
    coef = pipeline.named_steps["clf"].coef_[0]
    nombres = pipeline.named_steps["pre"].get_feature_names_out()
    aportes = transformado * coef
    orden = np.argsort(aportes)[::-1]
    factores = [etiqueta(nombres[i]) for i in orden[:3] if aportes[i] > 0.05]
    return prob, factores
