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
    return dataset_con_grupos(meses)


def dataset_con_grupos(meses: int = 12) -> pd.DataFrame:
    """Como la consulta SQL, más columnas de grupo para la revisión de equidad (no son variables del modelo)."""
    hasta = date(2025 + (meses - 1) // 12, (meses - 1) % 12 + 1, 28)
    pacientes, citas = simular(horarios_demo(), date(2025, 1, 1), hasta, 1200)
    edad = {p.indice: p.nacimiento for p in pacientes}
    por_indice = {p.indice: p for p in pacientes}
    df = pd.DataFrame([{
        "Paciente": c.paciente, "FechaHoraInicio": c.inicio, "Estado": c.estado,
        "DiasAnticipacion": (c.inicio.date() - c.creado_en.date()).days,
        "Hora": c.inicio.hour, "DiaSemana": c.inicio.isoweekday(), "Canal": c.canal,
        "PagoAnticipado": int(c.prepago), "Especialidad": c.especialidad, "Sede": SEDES[c.sede_id],
        "Edad": c.inicio.year - edad[c.paciente].year,
        "Sexo": "Mujer" if por_indice[c.paciente].sexo == "F" else "Hombre",
        "Prevision": por_indice[c.paciente].prevision.split("-")[0],
    } for c in citas]).sort_values("FechaHoraInicio")

    cerradas = df[df.Estado.isin(["ATENDIDA", "NO_ASISTIO"])].copy()
    cerradas["NoAsistio"] = (cerradas.Estado == "NO_ASISTIO").astype(int)
    g = cerradas.groupby("Paciente")
    cerradas["CitasPrevias"] = g.cumcount()
    cerradas["InasistenciasPrevias"] = g["NoAsistio"].cumsum() - cerradas["NoAsistio"]
    from app.revision import tramo_edad
    cerradas["TramoEdad"] = cerradas["Edad"].map(tramo_edad)
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


# ---------------------------------------------------------------- F5b: revisión del modelo
def test_probabilidades_calibradas_y_gate_de_calidad(datos):
    r = modelo.entrenar(datos)
    assert r.problemas_calidad() == []
    assert r.brier < r.brier_base                     # mejor que repetir la tasa base
    assert r.calibracion and len(r.calibracion) == 10
    for c in r.calibracion:                           # en cada decil la probabilidad se acerca a lo observado
        assert abs(c["probabilidadMedia"] - c["inasistenciaReal"]) < 0.10, c


def test_gate_rechaza_un_modelo_que_no_discrimina(datos):
    import numpy as np
    azar = datos.copy()
    azar["NoAsistio"] = np.random.default_rng(1).permutation(azar["NoAsistio"].to_numpy())
    r = modelo.entrenar(azar)
    assert any("AUC" in p for p in r.problemas_calidad())


def test_equidad_por_prevision_y_sexo(datos):
    """
    La previsión y el sexo no son variables del modelo. Criterios (grupos con 300 citas o más):
    - calibración dentro de cada grupo: la probabilidad media no se aleja más de 4 puntos de lo observado;
    - los falsos positivos (asistió pero quedó en ALTO) no difieren más de 6 puntos entre grupos.
    """
    from app.revision import evaluar
    e = evaluar(datos, ["Prevision", "Sexo"])
    for grupo in ("Prevision", "Sexo"):
        grandes = {k: m for k, m in e["grupos"][grupo].items() if m["citas"] >= 300}
        assert len(grandes) >= 2, grupo
        for nombre, m in grandes.items():
            assert abs(m["probMedia"] - m["tasaReal"]) < 0.04, (grupo, nombre, m)
        fp = [m["falsosPositivos"] for m in grandes.values()]
        assert max(fp) - min(fp) < 0.06, (grupo, grandes)
