<#
.SYNOPSIS
  Genera los secretos locales de ReservaMed (hallazgo C1: ningun secreto vive en el repositorio).

.DESCRIPTION
  - API Core: guarda Jwt:Key, Ml:ApiKey, Seguridad:TotpClave y Huella:Clave en "dotnet user-secrets".
  - BFF: escribe bff/.env con BFF_SESSION_KEY (cookie de sesion sellada, fase 2) y HUELLA_KEY
    (lector de huella simulado, fase 3).
  - ml-service: escribe ml-service/.env con la misma ML_API_KEY y la conexion ODBC.
  - Docker: escribe .env en la raiz con contrasenas aleatorias: sa (solo db-init), un usuario SQL por
    servicio, el administrador de RabbitMQ y un usuario por servicio, JWT y ML (hallazgos C1 y A5).
  Los archivos .env estan en .gitignore. Si ya existen, se reutilizan sus valores salvo que use -Forzar.

.PARAMETER Forzar
  Regenera todos los secretos aunque ya existan.

.PARAMETER SoloDocker
  Solo escribe .env (no toca user-secrets ni ml-service/.env). Lo usa CI.

.PARAMETER DriverOdbc
  Driver ODBC para el ml-service local (por defecto "ODBC Driver 18 for SQL Server").

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/configurar-secretos.ps1
#>
param(
    [switch]$Forzar,
    [switch]$SoloDocker,
    [string]$DriverOdbc = "ODBC Driver 18 for SQL Server"
)

$ErrorActionPreference = "Stop"
$raiz = Split-Path -Parent $PSScriptRoot

