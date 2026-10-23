"""
Pruebas del modelo sin base de datos: se simula el historial y se calculan las variables
en pandas replicando la consulta SQL de features.py.
Ejecutar:  python -m pytest -q
"""
from datetime import date, time

import pandas as pd
import pytest

from app import modelo
from app.features import preparar
from app.sintetico import Horario, simular

ESPECIALIDADES = ["Gastroenterología", "Medicina General", "Dermatología", "Traumatología", "Pediatría"]
SEDES = {1: "Providencia", 2: "Las Condes"}


def horarios_demo() -> list[Horario]:
    hs = []
    for prof, esp in enumerate(ESPECIALIDADES, start=1):
        for dia in range(1, 6):
            hs.append(Horario(prof, 1, prof, dia, time(8, 30), time(13, 0), 20, esp, 30000))
            if esp != "Pediatría":
                hs.append(Horario(prof, 2, prof, dia, time(15, 0), time(18, 30), 20, esp, 30000))
    return hs


def dataset_como_sql(meses: int = 12) -> pd.DataFrame:
    pacientes, citas = simular(horarios_demo(), date(2025, 1, 1), date(2025, 1, 1).replace(year=2025 + meses // 12), 1200)
    edad = {p.indice: p.nacimiento for p in pacientes}
    df = pd.DataFrame([{
        "Paciente": c.paciente, "FechaHoraInicio": c.inicio, "Estado": c.estado,
        "DiasAnticipacion": (c.inicio.date() - c.creado_en.date()).days,
        "Hora": c.inicio.hour, "DiaSemana": c.inicio.isoweekday(), "Canal": c.canal,
        "PagoAnticipado": int(c.prepago), "Especialidad": c.especialidad, "Sede": SEDES[c.sede_id],
        "Edad": c.inicio.year - edad[c.paciente].year,
    } for c in citas]).sort_values("FechaHoraInicio")

    cerradas = df[df.Estado.isin(["ATENDIDA", "NO_ASISTIO"])].copy()
    cerradas["NoAsistio"] = (cerradas.Estado == "NO_ASISTIO").astype(int)
    g = cerradas.groupby("Paciente")
    cerradas["CitasPrevias"] = g.cumcount()
    cerradas["InasistenciasPrevias"] = g["NoAsistio"].cumsum() - cerradas["NoAsistio"]
    return cerradas


@pytest.fixture(scope="module")
def datos():
    return dataset_como_sql()


def test_tasa_de_inasistencia_realista(datos):
    tasa = datos.NoAsistio.mean()
    assert 0.10 < tasa < 0.30, tasa


def test_modelo_aprende_patrones(datos):
    r = modelo.entrenar(datos)
    assert r.auc > 0.65, r.auc
    assert r.umbral_alto > r.umbral_medio
    assert r.precision_alto > r.tasa_base  # el nivel ALTO concentra más inasistencias que el promedio

    coef = {c["clave"]: c["coeficiente"] for c in r.coeficientes}
    assert coef["bin__PagoAnticipado"] < 0           # pagar antes reduce la inasistencia
    assert coef["num__DiasAnticipacionLog"] > 0      # reservar con mucha anticipación la aumenta
    assert coef["num__TasaInasistenciaPrevia"] > 0   # el historial predice


def test_prediccion_individual_con_factores(datos):
    r = modelo.entrenar(datos)
    fila = datos.iloc[[-1]]
    prob, factores = modelo.predecir(r.pipeline, fila)
    assert 0 < prob < 1
    assert isinstance(factores, list) and len(factores) <= 3
    assert modelo.nivel(prob, r.umbral_medio, r.umbral_alto) in ("BAJO", "MEDIO", "ALTO")


def test_preparar_tolera_valores_faltantes():
    crudo = pd.DataFrame([{
        "DiasAnticipacion": None, "Hora": 9, "DiaSemana": 1, "Canal": "WEB", "PagoAnticipado": 0,
        "Especialidad": "Dermatología", "Sede": "Providencia", "Edad": None,
        "CitasPrevias": 0, "InasistenciasPrevias": None,
    }])
    x = preparar(crudo)
    assert not x.isna().any().any()


def test_demanda_semanal_con_estacionalidad():
    from app import demanda

    _, citas = simular(horarios_demo(), date(2024, 1, 1), date(2025, 6, 30), 800)
    df = pd.DataFrame([{"Especialidad": c.especialidad, "Semana": demanda.lunes(c.inicio.date())} for c in citas])
    hist = df.groupby(["Especialidad", "Semana"]).size().reset_index(name="Citas")
    r = demanda.pronosticar(hist, {"Pediatría": 70, "Dermatología": 120}, date(2025, 7, 1), semanas=4)
    assert {x["especialidad"] for x in r} == set(ESPECIALIDADES)
    ped = next(x for x in r if x["especialidad"] == "Pediatría")
    assert len(ped["semanas"]) == 4 and ped["semanas"][0]["citasEstimadas"] > 0
    assert ped["semanas"][0]["ocupacion"] is not None
