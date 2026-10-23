"""
Pronóstico de demanda semanal por especialidad con regresión lineal.

Para cada especialidad:  citas_semana = b0 + b1·t + b2·sen(2πs/52) + b3·cos(2πs/52)
- t: número de semana (tendencia: ¿la demanda crece o baja?)
- seno/coseno de la semana del año: estacionalidad (p. ej. invierno con más consultas)
Se compara con la capacidad semanal (cupos de los horarios) para estimar la ocupación.
"""
from datetime import date, timedelta

import numpy as np
import pandas as pd
from sklearn.linear_model import LinearRegression
from sklearn.metrics import mean_absolute_error

SQL_HISTORIAL = """
SELECT e.Nombre AS Especialidad, x.Semana, COUNT(*) AS Citas
FROM dbo.Citas c
JOIN dbo.Profesionales pr ON pr.Id = c.ProfesionalId
JOIN dbo.Especialidades e ON e.Id = pr.EspecialidadId
CROSS APPLY (SELECT DATEADD(DAY, -((DATEPART(WEEKDAY, c.FechaHoraInicio) + @@DATEFIRST - 2) % 7),
                            CAST(c.FechaHoraInicio AS DATE)) AS Semana) x
WHERE c.Estado <> 'EXPIRADA' AND c.FechaHoraInicio < ?
GROUP BY e.Nombre, x.Semana
"""

SQL_CAPACIDAD = """
SELECT e.Nombre AS Especialidad,
       SUM(DATEDIFF(MINUTE, h.HoraInicio, h.HoraFin) / h.DuracionMinutos) AS CuposSemana
FROM dbo.HorariosProfesional h
JOIN dbo.Profesionales pr ON pr.Id = h.ProfesionalId AND pr.Activo = 1
JOIN dbo.Especialidades e ON e.Id = pr.EspecialidadId
WHERE h.VigenteDesde <= ? AND (h.VigenteHasta IS NULL OR h.VigenteHasta >= ?)
GROUP BY e.Nombre
"""

MIN_SEMANAS = 16


def lunes(d: date) -> date:
    return d - timedelta(days=d.weekday())


def _matriz(semanas: pd.Series, origen: pd.Timestamp) -> np.ndarray:
    s = pd.to_datetime(semanas)
    t = ((s - origen).dt.days // 7).to_numpy(dtype=float)
    semana_anio = s.dt.isocalendar().week.to_numpy(dtype=float)
    return np.column_stack([t, np.sin(2 * np.pi * semana_anio / 52), np.cos(2 * np.pi * semana_anio / 52)])


def pronosticar(historial: pd.DataFrame, capacidad: dict[str, int], hoy: date, semanas: int = 4) -> list[dict]:
    if historial.empty:
        return []
    historial = historial.copy()
    historial["Semana"] = pd.to_datetime(historial["Semana"])
    ultima_completa = pd.Timestamp(lunes(hoy) - timedelta(days=7))
    resultados = []

    for esp, g in historial.groupby("Especialidad"):
        serie = (g.set_index("Semana")["Citas"]
                 .reindex(pd.date_range(g["Semana"].min(), ultima_completa, freq="W-MON"), fill_value=0))
        if len(serie) < MIN_SEMANAS:
            continue
        origen = serie.index[0]
        x = _matriz(pd.Series(serie.index), origen)
        y = serie.to_numpy(dtype=float)

        # Error de validación: entrenar sin las últimas 8 semanas y predecirlas.
        corte = len(y) - 8
        val = LinearRegression().fit(x[:corte], y[:corte])
        mae = float(mean_absolute_error(y[corte:], val.predict(x[corte:])))

        reg = LinearRegression().fit(x, y)
        futuras = pd.Series(pd.date_range(pd.Timestamp(lunes(hoy)), periods=semanas, freq="W-MON"))
        pred = np.clip(reg.predict(_matriz(futuras, origen)), 0, None)
        cupos = int(capacidad.get(esp, 0))

        resultados.append({
            "especialidad": esp,
            "capacidadSemanal": cupos,
            "promedioUltimas8": round(float(y[-8:].mean()), 1),
            "tendenciaSemanal": round(float(reg.coef_[0]), 3),   # citas más (o menos) por semana
            "errorMedioAbsoluto": round(mae, 1),
            "semanas": [
                {
                    "semana": f.date().isoformat(),
                    "citasEstimadas": round(float(p), 1),
                    "ocupacion": round(float(p) / cupos, 3) if cupos else None,
                }
                for f, p in zip(futuras, pred, strict=True)
            ],
        })
    return sorted(resultados, key=lambda r: r["especialidad"])
