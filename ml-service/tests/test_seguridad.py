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
