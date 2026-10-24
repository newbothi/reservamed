<#
.SYNOPSIS
  Respaldo CIFRADO de la base ReservaMed (hallazgo M7).

.DESCRIPTION
  Ejecuta BACKUP DATABASE con cifrado AES-256 (certificado ReservaMedRespaldos, creado por
  db/07_privacidad.sql), compresion y CHECKSUM, y copia el archivo a la carpeta respaldos\
  (en .gitignore) junto con el certificado. Sin el certificado y su clave (SQL_TDE_PASS) el
  respaldo no se puede leer: guarde ambos en un lugar distinto del respaldo.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\respaldar-base.ps1
#>
param([string]$Carpeta = 'respaldos')

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz

$sa = $null
foreach ($l in Get-Content '.env') { if ($l -match '^\s*SA_PASSWORD=(.*)$') { $sa = $Matches[1] } }
if (-not $sa) { $sa = $env:SA_PASSWORD }
if (-not $sa) { throw 'Falta SA_PASSWORD en .env (ejecute scripts\configurar-secretos.ps1).' }

$nombre = "ReservaMed-$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
$enContenedor = "/var/opt/mssql/data/$nombre"
$sql = "BACKUP DATABASE ReservaMed TO DISK = N'$enContenedor' WITH COMPRESSION, CHECKSUM, INIT, " +
       "ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = ReservaMedRespaldos)"
docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $sa -C -b -Q $sql
if ($LASTEXITCODE -ne 0) { throw 'El respaldo fallo (revise que db-init haya creado el certificado ReservaMedRespaldos).' }

New-Item -ItemType Directory -Force -Path $Carpeta | Out-Null
$id = (docker compose ps -q sqlserver)
docker cp "${id}:$enContenedor" (Join-Path $Carpeta $nombre) | Out-Null
foreach ($f in 'ReservaMedRespaldos.cer', 'ReservaMedRespaldos.pvk') {
    docker cp "${id}:/var/opt/mssql/data/$f" (Join-Path $Carpeta $f) | Out-Null
}
docker compose exec -T sqlserver rm -f $enContenedor | Out-Null

$archivo = Get-Item (Join-Path $Carpeta $nombre)
Write-Host "Respaldo cifrado: $($archivo.FullName) ($([math]::Round($archivo.Length / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host 'Para restaurarlo en otro servidor se necesitan ReservaMedRespaldos.cer/.pvk y SQL_TDE_PASS.' -ForegroundColor Yellow
