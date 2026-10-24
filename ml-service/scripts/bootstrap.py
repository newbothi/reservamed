"""Servicio ml-bootstrap de Docker: prepara datos y modelo, y termina (código 1 si no pudo)."""
import logging
import sys

from app.bootstrap import preparar

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")

if __name__ == "__main__":
    sys.exit(0 if preparar() else 1)
