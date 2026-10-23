"""
Variables del modelo de inasistencia.

La MISMA consulta y la MISMA función `preparar` se usan para entrenar y para predecir:
así se evita el error clásico de calcular las variables distinto en producción.

Decisión de diseño: NO se usan el tipo de previsión ni el tramo Fonasa, porque son
proxies de nivel socioeconómico. El riesgo sólo se usa para recordar más o gestionar
sobrecupos, nunca para negar una hora.
"""
import numpy as np
import pandas as pd

SQL_BASE = """
SELECT c.Id AS CitaId,
       c.FechaHoraInicio,
       DATEDIFF(DAY, c.CreadoEn, c.FechaHoraInicio) AS DiasAnticipacion,
       DATEPART(HOUR, c.FechaHoraInicio) AS Hora,
       (DATEPART(WEEKDAY, c.FechaHoraInicio) + @@DATEFIRST - 2) % 7 + 1 AS DiaSemana,
       c.Canal,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.Pagos p
                         WHERE p.CitaId = c.Id AND p.Medio = 'WEBPAY' AND p.Estado = 'AUTORIZADO')
            THEN 1 ELSE 0 END AS PagoAnticipado,
       e.Nombre AS Especialidad,
       s.Comuna AS Sede,
       DATEDIFF(YEAR, pa.FechaNacimiento, c.FechaHoraInicio) AS Edad,
       ISNULL(hist.CitasPrevias, 0) AS CitasPrevias,
       ISNULL(hist.InasistenciasPrevias, 0) AS InasistenciasPrevias,
       CASE WHEN c.Estado = 'NO_ASISTIO' THEN 1 ELSE 0 END AS NoAsistio
FROM dbo.Citas c
JOIN dbo.Pacientes pa     ON pa.Id = c.PacienteId
JOIN dbo.Profesionales pr ON pr.Id = c.ProfesionalId
JOIN dbo.Especialidades e ON e.Id = pr.EspecialidadId
JOIN dbo.Sedes s          ON s.Id = c.SedeId
OUTER APPLY (
    SELECT COUNT(*) AS CitasPrevias,
           SUM(CASE WHEN h.Estado = 'NO_ASISTIO' THEN 1 ELSE 0 END) AS InasistenciasPrevias
    FROM dbo.Citas h
    WHERE h.PacienteId = c.PacienteId
      AND h.FechaHoraInicio < c.FechaHoraInicio
      AND h.Estado IN ('ATENDIDA', 'NO_ASISTIO')
) hist
"""

# Sólo citas con desenlace conocido sirven para entrenar.
SQL_ENTRENAMIENTO = SQL_BASE + " WHERE c.Estado IN ('ATENDIDA', 'NO_ASISTIO')"
SQL_UNA_CITA = SQL_BASE + " WHERE c.Id = ?"

NUMERICAS = ["DiasAnticipacionLog", "Edad", "CitasPrevias", "TasaInasistenciaPrevia"]
# La hora no es lineal (temprano y tarde faltan más que al mediodía): se usa como franja.
CATEGORICAS = ["FranjaHoraria", "DiaSemana", "Canal", "Especialidad", "Sede"]
BINARIAS = ["PagoAnticipado", "SinHistorial"]

DIAS = {1: "lunes", 2: "martes", 3: "miércoles", 4: "jueves", 5: "viernes", 6: "sábado", 7: "domingo"}

# Tasa previa suavizada: un paciente nuevo parte de la tasa general (prior bayesiano simple).
TASA_PRIOR, PESO_PRIOR = 0.18, 2.0


def preparar(df: pd.DataFrame) -> pd.DataFrame:
    """Convierte las columnas crudas de SQL en las variables que ve el modelo."""
    x = pd.DataFrame(index=df.index)
    dias = pd.to_numeric(df["DiasAnticipacion"], errors="coerce").fillna(0).clip(lower=0)
    previas = pd.to_numeric(df["CitasPrevias"], errors="coerce").fillna(0)
    inasist = pd.to_numeric(df["InasistenciasPrevias"], errors="coerce").fillna(0)

    x["DiasAnticipacionLog"] = np.log1p(dias)
    hora = pd.to_numeric(df["Hora"], errors="coerce").fillna(12)
    x["FranjaHoraria"] = pd.cut(hora, bins=[-1, 8, 12, 16, 24],
                                labels=["antes de 9", "9 a 13", "13 a 17", "17 o más"]).astype(str)
    x["Edad"] = pd.to_numeric(df["Edad"], errors="coerce").fillna(40).clip(0, 100)
    x["CitasPrevias"] = previas.clip(upper=30)
    x["TasaInasistenciaPrevia"] = (inasist + TASA_PRIOR * PESO_PRIOR) / (previas + PESO_PRIOR)
    x["DiaSemana"] = pd.to_numeric(df["DiaSemana"], errors="coerce").map(DIAS).fillna("otro")
    x["Canal"] = df["Canal"].astype(str)
    x["Especialidad"] = df["Especialidad"].astype(str)
    x["Sede"] = df["Sede"].astype(str)
    x["PagoAnticipado"] = pd.to_numeric(df["PagoAnticipado"], errors="coerce").fillna(0).astype(int)
    x["SinHistorial"] = (previas == 0).astype(int)
    return x[NUMERICAS + CATEGORICAS + BINARIAS]


ETIQUETAS = {
    "DiasAnticipacionLog": "Días de anticipación de la reserva",
    "Edad": "Edad del paciente",
    "CitasPrevias": "Citas previas en la clínica",
    "TasaInasistenciaPrevia": "Tasa de inasistencia previa",
    "PagoAnticipado": "Pagó online por adelantado",
    "SinHistorial": "Paciente sin historial",
}

CANALES = {"WEB": "web", "PRESENCIAL": "presencial", "TELEFONO": "teléfono"}


def etiqueta(nombre_sklearn: str) -> str:
    """'cat__Canal_TELEFONO' → 'Canal: teléfono'; 'num__Hora' → 'Hora de la cita'."""
    nombre = nombre_sklearn.split("__", 1)[-1]
    for cat in CATEGORICAS:
        if nombre.startswith(cat + "_"):
            valor = nombre[len(cat) + 1:]
            valor = CANALES.get(valor, valor)
            titulo = {"DiaSemana": "Día", "FranjaHoraria": "Horario"}.get(cat, cat)
            return f"{titulo}: {valor}"
    return ETIQUETAS.get(nombre, nombre)
