<#
.SYNOPSIS
  Verifica la fase 4 de seguridad (datos y privacidad: A9 M5 M6 M7 B2) contra el sistema levantado
  con docker compose. Gate de la fase: logs, mensajes y registros sin RUT ni correos, y la
  checklist OWASP ASVS nivel 2 (docs/asvs-nivel-2.md) completa.

.DESCRIPTION
  Corre igual en este PC (Windows PowerShell 5.1 o PowerShell 7) y en CI (pwsh en Linux).
  - Genera actividad con datos personales ficticios y unicos (paciente nuevo, busqueda por RUT,
    reserva y anulacion) y despues barre los logs de todos los servicios, la Outbox y el
    registro de notificaciones buscando ese RUT, ese correo, ese nombre o cualquier correo o RUT.
  - Revisa la auditoria (solo insercion, cadena de hashes integra), el enmascaramiento, el TDE y
    un respaldo cifrado; y que la API y el ml-service no publiquen documentacion ni detalles.
  - Usa las cuentas de QA de la fase 3 (TOTP en certs\qa-totp-*.txt).
  El resultado queda en verificacion-fase4.txt (en .gitignore).

.PARAMETER CI
  Emite anotaciones de GitHub Actions y termina con codigo 1 si hay fallas.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verificar-fase4.ps1
#>
param(
    [switch]$CI,
    [string]$Base = 'https://localhost'
)

$ErrorActionPreference = 'Continue'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Net.Http

$salida = Join-Path $raiz 'verificacion-fase4.txt'
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
    if ($CI) { Write-Host ("::error title=F4 " + $id + "::" + ($texto -replace '%', '%25' -replace "`r?`n", ' ')) }
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
    $todos = @('run', '--rm', '--network', $red, $curlImg) + $argumentos
    return ((& docker @todos 2>&1) -join "`n")
}

# Patrones de datos personales: cualquier correo y cualquier RUT con digito verificador.
$patronCorreo = '[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z0-9.-]+'
$patronRut = '(?<![\d.])\d{1,2}\.?\d{3}\.?\d{3}-[\dkK](?![\dA-Za-z])'

Escribir "Verificacion fase 4 - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - $env:COMPUTERNAME" 'White'
$inicio = (Get-Date).ToUniversalTime().AddSeconds(-2).ToString('yyyy-MM-ddTHH:mm:ssZ')
$sufijo = Get-Random -Minimum 100000 -Maximum 999999
$letras = -join ((1..6) | ForEach-Object { [char](Get-Random -Minimum 97 -Maximum 123) })
$nombreMarca = 'Privado' + (Get-Culture).TextInfo.ToTitleCase($letras)
$correoMarca = "f4-$sufijo@prueba.cl"
$rutMarca = Nuevo-Rut
$citasCreadas = New-Object System.Collections.Generic.List[int]

# ---------------------------------------------------------------- Sesiones
Seccion 'Sesiones del personal de QA'
$recProv = Login-Personal 'qa.recepcion@reservamed.cl'
$recLC = Login-Personal 'qa.recepcion.lc@reservamed.cl'
$admin = Login-Personal 'qa@reservamed.cl'
Revisar 'QA' ($recProv -and $recLC -and $admin) 'Sesion de recepcion (2 sedes) y admin de QA' 'Falta alguna sesion de QA (ver arriba)'
$sedeProv = [int](Sql "SELECT Id FROM dbo.Sedes WHERE Comuna = N'Providencia'")

# ---------------------------------------------------------------- Actividad con datos personales
Seccion 'Actividad con datos personales ficticios (para el barrido)'
Info "Marcas: nombre $nombreMarca, correo $correoMarca, RUT $rutMarca"
$cat = (Pedir $null 'GET' '/bff/catalogo').Json
$fonasa = @($cat.financiadores | Where-Object { $_.tipo -eq 'FONASA' })[0].id
$r = Pedir $recProv 'POST' '/bff/bo/api/admision/pacientes' @{ rut = $rutMarca; nombres = $nombreMarca; apellidoPaterno = 'Verificacion'; apellidoMaterno = 'Fase Cuatro'
    fechaNacimiento = '1979-11-03'; sexo = 'M'; email = $correoMarca; telefono = '+56 9 7' + (Get-Random -Minimum 1000000 -Maximum 9999999); comuna = 'Providencia' }
