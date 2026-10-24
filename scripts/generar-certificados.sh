#!/usr/bin/env bash
# Certificados para HTTPS local (C4) y AMQPS interno (M9) con openssl.
# Lo usa CI y sirve en Linux/macOS. En Windows use scripts/generar-certificados.ps1 (mkcert),
# que además instala la CA en el almacén del sistema para que el navegador confíe.
#
#   certs/ca.pem             CA local (solo el certificado; la clave privada se borra)
#   certs/reservamed.crt/key reservamed.localhost, localhost, 127.0.0.1 (NGINX)
#   certs/rabbitmq.crt/key   rabbitmq (TLS de AMQP en la red interna)
set -euo pipefail

dir="$(cd "$(dirname "$0")/.." && pwd)/certs"
if [[ -f "$dir/reservamed.crt" && "${1:-}" != "--forzar" ]]; then
  echo "Ya existen certificados en $dir (use --forzar para regenerarlos)."; exit 0
fi
mkdir -p "$dir"; cd "$dir"
rm -f ./*.pem ./*.crt ./*.key

openssl req -x509 -newkey rsa:2048 -nodes -days 90 -subj "/CN=ReservaMed CA local" \
  -keyout ca.key -out ca.pem \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null

emitir() {
  local nombre=$1 san=$2
  openssl req -newkey rsa:2048 -nodes -subj "/CN=$nombre" -keyout "$nombre.key" -out "$nombre.csr" 2>/dev/null
  openssl x509 -req -in "$nombre.csr" -CA ca.pem -CAkey ca.key -CAcreateserial -days 90 -out "$nombre.crt" \
    -extfile <(printf '%s\n' "subjectAltName=$san" "basicConstraints=critical,CA:FALSE" \
      "keyUsage=critical,digitalSignature,keyEncipherment" "extendedKeyUsage=serverAuth" \
      "subjectKeyIdentifier=hash" "authorityKeyIdentifier=keyid,issuer") 2>/dev/null
  rm -f "$nombre.csr"
}
emitir reservamed "DNS:reservamed.localhost,DNS:localhost,IP:127.0.0.1"
emitir rabbitmq "DNS:rabbitmq"

rm -f ca.key ca.srl
# NGINX (uid 101) y RabbitMQ (uid 100) leen las claves montadas: son certificados locales de desarrollo.
chmod 644 ./*.pem ./*.crt ./*.key
echo "Certificados generados en $dir"
