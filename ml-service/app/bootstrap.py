"""
Primer arranque de la demo: si no hay historial, genera datos sintéticos y entrena el primer modelo.

En Docker corre como servicio aparte (ml-bootstrap) con un usuario de base que puede insertar
pacientes y citas sintéticas. El ml-service en ejecución usa otro usuario que solo lee esas
tablas y escribe en ModelosML y PrediccionesCita (hallazgo A5: mínimo privilegio).
"""
import logging
import time

from .servicio import servicio

log = logging.getLogger("ml.bootstrap")


def preparar(intentos: int = 30, espera: float = 10) -> bool:
    from scripts.generar_datos import generar

    for intento in range(1, intentos + 1):
        try:
            if servicio.metadata_activo() is not None:
                if servicio.modelo_activo_firmado():
                    log.info("Ya hay un modelo activo y firmado: nada que preparar.")
                    return True
                # M5: modelos anteriores a la fase 4 (o alterados) no tienen firma válida: se reentrena.
                log.warning("El modelo activo no tiene firma válida: se entrena uno nuevo.")
            if servicio.muestras_disponibles() < 500:
                log.info("Sin historial suficiente: generando datos sintéticos…")
                generar(18, 1500, False)
            servicio.entrenar()
            log.info("Primer modelo entrenado.")
            return True
        except Exception as e:  # base aún no disponible
            log.warning("Bootstrap ML: base no disponible todavía (%s). Intento %d.", e, intento)
            time.sleep(espera)
    return False
