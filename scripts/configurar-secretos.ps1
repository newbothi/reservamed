<#
.SYNOPSIS
  Genera los secretos locales de ReservaMed (hallazgo C1: ningún secreto vive en el repositorio).

.DESCRIPTION
  - API Core: guarda Jwt:Key y Ml:ApiKey en "dotnet user-secrets" (fuera de la carpeta del proyecto).
  - ml-service: escribe ml-service/.env con la misma ML_API_KEY y la conexión ODBC.
  - Docker: escribe .env en la raíz con contraseñas aleatorias: sa (solo db-init), un usuario SQL por
    servicio, el administrador de RabbitMQ y un usuario por servicio, JWT y ML (hallazgos C1 y A5).
  Los archivos .env están en .gitignore. Si ya existen, se reutilizan sus valores salvo que use -Forzar.

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

# C2: en Docker, Webpay usa el ambiente de INTEGRACIÓN de Transbank. Esta clave es pública
# (documentación de Transbank, comercio 597055555532) y solo sirve con tarjetas de prueba.
# En producción se reemplaza por la clave real del comercio.
$webpayIntegracion = "579B532A7440BB0C9079DED94D31EA1615BACEB56610332264630D42D0A36B1C"
$webpayKey = if ($docker.ContainsKey("WEBPAY_API_KEY") -and $docker["WEBPAY_API_KEY"]) { $docker["WEBPAY_API_KEY"] } else { $webpayIntegracion }

if (-not $SoloDocker) {
# --- API Core: user-secrets ---
$api = Join-Path $raiz "api-core/src/ReservaMed.Api"
dotnet user-secrets set "Jwt:Key" $jwtKey --project $api | Out-Null
dotnet user-secrets set "Ml:ApiKey" $mlKey --project $api | Out-Null
Write-Host "API Core: Jwt:Key y Ml:ApiKey guardados en user-secrets." -ForegroundColor Green

# --- ml-service/.env ---
Escribir-Env $envMl @(
    "# Generado por scripts/configurar-secretos.ps1 - no subir a Git"
    "ML_API_KEY=$mlKey"
    "ML_DB=DRIVER={$DriverOdbc};SERVER=localhost;DATABASE=ReservaMed;Trusted_Connection=yes;TrustServerCertificate=yes"
)
Write-Host "ml-service: .env escrito (misma ML_API_KEY que la API)." -ForegroundColor Green
}

# --- .env para docker compose ---
Escribir-Env $envDocker @(
    "# Generado por scripts/configurar-secretos.ps1 - no subir a Git"
    "SA_PASSWORD=$saPass"
    "JWT_KEY=$jwtKey"
    "ML_API_KEY=$mlKey"
    "RABBIT_USER=reservamed"
    "RABBIT_PASS=$rbPass"
    "SQL_API_PASS=$sqlApi"
    "SQL_WORKER_PASS=$sqlWorker"
    "SQL_ML_PASS=$sqlMl"
    "SQL_ML_SEED_PASS=$sqlSeed"
    "RABBIT_API_PASS=$rbApi"
    "RABBIT_WORKER_PASS=$rbWorker"
    "RABBIT_ML_PASS=$rbMl"
    "WEBPAY_API_KEY=$webpayKey"
)
Write-Host "Docker: .env escrito en la raíz del proyecto." -ForegroundColor Green

Write-Host ""
Write-Host "Listo. Reinicie la API en Visual Studio y el ml-service para que tomen los nuevos valores."
if (-not (Test-Path (Join-Path $raiz "certs/reservamed.crt"))) {
    Write-Host "Para Docker falta HTTPS: ejecute scripts/generar-certificados.ps1 (requiere mkcert)." -ForegroundColor Yellow
}
