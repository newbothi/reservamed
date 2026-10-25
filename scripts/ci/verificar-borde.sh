#!/usr/bin/env bash
# Verificaciones de la fase 1 contra el sistema levantado con docker compose (job "borde" de CI).
# Cada comprobación fallida deja una anotación ::error:: en GitHub; al final, un resumen ::notice::.
# Hallazgos: C2 C4 A5 M4 M8 M9 (A4 y la fase 2: scripts/verificar-fase2.ps1).
set -uo pipefail

BASE=https://localhost
CURL=(curl -s --max-time 15 --cacert certs/ca.pem)
FALLAS=0
OK=()

fallo() { echo "::error title=$1::$2"; FALLAS=$((FALLAS + 1)); }
bien()  { OK+=("$1"); echo "OK  $1"; }
dc()    { docker compose "$@"; }

# ---------------- C4: HTTPS ----------------
redir=$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' http://localhost/citas)
[[ "$redir" == "301 https://localhost/citas" ]] && bien "C4 http→https 301" || fallo "C4 redirección" "http://localhost/citas devolvió '$redir'"

tls() { openssl s_client -connect localhost:443 -servername localhost "$@" </dev/null 2>/dev/null | grep -q "^New, TLS\|Protocol *: TLS\|^Protocol version"; }
for v in tls1_2 tls1_3; do tls "-$v" && bien "C4 acepta $v" || fallo "C4 TLS" "No negocia $v"; done
for v in tls1 tls1_1; do
  # Aceptado = el servidor negoció ese protocolo (aparece "New, TLSv1…, Cipher is X" con X ≠ NONE).
  salida=$(openssl s_client -connect 127.0.0.1:443 -servername localhost "-$v" -cipher 'DEFAULT@SECLEVEL=0' </dev/null 2>&1)
  if grep -qE "^New, TLSv1(\.1)?, Cipher is [A-Z0-9]" <<<"$salida" && ! grep -q "Cipher is (NONE)" <<<"$salida"; then
    fallo "C4 TLS antiguo" "Se negoció $v: $(grep -m1 '^New,' <<<"$salida")"
  else
    bien "C4 rechaza $v"
  fi
done

host_ajeno=$("${CURL[@]}" -o /dev/null -w '%{http_code}' -H 'Host: evil.example' "$BASE/")
[[ "$host_ajeno" == "421" ]] && bien "C4 Host ajeno → 421" || fallo "C4 Host" "Host ajeno devolvió $host_ajeno"

# ---------------- M8: cabeceras ----------------
cab=$("${CURL[@]}" -D - -o /dev/null "$BASE/")
for h in "strict-transport-security: max-age=31536000" "content-security-policy: default-src 'self'" \
         "x-frame-options: DENY" "x-content-type-options: nosniff" "permissions-policy:" \
         "referrer-policy: strict-origin-when-cross-origin" "cross-origin-opener-policy: same-origin"; do
  grep -qi "^$h" <<<"$cab" && bien "M8 ${h%%:*}" || fallo "M8 cabecera" "Falta '$h' en /"
done
grep -qi "^x-powered-by" <<<"$cab" && fallo "M8 X-Powered-By" "Se expone X-Powered-By" || bien "M8 sin X-Powered-By"
grep -qiE "^server: nginx/[0-9]" <<<"$cab" && fallo "M8 Server" "Server expone versión" || bien "M8 Server sin versión"
api_cab=$("${CURL[@]}" -D - -o /dev/null "$BASE/health")
grep -qi "^content-security-policy" <<<"$api_cab" && bien "M8 cabeceras también en respuestas de la API" || fallo "M8 API" "/health sin CSP"
grep -qi "^server: kestrel" <<<"$api_cab" && fallo "M8 Kestrel" "Se expone Server: Kestrel" || bien "M8 sin Server: Kestrel"

