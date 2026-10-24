#!/bin/sh
# A5/M9: vhost propio y un usuario por servicio con permisos mínimos (API de administración).
# Permisos de RabbitMQ: configure (declarar), write (publicar/enlazar) y read (consumir/enlazar),
# cada uno es una expresión regular sobre nombres de exchange y cola.
set -eu
API="http://rabbitmq:15672/api"
VH="reservamed"

req() {  # metodo ruta json
  code=$(curl -s -o /tmp/resp -w '%{http_code}' -u "$RABBIT_USER:$RABBIT_PASS" \
    -H 'content-type: application/json' -X "$1" "$API$2" ${3:+-d "$3"})
  case "$code" in 2*) ;; *) echo "Error $code en $1 $2: $(cat /tmp/resp)" >&2; exit 1 ;; esac
}

usuario() {  # nombre clave configure write read
  [ -n "$2" ] || { echo "Falta la clave de $1 en .env" >&2; exit 1; }
  req PUT "/users/$1" "{\"password\":\"$2\",\"tags\":\"\"}"
  req PUT "/permissions/$VH/$1" "{\"configure\":\"$3\",\"write\":\"$4\",\"read\":\"$5\"}"
  echo "Usuario $1 listo."
}

for i in $(seq 1 30); do
  curl -sf -u "$RABBIT_USER:$RABBIT_PASS" "$API/overview" >/dev/null && break
  echo "Esperando la API de administración de RabbitMQ ($i)..."; sleep 2
done

req PUT "/vhosts/$VH" '{"description":"ReservaMed"}'

# Regex en JSON: la barra invertida va doble (\\.) para que RabbitMQ reciba \. (punto literal).
E='reservamed\\.eventos'
D='reservamed\\.dlx'
COLAS_WORKER='reservamed\\.(notificaciones|his)(\\.dlq)?'
COLAS_ML='reservamed\\.ml(\\.dlq)?'

# API: solo declara y publica en el exchange de eventos.
usuario rm_api    "$RABBIT_API_PASS"    "^$E\$" "^$E\$" '^$'
# Worker: sus colas (notificaciones, his) y sus .dlq; write en el DLX lo exige declarar colas con dead-letter.
usuario rm_worker "$RABBIT_WORKER_PASS" "^($E|$D|$COLAS_WORKER)\$" "^($D|$COLAS_WORKER)\$" "^($E|$D|$COLAS_WORKER)\$"
# ml-service: su cola y su .dlq; write en el DLX por la misma razón.
usuario rm_ml     "$RABBIT_ML_PASS"     "^($E|$D|$COLAS_ML)\$"     "^($D|$COLAS_ML)\$"     "^($E|$D|$COLAS_ML)\$"

# Quien crea un vhost recibe permisos completos sobre él: se los quitamos al administrador,
# que solo administra (consola) y no puede leer ni publicar eventos de la aplicación.
req DELETE "/permissions/$VH/$RABBIT_USER"
echo "RabbitMQ configurado."
