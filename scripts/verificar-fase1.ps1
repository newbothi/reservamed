<#
.SYNOPSIS
  Verifica en este PC la fase 1 de seguridad (C2 C4 A4 A5 M4 M8 M9) contra el sistema levantado
  con docker compose, y hace una prueba funcional completa (reserva -> RabbitMQ TLS -> ML -> Worker).

.DESCRIPTION
  Es la version local del job "borde" de CI. No cambia la configuracion: solo consulta.
  La prueba funcional crea una reserva de prueba para ignacio@demo.cl y al final la anula.
  El resultado queda en verificacion-fase1.txt (en .gitignore) para revisarlo despues.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verificar-fase1.ps1
#>
$ErrorActionPreference = 'Continue'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$salida = Join-Path $raiz 'verificacion-fase1.txt'
$script:ok = 0
$script:fallas = 0
$script:lineas = New-Object System.Collections.Generic.List[string]

function Escribir([string]$texto, [string]$color = 'Gray') {
    Write-Host $texto -ForegroundColor $color
    $script:lineas.Add($texto)
}
function Bien([string]$id, [string]$texto)  { $script:ok++;     Escribir "  OK     [$id] $texto" 'Green' }
function Falla([string]$id, [string]$texto) { $script:fallas++; Escribir "  FALLA  [$id] $texto" 'Red' }
function Info([string]$texto)               { Escribir "         $texto" 'DarkGray' }
function Seccion([string]$titulo)           { Escribir ''; Escribir "== $titulo ==" 'Cyan' }
function Revisar([string]$id, [bool]$cond, [string]$siOk, [string]$siFalla) {
    if ($cond) { Bien $id $siOk } else { Falla $id $siFalla }
}

# Lee .env (claves que la verificacion necesita: SA_PASSWORD, HTTP_PORT)
$envDocker = @{}
if (Test-Path '.env') {
    foreach ($l in Get-Content '.env') { if ($l -match '^\s*([A-Z_]+)=(.*)$') { $envDocker[$Matches[1]] = $Matches[2] } }
}
$httpPort = if ($envDocker['HTTP_PORT']) { $envDocker['HTTP_PORT'] } else { '80' }
$base = 'https://localhost'
$curlImg = 'curlimages/curl:8.16.0@sha256:463eaf6072688fe96ac64fa623fe73e1dbe25d8ad6c34404a669ad3ce1f104b6'
$ca = (Join-Path $raiz 'certs\ca.pem')

# curl dentro de la red "app" de Docker (Linux + OpenSSL, igual que CI)
function CurlRed([string[]]$argumentos) {
    $todos = @('run', '--rm', '--network', 'reservamed_app', '-v', "${ca}:/ca.pem:ro", $curlImg) + $argumentos
    & docker @todos 2>&1
}

Escribir "Verificacion fase 1 - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - $env:COMPUTERNAME" 'White'

# ---------------------------------------------------------------- Estado
Seccion 'Contenedores'
$esperado = @{ 'sqlserver' = 'running'; 'rabbitmq' = 'running'; 'api-core' = 'running'; 'worker' = 'running';
               'ml-service' = 'running'; 'bff' = 'running'; 'nginx' = 'running';
               'db-init' = 'exited'; 'rabbit-init' = 'exited'; 'ml-bootstrap' = 'exited' }
$estados = @{}
foreach ($l in (docker compose ps -a --format '{{.Service}}|{{.State}}|{{.ExitCode}}|{{.Status}}')) {
    $p = $l -split '\|'; $estados[$p[0]] = $p
}
foreach ($s in $esperado.Keys | Sort-Object) {
    $e = $estados[$s]
    if (-not $e) { Falla 'estado' "$s no existe"; continue }
    $okEstado = ($e[1] -eq $esperado[$s]) -and ($esperado[$s] -eq 'running' -or $e[2] -eq '0')
    Revisar 'estado' $okEstado "$s : $($e[3])" "$s : $($e[3]) (se esperaba $($esperado[$s]))"
}

