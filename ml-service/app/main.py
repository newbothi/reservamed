"""
ml-service de ReservaMed (FastAPI).
Sólo es accesible desde la red interna: lo consumen la API Core (HTTP) y RabbitMQ (eventos).
"""
import asyncio
import logging
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, Header, HTTPException, Query
from pydantic import BaseModel

from .config import api_key_valida, config
from .servicio import ModeloNoDisponible, servicio

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
log = logging.getLogger("ml")


async def _bootstrap() -> None:
    """Demo local opcional (ML_BOOTSTRAP=true). En Docker lo hace el servicio ml-bootstrap."""
    from .bootstrap import preparar

    await asyncio.to_thread(preparar)


@asynccontextmanager
async def lifespan(_: FastAPI):
    if not api_key_valida(config.api_key):
        log.error("ML_API_KEY no está definida o es un valor por defecto: todas las peticiones HTTP serán rechazadas.")
    tareas = []
    if config.bootstrap:
        tareas.append(asyncio.create_task(_bootstrap()))
    if config.consumir_eventos:
        from .eventos import consumir

        tareas.append(asyncio.create_task(consumir()))
    yield
    for t in tareas:
        t.cancel()


app = FastAPI(title="ReservaMed ML", version="1.0.0", lifespan=lifespan)


def exigir_api_key(x_api_key: str | None = Header(default=None)) -> None:
    """Falla cerrado (C1): sin una API key configurada y válida, nadie entra."""
    if not api_key_valida(config.api_key) or x_api_key != config.api_key:
        raise HTTPException(status_code=401, detail="API key inválida")


class SolicitudPrediccion(BaseModel):
    citaId: int
    guardar: bool = True


@app.get("/health")
def health():
    return {"estado": "ok", "servicio": "reservamed-ml"}


@app.get("/modelos/actual", dependencies=[Depends(exigir_api_key)])
def modelo_actual():
    meta = servicio.metadata_activo()
    if meta is None:
        raise HTTPException(404, "No hay un modelo entrenado todavía.")
    return meta


@app.get("/modelos", dependencies=[Depends(exigir_api_key)])
def modelos():
    return servicio.historial_modelos()


@app.post("/modelos/entrenar", dependencies=[Depends(exigir_api_key)])
def entrenar():
    try:
        meta = servicio.entrenar()
    except ValueError as e:
        raise HTTPException(422, str(e)) from e
    meta["citasRecalculadas"] = servicio.recalcular_futuras()
    return meta


@app.post("/predicciones/inasistencia", dependencies=[Depends(exigir_api_key)])
def predecir(s: SolicitudPrediccion):
    try:
        return servicio.predecir(s.citaId, s.guardar)
    except ModeloNoDisponible as e:
        raise HTTPException(503, str(e)) from e
    except KeyError as e:
        raise HTTPException(404, str(e)) from e


@app.get("/predicciones/demanda", dependencies=[Depends(exigir_api_key)])
def demanda(semanas: int = Query(4, ge=1, le=12)):
    return servicio.demanda(semanas)
