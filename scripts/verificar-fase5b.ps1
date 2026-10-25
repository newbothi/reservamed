<#
.SYNOPSIS
  Verifica la fase 5b (calidad y release v2.0) contra el sistema levantado con docker compose.

.DESCRIPTION
  Corre en este PC (Windows PowerShell 5.1 o PowerShell 7). Necesita Node.js 22 (pruebas E2E) y Docker.
  - Modelo de ML: el modelo activo pasa el gate de calidad (AUC y Brier) y guarda su calibracion;
    un modelo de datos al azar no reemplaza al activo.
  - E2E y accesibilidad: e2e/ (Playwright + axe, WCAG 2.1 AA) contra https://localhost.
  - Carga: k6/carga.js (p95, errores y limite por IP de NGINX).
  - Dependencias de los frontends y del BFF sin vulnerabilidades altas (npm audit).
  - Documentacion del release (CHANGELOG, ficha y revision del modelo).
  El SAST (Semgrep), Trivy, pip-audit y NuGet se revisan en el job "Seguridad" del CI (bloqueante).
  El resultado queda en verificacion-fase5b.txt (en .gitignore).

.PARAMETER CI
  Emite anotaciones de GitHub Actions y termina con codigo 1 si hay fallas.

.PARAMETER SinE2E
  Omite las pruebas de Playwright (por ejemplo, si no tiene Node.js instalado).

.PARAMETER SinCarga
  Omite la prueba de carga con k6 (el CI la corre en un paso propio).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verificar-fase5b.ps1
#>
param(
    [switch]$CI,
    [switch]$SinE2E,
    [switch]$SinCarga,
    [string]$Base = 'https://localhost',
    [string]$Mailpit = 'http://127.0.0.1:8025'
)

$ErrorActionPreference = 'Continue'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Net.Http

$salida = Join-Path $raiz 'verificacion-fase5b.txt'
$script:ok = 0
$script:fallas = 0
$script:lineas = New-Object System.Collections.Generic.List[string]
$script:okIds = New-Object System.Collections.Generic.List[string]

function Escribir([string]$texto, [string]$color = 'Gray') {
    Write-Host $texto -ForegroundColor $color
    $script:lineas.Add($texto)
}
function Bien([string]$id, [string]$texto) { $script:ok++; $script:okIds.Add("$id $texto"); Escribir "  OK     [$id] $texto" 'Green' }
function Falla([string]$id, [string]$texto) {
    $script:fallas++
    Escribir "  FALLA  [$id] $texto" 'Red'
    if ($CI) { Write-Host ("::error title=F5b " + $id + "::" + ($texto -replace '%', '%25' -replace "`r?`n", ' ')) }
}
function Info([string]$texto) { Escribir "         $texto" 'DarkGray' }
function Seccion([string]$titulo) { Escribir ''; Escribir "== $titulo ==" 'Cyan' }
function Revisar([string]$id, [bool]$cond, [string]$siOk, [string]$siFalla) {
    if ($cond) { Bien $id $siOk } else { Falla $id $siFalla }
}

$envDocker = @{}
if (Test-Path '.env') {
    foreach ($l in Get-Content '.env') { if ($l -match '^\s*([A-Z_]+)=(.*)$') { $envDocker[$Matches[1]] = $Matches[2] } }
}
$sa = if ($envDocker['SA_PASSWORD']) { $envDocker['SA_PASSWORD'] } else { $env:SA_PASSWORD }

# ---------------------------------------------------------------- TOTP y prueba de trabajo
# C# 5 (lo compila tanto Windows PowerShell 5.1 como PowerShell 7).
Add-Type -TypeDefinition @'
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

public static class RmF2
{
    public static byte[] Base32(string s)
    {
        const string abc = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        s = s.Replace(" ", "").TrimEnd('=').ToUpperInvariant();
        byte[] r = new byte[s.Length * 5 / 8];
        int buffer = 0, bits = 0, i = 0;
        foreach (char c in s)
        {
            int v = abc.IndexOf(c);
            if (v < 0) throw new FormatException("Base32 invalido");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { r[i++] = (byte)(buffer >> (bits - 8)); bits -= 8; }
        }
        return r;
    }