# ---------------- C2: Webpay simulado ----------------
cuerpo=$("${CURL[@]}" "$BASE/webpay-mock/pagar?token_ws=x")
grep -qi "webpay" <<<"$cuerpo" && fallo "C2 webpay-mock" "NGINX publica la página simulada" || bien "C2 NGINX no publica /webpay-mock/"
# F5: la API atiende por HTTPS interno; se consulta desde la red "app" validando con la CA local.
interno=$(docker run --rm --network reservamed_app -v "$PWD/certs/ca.pem:/ca.pem:ro" \
  curlimages/curl:8.16.0@sha256:463eaf6072688fe96ac64fa623fe73e1dbe25d8ad6c34404a669ad3ce1f104b6 \
  -s --cacert /ca.pem -o /dev/null -w '%{http_code}' https://api-core:8443/webpay-mock/pagar 2>&1)
grep -q "^404$" <<<"$interno" && bien "C2 la API (Production) responde 404 en /webpay-mock/" || fallo "C2 controlador" "La API respondió: $(head -c 200 <<<"$interno")"

# ---------------- A5 / M9: puertos y red interna ----------------
mailpit=$(dc port mailpit 8025 2>&1)
[[ "$mailpit" == 127.0.0.1:8025 ]] && bien "A7 Mailpit solo en 127.0.0.1" || fallo "A7 Mailpit" "Mailpit publicado como '$mailpit'"
for p in 1433 5671 5672 15671 15672 1025; do
  if timeout 3 bash -c "</dev/tcp/127.0.0.1/$p" 2>/dev/null; then fallo "A5 puerto" "El puerto $p está abierto en el host"; else bien "A5 puerto $p cerrado en el host"; fi
done
if dc exec -T rabbitmq wget -q -T 5 -O /dev/null https://www.google.com 2>/dev/null; then
  fallo "A5 red interna" "La red de datos tiene salida a Internet"; else bien "A5 red de datos sin salida"; fi

listeners=$(dc exec -T rabbitmq rabbitmq-diagnostics -q listeners 2>&1)
grep -q "5671.*amqp/ssl\|amqp/ssl.*5671" <<<"$listeners" && bien "M9 AMQPS en 5671" || fallo "M9 TLS" "Sin listener amqp/ssl: $listeners"
grep -qE "port: 5672|5672, protocol: amqp\b" <<<"$listeners" && fallo "M9 AMQP plano" "Hay listener sin TLS en 5672" || bien "M9 sin AMQP en texto plano"

permisos=$(dc exec -T rabbitmq rabbitmqctl -q list_permissions -p reservamed 2>&1)
for u in rm_api rm_worker rm_ml; do grep -q "^$u" <<<"$permisos" && bien "A5 usuario RabbitMQ $u" || fallo "A5 RabbitMQ" "Falta $u en el vhost"; done
grep -q "^reservamed\b" <<<"$permisos" && fallo "A5 RabbitMQ admin" "El administrador tiene permisos en el vhost" || bien "A5 admin sin permisos en el vhost"

conexiones=$(dc exec -T rabbitmq rabbitmqctl -q list_connections user ssl 2>&1)
for u in rm_worker rm_ml; do
  grep -qE "^$u\s+true" <<<"$conexiones" && bien "M9 $u conectado por TLS" || fallo "M9 conexión" "$u no está conectado por TLS: $conexiones"
done

# A4 (IP real en la auditoría) se verifica en scripts/verificar-fase2.ps1 (login vía BFF con TOTP).

# ---------------- A5: usuarios SQL ----------------
sesiones=$(dc exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA_PASSWORD" -C -h -1 -W \
  -Q "SET NOCOUNT ON; SELECT DISTINCT login_name FROM sys.dm_exec_sessions WHERE is_user_process = 1 AND program_name NOT LIKE 'SQLCMD%'" 2>&1)
grep -qx "rm_api" <<<"$sesiones" && bien "A5 la API entra como rm_api" || fallo "A5 SQL" "No hay sesiones de rm_api: $sesiones"
grep -qx "sa" <<<"$sesiones" && fallo "A5 sa" "Un servicio está conectado como sa" || bien "A5 ningún servicio usa sa"

# ---------------- M4: contenedores sin root ----------------
for s in api-core worker bff nginx ml-service mailpit; do
  usuario=$(docker inspect -f '{{.Config.User}}' "$(dc ps -q "$s")" 2>/dev/null)
  if [[ -z "$usuario" || "$usuario" == "0" || "$usuario" == "root" ]]; then fallo "M4 root" "$s corre como root (User='$usuario')"
  else bien "M4 $s corre como $usuario"; fi
  ro=$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}} {{.HostConfig.CapDrop}}' "$(dc ps -q "$s")")
  [[ "$ro" == "true [ALL]" ]] && bien "M4 $s solo lectura y sin capacidades" || fallo "M4 endurecimiento" "$s: $ro"
done

echo "::notice title=Borde F1 (${#OK[@]} OK, $FALLAS fallas)::$(printf '%s; ' "${OK[@]}")"
exit $(( FALLAS > 0 ))