# ---------------------------------------------------------------- C4 HTTPS
Seccion 'C4 - HTTPS'
$r = & curl.exe --ssl-no-revoke -s -o NUL -w '%{http_code} %{redirect_url}' "http://localhost:$httpPort/citas" 2>&1
Revisar 'C4' ("$r" -eq '301 https://localhost/citas') "http://localhost:$httpPort redirige a https (301)" "Redireccion inesperada: '$r'"

$r = & curl.exe --ssl-no-revoke -s -o NUL -w '%{http_code}' "$base/" 2>&1
Revisar 'C4' ("$r" -eq '200') 'https://localhost responde 200 y Windows confia en el certificado (mkcert)' "https://localhost devolvio '$r' (si es 000, el certificado no es de confianza)"

foreach ($v in @(@('1.2', '--tlsv1.2', '--tls-max', '1.2'), @('1.3', '--tlsv1.3', '--tls-max', '1.3'))) {
    $r = CurlRed @('-s', '-o', '/dev/null', '-w', '%{http_code}', '--cacert', '/ca.pem', '--resolve', 'localhost:8443:172.30.1.10', $v[1], $v[2], $v[3], 'https://localhost:8443/')
    Revisar 'C4' ("$r" -eq '200') "Acepta TLS $($v[0])" "TLS $($v[0]) fallo: $r"
}
foreach ($v in @(@('1.0', '--tlsv1.0', '--tls-max', '1.0'), @('1.1', '--tlsv1.1', '--tls-max', '1.1'))) {
    $r = CurlRed @('-s', '-o', '/dev/null', '-w', '%{http_code}', '-k', '--ciphers', 'DEFAULT@SECLEVEL=0', '--resolve', 'localhost:8443:172.30.1.10', $v[1], $v[2], $v[3], 'https://localhost:8443/')
    Revisar 'C4' ("$r" -ne '200') "Rechaza TLS $($v[0])" "Se acepto TLS $($v[0])"
}
$r = & curl.exe --ssl-no-revoke -s -o NUL -w '%{http_code}' -H 'Host: evil.example' "$base/" 2>&1
Revisar 'C4' ("$r" -eq '421') 'Host ajeno responde 421' "Host ajeno devolvio '$r'"

# ---------------------------------------------------------------- M8 cabeceras
Seccion 'M8 - Cabeceras de seguridad'
$cab = (& curl.exe --ssl-no-revoke -s -D - -o NUL "$base/" 2>&1) -join "`n"
$req = @('strict-transport-security: max-age=31536000', "content-security-policy: default-src 'self'", 'x-frame-options: DENY',
         'x-content-type-options: nosniff', 'permissions-policy:', 'referrer-policy: strict-origin-when-cross-origin',
         'cross-origin-opener-policy: same-origin')
foreach ($h in $req) { Revisar 'M8' ($cab -match "(?im)^$([regex]::Escape($h))") ($h.Split(':')[0]) "Falta: $h" }
Revisar 'M8' (-not ($cab -match '(?im)^x-powered-by')) 'Sin X-Powered-By' 'Se expone X-Powered-By'
Revisar 'M8' (-not ($cab -match '(?im)^server: nginx/\d')) 'Server sin version' 'Server expone la version'
$cabApi = (& curl.exe --ssl-no-revoke -s -D - -o NUL "$base/health" 2>&1) -join "`n"
Revisar 'M8' ($cabApi -match '(?im)^content-security-policy') 'Respuestas de la API tambien llevan CSP' '/health sin CSP'
Revisar 'M8' (-not ($cabApi -match '(?im)^server: kestrel')) 'Sin Server: Kestrel' 'Se expone Kestrel'

