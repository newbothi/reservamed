"""Acceso a SQL Server con pyodbc (la misma base que usa la API Core)."""
from contextlib import contextmanager
from datetime import datetime
from zoneinfo import ZoneInfo

import pandas as pd

from .config import config


def ahora_chile() -> datetime:
    """Hora local de Chile sin zona, igual que guarda la API Core."""
    return datetime.now(ZoneInfo(config.zona)).replace(tzinfo=None, microsecond=0)


def conectar():
    import pyodbc  # import diferido: permite correr los tests sin driver ODBC

    return pyodbc.connect(config.db, autocommit=False, timeout=10)


@contextmanager
def conexion():
    cn = conectar()
    try:
        yield cn
        cn.commit()
    except Exception:
        cn.rollback()
        raise
    finally:
        cn.close()


def consultar(sql: str, params: tuple = ()) -> pd.DataFrame:
    """Ejecuta una consulta y devuelve un DataFrame con los nombres de columna de SQL."""
    with conexion() as cn:
        cur = cn.cursor()
        cur.execute(sql, params)
        columnas = [c[0] for c in cur.description]
        filas = [tuple(f) for f in cur.fetchall()]
    return pd.DataFrame.from_records(filas, columns=columnas)


def ejecutar(sql: str, params: tuple = ()) -> None:
    with conexion() as cn:
        cn.cursor().execute(sql, params)


def escalar(sql: str, params: tuple = ()):
    with conexion() as cn:
        fila = cn.cursor().execute(sql, params).fetchone()
        return None if fila is None else fila[0]