$pacienteId = if ($r.Json) { [int]$r.Json.id } else { 0 }
Pedir $recProv 'PUT' "/bff/bo/api/admision/pacientes/$pacienteId/prevision" @{ financiadorId = $fonasa; planSaludId = $null; tramoFonasa = 'C'; esCarga = $false; afiliadoRut = $null; afiliadoNombre = $null } | Out-Null
Revisar 'A9' ($pacienteId -gt 0) "Paciente ficticio creado en admision (id $pacienteId)" "Crear paciente respondio $($r.Status) $($r.Texto)"

$r = Pedir $recProv 'POST' '/bff/bo/api/admision/pacientes/buscar' @{ rut = $rutMarca }
Revisar 'A9' ($r.Status -eq 200 -and $r.Json.id -eq $pacienteId) 'Busqueda por RUT con el RUT en el cuerpo (POST), no en la URL' "Busqueda respondio $($r.Status) $($r.Texto)"
# Directo a la API dentro de la red interna (si pasara por NGINX, el RUT de esta prueba quedaria en su log).
$c = CurlRed 'reservamed_app' @('-s', '-o', '/dev/null', '-w', '%{http_code}', "http://api-core:8080/api/admision/pacientes/$rutMarca")
Revisar 'A9' ($c -in '401', '404', '405') "La ruta antigua con el RUT en la URL ya no existe ($c)" "GET con RUT en la URL respondio '$c'"

# Reserva y anulacion: generan eventos (Outbox, RabbitMQ), notificaciones (Worker) y riesgo (ml-service).
$desde = (Get-Date).AddDays(3).ToString('yyyy-MM-dd'); $hasta = (Get-Date).AddDays(12).ToString('yyyy-MM-dd')
$cupo = $null
foreach ($e in @($cat.especialidades)) {
    $dias = (Pedir $null 'GET' "/bff/agenda/disponibilidad?especialidadId=$($e.id)&sedeId=$sedeProv&desde=$desde&hasta=$hasta").Json
    foreach ($dia in @($dias)) { foreach ($p in @($dia.profesionales)) { if (-not $cupo -and @($p.horas).Count) { $cupo = @{ prof = $p.profesionalId; hora = @($p.horas)[-1] } } } }
    if ($cupo) { break }
}
$citaId = 0
if ($cupo) {
    $ini = if ($cupo.hora.inicio -is [datetime]) { $cupo.hora.inicio.ToString('yyyy-MM-ddTHH:mm', [Globalization.CultureInfo]::InvariantCulture) } else { "$($cupo.hora.inicio)".Substring(0, 16) }
    $r = Pedir $recProv 'POST' '/bff/bo/api/citas' @{ profesionalId = $cupo.prof; sedeId = $sedeProv; inicio = $ini; modalidadPago = 'EN_CLINICA'; pacienteId = $pacienteId; canal = 'PRESENCIAL' }
    if ($r.Json -and $r.Json.citaId) { $citaId = [int]$r.Json.citaId; $citasCreadas.Add($citaId) }
}
Revisar 'A9' ($citaId -gt 0) "Reserva de prueba creada (cita $citaId)" "No se pudo reservar: $($r.Status) $($r.Texto)"
if ($citaId) {
    $r = Pedir $recProv 'POST' "/bff/bo/api/citas/$citaId/cancelar" @{ motivo = 'verificacion fase 4' }
    if ($r.Status -lt 300) { [void]$citasCreadas.Remove($citaId) }
}
# Una visita con token en la query string (como el enlace de activacion de cuenta)
$tokenMarca = "TokenSecreto$sufijo"
Pedir $null 'GET' "/verificar-correo?token=$tokenMarca" | Out-Null
Info 'Esperando a que el Worker y el ml-service procesen los eventos (15 s)...'
Start-Sleep -Seconds 15

