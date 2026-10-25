<#
.SYNOPSIS
  Verifica la fase 5a de seguridad (cierre de las 18 brechas del checklist OWASP ASVS nivel 2)
  contra el sistema levantado con docker compose.

.DESCRIPTION
  Corre igual en este PC (Windows PowerShell 5.1 o PowerShell 7) y en CI (pwsh en Linux).
  - TLS interno (V1.9.1, V9.2.1, V9.2.2): cada servicio atiende solo por HTTPS/TLS con un
    certificado de la CA local y SQL Server no tiene conexiones sin cifrar.
  - Secretos (V6.4.1): ningun secreto de la aplicacion aparece en el entorno de los contenedores.
  - Cuenta (V2.1.3 V2.1.5 V2.1.6 V2.1.12 V2.2.3 V3.3.3 V3.3.4): un paciente ficticio nuevo cambia
    su contrasena, ve y cierra sesiones y recibe el aviso por correo (Mailpit).
  - Reautenticacion (V3.7.1) con la cuenta de QA de administrador (TOTP en certs\qa-totp.txt).
  - Datos del paciente (V8.3.2 V8.3.3): consentimiento, exportacion y solicitud de eliminacion.
  - Alertas (V11.1.7 V11.1.8): cuentas probadas desde una IP generan una alerta y un correo.
  - Gate: docs/asvs-nivel-2.md sin brechas (solo Cumple, No aplica o Riesgo aceptado justificado).
  El resultado queda en verificacion-fase5a.txt (en .gitignore).

.PARAMETER CI
  Emite anotaciones de GitHub Actions y termina con codigo 1 si hay fallas.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verificar-fase5a.ps1
#>
param(
    [switch]$CI,
    [string]$Base = 'https://localhost',
    [string]$Mailpit = 'http://127.0.0.1:8025'
)

$ErrorActionPreference = 'Continue'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Net.Http

$salida = Join-Path $raiz 'verificacion-fase5a.txt'
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
    if ($CI) { Write-Host ("::error title=F5a " + $id + "::" + ($texto -replace '%', '%25' -replace "`r?`n", ' ')) }
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

function Correos([string]$para) {
    try {
        $lista = Invoke-RestMethod -UseBasicParsing -Uri "$Mailpit/api/v1/search?query=$([uri]::EscapeDataString("to:$para"))"
        return @($lista.messages)
    } catch { return @() }
}
function Esperar-Correo([string]$para, [string]$patronAsunto, [int]$segundos = 30) {
    for ($i = 0; $i -lt [math]::Ceiling($segundos / 2); $i++) {
        $m = @(Correos $para | Where-Object { $_.Subject -match $patronAsunto })
        if ($m.Count) { return $m[0] }
        Start-Sleep -Seconds 2
    }
    return $null
}

# Reautenticacion TOTP de una sesion de personal: el paso de 30 s ya usado en el login no sirve dos veces.
function Reautenticar($nav, [string]$archivoSecreto) {
    $secreto = (Get-Content $archivoSecreto -Raw).Trim()
    foreach ($desfase in 1, 0) {
        $r = Pedir $nav 'POST' '/bff/bo/api/cuenta/reautenticar' @{ codigo = [RmF2]::Totp($secreto, [RmF2]::PasoActual() + $desfase) }
        if ($r.Status -eq 204) { return $r }
    }
    Info 'Esperando el siguiente codigo TOTP (30 s)...'
    Start-Sleep -Seconds 31
    return Pedir $nav 'POST' '/bff/bo/api/cuenta/reautenticar' @{ codigo = [RmF2]::Totp($secreto, [RmF2]::PasoActual()) }
}

$patronCorreo = '[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z0-9.-]+'

Escribir "Verificacion fase 5a - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - $env:COMPUTERNAME" 'White'
$sufijo = Get-Random -Minimum 100000 -Maximum 999999

