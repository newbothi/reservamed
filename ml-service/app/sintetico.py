"""
Simulador de historial de citas (datos SINTÉTICOS, sin relación con personas reales).

La base demo no trae historial, así que para entrenar se generan meses de citas pasadas
con patrones documentados en la literatura de inasistencia ambulatoria:
- mientras más días de anticipación, más inasistencia;
- primeras horas de la mañana y lunes, más inasistencia;
- quien paga por adelantado falta mucho menos;
- cada paciente tiene una "propensión" propia: el historial predice el futuro.
El modelo debe REDESCUBRIR estos patrones a partir de los datos.
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field
from datetime import date, datetime, time, timedelta

import numpy as np

NOMBRES_F = ["María", "Camila", "Valentina", "Javiera", "Constanza", "Francisca", "Catalina", "Fernanda",
             "Daniela", "Antonia", "Isidora", "Josefa", "Paula", "Carolina", "Andrea", "Patricia"]
NOMBRES_M = ["José", "Juan", "Matías", "Benjamín", "Vicente", "Tomás", "Felipe", "Sebastián", "Diego",
             "Nicolás", "Joaquín", "Cristián", "Rodrigo", "Pablo", "Francisco", "Luis"]
APELLIDOS = ["González", "Muñoz", "Rojas", "Díaz", "Pérez", "Soto", "Contreras", "Silva", "Martínez",
             "Sepúlveda", "Morales", "Rodríguez", "López", "Fuentes", "Hernández", "Torres", "Araya",
             "Flores", "Espinoza", "Valenzuela", "Castillo", "Tapia", "Reyes", "Gutiérrez", "Castro"]
COMUNAS = ["Santiago", "Providencia", "Ñuñoa", "Las Condes", "Maipú", "La Florida", "Puente Alto",
           "San Miguel", "Macul", "Peñalolén", "Vitacura", "Recoleta", "Independencia", "La Reina"]

EFECTO_ESPECIALIDAD = {
    "Medicina General": 0.15,
    "Gastroenterología": 0.0,
    "Dermatología": 0.25,
    "Traumatología": 0.10,
    "Pediatría": -0.25,
}
EFECTO_CANAL = {"WEB": 0.0, "PRESENCIAL": -0.35, "TELEFONO": 0.20}


@dataclass
class Horario:
    profesional_id: int
    sede_id: int
    prestacion_id: int
    dia_semana: int          # 1 = lunes
    hora_inicio: time
    hora_fin: time
    duracion: int
    especialidad: str
    valor: int


@dataclass
class PacienteSintetico:
    indice: int
    rut: str
    nombres: str
    ap_paterno: str
    ap_materno: str
    nacimiento: date
    sexo: str
    email: str
    telefono: str
    comuna: str
    propension: float
    es_nino: bool
    prevision: str           # FONASA-B/C/D, ISAPRE, PARTICULAR


@dataclass
class CitaSintetica:
    paciente: int            # índice del paciente
    profesional_id: int
    sede_id: int
    prestacion_id: int
    inicio: datetime
    fin: datetime
    estado: str
    canal: str
    creado_en: datetime
    prepago: bool
    monto: int
    especialidad: str = field(repr=False, default="")


def dv(numero: int) -> str:
    suma, mult = 0, 2
    while numero > 0:
        suma += numero % 10 * mult
        numero //= 10
        mult = 2 if mult == 7 else mult + 1
    r = 11 - suma % 11
    return "0" if r == 11 else "K" if r == 10 else str(r)


def prob_inasistencia(dias: int, hora: int, dia_semana: int, canal: str, prepago: bool,
                      edad: int, es_nino: bool, especialidad: str, propension: float) -> float:
    z = -2.1
    z += 0.32 * math.log1p(max(dias, 0))
    z += 0.45 if hora < 9 else 0.25 if hora >= 17 else 0.0
    z += 0.25 if dia_semana == 1 else -0.15 if dia_semana == 5 else 0.0
    z += EFECTO_CANAL.get(canal, 0.0)
    z -= 1.1 if prepago else 0.0
    z += -0.30 if es_nino else -0.018 * (edad - 40)
    z += EFECTO_ESPECIALIDAD.get(especialidad, 0.0)
    z += propension
    return 1 / (1 + math.exp(-z))


def simular(horarios: list[Horario], desde: date, hasta: date, n_pacientes: int = 1500,
            semilla: int = 2026, base_rut: int = 40_000_000) -> tuple[list[PacienteSintetico], list[CitaSintetica]]:
    rng = np.random.default_rng(semilla)

    pacientes: list[PacienteSintetico] = []
    for i in range(n_pacientes):
        es_nino = rng.random() < 0.15
        edad = int(rng.integers(0, 15)) if es_nino else int(np.clip(rng.normal(44, 17), 15, 90))
        sexo = "F" if rng.random() < 0.55 else "M"
        nombre = rng.choice(NOMBRES_F if sexo == "F" else NOMBRES_M)
        nacimiento = desde - timedelta(days=edad * 365 + int(rng.integers(0, 365)))
        numero = base_rut + i
        r = rng.random()
        prevision = ("FONASA-" + str(rng.choice(["B", "C", "D"]))) if r < 0.70 else "ISAPRE" if r < 0.95 else "PARTICULAR"
        pacientes.append(PacienteSintetico(
            indice=i, rut=f"{numero}-{dv(numero)}", nombres=str(nombre),
            ap_paterno=str(rng.choice(APELLIDOS)), ap_materno=str(rng.choice(APELLIDOS)),
            nacimiento=nacimiento, sexo=sexo, email=f"paciente{i}@sintetico.cl",
            telefono=f"+569{int(rng.integers(10_000_000, 99_999_999))}", comuna=str(rng.choice(COMUNAS)),
            propension=float(rng.normal(0, 0.9)), es_nino=es_nino, prevision=prevision,
        ))

    # Algunos pacientes consultan mucho más que otros (crónicos, familias).
    ninos = [p for p in pacientes if p.es_nino]
    adultos = [p for p in pacientes if not p.es_nino]
    peso_n = rng.gamma(1.2, 1.0, len(ninos)); peso_n /= peso_n.sum()
    peso_a = rng.gamma(1.2, 1.0, len(adultos)); peso_a /= peso_a.sum()

    citas: list[CitaSintetica] = []
    dia = desde
    while dia <= hasta:
        dia_semana = dia.isoweekday()
        invierno = dia.month in (6, 7, 8)
        for h in (x for x in horarios if x.dia_semana == dia_semana):
            ocupacion = 0.55 + (0.15 if invierno and h.especialidad in ("Medicina General", "Pediatría") else 0.0)
            t = datetime.combine(dia, h.hora_inicio)
            fin_turno = datetime.combine(dia, h.hora_fin)
            paso = timedelta(minutes=h.duracion)
            while t + paso <= fin_turno:
                if rng.random() < ocupacion:
                    pool, pesos = (ninos, peso_n) if h.especialidad == "Pediatría" else (adultos, peso_a)
                    pac = pool[int(rng.choice(len(pool), p=pesos))]
                    edad = (dia - pac.nacimiento).days // 365

                    r = rng.random()
                    dias = int(rng.integers(0, 2)) if r < 0.30 else int(rng.integers(2, 15)) if r < 0.80 else int(rng.integers(15, 61))
                    r = rng.random()
                    canal = "WEB" if r < 0.55 else "PRESENCIAL" if r < 0.80 else "TELEFONO"
                    prepago = canal == "WEB" and rng.random() < 0.45
                    creado = t - timedelta(days=dias, hours=int(rng.integers(1, 10)))

                    if rng.random() < 0.07:
                        estado = "CANCELADA"
                    else:
                        p = prob_inasistencia(dias, t.hour, dia_semana, canal, prepago, edad,
                                              pac.es_nino, h.especialidad, pac.propension)
                        estado = "NO_ASISTIO" if rng.random() < p else "ATENDIDA"

                    citas.append(CitaSintetica(
                        paciente=pac.indice, profesional_id=h.profesional_id, sede_id=h.sede_id,
                        prestacion_id=h.prestacion_id, inicio=t, fin=t + paso, estado=estado, canal=canal,
                        creado_en=creado, prepago=prepago, monto=int(round(h.valor * 0.5, -1)),
                        especialidad=h.especialidad,
                    ))
                t += paso
        dia += timedelta(days=1)

    return pacientes, citas