function Nuevo-Secreto([int]$bytes) {
    $buffer = New-Object byte[] $bytes
    # Create().GetBytes funciona en Windows PowerShell 5.1 y en PowerShell 7.
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($buffer)
    $rng.Dispose()
    # Base64 URL-safe, sin relleno: sirve en .env, JSON y URLs.
    return [Convert]::ToBase64String($buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Escribir-Env([string]$ruta, [string[]]$lineas) {
    # UTF-8 sin BOM: docker compose no acepta el BOM en archivos .env.
    [System.IO.File]::WriteAllLines($ruta, $lineas, (New-Object System.Text.UTF8Encoding $false))
}

function Leer-Env([string]$ruta) {
    $valores = @{}
    if (Test-Path $ruta) {
        foreach ($linea in Get-Content $ruta) {
            if ($linea -match '^\s*([A-Z_][A-Z0-9_]*)\s*=\s*(.*)\s*$') { $valores[$Matches[1]] = $Matches[2] }
        }
    }
    return $valores
}

function Valor-O-Nuevo($actuales, [string]$clave, [scriptblock]$generar) {
    if (-not $Forzar -and $actuales.ContainsKey($clave) -and $actuales[$clave].Length -ge 16) { return $actuales[$clave] }
    return & $generar
}

$envMl = Join-Path $raiz "ml-service/.env"
$envDocker = Join-Path $raiz ".env"
$ml = Leer-Env $envMl
$docker = Leer-Env $envDocker

$mlKey   = Valor-O-Nuevo $ml "ML_API_KEY" { Nuevo-Secreto 32 }
$jwtKey  = Valor-O-Nuevo $docker "JWT_KEY" { Nuevo-Secreto 48 }
$saPass  = Valor-O-Nuevo $docker "SA_PASSWORD" { "Rm1!" + (Nuevo-Secreto 18) }
$rbPass  = Valor-O-Nuevo $docker "RABBIT_PASS" { Nuevo-Secreto 24 }
# A5: un usuario por servicio en SQL Server y RabbitMQ.
$sqlApi    = Valor-O-Nuevo $docker "SQL_API_PASS" { "Rm1!" + (Nuevo-Secreto 18) }
$sqlWorker = Valor-O-Nuevo $docker "SQL_WORKER_PASS" { "Rm1!" + (Nuevo-Secreto 18) }
$sqlMl     = Valor-O-Nuevo $docker "SQL_ML_PASS" { "Rm1!" + (Nuevo-Secreto 18) }
$sqlSeed   = Valor-O-Nuevo $docker "SQL_ML_SEED_PASS" { "Rm1!" + (Nuevo-Secreto 18) }
$rbApi     = Valor-O-Nuevo $docker "RABBIT_API_PASS" { Nuevo-Secreto 24 }
$rbWorker  = Valor-O-Nuevo $docker "RABBIT_WORKER_PASS" { Nuevo-Secreto 24 }
$rbMl      = Valor-O-Nuevo $docker "RABBIT_ML_PASS" { Nuevo-Secreto 24 }
# F2: clave AES-256 de los secretos TOTP (A2) y clave de la cookie sellada del BFF (A1).
# Si TOTP_KEY cambia, los autenticadores ya enrolados dejan de servir (se pide enrolar de nuevo).
$totpKey   = Valor-O-Nuevo $docker "TOTP_KEY" { Nuevo-Secreto 32 }
$bffKey    = Valor-O-Nuevo $docker "BFF_SESSION_KEY" { Nuevo-Secreto 32 }
# F3 (C3): clave maestra de los lectores de huella. La comparten la API (valida) y el simulador
# del lector en el BFF (firma); el navegador nunca la ve.
$huellaKey = Valor-O-Nuevo $docker "HUELLA_KEY" { Nuevo-Secreto 32 }
# F4 (M7): clave de la llave maestra de SQL Server (TDE y certificados de respaldo). OJO: si cambia
# despues de creada la llave, db-init no la reemplaza; los certificados siguen protegidos por la anterior.
$tdePass   = Valor-O-Nuevo $docker "SQL_TDE_PASS" { "Rm1!" + (Nuevo-Secreto 18) }

# C2: en Docker, Webpay usa el ambiente de INTEGRACION de Transbank. Esta clave es publica
# (documentacion de Transbank, comercio 597055555532) y solo sirve con tarjetas de prueba.
# En produccion se reemplaza por la clave real del comercio.
$webpayIntegracion = "579B532A7440BB0C9079DED94D31EA1615BACEB56610332264630D42D0A36B1C"
$webpayKey = if ($docker.ContainsKey("WEBPAY_API_KEY") -and $docker["WEBPAY_API_KEY"]) { $docker["WEBPAY_API_KEY"] } else { $webpayIntegracion }

if (-not $SoloDocker) {
# --- API Core: user-secrets ---
$api = Join-Path $raiz "api-core/src/ReservaMed.Api"
dotnet user-secrets set "Jwt:Key" $jwtKey --project $api | Out-Null
dotnet user-secrets set "Ml:ApiKey" $mlKey --project $api | Out-Null
dotnet user-secrets set "Seguridad:TotpClave" $totpKey --project $api | Out-Null
dotnet user-secrets set "Huella:Clave" $huellaKey --project $api | Out-Null
Write-Host "API Core: Jwt:Key, Ml:ApiKey, Seguridad:TotpClave y Huella:Clave guardados en user-secrets." -ForegroundColor Green

# --- bff/.env (desarrollo local con npm run start:dev) ---
Escribir-Env (Join-Path $raiz "bff/.env") @(
    "# Generado por scripts/configurar-secretos.ps1 - no subir a Git"
    "BFF_SESSION_KEY=$bffKey"
    "HUELLA_KEY=$huellaKey"
    "API_CORE_URL=http://localhost:5080"
)
Write-Host "BFF: bff/.env escrito." -ForegroundColor Green

# --- ml-service/.env ---
Escribir-Env $envMl @(
    "# Generado por scripts/configurar-secretos.ps1 - no subir a Git"
    "ML_API_KEY=$mlKey"
    "ML_DB=DRIVER={$DriverOdbc};SERVER=localhost;DATABASE=ReservaMed;Trusted_Connection=yes;TrustServerCertificate=yes"
)
Write-Host "ml-service: .env escrito (misma ML_API_KEY que la API)." -ForegroundColor Green
}

# --- .env para docker compose ---
# Se conservan los ajustes no secretos que el usuario haya agregado (p. ej. HTTP_PORT=8081).
$extras = @()
foreach ($k in @("HTTP_PORT")) { if ($docker.ContainsKey($k) -and $docker[$k]) { $extras += "$k=$($docker[$k])" } }
Escribir-Env $envDocker (@(
    "# Generado por scripts/configurar-secretos.ps1 - no subir a Git"
    "SA_PASSWORD=$saPass"
    "JWT_KEY=$jwtKey"
    "TOTP_KEY=$totpKey"
    "BFF_SESSION_KEY=$bffKey"
    "HUELLA_KEY=$huellaKey"
    "ML_API_KEY=$mlKey"
    "RABBIT_USER=reservamed"
    "RABBIT_PASS=$rbPass"
    "SQL_API_PASS=$sqlApi"
    "SQL_WORKER_PASS=$sqlWorker"
    "SQL_ML_PASS=$sqlMl"
    "SQL_ML_SEED_PASS=$sqlSeed"
    "SQL_TDE_PASS=$tdePass"
    "RABBIT_API_PASS=$rbApi"
    "RABBIT_WORKER_PASS=$rbWorker"
    "RABBIT_ML_PASS=$rbMl"
    "WEBPAY_API_KEY=$webpayKey"
) + $extras)
Write-Host "Docker: .env escrito en la raiz del proyecto." -ForegroundColor Green

# --- secretos/ para docker compose (F5, ASVS V6.4.1) ---
# Los servicios de la aplicacion reciben sus claves como Docker secrets (archivos en /run/secrets),
# no como variables de entorno: asi no aparecen en "docker inspect". Mismos valores que .env.
$carpetaSecretos = Join-Path $raiz "secretos"
New-Item -ItemType Directory -Force -Path $carpetaSecretos | Out-Null
$secretos = [ordered]@{
    SA_PASSWORD = $saPass; SQL_API_PASS = $sqlApi; SQL_WORKER_PASS = $sqlWorker; SQL_ML_PASS = $sqlMl
    SQL_ML_SEED_PASS = $sqlSeed; SQL_TDE_PASS = $tdePass; RABBIT_PASS = $rbPass; RABBIT_API_PASS = $rbApi
    RABBIT_WORKER_PASS = $rbWorker; RABBIT_ML_PASS = $rbMl; JWT_KEY = $jwtKey; TOTP_KEY = $totpKey
    HUELLA_KEY = $huellaKey; BFF_SESSION_KEY = $bffKey; ML_API_KEY = $mlKey; WEBPAY_API_KEY = $webpayKey
}
foreach ($k in $secretos.Keys) {
    # Sin salto de linea final ni BOM: el archivo es exactamente el valor.
    [System.IO.File]::WriteAllText((Join-Path $carpetaSecretos $k), $secretos[$k], (New-Object System.Text.UTF8Encoding $false))
}
Write-Host "Docker: $($secretos.Count) secretos escritos en secretos/ (Docker secrets)." -ForegroundColor Green

Write-Host ""
Write-Host "Listo. Reinicie la API en Visual Studio y el ml-service para que tomen los nuevos valores."
if (-not (Test-Path (Join-Path $raiz "certs/reservamed.crt"))) {
    Write-Host "Para Docker falta HTTPS: ejecute scripts/generar-certificados.ps1 (requiere mkcert)." -ForegroundColor Yellow
}
