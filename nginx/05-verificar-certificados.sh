#!/bin/sh
# Falla con un mensaje claro si no se generaron los certificados (C4: sin HTTPS no se levanta).
for f in /etc/nginx/certs/reservamed.crt /etc/nginx/certs/reservamed.key /etc/nginx/certs/ca.pem; do
  if [ ! -r "$f" ]; then
    echo "ERROR: falta $f." >&2
    echo "Genere los certificados con scripts/generar-certificados.ps1 (Windows, mkcert)" >&2
    echo "o scripts/generar-certificados.sh (Linux/macOS/CI) y vuelva a ejecutar docker compose up." >&2
    exit 1
  fi
done
