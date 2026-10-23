"""Orquesta datos, entrenamiento, registro de modelos y predicciones contra SQL Server."""
from __future__ import annotations

import json
import logging
import threading
from datetime import timedelta
from pathlib import Path

import joblib

from . import db, demanda, modelo
from .config import config
from .features import SQL_ENTRENAMIENTO, SQL_UNA_CITA

log = logging.getLogger("ml.servicio")
TIPO = "INASISTENCIA"


class ModeloNoDisponible(Exception):
    pass


class ServicioInasistencia:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._cache: dict | None = None      # {"version", "pipeline", "umbral_medio", "umbral_alto"}
        Path(config.carpeta_modelos).mkdir(parents=True, exist_ok=True)

    # ---------- Entrenamiento ----------

    def entrenar(self) -> dict:
        datos = db.consultar(SQL_ENTRENAMIENTO)
        resultado = modelo.entrenar(datos)
        ahora = db.ahora_chile()
        version = f"inasistencia-{ahora:%Y%m%d-%H%M%S}"
        sinteticos = bool(db.escalar("SELECT COUNT(1) FROM dbo.Pacientes WHERE Email LIKE '%@sintetico.cl'"))

        joblib.dump(
            {"version": version, "pipeline": resultado.pipeline,
             "umbral_medio": resultado.umbral_medio, "umbral_alto": resultado.umbral_alto},
            Path(config.carpeta_modelos) / f"{version}.joblib",
        )

        with db.conexion() as cn:
            cur = cn.cursor()
            cur.execute("UPDATE dbo.ModelosML SET Activo = 0 WHERE Tipo = ? AND Activo = 1", TIPO)
            cur.execute(
                """INSERT INTO dbo.ModelosML (Tipo, Version, Algoritmo, EntrenadoEn, Muestras, TasaBase, Auc,
                       PrecisionAlto, RecallAlto, UmbralMedio, UmbralAlto, Coeficientes, DatosSinteticos, Activo)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1)""",
                TIPO, version, "LogisticRegression (scikit-learn)", ahora, resultado.muestras,
                round(resultado.tasa_base, 4), round(resultado.auc, 4), round(resultado.precision_alto, 4),
                round(resultado.recall_alto, 4), round(resultado.umbral_medio, 4), round(resultado.umbral_alto, 4),
                json.dumps(resultado.coeficientes, ensure_ascii=False), sinteticos,
            )
        with self._lock:
            self._cache = None
        log.info("Modelo %s entrenado: AUC %.3f, %d muestras", version, resultado.auc, resultado.muestras)
        return self.metadata_activo() or {}

    # ---------- Modelo activo ----------

    def metadata_activo(self) -> dict | None:
        df = db.consultar("SELECT * FROM dbo.ModelosML WHERE Tipo = ? AND Activo = 1", (TIPO,))
        return None if df.empty else _fila_modelo(df.iloc[0])

    def historial_modelos(self) -> list[dict]:
        df = db.consultar("SELECT TOP 20 * FROM dbo.ModelosML WHERE Tipo = ? ORDER BY Id DESC", (TIPO,))
        return [{k: v for k, v in _fila_modelo(r).items() if k != "coeficientes"} for _, r in df.iterrows()]

    def _modelo(self) -> dict:
        meta = self.metadata_activo()
        if meta is None:
            raise ModeloNoDisponible("No hay un modelo entrenado todavía.")
        with self._lock:
            if self._cache is None or self._cache["version"] != meta["version"]:
                ruta = Path(config.carpeta_modelos) / f"{meta['version']}.joblib"
                if not ruta.exists():
                    raise ModeloNoDisponible(f"Falta el archivo del modelo {meta['version']}. Vuelva a entrenar.")
                self._cache = joblib.load(ruta)
            return self._cache

    # ---------- Predicción ----------

    def predecir(self, cita_id: int, guardar: bool = True) -> dict:
        m = self._modelo()
        crudo = db.consultar(SQL_UNA_CITA, (cita_id,))
        if crudo.empty:
            raise KeyError(f"La cita {cita_id} no existe.")

        prob, factores = modelo.predecir(m["pipeline"], crudo)
        nivel = modelo.nivel(prob, m["umbral_medio"], m["umbral_alto"])
        resultado = {
            "citaId": cita_id, "probabilidad": round(prob, 4), "nivel": nivel,
            "factores": factores, "modeloVersion": m["version"],
        }
        if guardar:
            db.ejecutar(
                """MERGE dbo.PrediccionesCita AS d
                   USING (SELECT ? AS CitaId) AS o ON d.CitaId = o.CitaId
                   WHEN MATCHED THEN UPDATE SET Probabilidad = ?, Nivel = ?, Factores = ?, ModeloVersion = ?, CalculadoEn = ?
                   WHEN NOT MATCHED THEN INSERT (CitaId, Probabilidad, Nivel, Factores, ModeloVersion, CalculadoEn)
                        VALUES (?, ?, ?, ?, ?, ?);""",
                (cita_id, resultado["probabilidad"], nivel, json.dumps(factores, ensure_ascii=False), m["version"], db.ahora_chile(),
                 cita_id, resultado["probabilidad"], nivel, json.dumps(factores, ensure_ascii=False), m["version"], db.ahora_chile()),
            )
        return resultado

    def recalcular_futuras(self) -> int:
        """Tras reentrenar, recalcula el riesgo de las citas próximas con el nuevo modelo."""
        ids = db.consultar(
            """SELECT Id FROM dbo.Citas WHERE Estado IN ('RESERVADA','CONFIRMADA')
               AND FechaHoraInicio >= ? AND FechaHoraInicio < ?""",
            (db.ahora_chile(), db.ahora_chile() + timedelta(days=30)),
        )
        for cita_id in ids["Id"].tolist():
            self.predecir(int(cita_id))
        return len(ids)

    # ---------- Demanda ----------

    def demanda(self, semanas: int) -> list[dict]:
        hoy = db.ahora_chile().date()
        historial = db.consultar(demanda.SQL_HISTORIAL, (demanda.lunes(hoy),))
        cap = db.consultar(demanda.SQL_CAPACIDAD, (hoy, hoy))
        capacidad = dict(zip(cap["Especialidad"], cap["CuposSemana"].astype(int), strict=True)) if not cap.empty else {}
        return demanda.pronosticar(historial, capacidad, hoy, semanas)

    def muestras_disponibles(self) -> int:
        return int(db.escalar("SELECT COUNT(1) FROM dbo.Citas WHERE Estado IN ('ATENDIDA','NO_ASISTIO')") or 0)


def _fila_modelo(r) -> dict:
    return {
        "version": r["Version"],
        "algoritmo": r["Algoritmo"],
        "entrenadoEn": r["EntrenadoEn"].isoformat(),
        "muestras": int(r["Muestras"]),
        "tasaBase": float(r["TasaBase"]),
        "auc": float(r["Auc"]),
        "precisionAlto": float(r["PrecisionAlto"]),
        "recallAlto": float(r["RecallAlto"]),
        "umbralMedio": float(r["UmbralMedio"]),
        "umbralAlto": float(r["UmbralAlto"]),
        "datosSinteticos": bool(r["DatosSinteticos"]),
        "activo": bool(r["Activo"]),
        "coeficientes": json.loads(r["Coeficientes"]),
    }


servicio = ServicioInasistencia()