    public static long PasoActual()
    {
        return (long)Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds / 30);
    }

    public static string Totp(string secretoBase32, long paso)
    {
        byte[] contador = new byte[8];
        for (int i = 7; i >= 0; i--) { contador[i] = (byte)(paso & 0xff); paso >>= 8; }
        byte[] h;
        using (HMACSHA1 mac = new HMACSHA1(Base32(secretoBase32))) { h = mac.ComputeHash(contador); }
        int o = h[h.Length - 1] & 0x0f;
        int bin = ((h[o] & 0x7f) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (bin % 1000000).ToString("D6", CultureInfo.InvariantCulture);
    }

    public static int BitsEnCero(byte[] h)
    {
        int n = 0;
        foreach (byte b in h)
        {
            if (b == 0) { n += 8; continue; }
            int x = b;
            while ((x & 0x80) == 0) { n++; x <<= 1; }
            return n;
        }
        return n;
    }

    public static long ResolverPow(string sal, int dificultad)
    {
        using (SHA256 sha = SHA256.Create())
        {
            for (long n = 0; ; n++)
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(sal + n.ToString(CultureInfo.InvariantCulture)));
                if (BitsEnCero(h) >= dificultad) return n;
            }
        }
    }

    public static int DvRut(int cuerpo)
    {
        int suma = 0, m = 2;
        while (cuerpo > 0) { suma += (cuerpo % 10) * m; cuerpo /= 10; m = m == 7 ? 2 : m + 1; }
        int dv = 11 - (suma % 11);
        return dv == 11 ? 0 : dv;   // 10 = K
    }
}
'@

# ---------------------------------------------------------------- Cliente HTTP
# Cookies manejadas a mano: asi se puede copiar o alterar una cookie y probar que no sirve.
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseCookies = $false
$handler.AllowAutoRedirect = $false
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromSeconds(60)

function Nuevo-Navegador { return @{ Cookies = @{} } }

# NGINX limita login, segundo factor y registro a 10 por minuto (rafaga 5): se espacian.
$script:rutasLogin = @('/bff/auth/login', '/bff/auth/registro', '/bff/bo/auth/login', '/bff/bo/auth/totp')
$script:llamadasLogin = 0