# ---------------------------------------------------------------- A9: barrido
Seccion 'A9 / Gate - Barrido: logs, mensajes y registros sin RUT ni correos'
$servicios = @('nginx', 'bff', 'api-core', 'worker', 'ml-service', 'rabbitmq', 'sqlserver')
foreach ($s in $servicios) {
    $log = (docker compose logs --no-color --since $inicio $s 2>&1) -join "`n"
    $hallazgos = @()
    foreach ($m in @($rutMarca, $rutMarca.Replace('-', ''), $correoMarca, $nombreMarca, $tokenMarca)) { if ($log.Contains($m)) { $hallazgos += $m } }
    $hallazgos += @([regex]::Matches($log, $patronCorreo) | ForEach-Object { $_.Value } | Select-Object -Unique -First 3)
    $hallazgos += @([regex]::Matches($log, $patronRut) | ForEach-Object { $_.Value } | Select-Object -Unique -First 3)
    Revisar 'A9' ($hallazgos.Count -eq 0) "Logs de $s sin RUT, correos, nombres ni tokens ($(($log -split "`n").Count) lineas)" "Logs de $s contienen: $($hallazgos -join ', ')"
}
$nginx = (docker compose logs --no-color --since $inicio nginx 2>&1) -join "`n"
Revisar 'A9' ($nginx -match 'GET /verificar-correo ') 'NGINX registra la ruta sin la query string (el token no queda en el log)' 'No se encontro el acceso a /verificar-correo en el log de NGINX'

$outbox = Sql "SELECT STRING_AGG(Payload, N' ') FROM dbo.Outbox WHERE CreadoEn >= DATEADD(MINUTE, -10, SYSDATETIME()) AND JSON_VALUE(Payload, '$.citaId') = '$citaId'"
$camposRaros = Sql "SELECT COUNT(*) FROM dbo.Outbox o CROSS APPLY OPENJSON(o.Payload) j WHERE o.CreadoEn >= DATEADD(MINUTE, -10, SYSDATETIME()) AND j.[key] NOT IN ('citaId','estado','version')"
Revisar 'A9' ($outbox -match '"citaId"' -and [int]$camposRaros -eq 0 -and $outbox -notmatch $patronCorreo -and -not $outbox.Contains($nombreMarca)) 'Eventos de la Outbox (lo que viaja por RabbitMQ): solo citaId, estado y version' "Outbox: $outbox (campos extra: $camposRaros)"

$notif = Sql "SELECT STRING_AGG(CONCAT(Destino, N' | ', Cuerpo), N' || ') FROM dbo.Notificaciones WHERE CitaId = $citaId"
Revisar 'A9' ($notif -match '\*\*\*' -and $notif -notmatch $patronCorreo -and -not $notif.Contains($nombreMarca) -and $notif.Contains('{paciente}')) 'Registro de notificaciones: destino enmascarado y sin el nombre del paciente' "Notificaciones de la cita ${citaId}: $notif"
$viejas = [int](Sql "SELECT COUNT(*) FROM dbo.Notificaciones WHERE Destino NOT LIKE N'%***%'")
$outboxViejo = [int](Sql "SELECT COUNT(*) FROM dbo.Outbox WHERE ISJSON(Payload) = 1 AND JSON_VALUE(Payload, '$.pacienteEmail') IS NOT NULL")
Revisar 'A9' ($viejas -eq 0 -and $outboxViejo -eq 0) 'Los registros anteriores a la fase 4 tambien quedaron limpios' "Quedan $viejas notificaciones y $outboxViejo eventos con datos personales"

# ---------------------------------------------------------------- M6
Seccion 'M6 - Auditoria de solo insercion y encadenada'
$err = Sql "UPDATE dbo.Auditoria SET Detalle = N'alterado' WHERE Id = (SELECT MAX(Id) FROM dbo.Auditoria)"
Revisar 'M6' ($err -match 'solo inserc') 'Ni siquiera sa puede modificar una fila de la auditoria (trigger)' "UPDATE como sa respondio: $err"
$deny = [int](Sql "SELECT COUNT(*) FROM sys.database_permissions WHERE major_id = OBJECT_ID('dbo.Auditoria') AND grantee_principal_id = USER_ID('rm_api') AND state = 'D' AND permission_name IN ('INSERT','UPDATE','DELETE')")
Revisar 'M6' ($deny -eq 3) 'La API no tiene INSERT, UPDATE ni DELETE directos (solo el procedimiento encadenado)' "Permisos DENY de rm_api sobre Auditoria: $deny"
if ($admin) {
    $r = Pedir $admin 'GET' '/bff/bo/api/auditoria/integridad'
    Revisar 'M6' ($r.Status -eq 200 -and $r.Json.integra -eq $true) "Cadena de hashes integra ($($r.Json.filas) filas revisadas)" "Integridad respondio $($r.Status) $($r.Texto)"
    $r = Pedir $recLC 'GET' "/bff/bo/api/admision/citas/$citaId"
    $aud = Pedir $admin 'GET' '/bff/bo/api/auditoria?accion=ACCESO_DENEGADO&top=20'
    Revisar 'M6' (@($aud.Json | Where-Object { $_.email -eq 'qa.recepcion.lc@reservamed.cl' }).Count -gt 0) 'Un acceso denegado (recepcion de otra sede) queda en la auditoria' 'No se registro el acceso denegado'
    $aud = Pedir $admin 'GET' '/bff/bo/api/auditoria?top=50'
    $acciones = @($aud.Json | ForEach-Object { $_.accion } | Select-Object -Unique)
    Revisar 'M6' ($acciones -contains 'VER_AUDITORIA' -and $acciones -contains 'VER_PACIENTE') "Tambien se auditan las lecturas (VER_PACIENTE, VER_AUDITORIA...)" "Acciones recientes: $($acciones -join ', ')"
    $salud = (Pedir $null 'GET' '/health').Status
    Revisar 'M6' ($salud -eq 200) '/health sano: no hubo fallas de auditoria' "/health respondio $salud (una falla de auditoria lo pone en Unhealthy)"
}

# ---------------------------------------------------------------- M7
Seccion 'M7 - Cifrado en reposo y enmascaramiento'
$tde = Sql "SELECT CONCAT(encryption_state, ' ', key_algorithm, ' ', key_length) FROM sys.dm_database_encryption_keys WHERE database_id = DB_ID('ReservaMed')"
Revisar 'M7' ($tde -eq '3 AES 256') "Base ReservaMed cifrada con TDE (AES-256)" "Estado de cifrado: '$tde'"
$mascaras = Sql "SELECT STRING_AGG(CONCAT(OBJECT_NAME(object_id), '.', name), ', ') FROM sys.masked_columns"
Revisar 'M7' (@($mascaras -split ', ').Count -ge 5) "Columnas enmascaradas: $mascaras" "Enmascaramiento incompleto: $mascaras"
$rutSeed = Sql "EXECUTE AS USER = 'rm_ml_seed'; SELECT TOP 1 Rut FROM dbo.Pacientes WHERE Id = $pacienteId; REVERT;"
$fechaSeed = Sql "EXECUTE AS USER = 'rm_ml_seed'; SELECT TOP 1 CONVERT(VARCHAR(10), FechaNacimiento, 23) FROM dbo.Pacientes WHERE Id = $pacienteId; REVERT;"
# La fecha enmascarada se ve como 1900-01-01, o como "xxxx" al convertirla a texto.
Revisar 'M7' ($rutSeed -like 'xx.xxx.*' -and ($fechaSeed -eq '1900-01-01' -or $fechaSeed -match '^x+$')) "Cuenta no clinica (rm_ml_seed) ve el RUT como $rutSeed y la fecha como $fechaSeed" "rm_ml_seed ve: $rutSeed / $fechaSeed"
$rutMl = Sql "EXECUTE AS USER = 'rm_ml'; SELECT TOP 1 Rut FROM dbo.Pacientes WHERE Id = $pacienteId; REVERT;"
Revisar 'M7' ($rutMl -like 'xx.xxx.*') "El ml-service (rm_ml) tampoco ve el RUT ($rutMl)" "rm_ml ve el RUT: $rutMl"
$rutWorker = Sql "EXECUTE AS USER = 'rm_worker'; SELECT TOP 1 Rut FROM dbo.Pacientes; REVERT;"
Revisar 'M7' ($rutWorker -match 'denied|denegad|230') 'El Worker no tiene permiso para leer el RUT' "rm_worker obtuvo: $rutWorker"

# Windows PowerShell en el PC; pwsh en Linux (CI).
$psExe = if (Get-Command powershell -ErrorAction SilentlyContinue) { 'powershell' } else { 'pwsh' }
$respaldo = (& $psExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $raiz 'scripts/respaldar-base.ps1') 2>&1) -join ' '
$ultimo = Sql "SELECT TOP 1 CONCAT(key_algorithm, ' ', encryptor_type) FROM msdb.dbo.backupset WHERE database_name = 'ReservaMed' ORDER BY backup_finish_date DESC"
Revisar 'M7' ($respaldo -match 'Respaldo cifrado' -and $ultimo -eq 'aes_256 CERTIFICATE') "Respaldo cifrado con AES-256 y certificado ($ultimo)" "Respaldo: $respaldo / ultimo en msdb: '$ultimo'"
Get-ChildItem (Join-Path $raiz 'respaldos') -Filter 'ReservaMed-*.bak' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -Skip 3 | Remove-Item -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- M5 y B2
Seccion 'M5 / B2 - Sin documentacion ni detalles internos fuera de desarrollo'
foreach ($ruta in '/docs', '/redoc', '/openapi.json') {
    $c = CurlRed 'reservamed_datos' @('-s', '-o', '/dev/null', '-w', '%{http_code}', "http://ml-service:8000$ruta")
    Revisar 'M5' ($c -eq '404') "ml-service no publica $ruta (404)" "ml-service $ruta respondio '$c'"
}
$claveFalsa = 'falsa' + ('z' * 30)   # se arma en tiempo de ejecucion: no es una credencial
$c = CurlRed 'reservamed_datos' @('-s', '-o', '/dev/null', '-w', '%{http_code}', '-H', "X-Api-Key: $claveFalsa", 'http://ml-service:8000/modelos/actual')
Revisar 'M5' ($c -eq '401') 'ml-service rechaza una API key equivocada (401)' "API key equivocada respondio '$c'"
$firmas = (docker compose exec -T ml-service sh -c 'ls /data/modelos' 2>&1) -join ' '
Revisar 'M5' ($firmas -match '\.joblib\.firma') 'El modelo activo esta firmado (.joblib.firma)' "Archivos de modelos: $firmas"
foreach ($ruta in '/openapi/v1.json', '/swagger', '/swagger/index.html') {
    $c = CurlRed 'reservamed_app' @('-s', '-o', '/dev/null', '-w', '%{http_code}', "http://api-core:8080$ruta")
    Revisar 'B2' ($c -eq '404') "La API en produccion no publica $ruta (404)" "API $ruta respondio '$c'"
}
$cuerpo = CurlRed 'reservamed_app' @('-s', '-X', 'POST', '-H', 'Content-Type: application/json', '--data', '{ esto no es json', 'http://api-core:8080/api/auth/login')
Revisar 'B2' ($cuerpo -match '"VALIDACION"' -and $cuerpo -notmatch 'Exception|StackTrace|at ReservaMed|\.cs:line') 'Un error de la API no expone excepciones ni pila' "Respuesta de error: $cuerpo"
$r = Pedir $null 'GET' '/bff/agenda/disponibilidad?especialidadId=abc&desde=x&hasta=y'
Revisar 'B2' ($r.Status -ge 400 -and $r.Texto -notmatch 'Error:|\s+at\s+\S+\s+\(|node_modules') "Un error del BFF no expone la pila ($($r.Status))" "Respuesta del BFF: $($r.Texto)"