# ---------------------------------------------------------------- TLS interno
Seccion 'V1.9.1 / V9.2.1 / V9.2.2 - Todo el trafico interno cifrado y verificado'
$casos = @(
    @{ Red = 'reservamed_app';   Https = 'https://api-core:8443/health'; Http = 'http://api-core:8080/health'; Nombre = 'API Core' },
    @{ Red = 'reservamed_app';   Https = 'https://bff:3443/bff/health';  Http = 'http://bff:3000/bff/health';  Nombre = 'BFF' },
    @{ Red = 'reservamed_datos'; Https = 'https://ml-service:8443/health'; Http = 'http://ml-service:8000/health'; Nombre = 'ml-service' }
)
foreach ($c in $casos) {
    $s = CurlRed $c.Red @('-s', '-o', '/dev/null', '-w', '%{http_code}', '--max-time', '10', $c.Https)
    Revisar 'V9.2.2' ($s -eq '200') "$($c.Nombre) responde por HTTPS con certificado de la CA local" "$($c.Nombre) por HTTPS respondio '$s'"
    $p = CurlRed $c.Red @('-s', '-o', '/dev/null', '-w', '%{http_code}', '--max-time', '5', $c.Http)
    Revisar 'V9.2.2' ($p -match '^000') "$($c.Nombre) no atiende HTTP en texto plano" "$($c.Nombre) por HTTP respondio '$p'"
}
# Sin la CA local, curl no confia: el certificado no es publico ni se acepta cualquiera.
$sinCa = ((& docker run --rm --network reservamed_app $curlImg -s -o /dev/null -w '%{http_code}' --max-time 10 'https://api-core:8443/health' 2>&1) -join '')
Revisar 'V9.2.1' ($sinCa -match '^000') 'Sin la CA local el certificado de la API no se acepta' "Sin CA respondio '$sinCa'"
$mgmt = CurlRed 'reservamed_datos' @('-s', '-o', '/dev/null', '-w', '%{http_code}', '--max-time', '10', 'https://rabbitmq:15671/api/overview')
Revisar 'V9.2.2' ($mgmt -eq '401') 'Consola de RabbitMQ solo por HTTPS (401 sin credenciales)' "Consola por HTTPS respondio '$mgmt'"
$mgmtHttp = CurlRed 'reservamed_datos' @('-s', '-o', '/dev/null', '-w', '%{http_code}', '--max-time', '5', 'http://rabbitmq:15672/api/overview')
Revisar 'V9.2.2' ($mgmtHttp -match '^000') 'La consola de RabbitMQ no atiende HTTP' "Consola por HTTP respondio '$mgmtHttp'"

