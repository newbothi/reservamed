<#
.SYNOPSIS
  Genera los certificados de HTTPS local (C4) y AMQPS interno (M9) con mkcert.

.DESCRIPTION
  mkcert crea una CA local y la instala en Windows, así el navegador confía en
  https://reservamed.localhost sin advertencias. Se generan en la carpeta certs/ (en .gitignore):
    ca.pem                  certificado de la CA (la clave privada de la CA queda en la carpeta de mkcert)
    reservamed.crt/.key     reservamed.localhost, localhost, 127.0.0.1, ::1  (NGINX)
    rabbitmq.crt/.key       rabbitmq  (TLS de AMQP dentro de Docker)

  Requiere mkcert:  winget install FiloSottile.mkcert   (o: choco install mkcert)

.PARAMETER Forzar
  Regenera los certificados aunque ya existan.
#>
param([switch]$Forzar)
$ErrorActionPreference = 'Stop'

if (-not (Get-Command mkcert -ErrorAction SilentlyContinue)) {
    throw "No se encontró mkcert. Instálelo con 'winget install FiloSottile.mkcert', abra una terminal nueva y vuelva a ejecutar este script."
}

$raiz = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $raiz 'certs'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

mkcert -install
if ($LASTEXITCODE -ne 0) { throw 'mkcert -install falló.' }

function Emitir([string]$nombre, [string[]]$nombres) {
    $crt = Join-Path $dir "$nombre.crt"
    if ((Test-Path $crt) -and -not $Forzar) { Write-Host "Ya existe $nombre.crt (use -Forzar para regenerarlo)."; return }
    mkcert -cert-file $crt -key-file (Join-Path $dir "$nombre.key") @nombres
    if ($LASTEXITCODE -ne 0) { throw "mkcert falló al generar $nombre." }
}

Emitir 'reservamed' @('reservamed.localhost', 'localhost', '127.0.0.1', '::1')
Emitir 'rabbitmq' @('rabbitmq')

# Solo el certificado público de la CA: los contenedores lo usan para validar a RabbitMQ.
# La clave privada (rootCA-key.pem) nunca sale de la carpeta de mkcert.
$caroot = (mkcert -CAROOT).Trim()
Copy-Item (Join-Path $caroot 'rootCA.pem') (Join-Path $dir 'ca.pem') -Force

Write-Host ''
Write-Host "Certificados listos en $dir" -ForegroundColor Green
Write-Host 'Abra https://reservamed.localhost después de docker compose up --build.'