function Pedir($nav, [string]$metodo, [string]$ruta, $cuerpo = $null, [hashtable]$extra = @{}, [switch]$SinCabeceraApp) {
    if ($script:rutasLogin -contains $ruta) {
        $script:llamadasLogin++
        if ($script:llamadasLogin -gt 1) { Start-Sleep -Milliseconds 6500 }   # puede correr justo despues de la fase 2
    }
    for ($intento = 1; $intento -le 2; $intento++) {
        $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($metodo)), "$Base$ruta")
        if (-not $SinCabeceraApp) { [void]$req.Headers.TryAddWithoutValidation('X-ReservaMed', '1') }
        foreach ($k in $extra.Keys) { [void]$req.Headers.TryAddWithoutValidation($k, [string]$extra[$k]) }
        if ($nav -and $nav.Cookies.Count) {
            [void]$req.Headers.TryAddWithoutValidation('Cookie', (($nav.Cookies.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; '))
        }
        if ($null -ne $cuerpo) {
            $json = if ($cuerpo -is [string]) { $cuerpo } else { $cuerpo | ConvertTo-Json -Depth 6 -Compress }
            $req.Content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
        }
        try {
            $resp = $http.SendAsync($req).GetAwaiter().GetResult()
        } catch {
            return [pscustomobject]@{ Status = 0; Texto = "$($_.Exception.GetBaseException().Message)"; Json = $null; SetCookie = @() }
        }
        $texto = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $status = [int]$resp.StatusCode
        # 429 de NGINX (HTML) o del limite por IP de la API (LIMITE, 10 por minuto): el limite
        # funciona; se espera y se reintenta una vez. Un bloqueo de cuenta (BLOQUEADA) no se reintenta.
        if ($status -eq 429 -and $intento -eq 1 -and $texto -notmatch 'BLOQUEADA') {
            $espera = if ($texto -match 'LIMITE') { 40 } else { 15 }
            Info "Limite de solicitudes alcanzado en $ruta; se espera $espera s"
            Start-Sleep -Seconds $espera
            continue
        }
        $setCookie = @()
        $vals = $null
        if ($resp.Headers.TryGetValues('Set-Cookie', [ref]$vals)) { $setCookie = @($vals) }
        foreach ($c in @(if ($nav) { $setCookie })) {
            if ($c -match '^\s*([^=;\s]+)=([^;]*)') {
                $n = $Matches[1]; $v = $Matches[2]
                if (-not $v -or $c -match '(?i)max-age=0') { [void]$nav.Cookies.Remove($n) } else { $nav.Cookies[$n] = $v }
            }
        }
        $obj = $null
        if ($texto -match '^\s*[\{\[]') { try { $obj = $texto | ConvertFrom-Json } catch { $obj = $null } }
        return [pscustomobject]@{ Status = $status; Texto = $texto; Json = $obj; SetCookie = $setCookie }
    }
}

function Pow {
    $d = (Pedir $null 'GET' '/bff/pow').Json
    if (-not $d) { return $null }
    $n = [RmF2]::ResolverPow($d.sal, [int]$d.dificultad)
    return @{ sal = $d.sal; dificultad = [int]$d.dificultad; firma = $d.firma; numero = $n }
}

function Sql([string]$consulta) {
    $r = docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $sa -C -I -d ReservaMed -h -1 -W -b -Q "SET NOCOUNT ON; $consulta" 2>&1
    return (@($r) | ForEach-Object { "$_".Trim() } | Where-Object { $_ }) -join ' '
}

function Banderas-Cookie([string[]]$setCookie, [string]$nombre) {
    $c = $setCookie | Where-Object { $_ -like "$nombre=*" } | Select-Object -First 1
    if (-not $c) { return $null }
    return @{
        HttpOnly = $c -match '(?i);\s*HttpOnly'; Secure = $c -match '(?i);\s*Secure'
        Strict = $c -match '(?i);\s*SameSite=Strict'; Raiz = $c -match '(?i);\s*Path=/(;|$)'
        SinDominio = $c -notmatch '(?i);\s*Domain='
    }
}


function Codigo($r) { if ($r.Json -and $r.Json.codigo) { return [string]$r.Json.codigo } return '' }
function Denegado($r) { return ($r.Status -in 401, 403) -or ($r.Status -eq 404 -and (Codigo $r) -eq 'SIN_ACCESO') }

# Personal: contrasena + TOTP. El secreto de cada cuenta de QA queda en certs\qa-totp-<cuenta>.txt.
function Login-Personal([string]$email) {
    $nombre = $email.Split('@')[0]
    $archivo = if ($nombre -eq 'qa') { Join-Path $raiz 'certs/qa-totp.txt' } else { Join-Path $raiz "certs/qa-totp-$nombre.txt" }
    for ($intento = 1; $intento -le 2; $intento++) {
        $nav = Nuevo-Navegador
        $r = Pedir $nav 'POST' '/bff/bo/auth/login' @{ email = $email; password = 'Demo1234!' }
        if ($r.Json -and $r.Json.estado -eq 'ENROLAR_TOTP') {
            Set-Content -Path $archivo -Value $r.Json.secretoTotp -Encoding Ascii
            Info "Autenticador de $email enrolado (secreto en certs)"
        } elseif (-not ($r.Json -and $r.Json.estado -eq 'TOTP' -and (Test-Path $archivo))) {
            if ($intento -eq 1) {
                # TOTP activo sin secreto local (o login fallido): se reinicia SOLO esta cuenta de QA.
                Sql "UPDATE dbo.Usuarios SET TotpActivo = 0, TotpSecreto = NULL, TotpUltimoPaso = NULL, IntentosFallidos = 0, BloqueadoHasta = NULL WHERE Email = N'$email'" | Out-Null
                continue
            }
            Falla 'LOGIN' "No se pudo iniciar sesion como $email ($($r.Status) $($r.Texto))"
            return $null
        }
        $secreto = (Get-Content $archivo -Raw).Trim()
        $ok = Pedir $nav 'POST' '/bff/bo/auth/totp' @{ codigo = [RmF2]::Totp($secreto, [RmF2]::PasoActual()) }
        if ($ok.Status -eq 401 -and (Codigo $ok) -ne 'DESAFIO') {
            $ok = Pedir $nav 'POST' '/bff/bo/auth/totp' @{ codigo = [RmF2]::Totp($secreto, [RmF2]::PasoActual() + 1) }
        }
        if ($ok.Status -eq 200) { return $nav }
        if ($intento -eq 1) { Remove-Item $archivo -ErrorAction SilentlyContinue; continue }
        Falla 'LOGIN' "TOTP de $email rechazado ($($ok.Status) $($ok.Texto))"
        return $null
    }
}

# Envia varias solicitudes a la vez (para probar concurrencia) y devuelve los codigos HTTP.
function Pedir-Simultaneas($nav, [string]$ruta, [object[]]$cuerpos) {
    $cookie = ($nav.Cookies.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; '
    $tareas = @()
    foreach ($c in $cuerpos) {
        $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$Base$ruta")
        [void]$req.Headers.TryAddWithoutValidation('X-ReservaMed', '1')
        [void]$req.Headers.TryAddWithoutValidation('Cookie', $cookie)
        $req.Content = New-Object System.Net.Http.StringContent(($c | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
        $tareas += $http.SendAsync($req)
    }
    [void][System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$tareas, 60000)
    $res = @()
    foreach ($t in $tareas) {
        $texto = $t.Result.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $obj = $null; try { $obj = $texto | ConvertFrom-Json } catch { }
        $res += [pscustomobject]@{ Status = [int]$t.Result.StatusCode; Json = $obj; Texto = $texto }
    }
    return $res
}

function Nuevo-Rut {
    $n = Get-Random -Minimum 30000000 -Maximum 39999999
    $dv = [RmF2]::DvRut($n)
    return "$n-$(if ($dv -eq 10) { 'K' } else { $dv })"
}


$curlImg = 'curlimages/curl:8.16.0@sha256:463eaf6072688fe96ac64fa623fe73e1dbe25d8ad6c34404a669ad3ce1f104b6'
# curl dentro de una red interna de Docker (la API y el ml-service no se publican al PC)
function CurlRed([string]$red, [string[]]$argumentos) {
    # F5: los servicios internos atienden por HTTPS; se valida su certificado con la CA local.
    $todos = @('run', '--rm', '--network', $red, '-v', "$(Join-Path $raiz 'certs\ca.pem'):/ca.pem:ro", $curlImg, '--cacert', '/ca.pem') + $argumentos
    return ((& docker @todos 2>&1) -join "`n")
}

Escribir "Verificacion fase 5b - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - $env:COMPUTERNAME" 'White'

# ---------------------------------------------------------------- ML
Seccion 'Revision del modelo de ML - calibracion y gate de calidad'
$admin = Login-Personal 'qa@reservamed.cl'
$brier = Sql "SELECT COUNT(1) FROM dbo.ModelosML WHERE Tipo = 'INASISTENCIA' AND Activo = 1 AND Brier IS NOT NULL"
if ($admin -and $brier -ne '1') {
    Info 'El modelo activo es anterior a la fase 5b (sin calibracion guardada): se reentrena.'
    $r = Pedir $admin 'POST' '/bff/bo/api/ml/modelo/entrenar' @{}
    Info "Entrenamiento: $($r.Status)"
}
$m = Sql "SELECT CONCAT(Auc, '|', Brier, '|', BrierBase, '|', PrecisionAlto, '|', TasaBase, '|', LEN(Calibracion)) FROM dbo.ModelosML WHERE Tipo = 'INASISTENCIA' AND Activo = 1"
$p = "$m".Split('|')
$gateOk = $p.Count -eq 6 -and [double]::Parse($p[0], [Globalization.CultureInfo]::InvariantCulture) -ge 0.60 -and
      [double]::Parse($p[1], [Globalization.CultureInfo]::InvariantCulture) -lt [double]::Parse($p[2], [Globalization.CultureInfo]::InvariantCulture) -and
      [double]::Parse($p[3], [Globalization.CultureInfo]::InvariantCulture) -gt [double]::Parse($p[4], [Globalization.CultureInfo]::InvariantCulture) -and [int]$p[5] -gt 100
Revisar 'ML' $gateOk "Modelo activo dentro del gate: AUC $($p[0]), Brier $($p[1]) < $($p[2]) (tasa base), calibracion por decil guardada" "Modelo activo: '$m'"
$r = Pedir $admin 'GET' '/bff/bo/api/ml/modelo'
Revisar 'ML' ($r.Status -eq 200 -and $null -ne $r.Json.brier -and @($r.Json.calibracion).Count -ge 5) 'El backoffice recibe el Brier y la calibracion del modelo' "GET modelo respondio $($r.Status)"
$sinGrupo = Sql "SELECT COUNT(1) FROM dbo.ModelosML WHERE Coeficientes LIKE '%Prevision%' OR Coeficientes LIKE '%Tramo%' OR Coeficientes LIKE '%Sexo%'"
Revisar 'ML' ($sinGrupo -eq '0') 'El modelo no usa prevision, tramo Fonasa ni sexo como variables' "Modelos con esas variables: $sinGrupo"
foreach ($doc in 'docs/ml/ficha-modelo.md', 'docs/ml/revision-modelo.md', 'CHANGELOG.md') {
    Revisar 'DOC' (Test-Path (Join-Path $raiz $doc)) "Existe $doc" "Falta $doc"
}

# ---------------------------------------------------------------- Dependencias
Seccion 'Dependencias sin vulnerabilidades altas (npm audit)'
$npm = Get-Command npm -ErrorAction SilentlyContinue
foreach ($d in 'portal', 'backoffice', 'bff') {
    if (-not $npm) { Info 'npm no esta instalado: se omite (el CI lo revisa en el job Seguridad).'; break }
    Push-Location (Join-Path $raiz $d)
    $json = (& npm audit --omit=dev --json 2>$null) -join "`n"
    Pop-Location
    $altas = 0
    try { $a = $json | ConvertFrom-Json; $altas = [int]$a.metadata.vulnerabilities.high + [int]$a.metadata.vulnerabilities.critical } catch { $altas = -1 }
    Revisar 'DEP' ($altas -eq 0) "$d sin vulnerabilidades high/critical" "${d}: npm audit $(if ($altas -lt 0) { 'no respondio' } else { "$altas altas o criticas" })"
}

# ---------------------------------------------------------------- E2E
Seccion 'E2E y accesibilidad (Playwright + axe, WCAG 2.1 AA)'
if ($SinE2E -or -not $npm) {
    Info 'Se omiten las pruebas E2E (-SinE2E o sin Node.js).'
} else {
    Push-Location (Join-Path $raiz 'e2e')
    & npm ci --no-audit --no-fund 2>&1 | Out-Null
    & npx playwright install chromium 2>&1 | Out-Null
    $env:BASE_URL = $Base
    $salidaE2E = (& npx playwright test --reporter=json 2>$null) -join "`n"
    Pop-Location
    try {
        $rep = $salidaE2E | ConvertFrom-Json
        $esperadas = [int]$rep.stats.expected; $inesperadas = [int]$rep.stats.unexpected; $inestables = [int]$rep.stats.flaky
        Revisar 'E2E' ($esperadas -gt 0 -and $inesperadas -eq 0) "Playwright: $esperadas pruebas OK ($inestables con reintento)" "Playwright: $inesperadas fallaron de $($esperadas + $inesperadas)"
        foreach ($s in @($rep.suites)) {
            foreach ($t in @($s.suites | ForEach-Object { $_.specs } | Where-Object { $_ }) + @($s.specs | Where-Object { $_ })) {
                if (-not $t.ok) { Info "Fallo: $($t.title) $((@($t.tests)[0].results | Select-Object -Last 1).error.message)" }
            }
        }
    } catch { Falla 'E2E' "No se pudo leer el resultado de Playwright: $($salidaE2E.Substring(0, [Math]::Min(300, $salidaE2E.Length)))" }
}

# ---------------------------------------------------------------- k6
Seccion 'Carga con k6 (p95 < 800 ms, < 1% errores, limite por IP activo)'
if ($SinCarga) { Info 'Se omite la prueba de carga (-SinCarga).' } else {
    # Un solo PC genera la carga: se sube el limite por IP del BFF para simular muchos pacientes; NGINX queda igual.
    $env:BFF_LIMITE_POR_MINUTO = '100000'
    docker compose up -d --no-deps bff 2>&1 | Out-Null
    for ($i = 0; $i -lt 30; $i++) { if ((Pedir $null 'GET' '/bff/health').Status -eq 200) { break }; Start-Sleep -Seconds 2 }
    $k6 = (& docker run --rm --network reservamed_app --add-host 'reservamed.localhost:172.30.1.10' `
            -v "$(Join-Path $raiz 'k6'):/k6" -v "$(Join-Path $raiz 'certs\ca.pem'):/ca.pem:ro" -e SSL_CERT_FILE=/ca.pem `
            -e BASE_URL=https://reservamed.localhost:8443 grafana/k6:1.3.0 run --quiet /k6/carga.js 2>&1) -join "`n"
    $codigoK6 = $LASTEXITCODE
    Remove-Item Env:\BFF_LIMITE_POR_MINUTO -ErrorAction SilentlyContinue
    docker compose up -d --no-deps bff 2>&1 | Out-Null
    $resultado = Join-Path $raiz 'k6/resultado.json'
    if (Test-Path $resultado) {
        $k = Get-Content $resultado -Raw | ConvertFrom-Json
        Revisar 'K6' ($codigoK6 -eq 0 -and @($k.umbralesFallidos).Count -eq 0) "Navegacion: p95 $($k.p95) ms, p99 $($k.p99) ms, errores $($k.tasaErrores)%, $($k.rps) req/s" "Umbrales fallidos: $(@($k.umbralesFallidos) -join ', ') (p95 $($k.p95) ms, errores $($k.tasaErrores)%)"
        Revisar 'K6' ([int]$k.rafaga429 -gt 0) "Rafaga: NGINX limito $($k.rafaga429) de 80 solicitudes simultaneas (429)" 'La rafaga no topo el limite de NGINX'
    } else { Falla 'K6' "k6 no dejo resultado: $($k6.Substring(0, [Math]::Min(400, $k6.Length)))" }
    for ($i = 0; $i -lt 30; $i++) { if ((Pedir $null 'GET' '/bff/health').Status -eq 200) { break }; Start-Sleep -Seconds 2 }
}

# ---------------------------------------------------------------- Limpieza
if ($admin) { Pedir $admin 'POST' '/bff/bo/auth/salir' @{} | Out-Null }

# ---------------------------------------------------------------- Resumen
Seccion 'Resumen'
$color = if ($script:fallas -eq 0) { 'Green' } else { 'Yellow' }
Escribir "  $($script:ok) OK, $($script:fallas) fallas" $color
[System.IO.File]::WriteAllLines($salida, $script:lineas, (New-Object System.Text.UTF8Encoding $false))
Escribir "  Resultado guardado en $salida"
if ($CI) {
    Write-Host ("::notice title=Fase 5b ($($script:ok) OK, $($script:fallas) fallas)::" + (($script:okIds | ForEach-Object { $_ }) -join '; '))
    if ($script:fallas -gt 0) { exit 1 }
}
