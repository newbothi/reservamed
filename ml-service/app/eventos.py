"""
Consumidor de RabbitMQ: cada vez que una cita queda reservada (pago en clínica) o
confirmada (pagada online), se calcula su riesgo de inasistencia y se guarda.
La reserva nunca espera al modelo: todo ocurre de forma asíncrona.
"""
import asyncio
import json
import logging
import ssl

import aio_pika

from .config import config
from .servicio import ModeloNoDisponible, servicio

log = logging.getLogger("ml.eventos")

EXCHANGE = "reservamed.eventos"
DLX = "reservamed.dlx"
COLA = "reservamed.ml"
EVENTOS = ("cita.reservada", "cita.confirmada")


async def _procesar(mensaje: aio_pika.abc.AbstractIncomingMessage) -> None:
    # requeue=False: si falla, el mensaje va a la cola de muertos (reservamed.ml.dlq) para revisión.
    async with mensaje.process(requeue=False):
        cita_id = int(json.loads(mensaje.body)["citaId"])
        try:
            r = await asyncio.to_thread(servicio.predecir, cita_id)
            log.info("Cita %s: riesgo %s (%.0f%%)", cita_id, r["nivel"], r["probabilidad"] * 100)
        except ModeloNoDisponible:
            log.warning("Cita %s sin predicción: aún no hay modelo entrenado.", cita_id)


def contexto_tls(url: str, ca: str) -> ssl.SSLContext | None:
    """M9: amqps:// valida el certificado del broker (y su nombre) contra la CA local."""
    if not url.startswith("amqps://"):
        return None
    ctx = ssl.create_default_context(cafile=ca or None)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    return ctx


async def consumir() -> None:
    tls = contexto_tls(config.rabbit_url, config.rabbit_ca)
    while True:
        try:
            conexion = await aio_pika.connect_robust(config.rabbit_url, ssl_context=tls)
            async with conexion:
                canal = await conexion.channel()
                await canal.set_qos(prefetch_count=10)
                exchange = await canal.declare_exchange(EXCHANGE, aio_pika.ExchangeType.TOPIC, durable=True)
                dlx = await canal.declare_exchange(DLX, aio_pika.ExchangeType.DIRECT, durable=True)
                dlq = await canal.declare_queue(f"{COLA}.dlq", durable=True)
                await dlq.bind(dlx, routing_key=COLA)
                cola = await canal.declare_queue(
                    COLA, durable=True,
                    arguments={"x-dead-letter-exchange": DLX, "x-dead-letter-routing-key": COLA},
                )
                for evento in EVENTOS:
                    await cola.bind(exchange, routing_key=evento)

                log.info("Escuchando %s en la cola %s", ", ".join(EVENTOS), COLA)
                await cola.consume(_procesar)
                await asyncio.Future()  # mantener vivo hasta que se cancele
        except asyncio.CancelledError:
            raise
        except Exception as e:  # broker caído o reiniciándose
            log.warning("RabbitMQ no disponible (%s). Reintentando en 5 s…", e)
            await asyncio.sleep(5)