# ---------------------------------------------------------------- C2 Webpay
Seccion 'C2 - Webpay simulado fuera de produccion'
$cuerpo = (& curl.exe --ssl-no-revoke -s "$base/webpay-mock/pagar?token_ws=x" 2>&1) -join ''
Revisar 'C2' (-not ($cuerpo -match 'webpay')) 'NGINX no publica /webpay-mock/' 'NGINX publica la pagina simulada'
$r = CurlRed @('-s', '-o', '/dev/null', '-w', '%{http_code}', 'http://api-core:8080/webpay-mock/pagar')
Revisar 'C2' ("$r" -eq '404') 'La API en Production responde 404 en /webpay-mock/' "La API respondio '$r'"
$cfgApi = (docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' (docker compose ps -q api-core)) -join "`n"
Revisar 'C2' ($cfgApi -match 'Webpay__Modo=Integracion') 'Webpay en modo Integracion (Transbank)' 'Webpay no esta en modo Integracion'

# ---------------------------------------------------------------- A5 / M9 red y RabbitMQ
Seccion 'A5 / M9 - Red interna, usuarios por servicio y AMQPS'
foreach ($l in (docker ps --filter 'label=com.docker.compose.project=reservamed' --format '{{.Label "com.docker.compose.service"}}|{{.Ports}}')) {
    $p = $l -split '\|', 2
    $publicado = $p[1] -match '->'
    if ($p[0] -eq 'nginx') { Revisar 'A5' $publicado "nginx publica solo: $($p[1])" 'nginx no publica puertos' }
    else { Revisar 'A5' (-not $publicado) "$($p[0]) sin puertos en el host" "$($p[0]) publica puertos: $($p[1])" }
}
docker compose exec -T rabbitmq wget -q -T 5 -O /dev/null https://www.google.com 2>$null | Out-Null
Revisar 'A5' ($LASTEXITCODE -ne 0) 'La red de datos no tiene salida a Internet' 'La red de datos sale a Internet'

$lis = (docker compose exec -T rabbitmq rabbitmq-diagnostics -q listeners 2>&1) -join "`n"
Revisar 'M9' ($lis -match '5671.*amqp/ssl') 'RabbitMQ escucha AMQPS en 5671' "Sin AMQPS: $lis"
Revisar 'M9' (-not ($lis -match 'port: 5672|5672, protocol: amqp\b')) 'Sin AMQP en texto plano (5672)' 'Hay AMQP sin TLS'
$perm = (docker compose exec -T rabbitmq rabbitmqctl -q list_permissions -p reservamed 2>&1) -join "`n"
foreach ($u in 'rm_api', 'rm_worker', 'rm_ml') { Revisar 'A5' ($perm -match "(?m)^$u\s") "Usuario RabbitMQ $u en el vhost" "Falta $u" }
Revisar 'A5' (-not ($perm -match '(?m)^reservamed\s')) 'El administrador no tiene permisos en el vhost' 'El administrador tiene permisos en el vhost'
$con = (docker compose exec -T rabbitmq rabbitmqctl -q list_connections user ssl 2>&1) -join "`n"
foreach ($u in 'rm_worker', 'rm_ml') { Revisar 'M9' ($con -match "(?m)^$u\s+true") "$u conectado por TLS" "$u no esta conectado por TLS" }

# ---------------------------------------------------------------- M4 contenedores
Seccion 'M4 - Contenedores sin root y endurecidos'
foreach ($s in 'api-core', 'worker', 'bff', 'nginx', 'ml-service') {
    $id = docker compose ps -q $s
    $u = docker inspect -f '{{.Config.User}}' $id
    Revisar 'M4' ($u -and $u -ne '0' -and $u -ne 'root') "$s corre como '$u'" "$s corre como root"
    $h = docker inspect -f '{{.HostConfig.ReadonlyRootfs}} {{.HostConfig.CapDrop}} {{.HostConfig.SecurityOpt}}' $id
    Revisar 'M4' ($h -match '^true \[ALL\] \[no-new-privileges:true\]') "${s}: solo lectura, sin capacidades, no-new-privileges" "${s}: $h"
}

# ---------------------------------------------------------------- A4 IP real
Seccion 'A4 - IP del cliente en la auditoria'
function Json([string]$metodo, [string]$url, $cuerpo = $null, [string]$token = $null, [hashtable]$extra = @{}) {
    $h = @{} + $extra
    if ($token) { $h['Authorization'] = "Bearer $token" }
    $p = @{ Method = $metodo; Uri = $url; Headers = $h; ContentType = 'application/json'; UseBasicParsing = $true }
    if ($null -ne $cuerpo) { $p['Body'] = ($cuerpo | ConvertTo-Json -Compress) }
    try { return Invoke-RestMethod @p } catch { return $null }
}
$sufijo = Get-Random -Minimum 1000 -Maximum 9999
Json 'POST' "$base/api/auth/login" @{ email = "a4-nginx-$sufijo@prueba.cl"; password = 'x' } $null @{ 'X-Forwarded-For' = '203.0.113.66' } | Out-Null
Json 'POST' "$base/bff/auth/login" @{ email = "a4-bff-$sufijo@prueba.cl"; password = 'x' } $null @{ 'X-Forwarded-For' = '203.0.113.77' } | Out-Null
$admin = Json 'POST' "$base/api/auth/login" @{ email = 'admin@reservamed.cl'; password = 'Demo1234!' }
if (-not $admin) { Falla 'A4' 'No se pudo iniciar sesion como admin' }
else {
    $aud = Json 'GET' "$base/api/auditoria?accion=LOGIN_FALLIDO&top=50" $null $admin.token
    foreach ($c in @(@("a4-nginx-$sufijo@prueba.cl", '203.0.113.66', 'via NGINX'), @("a4-bff-$sufijo@prueba.cl", '203.0.113.77', 'via BFF'))) {
        $reg = $aud | Where-Object { "$($_.detalle)$($_.email)" -like "*$($c[0])*" } | Select-Object -First 1
        if (-not $reg) { Falla 'A4' "No se registro el login fallido $($c[2])" }
        elseif ($reg.ip -eq $c[1]) { Falla 'A4' "La auditoria guardo la IP falsa $($c[1]) $($c[2])" }
        elseif ($reg.ip -in '172.30.1.10', '172.30.1.20') { Falla 'A4' "Se guardo la IP del proxy ($($reg.ip)) $($c[2])" }
        else { Bien 'A4' "IP falsa ignorada $($c[2]); se guardo $($reg.ip)" }
    }
}

# ---------------------------------------------------------------- A5 SQL
Seccion 'A5 - Usuarios de SQL Server'
$sa = $envDocker['SA_PASSWORD']
$q = "SET NOCOUNT ON; SELECT DISTINCT login_name FROM sys.dm_exec_sessions WHERE is_user_process = 1 AND program_name NOT LIKE 'SQLCMD%'"
$ses = docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $sa -C -h -1 -W -Q $q 2>&1
$ses = @($ses | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
Info ("Sesiones activas: " + ($ses -join ', '))
Revisar 'A5' ($ses -contains 'rm_api') 'La API entra como rm_api' 'No hay sesiones de rm_api'
Revisar 'A5' (-not ($ses -contains 'sa')) 'Ningun servicio usa sa' 'Un servicio esta conectado como sa'
$roles = (docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $sa -C -d ReservaMed -h -1 -W -Q "SET NOCOUNT ON; SELECT r.name + ':' + m.name FROM sys.database_role_members x JOIN sys.database_principals r ON r.principal_id = x.role_principal_id JOIN sys.database_principals m ON m.principal_id = x.member_principal_id WHERE m.name LIKE 'rm[_]%'" 2>&1) -join ' '
Revisar 'A5' (-not ($roles -match 'db_owner')) 'Ningun usuario de servicio es db_owner' "Hay un usuario de servicio db_owner: $roles"

# ---------------------------------------------------------------- Prueba funcional
Seccion 'Prueba funcional: reserva -> RabbitMQ (TLS) -> ML y Worker'
$paciente = Json 'POST' "$base/bff/auth/login" @{ email = 'ignacio@demo.cl'; password = 'Demo1234!' }
if (-not $paciente) { Falla 'E2E' 'Login de ignacio@demo.cl por el BFF fallo' }
else {
    Bien 'E2E' 'Login del paciente por el BFF'
    $esps = Json 'GET' "$base/bff/catalogo"
    $desde = (Get-Date).AddDays(2).ToString('yyyy-MM-dd'); $hasta = (Get-Date).AddDays(12).ToString('yyyy-MM-dd')
    $cita = $null
    foreach ($e in @($esps.especialidades | Select-Object -First 4)) {
        $dias = Json 'GET' "$base/bff/agenda/disponibilidad?especialidadId=$($e.id)&desde=$desde&hasta=$hasta"
        foreach ($d in @($dias)) { foreach ($p in @($d.profesionales)) { foreach ($h in @($p.horas | Select-Object -Last 2)) {
            if ($cita) { break }
            $cita = Json 'POST' "$base/bff/citas" @{ profesionalId = $p.profesionalId; sedeId = $p.sedeId; inicio = $h.inicio.Substring(0, 16); modalidadPago = 'EN_CLINICA' } $paciente.token
            if ($cita) { Info "Reserva de prueba: cita $($cita.citaId), $($e.nombre), $($h.inicio)" }
        } } }
        if ($cita) { break }
    }
    if (-not $cita) { Falla 'E2E' 'No se pudo crear una reserva de prueba (sin cupos o regla de negocio)' }
    else {
        Bien 'E2E' "Reserva creada por el BFF (cita $($cita.citaId), estado $($cita.estado))"
        $riesgo = $null
        for ($i = 0; $i -lt 20 -and -not ($riesgo -and $riesgo.disponible); $i++) {
            Start-Sleep -Seconds 3
            $riesgo = Json 'GET' "$base/api/ml/citas/$($cita.citaId)/riesgo" $null $admin.token
        }
        Revisar 'E2E' ($riesgo -and $riesgo.disponible) "El ml-service calculo el riesgo por evento AMQPS: $($riesgo.nivel) $([math]::Round(100 * [double]$riesgo.probabilidad))%" 'El riesgo no llego en 60 s (API -> RabbitMQ -> ml-service)'
        $logW = (docker compose logs --since 3m worker 2>&1) -join "`n"
        Revisar 'E2E' ($logW -match 'EMAIL a') 'El Worker recibio el evento y genero la notificacion' 'El Worker no registro la notificacion'
        $logA = (docker compose logs api-core 2>&1) -join "`n"
        Revisar 'M9' ($logA -match 'Conectado a RabbitMQ en rabbitmq:5671 \(vhost reservamed, TLS True\)') 'La API publica por AMQPS (5671, vhost reservamed)' 'La API no muestra conexion TLS a RabbitMQ'
        $anul = $null
        try { Invoke-WebRequest -Method POST -Uri "$base/bff/citas/$($cita.citaId)/cancelar" -Headers @{ Authorization = "Bearer $($paciente.token)" } -ContentType 'application/json' -Body '{"motivo":"verificacion fase 1"}' -UseBasicParsing | Out-Null; $anul = $true } catch { $anul = $false }
        Revisar 'E2E' $anul 'Reserva de prueba anulada (limpieza)' 'No se pudo anular la reserva de prueba'
    }
}

# ---------------------------------------------------------------- Resumen
Seccion 'Resumen'
$color = if ($script:fallas -eq 0) { 'Green' } else { 'Yellow' }
Escribir "  $($script:ok) OK, $($script:fallas) fallas" $color
[System.IO.File]::WriteAllLines($salida, $script:lineas, (New-Object System.Text.UTF8Encoding $false))
Escribir "  Resultado guardado en $salida"
