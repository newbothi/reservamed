<#
.SYNOPSIS
  Verifica la fase 2 de seguridad (identidad y sesion: A1 A2 A4 A7 A8 M1 M2 B4) contra el sistema
  levantado con docker compose. Gate de la fase: un token robado o revocado no sirve.

.DESCRIPTION
  Corre igual en este PC (Windows PowerShell 5.1 o PowerShell 7) y en CI (pwsh en Linux).
  - Usa solo el borde publico (https://localhost) y Mailpit (http://127.0.0.1:8025).
  - Personal: inicia sesion como qa@reservamed.cl (administrador de QA, clave demo) con TOTP.
    El secreto de su autenticador queda en certs\qa-totp.txt (carpeta en .gitignore). Si falta o
    ya no sirve, se reinicia el TOTP de ESA cuenta por SQL y se vuelve a enrolar.
  - Pacientes: registra cuentas nuevas con datos ficticios (RUT aleatorio 30-40 millones) y las
    activa con el enlace que llega a Mailpit. Las reservas de prueba se anulan al final.
  El resultado queda en verificacion-fase2.txt (en .gitignore).

.PARAMETER CI
  Emite anotaciones de GitHub Actions y termina con codigo 1 si hay fallas.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verificar-fase2.ps1
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

$salida = Join-Path $raiz 'verificacion-fase2.txt'
$archivoTotp = Join-Path $raiz 'certs/qa-totp.txt'
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
    if ($CI) { Write-Host ("::error title=F2 " + $id + "::" + ($texto -replace '%', '%25' -replace "`r?`n", ' ')) }
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
        if ($script:llamadasLogin -gt 4) { Start-Sleep -Milliseconds 6500 }
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
        # 429 de NGINX (HTML, sin JSON): se espera y se reintenta una vez.
        if ($status -eq 429 -and $texto -notmatch '^\s*\{' -and $intento -eq 1) { Start-Sleep -Seconds 15; continue }
        $setCookie = @()
        $vals = $null
        if ($resp.Headers.TryGetValues('Set-Cookie', [ref]$vals)) { $setCookie = @($vals) }
        foreach ($c in $setCookie) {
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
    $r = docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $sa -C -d ReservaMed -h -1 -W -b -Q "SET NOCOUNT ON; $consulta" 2>&1
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

Escribir "Verificacion fase 2 - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - $env:COMPUTERNAME" 'White'
$sufijo = Get-Random -Minimum 100000 -Maximum 999999

# ---------------------------------------------------------------- Infraestructura
Seccion 'Infraestructura de la fase 2'
$estado = (docker compose ps --format '{{.Service}}|{{.State}}' 2>&1) -join ' '
Revisar 'A7' ($estado -match 'mailpit\|running') 'Mailpit (buzon de pruebas) en ejecucion' "Mailpit no esta corriendo: $estado"
$puerto = (docker compose port mailpit 8025 2>&1) -join ' '
Revisar 'A7' ($puerto -match '^127\.0\.0\.1:8025') "La bandeja de Mailpit solo escucha en este PC ($puerto)" "Mailpit publicado fuera de 127.0.0.1: '$puerto'"
$logApi = (docker compose logs api-core 2>&1) -join "`n"
Revisar 'A2' ($logApi -notmatch 'TotpClave') 'La API arranco con su clave TOTP' 'La API reclama por Seguridad:TotpClave'

# ---------------------------------------------------------------- Borde
Seccion 'A1/A2 - La API ya no se publica; todo pasa por el BFF'
$r = Pedir $null 'GET' '/api/catalogo/sedes'
Revisar 'A1' ($r.Status -eq 404) '/api/ responde 404 en el borde' "/api/catalogo/sedes respondio $($r.Status)"
$r = Pedir $null 'GET' '/fhir/Patient/1'
Revisar 'A1' ($r.Status -eq 404) '/fhir/ responde 404 en el borde' "/fhir/Patient/1 respondio $($r.Status)"
$r = Pedir $null 'GET' '/bff/bo/api/auth/yo'
Revisar 'A1' ($r.Status -eq 404) 'El proxy del backoffice no expone /api/auth' "/bff/bo/api/auth/yo respondio $($r.Status)"
$r = Pedir $null 'GET' '/bff/bo/api/agenda/dia'
Revisar 'A1' ($r.Status -eq 401) 'Sin cookie de sesion el backoffice responde 401' "Sin sesion respondio $($r.Status)"

Seccion 'CSRF - acciones con la cookie solo desde el propio sitio'
$r = Pedir $null 'POST' '/bff/auth/salir' @{} @{} -SinCabeceraApp
Revisar 'CSRF' ($r.Status -eq 403) 'Sin la cabecera X-ReservaMed: 403' "Sin cabecera respondio $($r.Status)"
$r = Pedir $null 'POST' '/bff/auth/salir' @{} @{ Origin = 'https://evil.example' }
Revisar 'CSRF' ($r.Status -eq 403) 'Origin de otro sitio: 403' "Origin ajeno respondio $($r.Status)"
$r = Pedir $null 'POST' '/bff/auth/salir' @{} @{ 'Sec-Fetch-Site' = 'cross-site' }
Revisar 'CSRF' ($r.Status -eq 403) 'Sec-Fetch-Site cross-site: 403' "cross-site respondio $($r.Status)"

# ---------------------------------------------------------------- A4 (via BFF)
Seccion 'A4 - IP del cliente en la auditoria'
$intrusos = @(
    @("a4-portal-$sufijo@prueba.cl", '203.0.113.66', 'via portal', '/bff/auth/login'),
    @("a4-bo-$sufijo@prueba.cl", '203.0.113.77', 'via backoffice', '/bff/bo/auth/login')
)
foreach ($c in $intrusos) { Pedir $null 'POST' $c[3] @{ email = $c[0]; password = 'x' } @{ 'X-Forwarded-For' = $c[1] } | Out-Null }

# ---------------------------------------------------------------- A2 personal con TOTP
Seccion 'A2 - Personal: contrasena + TOTP'
$staff = Nuevo-Navegador
$qa = 'qa@reservamed.cl'
function Login-Qa($nav) {
    $r = Pedir $nav 'POST' '/bff/bo/auth/login' @{ email = $qa; password = 'Demo1234!' }
    if ($r.Json -and $r.Json.estado -eq 'ENROLAR_TOTP') {
        Set-Content -Path $archivoTotp -Value $r.Json.secretoTotp -Encoding Ascii
        Info 'Autenticador de qa@reservamed.cl enrolado (secreto en certs\qa-totp.txt)'
    }
    return $r
}
$r = Login-Qa $staff
$enrolo = $r.Json -and $r.Json.estado -eq 'ENROLAR_TOTP'
if ($r.Json -and $r.Json.estado -eq 'TOTP' -and -not (Test-Path $archivoTotp)) {
    Info 'qa@reservamed.cl ya tenia TOTP y no hay secreto local: se reinicia solo esa cuenta y se vuelve a enrolar.'
    Sql "UPDATE dbo.Usuarios SET TotpActivo = 0, TotpSecreto = NULL, TotpUltimoPaso = NULL WHERE Email = N'$qa'" | Out-Null
    $staff = Nuevo-Navegador
    $r = Login-Qa $staff
    $enrolo = $r.Json -and $r.Json.estado -eq 'ENROLAR_TOTP'
}
Revisar 'A2' ($r.Status -eq 200 -and $r.Json.estado -in 'TOTP', 'ENROLAR_TOTP') "La contrasena sola no basta: pide el segundo factor ($($r.Json.estado))" "Login de personal respondio $($r.Status) $($r.Texto)"
Revisar 'A2' (-not $staff.Cookies.ContainsKey('__Host-rm_b')) 'Antes del TOTP no hay cookie de sesion' 'Se emitio sesion antes del segundo factor'
if ($enrolo) { Revisar 'A2' ($r.Json.uriTotp -like 'otpauth://totp/*') 'Enrolamiento con URI otpauth para el QR' "URI de enrolamiento invalida: $($r.Json.uriTotp)" }
$s = Pedir $staff 'GET' '/bff/bo/auth/sesion'
Revisar 'A2' ($s.Status -eq 401) 'El desafio pendiente no sirve como sesion' "Con solo la contrasena /auth/sesion respondio $($s.Status)"

$secreto = if (Test-Path $archivoTotp) { (Get-Content $archivoTotp -Raw).Trim() } else { '' }
$sesionStaff = $false
if ($secreto) {
    $paso = [RmF2]::PasoActual()
    $malo = Pedir $staff 'POST' '/bff/bo/auth/totp' @{ codigo = [RmF2]::Totp($secreto, $paso + 7) }
    Revisar 'A2' ($malo.Status -eq 401) 'Un codigo TOTP incorrecto se rechaza' "Codigo incorrecto respondio $($malo.Status)"
    $bueno = Pedir $staff 'POST' '/bff/bo/auth/totp' @{ codigo = [RmF2]::Totp($secreto, [RmF2]::PasoActual()) }
    if ($bueno.Status -eq 401 -and $bueno.Json.codigo -ne 'DESAFIO') {
        # El mismo paso de 30 s ya se uso en otra ejecucion (anti-repeticion): se usa el siguiente.
        Start-Sleep -Seconds 1
        $bueno = Pedir $staff 'POST' '/bff/bo/auth/totp' @{ codigo = [RmF2]::Totp($secreto, [RmF2]::PasoActual() + 1) }
    }
    $sesionStaff = $bueno.Status -eq 200
    Revisar 'A2' $sesionStaff "Contrasena + TOTP abre sesion de $($bueno.Json.usuario.rol)" "TOTP correcto respondio $($bueno.Status) $($bueno.Texto)"
    $b = Banderas-Cookie $bueno.SetCookie '__Host-rm_b'
    Revisar 'A1' ($b -and $b.HttpOnly -and $b.Secure -and $b.Strict -and $b.Raiz -and $b.SinDominio) 'Cookie __Host-rm_b: HttpOnly, Secure, SameSite=Strict, Path=/' "Banderas de la cookie: $($bueno.SetCookie -join ' | ')"
    Revisar 'A1' ($bueno.Texto -notmatch 'accessToken|refreshToken|eyJ') 'El navegador no recibe ningun token' 'La respuesta del login contiene tokens'
} else {
    Falla 'A2' 'No hay secreto TOTP de qa@reservamed.cl para continuar'
}

if ($sesionStaff) {
    $aud = Pedir $staff 'GET' '/bff/bo/api/auditoria?accion=LOGIN_FALLIDO&top=50'
    Revisar 'A1' ($aud.Status -eq 200) 'El backoffice consulta la API por el proxy del BFF' "Auditoria respondio $($aud.Status)"
    foreach ($c in $intrusos) {
        $reg = @($aud.Json) | Where-Object { "$($_.detalle)$($_.email)" -like "*$($c[0])*" } | Select-Object -First 1
        if (-not $reg) { Falla 'A4' "No se registro el login fallido $($c[2])" }
        elseif ($reg.ip -eq $c[1]) { Falla 'A4' "La auditoria guardo la IP falsa $($c[1]) $($c[2])" }
        elseif ($reg.ip -in '172.30.1.10', '172.30.1.20') { Falla 'A4' "Se guardo la IP del proxy ($($reg.ip)) $($c[2])" }
        else { Bien 'A4' "IP falsa ignorada $($c[2]); se guardo $($reg.ip)" }
    }
    $fhir = Pedir $staff 'GET' '/bff/bo/fhir/Patient/1'
    Revisar 'A1' ($fhir.Status -in 200, 404) "FHIR disponible para el personal via /bff/bo/fhir ($($fhir.Status))" "FHIR por el BFF respondio $($fhir.Status)"
}

# ---------------------------------------------------------------- Pacientes: registro
Seccion 'A7 B4 M2 A8 - Registro de pacientes'
$numero = Get-Random -Minimum 30000000 -Maximum 39999999
$dv = [RmF2]::DvRut($numero); $dvTxt = if ($dv -eq 10) { 'K' } else { "$dv" }
$rut = "$numero-$dvTxt"
$email = "f2-$sufijo@prueba.cl"
$clave = "Frase-larga-de-prueba-$sufijo"
function Ficha([string]$correo, [string]$pass, $pow) {
    $f = @{ rut = $rut; nombres = 'Paciente'; apellidoPaterno = 'Verificacion'; apellidoMaterno = 'Fase Dos'
            fechaNacimiento = '1991-03-14'; sexo = 'F'; email = $correo; telefono = ''; comuna = 'Providencia'
            password = $pass; financiadorId = 1; planSaludId = $null; tramoFonasa = 'B'; esCarga = $false
            afiliadoRut = $null; afiliadoNombre = $null }
    if ($pow) { $f['pow'] = $pow }
    return $f
}

$r = Pedir $null 'POST' '/bff/auth/registro' (Ficha $email $clave $null)
Revisar 'A8' ($r.Status -eq 400) 'Registro sin prueba anti-bots: 400' "Sin prueba anti-bots respondio $($r.Status)"
$r = Pedir $null 'POST' '/bff/auth/registro' (Ficha $email '123456789012' (Pow))
Revisar 'M2' ($r.Status -eq 422) 'Contrasena comun de 12 caracteres rechazada (422)' "Contrasena comun respondio $($r.Status) $($r.Texto)"
$powOk = Pow
$r = Pedir $null 'POST' '/bff/auth/registro' (Ficha $email $clave $powOk)
$mensajeNuevo = $r.Texto
Revisar 'A7' ($r.Status -eq 202) 'Registro aceptado (202): debe activar la cuenta por correo' "Registro respondio $($r.Status) $($r.Texto)"
$r = Pedir $null 'POST' '/bff/auth/registro' (Ficha "otro-$sufijo@prueba.cl" $clave $powOk)
Revisar 'A8' ($r.Status -eq 400) 'Una prueba anti-bots no se puede reutilizar' "Prueba reutilizada respondio $($r.Status)"
$r = Pedir $null 'POST' '/bff/auth/registro' (Ficha $email $clave (Pow))
Revisar 'B4' ($r.Status -eq 202 -and $r.Texto -eq $mensajeNuevo) 'Registrar un correo existente responde exactamente igual (no revela cuentas)' "Duplicado respondio $($r.Status) '$($r.Texto)' vs '$mensajeNuevo'"

$paciente = Nuevo-Navegador
$r = Pedir $paciente 'POST' '/bff/auth/login' @{ email = $email; password = $clave }
Revisar 'A7' ($r.Status -eq 403 -and $r.Json.codigo -eq 'CORREO_NO_VERIFICADO') 'Sin verificar el correo no se puede entrar' "Login sin verificar respondio $($r.Status) $($r.Texto)"

$token = $null; $avisoDuplicado = $false
for ($i = 0; $i -lt 10 -and -not $token; $i++) {
    try {
        $lista = Invoke-RestMethod -UseBasicParsing -Uri "$Mailpit/api/v1/search?query=$([uri]::EscapeDataString("to:$email"))"
        foreach ($m in @($lista.messages)) {
            $msg = Invoke-RestMethod -UseBasicParsing -Uri "$Mailpit/api/v1/message/$($m.ID)"
            $cuerpo = "$($msg.Text) $($msg.HTML)"
            if ($cuerpo -match 'verificar-correo\?token=([A-Za-z0-9_\-]+)') { $token = $Matches[1] }
            elseif ($m.Subject -notmatch 'Active') { $avisoDuplicado = $true }
        }
    } catch { }
    if (-not $token) { Start-Sleep -Seconds 2 }
}
Revisar 'A7' ([bool]$token) 'Llego a Mailpit el correo con el enlace de activacion' "No llego el correo de activacion a $email"
Revisar 'B4' $avisoDuplicado 'El intento de registro duplicado se avisa al dueno por correo' 'No llego el aviso de registro duplicado'
if ($token) {
    $v = Pedir $null 'POST' '/bff/auth/verificar-correo' @{ token = $token }
    Revisar 'A7' ($v.Status -eq 200) 'El enlace activa la cuenta' "Verificar respondio $($v.Status) $($v.Texto)"
    $v2 = Pedir $null 'POST' '/bff/auth/verificar-correo' @{ token = $token }
    Revisar 'A7' ($v2.Status -ge 400) 'El enlace no sirve dos veces' "Segundo uso del enlace respondio $($v2.Status)"
}
$r = Pedir $paciente 'POST' '/bff/auth/login' @{ email = $email; password = $clave }
$sesionPaciente = $r.Status -eq 200
Revisar 'A7' $sesionPaciente 'Cuenta activada: el paciente inicia sesion' "Login tras activar respondio $($r.Status) $($r.Texto)"
$b = Banderas-Cookie $r.SetCookie '__Host-rm_p'
Revisar 'A1' ($b -and $b.HttpOnly -and $b.Secure -and $b.Strict -and $b.Raiz) 'Cookie __Host-rm_p: HttpOnly, Secure, SameSite=Strict' "Banderas: $($r.SetCookie -join ' | ')"
Revisar 'A1' ($r.Texto -notmatch 'accessToken|refreshToken|eyJ') 'El portal no recibe ningun token' 'La respuesta del login del portal contiene tokens'

# ---------------------------------------------------------------- Reservas
$cupos = New-Object System.Collections.Generic.List[object]
if ($sesionPaciente) {
    $cat = (Pedir $null 'GET' '/bff/catalogo').Json
    $desde = (Get-Date).AddDays(2).ToString('yyyy-MM-dd'); $hasta = (Get-Date).AddDays(12).ToString('yyyy-MM-dd')
    $vistos = @{}
    foreach ($e in @($cat.especialidades)) {
        $dias = (Pedir $null 'GET' "/bff/agenda/disponibilidad?especialidadId=$($e.id)&desde=$desde&hasta=$hasta").Json
        foreach ($d in @($dias)) { foreach ($p in @($d.profesionales)) {
            if ($vistos.ContainsKey("$($p.profesionalId)") -or -not @($p.horas).Count) { continue }
            $vistos["$($p.profesionalId)"] = $true
            $h = @($p.horas)[-1]
            $cupos.Add(@{ profesionalId = $p.profesionalId; sedeId = $p.sedeId; inicio = $h.inicio.Substring(0, 16); esp = $e.nombre })
        } }
        if ($cupos.Count -ge 4) { break }
    }
}
$creadas = New-Object System.Collections.Generic.List[int]
if ($cupos.Count -ge 4) {
    Seccion 'A8 - Acaparamiento de horas'
    $estados = @()
    for ($i = 0; $i -lt 3; $i++) {
        $c = $cupos[$i]
        $r = Pedir $paciente 'POST' '/bff/citas' @{ profesionalId = $c.profesionalId; sedeId = $c.sedeId; inicio = $c.inicio; modalidadPago = 'ONLINE'; pow = (Pow) }
        $estados += $r.Status
        if ($r.Json.citaId) { $creadas.Add([int]$r.Json.citaId) }
    }
    Revisar 'A8' ($estados[0] -lt 300 -and $estados[1] -lt 300 -and $estados[2] -eq 409) "Maximo 2 reservas sin pagar: $($estados -join ', ')" "Tres reservas ONLINE respondieron $($estados -join ', ')"
    $c = $cupos[3]
    $r = Pedir $paciente 'POST' '/bff/citas' @{ profesionalId = $c.profesionalId; sedeId = $c.sedeId; inicio = $c.inicio; modalidadPago = 'EN_CLINICA' }
    Revisar 'A8' ($r.Status -eq 400) 'Cada reserva exige su prueba anti-bots' "Reserva sin prueba respondio $($r.Status)"

    Seccion 'Prueba funcional: reserva por cookie -> RabbitMQ -> ML y Worker'
    $r = Pedir $paciente 'POST' '/bff/citas' @{ profesionalId = $c.profesionalId; sedeId = $c.sedeId; inicio = $c.inicio; modalidadPago = 'EN_CLINICA'; pow = (Pow) }
    Revisar 'E2E' ($r.Status -lt 300 -and $r.Json.citaId) "Reserva por la cookie del portal (cita $($r.Json.citaId), $($c.esp))" "Reserva EN_CLINICA respondio $($r.Status) $($r.Texto)"
    if ($r.Json.citaId) {
        $id = [int]$r.Json.citaId; $creadas.Add($id)
        if ($sesionStaff) {
            $riesgo = $null
            for ($i = 0; $i -lt 20 -and -not ($riesgo -and $riesgo.disponible); $i++) {
                Start-Sleep -Seconds 3
                $riesgo = (Pedir $staff 'GET' "/bff/bo/api/ml/citas/$id/riesgo").Json
            }
            Revisar 'E2E' ($riesgo -and $riesgo.disponible) "El ml-service calculo el riesgo: $($riesgo.nivel)" 'El riesgo no llego en 60 s'
        }
        $logW = (docker compose logs --since 5m worker 2>&1) -join "`n"
        Revisar 'E2E' ($logW -match 'EMAIL a') 'El Worker genero la notificacion' 'El Worker no registro la notificacion'
    }
} elseif ($sesionPaciente) {
    Falla 'A8' "Solo hay $($cupos.Count) profesionales con cupos; se necesitan 4"
}
foreach ($id in $creadas) {
    $r = Pedir $paciente 'POST' "/bff/citas/$id/cancelar" @{ motivo = 'verificacion fase 2' }
    if ($r.Status -ge 300) { Info "No se pudo anular la cita de prueba $id ($($r.Status))" }
}
if ($creadas.Count) { Info "Reservas de prueba anuladas: $($creadas -join ', ')" }

# ---------------------------------------------------------------- Gate: robada o revocada
Seccion 'Gate F2 - una sesion robada o revocada no sirve'
if ($sesionStaff) {
    $robada = $staff.Cookies['__Host-rm_b']
    $copia = @{ Cookies = @{ '__Host-rm_b' = $robada } }
    $r = Pedir $copia 'GET' '/bff/bo/api/auditoria?top=1'
    Info "Copia de la cookie mientras la sesion esta viva: $($r.Status) (por eso es HttpOnly: JavaScript no puede leerla)"
    $alterada = $robada.Substring(0, $robada.Length - 3) + $(if ($robada.EndsWith('A')) { 'BBB' } else { 'AAA' })
    $r = Pedir @{ Cookies = @{ '__Host-rm_b' = $alterada } } 'GET' '/bff/bo/api/auditoria?top=1'
    Revisar 'A1' ($r.Status -eq 401) 'Una cookie alterada se rechaza (401)' "Cookie alterada respondio $($r.Status)"
    $r = Pedir @{ Cookies = @{ '__Host-rm_p' = $robada } } 'GET' '/bff/auth/sesion'
    Revisar 'A1' ($r.Status -eq 401) 'La cookie del backoffice no sirve en el portal' "Cookie cruzada respondio $($r.Status)"
    $r = Pedir $staff 'POST' '/bff/bo/auth/salir' @{}
    Revisar 'A1' ($r.Status -eq 204) 'Cerrar sesion revoca la sesion en la API' "Salir respondio $($r.Status)"
    $r = Pedir $copia 'GET' '/bff/bo/api/auditoria?top=1'
    Revisar 'GATE' ($r.Status -eq 401) 'Tras cerrar sesion, una copia robada de la cookie ya no sirve (401)' "La copia de la cookie siguio sirviendo tras salir: $($r.Status)"
}
if ($sesionPaciente) {
    $copiaP = @{ Cookies = @{ '__Host-rm_p' = $paciente.Cookies['__Host-rm_p'] } }
    Pedir $paciente 'POST' '/bff/auth/salir' @{} | Out-Null
    $r = Pedir $copiaP 'GET' '/bff/citas/mias'
    Revisar 'GATE' ($r.Status -eq 401) 'Portal: tras salir, la copia de la cookie no sirve (401)' "La copia siguio sirviendo: $($r.Status)"
}

# ---------------------------------------------------------------- Resumen
Seccion 'Resumen'
$color = if ($script:fallas -eq 0) { 'Green' } else { 'Yellow' }
Escribir "  $($script:ok) OK, $($script:fallas) fallas" $color
[System.IO.File]::WriteAllLines($salida, $script:lineas, (New-Object System.Text.UTF8Encoding $false))
Escribir "  Resultado guardado en $salida"
if ($CI) {
    Write-Host ("::notice title=Fase 2 ($($script:ok) OK, $($script:fallas) fallas)::" + (($script:okIds | ForEach-Object { $_ }) -join '; '))
    if ($script:fallas -gt 0) { exit 1 }
}
