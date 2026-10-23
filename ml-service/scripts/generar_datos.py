"""
Genera historial SINTÉTICO de citas en SQL Server para entrenar los modelos.

Uso (desde la carpeta ml-service):
    python -m scripts.generar_datos                  # 18 meses, 1500 pacientes
    python -m scripts.generar_datos --meses 12 --pacientes 800
    python -m scripts.generar_datos --limpiar        # borra sólo los datos sintéticos

Los pacientes sintéticos se reconocen por el correo @sintetico.cl.
"""
import argparse
import logging
from datetime import time, timedelta

import pandas as pd

from app import db
from app.sintetico import Horario, simular

log = logging.getLogger("ml.generador")

SQL_LIMPIAR = """
DECLARE @Ids TABLE (Id INT PRIMARY KEY);
INSERT INTO @Ids SELECT Id FROM dbo.Pacientes WHERE Email LIKE '%@sintetico.cl';
DELETE pc FROM dbo.PrediccionesCita pc JOIN dbo.Citas c ON c.Id = pc.CitaId WHERE c.PacienteId IN (SELECT Id FROM @Ids);
DELETE p  FROM dbo.Pagos p JOIN dbo.Citas c ON c.Id = p.CitaId WHERE c.PacienteId IN (SELECT Id FROM @Ids);
DELETE FROM dbo.Citas       WHERE PacienteId IN (SELECT Id FROM @Ids);
DELETE FROM dbo.Previsiones WHERE PacienteId IN (SELECT Id FROM @Ids);
DELETE FROM dbo.Pacientes   WHERE Id IN (SELECT Id FROM @Ids);
"""


def limpiar() -> None:
    with db.conexion() as cn:
        cn.cursor().execute(SQL_LIMPIAR)
    log.info("Datos sintéticos eliminados.")


def _horarios() -> list[Horario]:
    df = db.consultar("""
        SELECT h.ProfesionalId, h.SedeId, h.PrestacionId, h.DiaSemana,
               -- TIME como texto: pyodbc no siempre soporta el tipo TIME de SQL Server
               CONVERT(VARCHAR(8), h.HoraInicio, 108) AS HoraInicio,
               CONVERT(VARCHAR(8), h.HoraFin, 108) AS HoraFin,
               h.DuracionMinutos, e.Nombre AS Especialidad, ps.ValorParticular
        FROM dbo.HorariosProfesional h
        JOIN dbo.Profesionales p  ON p.Id = h.ProfesionalId
        JOIN dbo.Especialidades e ON e.Id = p.EspecialidadId
        JOIN dbo.Prestaciones ps  ON ps.Id = h.PrestacionId
    """)
    return [Horario(int(r.ProfesionalId), int(r.SedeId), int(r.PrestacionId), int(r.DiaSemana),
                    time.fromisoformat(r.HoraInicio), time.fromisoformat(r.HoraFin), int(r.DuracionMinutos), r.Especialidad, int(r.ValorParticular))
            for r in df.itertuples()]


