"""Hallazgo C1 en el ml-service: la API key no tiene valor por defecto y la verificación falla cerrado."""
import dataclasses
import os

os.environ.setdefault("ML_CONSUMIR_EVENTOS", "false")

import pytest
from fastapi.testclient import TestClient

from app import main
from app.config import api_key_valida

CLAVE_BUENA = "pruebas-ml-Zr6Xp4Lk8Wq2Ty9U"


@pytest.fixture
def cliente(monkeypatch):
    def con_clave(clave: str) -> TestClient:
        monkeypatch.setattr(main, "config", dataclasses.replace(main.config, api_key=clave, consumir_eventos=False))
        return TestClient(main.app)

    return con_clave


@pytest.mark.parametrize("clave", ["", "corta", "dev-ml-key-cambiar", "CAMBIAR-una-clave-larga-pero-de-ejemplo"])
def test_claves_invalidas_no_se_aceptan_como_configuracion(clave):
    assert not api_key_valida(clave)


def test_clave_valida_se_acepta():
    assert api_key_valida(CLAVE_BUENA)


def test_sin_clave_configurada_rechaza_todo_incluso_header_vacio(cliente):
    c = cliente("")
    assert c.get("/modelos/actual", headers={"X-Api-Key": ""}).status_code == 401
    assert c.get("/modelos/actual").status_code == 401


def test_clave_publicada_es_rechazada_aunque_se_configure(cliente):
    c = cliente("dev-ml-key-cambiar")
    assert c.get("/modelos/actual", headers={"X-Api-Key": "dev-ml-key-cambiar"}).status_code == 401


def test_header_incorrecto_es_rechazado(cliente):
    c = cliente(CLAVE_BUENA)
    assert c.get("/modelos/actual", headers={"X-Api-Key": "otra-clave-cualquiera-123456"}).status_code == 401


def test_health_es_publico(cliente):
    assert cliente("").get("/health").status_code == 200


def test_cargar_env_no_sobrescribe_variables_existentes(tmp_path, monkeypatch):
    from app.config import cargar_env

    archivo = tmp_path / ".env"
    archivo.write_text("﻿# comentario\nML_PRUEBA_A=desde-archivo\nML_PRUEBA_B='con comillas'\n", encoding="utf-8")
    monkeypatch.setenv("ML_PRUEBA_A", "desde-entorno")
    monkeypatch.delenv("ML_PRUEBA_B", raising=False)

    cargar_env(archivo)

    assert os.environ["ML_PRUEBA_A"] == "desde-entorno"
    assert os.environ["ML_PRUEBA_B"] == "con comillas"


def test_amqps_valida_certificado_y_nombre(tmp_path):
    from app.eventos import contexto_tls

    assert contexto_tls("amqp://u:p@localhost/", "") is None
    ctx = contexto_tls("amqps://u:p@rabbitmq:5671/reservamed", "")
    assert ctx is not None
    import ssl

    assert ctx.verify_mode == ssl.CERT_REQUIRED
    assert ctx.check_hostname is True
    assert ctx.minimum_version >= ssl.TLSVersion.TLSv1_2


# ---------- Fase 4 (M5) ----------

def test_api_key_se_compara_en_tiempo_constante():
    import inspect

    assert "compare_digest" in inspect.getsource(main.exigir_api_key)


def test_documentacion_cerrada_fuera_de_desarrollo(cliente):
    c = cliente(CLAVE_BUENA)
    assert not main.config.desarrollo
    for ruta in ("/docs", "/redoc", "/openapi.json"):
        assert c.get(ruta).status_code == 404


def test_modelo_firmado_y_alterado(tmp_path):
    from app.servicio import firma_valida, firmar_archivo, ruta_firma

    ruta = tmp_path / "inasistencia-prueba.joblib"
    ruta.write_bytes(b"contenido-del-modelo")
    firmar_archivo(ruta, CLAVE_BUENA)
    assert firma_valida(ruta, CLAVE_BUENA)

    assert not firma_valida(ruta, CLAVE_BUENA + "x")          # otra clave
    ruta.write_bytes(b"contenido-del-modelo-alterado")           # archivo cambiado
    assert not firma_valida(ruta, CLAVE_BUENA)
    ruta_firma(ruta).unlink()                                    # sin firma
    assert not firma_valida(ruta, CLAVE_BUENA)


def test_no_carga_un_modelo_sin_firma(tmp_path, monkeypatch):
    import joblib

    from app import servicio as modulo

    monkeypatch.setattr(modulo, "config", dataclasses.replace(modulo.config, carpeta_modelos=str(tmp_path), api_key=CLAVE_BUENA))
    joblib.dump({"version": "v-falsa"}, tmp_path / "v-falsa.joblib")
    s = modulo.ServicioInasistencia()
    monkeypatch.setattr(s, "metadata_activo", lambda: {"version": "v-falsa"})
    with pytest.raises(modulo.ModeloNoDisponible):
        s._modelo()


# ---------------------------------------------------------------- F5: secretos como archivos
def test_secretos_desde_archivos(tmp_path, monkeypatch):
    from app.config import cargar_secretos_archivo
    (tmp_path / "ML_API_KEY").write_text("desde-archivo\n", encoding="utf-8")
    (tmp_path / "no-es-variable").write_text("x", encoding="utf-8")
    monkeypatch.setenv("ML_API_KEY", "desde-entorno")
    assert cargar_secretos_archivo(tmp_path) == ["ML_API_KEY"]
    import os
    assert os.environ["ML_API_KEY"] == "desde-archivo"


def test_claves_se_agregan_a_las_cadenas():
    from app.config import con_clave_amqp, con_clave_odbc
    assert con_clave_odbc("DRIVER={X};SERVER=s;UID=u;", "a}b") == "DRIVER={X};SERVER=s;UID=u;PWD={a}}b};"
    assert con_clave_odbc("SERVER=s;PWD=x", "otra") == "SERVER=s;PWD=x"
    assert con_clave_amqp("amqps://rm_ml@rabbitmq:5671/reservamed", "c/l@ve") == "amqps://rm_ml:c%2Fl%40ve@rabbitmq:5671/reservamed"