# Actividad para que haya conexiones abiertas de los tres servicios
Pedir $null 'GET' '/bff/catalogo' | Out-Null
$sinCifrar = Sql "SELECT COUNT(1) FROM sys.dm_exec_connections WHERE net_transport = 'TCP' AND encrypt_option <> 'TRUE'"
Revisar 'V9.2.2' ($sinCifrar -eq '0') 'SQL Server: ninguna conexion TCP sin cifrar' "Conexiones sin cifrar: '$sinCifrar'"
$cifradas = Sql "SELECT STRING_AGG(login_name, ',') FROM (SELECT DISTINCT s.login_name FROM sys.dm_exec_connections c JOIN sys.dm_exec_sessions s ON s.session_id = c.session_id WHERE c.encrypt_option = 'TRUE' AND s.login_name LIKE 'rm[_]%') x"
Revisar 'V9.2.2' ($cifradas -match 'rm_api') "SQL Server: los servicios se conectan cifrado ($cifradas)" "Conexiones cifradas de servicios: '$cifradas'"
$forzado = (docker compose exec -T sqlserver cat /var/opt/mssql/mssql.conf 2>&1) -join ' '
Revisar 'V9.2.2' ($forzado -match 'forceencryption\s*=\s*1') 'SQL Server exige cifrado (forceencryption = 1)' "mssql.conf: $forzado"
$cfg = @{}
foreach ($s in 'api-core', 'worker', 'bff', 'ml-service', 'ml-bootstrap', 'db-init', 'rabbit-init') {
    $id = (docker compose ps -a -q $s 2>$null | Select-Object -First 1)
    $cfg[$s] = if ($id) { (docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' $id) -join "`n" } else { '' }
}
$confianTodo = @($cfg.Keys | Where-Object { $cfg[$_] -match '(?i)TrustServerCertificate=(True|yes)' })
Revisar 'V9.2.1' ($confianTodo.Count -eq 0) 'Ningun servicio confia en cualquier certificado de SQL Server' "Con TrustServerCertificate: $($confianTodo -join ', ')"

# ---------------------------------------------------------------- Secretos
Seccion 'V6.4.1 - Secretos como Docker secrets, no en el entorno'
$claves = 'SQL_API_PASS', 'SQL_WORKER_PASS', 'SQL_ML_PASS', 'SQL_ML_SEED_PASS', 'SQL_TDE_PASS', 'RABBIT_API_PASS', 'RABBIT_WORKER_PASS',
          'RABBIT_ML_PASS', 'JWT_KEY', 'TOTP_KEY', 'HUELLA_KEY', 'BFF_SESSION_KEY', 'ML_API_KEY'
$expuestos = @()
foreach ($s in $cfg.Keys) {
    foreach ($k in $claves) {
        $v = $envDocker[$k]
        if ($v -and $cfg[$s].Contains($v)) { $expuestos += "$k en $s" }
    }
}
Revisar 'V6.4.1' ($claves.Count -gt 0 -and $envDocker['JWT_KEY'] -and $expuestos.Count -eq 0) 'Ningun secreto de la aplicacion aparece en docker inspect (entorno de 7 contenedores)' "Secretos en el entorno: $($expuestos -join ', ')"
$faltan = @($claves | Where-Object { -not (Test-Path (Join-Path $raiz "secretos/$_")) })
Revisar 'V6.4.1' ($faltan.Count -eq 0) "Los $($claves.Count) secretos estan en secretos/ (montados en /run/secrets)" "Faltan en secretos/: $($faltan -join ', ')"
# La carpeta del proyecto puede no ser un repositorio git (copia de trabajo): se revisa .gitignore.
$ignorado = @(Get-Content (Join-Path $raiz '.gitignore') -ErrorAction SilentlyContinue | Where-Object { $_.Trim() -match '^/?secretos/?$' }).Count -gt 0
if (-not $ignorado -and (Test-Path (Join-Path $raiz '.git'))) { $ignorado = [bool](git -C $raiz check-ignore secretos/JWT_KEY 2>$null) }
Revisar 'V6.4.1' $ignorado 'secretos/ esta en .gitignore' 'secretos/ no esta en .gitignore'

# ---------------------------------------------------------------- Registro con consentimiento
Seccion 'V8.3.3 - Politica de privacidad y consentimiento'
$numero = Get-Random -Minimum 30000000 -Maximum 39999999
$dv = [RmF2]::DvRut($numero); $rut = "$numero-$(if ($dv -eq 10) { 'K' } else { $dv })"
$email = "f5-$sufijo@prueba.cl"
$clave = "Frase-larga-de-prueba-$sufijo"
function Ficha([bool]$acepta, $pow) {
    return @{ rut = $rut; nombres = 'Paciente'; apellidoPaterno = 'Verificacion'; apellidoMaterno = 'Fase Cinco'
              fechaNacimiento = '1990-06-21'; sexo = 'X'; email = $email; telefono = ''; comuna = 'Providencia'
              password = $clave; financiadorId = 1; planSaludId = $null; tramoFonasa = 'B'; esCarga = $false
              afiliadoRut = $null; afiliadoNombre = $null; aceptaPrivacidad = $acepta; pow = $pow }
}
$r = Pedir $null 'POST' '/bff/auth/registro' (Ficha $false (Pow))
Revisar 'V8.3.3' ($r.Status -eq 400 -and $r.Texto -match 'privacidad') 'Sin aceptar la politica de privacidad no se puede crear la cuenta (400)' "Registro sin consentimiento respondio $($r.Status) $($r.Texto)"
$r = Pedir $null 'POST' '/bff/auth/registro' (Ficha $true (Pow))
Revisar 'V8.3.3' ($r.Status -eq 202) 'Con el consentimiento, el registro se acepta (202)' "Registro respondio $($r.Status) $($r.Texto)"
$token = $null
for ($i = 0; $i -lt 15 -and -not $token; $i++) {
    foreach ($m in @(Correos $email)) {
        try {
            $msg = Invoke-RestMethod -UseBasicParsing -Uri "$Mailpit/api/v1/message/$($m.ID)"
            if ("$($msg.Text)" -match 'verificar-correo\?token=([A-Za-z0-9_\-]+)') { $token = $Matches[1] }
        } catch { }
    }
    if (-not $token) { Start-Sleep -Seconds 2 }
}
if ($token) { Pedir $null 'POST' '/bff/auth/verificar-correo' @{ token = $token } | Out-Null }
$acepto = Sql "SELECT CASE WHEN PrivacidadAceptadaEn IS NOT NULL THEN 'si' ELSE 'no' END FROM dbo.Usuarios WHERE Email = N'$email'"
Revisar 'V8.3.3' ($acepto -eq 'si') 'Queda registrada la fecha de aceptacion (Usuarios.PrivacidadAceptadaEn)' "PrivacidadAceptadaEn: '$acepto'"
$inicio = Pedir $null 'GET' '/'
$js = if ($inicio.Texto -match 'src="(/assets/[^"]+\.js)"') { (Pedir $null 'GET' $Matches[1]).Texto } else { '' }
Revisar 'V8.3.3' ($js -match 'Pol\S*tica de privacidad' -and $js -match '/privacidad') 'El portal publica la politica de privacidad (/privacidad)' 'No se encontro la politica de privacidad en el portal'
Revisar 'V2.1.12' ($js -match 'Mostrar contrase') 'Los campos de contrasena tienen el boton Ver/Ocultar' 'No se encontro el boton para ver la contrasena en el portal'

# ---------------------------------------------------------------- Cuenta
Seccion 'V2.1.5 / V2.1.6 / V2.2.3 / V3.3.3 / V3.3.4 - Cambio de contrasena y sesiones'
$nav = Nuevo-Navegador
$otro = Nuevo-Navegador
$l1 = Pedir $otro 'POST' '/bff/auth/login' @{ email = $email; password = $clave }
$l2 = Pedir $nav 'POST' '/bff/auth/login' @{ email = $email; password = $clave }
$sesionOk = $l1.Status -eq 200 -and $l2.Status -eq 200
Revisar 'V2.1.5' $sesionOk 'El paciente ficticio inicia sesion en dos navegadores' "Login respondio $($l1.Status) / $($l2.Status) $($l2.Texto)"
$lista = Pedir $nav 'GET' '/bff/cuenta/sesiones'
$actuales = @($lista.Json | Where-Object { $_.actual })
Revisar 'V3.3.4' ($lista.Status -eq 200 -and @($lista.Json).Count -eq 2 -and $actuales.Count -eq 1) 'Ve sus 2 sesiones abiertas y cual es la actual' "Sesiones: $($lista.Status) $($lista.Texto)"

$r = Pedir $nav 'POST' '/bff/cuenta/clave' @{ actual = 'no-es-la-clave-actual'; nueva = "Otra-frase-larga-$sufijo" }
Revisar 'V2.1.6' ($r.Status -eq 422 -and (Codigo $r) -eq 'CLAVE_ACTUAL') 'Cambiar la contrasena exige la actual (422 CLAVE_ACTUAL, sin cerrar la sesion)' "Con clave actual incorrecta respondio $($r.Status) $($r.Texto)"
$larga = "Frase-de-paso-larga-$sufijo-" + ('x' * 70) + '-A'
$r = Pedir $nav 'POST' '/bff/cuenta/clave' @{ actual = $clave; nueva = $larga }
Revisar 'V2.1.5' ($r.Status -eq 200) 'El paciente cambia su contrasena' "Cambio de contrasena respondio $($r.Status) $($r.Texto)"
Revisar 'V3.3.3' ($r.Json -and [int]$r.Json.sesionesCerradas -eq 1) 'Se cerro la otra sesion abierta' "Respuesta: $($r.Texto)"
$r = Pedir $otro 'GET' '/bff/cuenta/sesiones'
Revisar 'V3.3.3' ($r.Status -eq 401) 'La sesion del otro navegador ya no sirve (401)' "La otra sesion respondio $($r.Status)"
$r = Pedir $nav 'GET' '/bff/cuenta/sesiones'
Revisar 'V3.3.3' ($r.Status -eq 200) 'La sesion desde la que se cambio sigue activa' "La sesion actual respondio $($r.Status)"
$aviso = Esperar-Correo $email 'contrase.+cambi'
Revisar 'V2.2.3' ([bool]$aviso) 'Llego el aviso por correo del cambio de contrasena' "No llego el aviso a $email"

Seccion 'V2.1.3 - Contrasenas largas sin truncar (mas de 72 bytes)'
$casi = $larga.Substring(0, $larga.Length - 1) + 'B'
$r = Pedir (Nuevo-Navegador) 'POST' '/bff/auth/login' @{ email = $email; password = $casi }
Revisar 'V2.1.3' ($r.Status -eq 401) 'Una contrasena igual en los primeros 72 bytes pero distinta al final se rechaza' "Respondio $($r.Status)"
$r = Pedir $otro 'POST' '/bff/auth/login' @{ email = $email; password = $larga }
Revisar 'V2.1.3' ($r.Status -eq 200) 'La contrasena larga exacta entra' "Respondio $($r.Status) $($r.Texto)"
$formato = Sql "SELECT LEFT(PasswordHash, 5) FROM dbo.Usuarios WHERE Email = N'$email'"
Revisar 'V2.1.3' ($formato -eq '$rm2$') 'El hash usa el formato nuevo ($rm2$: SHA-384 + BCrypt)' "Prefijo del hash: '$formato'"
$ses = @((Pedir $nav 'GET' '/bff/cuenta/sesiones').Json)
$ajena = $ses | Where-Object { -not $_.actual } | Select-Object -First 1
if ($ajena) {
    $c = Pedir $nav 'POST' "/bff/cuenta/sesiones/$($ajena.id)/cerrar" @{}
    $despues = Pedir $otro 'GET' '/bff/cuenta/sesiones'
    Revisar 'V3.3.4' ($c.Status -eq 204 -and $despues.Status -eq 401) 'Cierra otra de sus sesiones desde Mi cuenta' "Cerrar respondio $($c.Status); la sesion cerrada respondio $($despues.Status)"
} else { Falla 'V3.3.4' "No aparecio la segunda sesion: $(($ses | ConvertTo-Json -Compress))" }

# ---------------------------------------------------------------- Reautenticacion
Seccion 'V3.7.1 - Acciones sensibles exigen confirmar el TOTP'
$admin = Login-Personal 'qa@reservamed.cl'
$usuarioId = Sql "SELECT Id FROM dbo.Usuarios WHERE Email = N'$email'"
if ($admin -and $usuarioId) {
    $r = Pedir $admin 'POST' "/bff/bo/api/usuarios/$usuarioId/desactivar" @{}
    Revisar 'V3.7.1' ($r.Status -eq 403 -and (Codigo $r) -eq 'REAUTENTICAR') 'Desactivar un usuario sin confirmar el TOTP: 403 REAUTENTICAR' "Respondio $($r.Status) $($r.Texto)"
    $r = Pedir $admin 'POST' '/bff/bo/api/cuenta/reautenticar' @{ codigo = '000000' }
    Revisar 'V3.7.1' ($r.Status -eq 422) 'Un codigo incorrecto no reautentica (422)' "Respondio $($r.Status) $($r.Texto)"
    $r = Reautenticar $admin (Join-Path $raiz 'certs/qa-totp.txt')
    Revisar 'V3.7.1' ($r.Status -eq 204) 'Con el codigo del autenticador se confirma la identidad' "Reautenticar respondio $($r.Status) $($r.Texto)"
    $d = Pedir $admin 'POST' "/bff/bo/api/usuarios/$usuarioId/desactivar" @{}
    $a = Pedir $admin 'POST' "/bff/bo/api/usuarios/$usuarioId/activar" @{}
    Revisar 'V3.7.1' ($d.Status -eq 204 -and $a.Status -eq 204) 'Confirmado el TOTP, la accion se permite durante 5 minutos' "Desactivar $($d.Status), activar $($a.Status)"
} else { Falla 'V3.7.1' 'Sin sesion de administrador de QA o sin el usuario de prueba' }

# ---------------------------------------------------------------- Datos del paciente
Seccion 'V8.3.2 - Exportar los datos y pedir su eliminacion'
$nav = Nuevo-Navegador
$r = Pedir $nav 'POST' '/bff/auth/login' @{ email = $email; password = $larga }
$exp = Pedir $nav 'GET' '/bff/cuenta/datos'
Revisar 'V8.3.2' ($exp.Status -eq 200 -and $exp.Json.paciente.email -eq $email -and $null -ne $exp.Json.accesosASuFicha) 'El paciente descarga sus datos (JSON con ficha, horas, pagos y accesos)' "Exportar respondio $($exp.Status) $($exp.Texto)"
$r = Pedir $nav 'POST' '/bff/cuenta/eliminacion' @{ clave = $larga; confirmacion = 'si' }
Revisar 'V8.3.2' ($r.Status -eq 400) 'La eliminacion exige escribir ELIMINAR (400)' "Respondio $($r.Status)"
$r = Pedir $nav 'POST' '/bff/cuenta/eliminacion' @{ clave = $larga; confirmacion = 'ELIMINAR' }
Revisar 'V8.3.2' ($r.Status -eq 204) 'Solicitud de eliminacion aceptada (204)' "Respondio $($r.Status) $($r.Texto)"
$r = Pedir (Nuevo-Navegador) 'POST' '/bff/auth/login' @{ email = $email; password = $larga }
Revisar 'V8.3.2' ($r.Status -eq 401) 'La cuenta queda cerrada: ya no se puede ingresar' "Login despues de eliminar respondio $($r.Status)"
$sol = Sql "SELECT CONCAT(d.Tipo, ':', d.Estado, ':', CASE WHEN p.Email LIKE 'eliminado-%' AND p.Telefono IS NULL THEN 'anonimo' ELSE 'con-datos' END) FROM dbo.SolicitudesDatos d JOIN dbo.Pacientes p ON p.Id = d.PacienteId WHERE d.UsuarioId = $usuarioId AND d.Tipo = 'ELIMINAR'"
Revisar 'V8.3.2' ($sol -eq 'ELIMINAR:PENDIENTE:anonimo') 'Contacto eliminado y solicitud PENDIENTE (la ficha queda en retencion legal)' "Solicitud: '$sol'"

# ---------------------------------------------------------------- Alertas
Seccion 'V11.1.7 / V11.1.8 - Alertas de actividad inusual'
if ($admin) {
    $previas = @((Pedir $admin 'GET' '/bff/bo/api/seguridad/alertas?pendientes=true').Json)
    foreach ($p in $previas) { Pedir $admin 'POST' "/bff/bo/api/seguridad/alertas/$($p.id)/revisada" @{} | Out-Null }
    $antes = @(Correos 'qa@reservamed.cl' | Where-Object { $_.Subject -match 'CUENTAS_PROBADAS_IP' }).Count
    Info 'Probando 9 cuentas inexistentes desde la misma IP (umbral: 8 en 15 minutos)...'
    for ($i = 1; $i -le 9; $i++) {
        Pedir (Nuevo-Navegador) 'POST' '/bff/auth/login' @{ email = "no-existe-$sufijo-$i@prueba.cl"; password = 'x' } | Out-Null
    }
    $alerta = $null
    for ($i = 0; $i -lt 18 -and -not $alerta; $i++) {
        Start-Sleep -Seconds 10
        $alerta = @((Pedir $admin 'GET' '/bff/bo/api/seguridad/alertas?pendientes=true').Json | Where-Object { $_.regla -eq 'CUENTAS_PROBADAS_IP' }) | Select-Object -First 1
    }
    Revisar 'V11.1.7' ([bool]$alerta) "Se genero la alerta CUENTAS_PROBADAS_IP ($($alerta.detalle))" 'No aparecio la alerta en 3 minutos'
    Revisar 'V11.1.7' ($alerta -and "$($alerta.detalle)" -notmatch $patronCorreo) 'La alerta no repite los correos probados' "Detalle: $($alerta.detalle)"
    $correo = $null
    for ($i = 0; $i -lt 10 -and -not $correo; $i++) {
        if (@(Correos 'qa@reservamed.cl' | Where-Object { $_.Subject -match 'CUENTAS_PROBADAS_IP' }).Count -gt $antes) { $correo = $true } else { Start-Sleep -Seconds 2 }
    }
    Revisar 'V11.1.8' ([bool]$correo) 'La alerta llego por correo a los administradores' 'No llego el correo de la alerta a qa@reservamed.cl'
    if ($alerta) {
        $r = Pedir $admin 'POST' "/bff/bo/api/seguridad/alertas/$($alerta.id)/revisada" @{}
        Revisar 'V11.1.7' ($r.Status -eq 204) 'El administrador marca la alerta como revisada' "Respondio $($r.Status)"
    }
    $rec = Login-Personal 'qa.recepcion@reservamed.cl'
    if ($rec) {
        $r = Pedir $rec 'GET' '/bff/bo/api/seguridad/alertas'
        Revisar 'V11.1.7' (Denegado $r) 'Recepcion no ve las alertas de seguridad' "Recepcion respondio $($r.Status)"
        Pedir $rec 'POST' '/bff/bo/auth/salir' @{} | Out-Null
    }
}
Info 'V14.2.5: el SBOM CycloneDX de cada imagen se genera y valida en CI (artefacto reportes-borde).'

# ---------------------------------------------------------------- Gate: ASVS
Seccion 'Gate - Checklist OWASP ASVS nivel 2 sin brechas'
$asvs = Join-Path $raiz 'docs/asvs-nivel-2.md'
if (Test-Path $asvs) {
    $filas = @(Get-Content $asvs -Encoding UTF8 | Where-Object { $_ -match '^\|\s*V\d+\.\d+' })
    $sinEvaluar = @($filas | Where-Object { $_ -notmatch '\|\s*(Cumple|No aplica|Riesgo aceptado)\s*\|\s*\S' })
    $cumple = @($filas | Where-Object { $_ -match '\|\s*Cumple\s*\|' }).Count
    $noAplica = @($filas | Where-Object { $_ -match '\|\s*No aplica\s*\|' }).Count
    $riesgos = @($filas | Where-Object { $_ -match '\|\s*Riesgo aceptado\s*\|' })
    $brechas = @($filas | Where-Object { $_ -match 'Brecha' })
    Revisar 'ASVS' ($filas.Count -ge 145 -and $sinEvaluar.Count -eq 0 -and $brechas.Count -eq 0) "Checklist ASVS nivel 2: $($filas.Count) requisitos, $cumple cumple, $noAplica no aplica, $($riesgos.Count) riesgo aceptado, 0 brechas" "ASVS: $($filas.Count) filas, $($sinEvaluar.Count) sin evaluar, $($brechas.Count) brechas: $(($sinEvaluar + $brechas | Select-Object -First 3) -join ' / ')"
    $sinJustificar = @($riesgos | Where-Object { (($_ -split '\|')[4]).Trim().Length -lt 80 })
    Revisar 'ASVS' ($riesgos.Count -le 2 -and $sinJustificar.Count -eq 0) "Cada riesgo aceptado tiene razon, mitigacion y plan ($(($riesgos | ForEach-Object { ($_ -split '\|')[1].Trim() }) -join ', '))" "Riesgos aceptados: $($riesgos.Count), sin justificar: $($sinJustificar.Count)"
} else { Falla 'ASVS' 'No existe docs/asvs-nivel-2.md' }

# ---------------------------------------------------------------- Limpieza
foreach ($n in @($admin)) { if ($n) { Pedir $n 'POST' '/bff/bo/auth/salir' @{} | Out-Null } }

# ---------------------------------------------------------------- Resumen
Seccion 'Resumen'
$color = if ($script:fallas -eq 0) { 'Green' } else { 'Yellow' }
Escribir "  $($script:ok) OK, $($script:fallas) fallas" $color
[System.IO.File]::WriteAllLines($salida, $script:lineas, (New-Object System.Text.UTF8Encoding $false))
Escribir "  Resultado guardado en $salida"
if ($CI) {
    Write-Host ("::notice title=Fase 5a ($($script:ok) OK, $($script:fallas) fallas)::" + (($script:okIds | ForEach-Object { $_ }) -join '; '))
    if ($script:fallas -gt 0) { exit 1 }
}