# ---------------------------------------------------------------- Gate: ASVS
Seccion 'Gate - Checklist OWASP ASVS nivel 2'
$asvs = Join-Path $raiz 'docs/asvs-nivel-2.md'
if (Test-Path $asvs) {
    $filas = @(Get-Content $asvs -Encoding UTF8 | Where-Object { $_ -match '^\|\s*V\d+\.\d+' })
    # Completa = cada requisito evaluado: Cumple, No aplica o Brecha asignada a la fase 5, con evidencia.
    $sinEvaluar = @($filas | Where-Object { $_ -notmatch '\|\s*(Cumple|No aplica|Brecha \(F5\))\s*\|\s*\S' })
    $cumple = @($filas | Where-Object { $_ -match '\|\s*Cumple\s*\|' }).Count
    $noAplica = @($filas | Where-Object { $_ -match '\|\s*No aplica\s*\|' }).Count
    $brechas = @($filas | Where-Object { $_ -match '\|\s*Brecha \(F5\)\s*\|' })
    Revisar 'ASVS' ($filas.Count -ge 100 -and $sinEvaluar.Count -eq 0) "Checklist ASVS nivel 2 completa: $($filas.Count) requisitos evaluados ($cumple cumple, $noAplica no aplica, $($brechas.Count) brechas para F5)" "ASVS: $($filas.Count) filas, $($sinEvaluar.Count) sin evaluar: $(($sinEvaluar | Select-Object -First 3) -join ' / ')"
    foreach ($b in $brechas) { Info ("Brecha F5: " + (($b -split '\|')[1].Trim()) + ' ' + (($b -split '\|')[2].Trim())) }
} else { Falla 'ASVS' "No existe docs/asvs-nivel-2.md" }

# ---------------------------------------------------------------- Limpieza
foreach ($id in $citasCreadas) { Pedir $recProv 'POST' "/bff/bo/api/citas/$id/cancelar" @{ motivo = 'verificacion fase 4' } | Out-Null }
foreach ($n in @($recProv, $recLC, $admin)) { if ($n) { Pedir $n 'POST' '/bff/bo/auth/salir' @{} | Out-Null } }

# ---------------------------------------------------------------- Resumen
Seccion 'Resumen'
$color = if ($script:fallas -eq 0) { 'Green' } else { 'Yellow' }
Escribir "  $($script:ok) OK, $($script:fallas) fallas" $color
[System.IO.File]::WriteAllLines($salida, $script:lineas, (New-Object System.Text.UTF8Encoding $false))
Escribir "  Resultado guardado en $salida"
if ($CI) {
    Write-Host ("::notice title=Fase 4 ($($script:ok) OK, $($script:fallas) fallas)::" + (($script:okIds | ForEach-Object { $_ }) -join '; '))
    if ($script:fallas -gt 0) { exit 1 }
}
