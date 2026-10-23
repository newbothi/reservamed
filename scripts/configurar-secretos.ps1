<#
.SYNOPSIS
  Genera los secretos locales de ReservaMed (hallazgo C1: ningún secreto vive en el repositorio).

.DESCRIPTION
  - API Core: guarda Jwt:Key y Ml:ApiKey en "dotnet user-secrets" (fuera de la carpeta del proyecto).
  - ml-service: escribe ml-service/.env con la misma ML_API_KEY y la conexión ODBC.
  - Docker: escribe .env en la raíz con contraseñas aleatorias para SQL Server, RabbitMQ, JWT y ML.
  Los archivos .env están en .gitignore. Si ya existen, se reutilizan sus valores salvo que use -Forzar.

.PARAMETER Forzar
  Regenera todos los secretos aunque ya existan.

.PARAMETER DriverOdbc
  Driver ODBC para el ml-service local (por defecto "ODBC Driver 18 for SQL Server").

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/configurar-secretos.ps1
#>
param(
    [switch]$Forzar,
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

# --- .env para docker compose ---
Escribir-Env $envDocker @(
    "# Generado por scripts/configurar-secretos.ps1 - no subir a Git"
    "SA_PASSWORD=$saPass"
    "JWT_KEY=$jwtKey"
    "ML_API_KEY=$mlKey"
    "RABBIT_USER=reservamed"
    "RABBIT_PASS=$rbPass"
)
Write-Host "Docker: .env escrito en la raíz del proyecto." -ForegroundColor Green

Write-Host ""
Write-Host "Listo. Reinicie la API en Visual Studio y el ml-service para que tomen los nuevos valores."
