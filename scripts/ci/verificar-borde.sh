#!/usr/bin/env bash
# Verificaciones de la fase 1 contra el sistema levantado con docker compose (job "borde" de CI).
# Cada comprobación fallida deja una anotación ::error:: en GitHub; al final, un resumen ::notice::.
# Hallazgos: C2 C4 A4 A5 M4 M8 M9.
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
interno=$(dc exec -T nginx wget -S -q -O /dev/null http://api-core:8080/webpay-mock/pagar 2>&1)
grep -q "404" <<<"$interno" && bien "C2 la API (Production) responde 404 en /webpay-mock/" || fallo "C2 controlador" "La API respondió: $(head -c 200 <<<"$interno")"

# ---------------- A5 / M9: puertos y red interna ----------------
for p in 1433 5671 5672 15672; do
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

# ---------------- A4: IP falsificada ----------------
login() { "${CURL[@]}" -o /dev/null -w '%{http_code}' -X POST "$BASE$1" -H 'Content-Type: application/json' "${@:2}"; }
login /api/auth/login -H 'X-Forwarded-For: 203.0.113.66' -d '{"email":"a4-api@prueba.cl","password":"x"}' >/dev/null
login /bff/auth/login -H 'X-Forwarded-For: 203.0.113.77' -d '{"email":"a4-bff@prueba.cl","password":"x"}' >/dev/null
token=$("${CURL[@]}" -X POST "$BASE/api/auth/login" -H 'Content-Type: application/json' \
  -d '{"email":"admin@reservamed.cl","password":"Demo1234!"}' | python3 -c 'import sys,json; print(json.load(sys.stdin).get("token",""))' 2>/dev/null)
if [[ -z "$token" ]]; then
  fallo "A4 login admin" "No se pudo iniciar sesión como admin para revisar la auditoría"
else
  aud=$("${CURL[@]}" -H "Authorization: Bearer $token" "$BASE/api/auditoria?accion=LOGIN_FALLIDO&top=50")
  for caso in "a4-api@prueba.cl|203.0.113.66|vía NGINX" "a4-bff@prueba.cl|203.0.113.77|vía BFF"; do
    IFS='|' read -r email falsa via <<<"$caso"
    # En LOGIN_FALLIDO el correo intentado queda en "detalle" (no hay usuario autenticado).
    ip=$(python3 -c 'import sys,json; d=json.loads(sys.argv[1]); print(next((r.get("ip") or "" for r in d if sys.argv[2] in ((r.get("detalle") or "") + (r.get("email") or ""))),"NO-REGISTRADO"))' "$aud" "$email")
    if [[ "$ip" == "NO-REGISTRADO" ]]; then fallo "A4 auditoría" "No quedó registrado el login fallido $via"
    elif [[ "$ip" == "$falsa" ]]; then fallo "A4 IP falsificada" "La auditoría guardó la IP falsa $falsa $via"
    elif [[ "$ip" == 172.30.1.10 || "$ip" == 172.30.1.20 ]]; then fallo "A4 IP del proxy" "Se guardó la IP del proxy ($ip) $via, no la del cliente"
    else bien "A4 auditoría guarda $ip (no la falsa) $via"; fi
  done
fi

# ---------------- A5: usuarios SQL ----------------
sesiones=$(dc exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA_PASSWORD" -C -h -1 -W \
  -Q "SET NOCOUNT ON; SELECT DISTINCT login_name FROM sys.dm_exec_sessions WHERE is_user_process = 1 AND program_name NOT LIKE 'SQLCMD%'" 2>&1)
grep -qx "rm_api" <<<"$sesiones" && bien "A5 la API entra como rm_api" || fallo "A5 SQL" "No hay sesiones de rm_api: $sesiones"
grep -qx "sa" <<<"$sesiones" && fallo "A5 sa" "Un servicio está conectado como sa" || bien "A5 ningún servicio usa sa"

# ---------------- M4: contenedores sin root ----------------
for s in api-core worker bff nginx ml-service; do
  usuario=$(docker inspect -f '{{.Config.User}}' "$(dc ps -q "$s")" 2>/dev/null)
  if [[ -z "$usuario" || "$usuario" == "0" || "$usuario" == "root" ]]; then fallo "M4 root" "$s corre como root (User='$usuario')"
  else bien "M4 $s corre como $usuario"; fi
  ro=$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}} {{.HostConfig.CapDrop}}' "$(dc ps -q "$s")")
  [[ "$ro" == "true [ALL]" ]] && bien "M4 $s solo lectura y sin capacidades" || fallo "M4 endurecimiento" "$s: $ro"
done

echo "::notice title=Borde F1 (${#OK[@]} OK, $FALLAS fallas)::$(printf '%s; ' "${OK[@]}")"
exit $(( FALLAS > 0 ))