def generar(meses: int = 18, n_pacientes: int = 1500, reemplazar: bool = False) -> int:
    existentes = db.escalar("SELECT COUNT(1) FROM dbo.Pacientes WHERE Email LIKE '%@sintetico.cl'")
    if existentes:
        if not reemplazar:
            log.info("Ya existen %s pacientes sintéticos. Use --reemplazar para regenerar.", existentes)
            return 0
        limpiar()

    hoy = db.ahora_chile().date()
    hasta = hoy - timedelta(days=1)              # sólo el pasado: no ocupa cupos futuros
    desde = hasta - timedelta(days=int(meses * 30.4))
    pacientes, citas = simular(_horarios(), desde, hasta, n_pacientes)

    # Respeta citas reales que ya ocupen esos cupos (índice único profesional + hora).
    ocupados = db.consultar(
        "SELECT ProfesionalId, FechaHoraInicio FROM dbo.Citas WHERE OcupaCupo = 1 AND FechaHoraInicio BETWEEN ? AND ?",
        (desde, hasta + timedelta(days=1)),
    )
    if not ocupados.empty:
        tomados = {(int(r.ProfesionalId), r.FechaHoraInicio.to_pydatetime() if hasattr(r.FechaHoraInicio, "to_pydatetime")
                    else r.FechaHoraInicio) for r in ocupados.itertuples()}
        citas = [c for c in citas if (c.profesional_id, c.inicio) not in tomados]
    log.info("Simuladas %d citas para %d pacientes (%s a %s).", len(citas), len(pacientes), desde, hasta)

    fin = db.consultar("""
        SELECT f.Codigo, f.Id AS FinanciadorId, MIN(pl.Id) AS PlanId
        FROM dbo.Financiadores f LEFT JOIN dbo.PlanesSalud pl ON pl.FinanciadorId = f.Id
        GROUP BY f.Codigo, f.Id
    """).set_index("Codigo")
    isapres = [c for c in fin.index if c not in ("FONASA", "PARTICULAR")]

    with db.conexion() as cn:
        cur = cn.cursor()
        cur.fast_executemany = True

        cur.executemany(
            """INSERT INTO dbo.Pacientes (Rut, Nombres, ApellidoPaterno, ApellidoMaterno, FechaNacimiento,
                                          Sexo, Email, Telefono, Comuna, CreadoEn)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            [(p.rut, p.nombres, p.ap_paterno, p.ap_materno, p.nacimiento, p.sexo, p.email, p.telefono,
              p.comuna, desde) for p in pacientes],
        )
        ids = dict(cur.execute("SELECT Email, Id FROM dbo.Pacientes WHERE Email LIKE '%@sintetico.cl'").fetchall())
        id_de = {p.indice: ids[p.email] for p in pacientes}

        previsiones = []
        for p in pacientes:
            if p.prevision.startswith("FONASA"):
                f = fin.loc["FONASA"]; tramo = p.prevision[-1]
            elif p.prevision == "ISAPRE":
                f = fin.loc[isapres[p.indice % len(isapres)]]; tramo = None
            else:
                f = fin.loc["PARTICULAR"]; tramo = None
            plan = None if f.name == "PARTICULAR" or pd.isna(f["PlanId"]) else int(f["PlanId"])
            previsiones.append((id_de[p.indice], int(f["FinanciadorId"]), plan, tramo))
        cur.executemany(
            "INSERT INTO dbo.Previsiones (PacienteId, FinanciadorId, PlanSaludId, TramoFonasa) VALUES (?, ?, ?, ?)",
            previsiones,
        )

        cur.executemany(
            """INSERT INTO dbo.Citas (PacienteId, ProfesionalId, SedeId, PrestacionId, FechaHoraInicio, FechaHoraFin,
                                      Estado, Canal, OcupaCupo, RecordatorioEnviado, MotivoCancelacion, CreadoEn)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?)""",
            [(id_de[c.paciente], c.profesional_id, c.sede_id, c.prestacion_id, c.inicio, c.fin, c.estado, c.canal,
              0 if c.estado == "CANCELADA" else 1,
              "Anulada por el paciente (sintético)" if c.estado == "CANCELADA" else None, c.creado_en)
             for c in citas],
        )

        # Pagos Webpay de las citas pagadas por adelantado (se enlazan por profesional + hora).
        cur.execute("CREATE TABLE #Prepago (ProfesionalId INT, Inicio DATETIME2(0), Monto INT)")
        prepagos = [(c.profesional_id, c.inicio, c.monto) for c in citas if c.prepago]
        if prepagos:
            cur.executemany("INSERT INTO #Prepago VALUES (?, ?, ?)", prepagos)
        cur.execute("""
            INSERT INTO dbo.Pagos (CitaId, Medio, Monto, Estado, BuyOrder, CreadoEn, FechaPago)
            SELECT c.Id, 'WEBPAY', pp.Monto,
                   CASE WHEN c.Estado = 'CANCELADA' THEN 'ANULADO' ELSE 'AUTORIZADO' END,
                   CONCAT('SIM', c.Id), c.CreadoEn, c.CreadoEn
            FROM #Prepago pp
            JOIN dbo.Citas c ON c.ProfesionalId = pp.ProfesionalId AND c.FechaHoraInicio = pp.Inicio
            JOIN dbo.Pacientes pa ON pa.Id = c.PacienteId AND pa.Email LIKE '%@sintetico.cl'
        """)
        cur.execute("DROP TABLE #Prepago")

    log.info("Historial sintético cargado: %d citas.", len(citas))
    return len(citas)


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
    ap = argparse.ArgumentParser(description="Genera historial sintético de citas para ReservaMed.")
    ap.add_argument("--meses", type=int, default=18)
    ap.add_argument("--pacientes", type=int, default=1500)
    ap.add_argument("--reemplazar", action="store_true", help="borra los sintéticos existentes y genera de nuevo")
    ap.add_argument("--limpiar", action="store_true", help="sólo borra los datos sintéticos")
    a = ap.parse_args()
    if a.limpiar:
        limpiar()
    else:
        generar(a.meses, a.pacientes, a.reemplazar)
