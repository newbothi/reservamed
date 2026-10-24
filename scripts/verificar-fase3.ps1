<#
.SYNOPSIS
  Verifica la fase 3 de seguridad (autorizacion y dominio: C3 C5 A3 M3 B3) contra el sistema
  levantado con docker compose. La matriz completa rol x endpoint (gate de la fase) y A6 (Webpay)
  se prueban en CI con pruebas de integracion; aqui se repiten los casos clave de punta a punta.

.DESCRIPTION
  Corre igual en este PC (Windows PowerShell 5.1 o PowerShell 7) y en CI (pwsh en Linux).
  - Usa solo el borde publico (https://localhost).
  - Personal: cuentas de QA por rol (qa.recepcion@, qa.recepcion.lc@, qa.medico@ y qa@), con
    TOTP. Los secretos quedan en certs\qa-totp-*.txt (carpeta en .gitignore). No toca los
    autenticadores de las cuentas demo.
  - Crea pacientes ficticios (RUT aleatorio 30-40 millones) y citas de prueba que anula al final.
  El resultado queda en verificacion-fase3.txt (en .gitignore).

.PARAMETER CI
  Emite anotaciones de GitHub Actions y termina con codigo 1 si hay fallas.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verificar-fase3.ps1
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

$salida = Join-Path $raiz 'verificacion-fase3.txt'
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
    if ($CI) { Write-Host ("::error title=F3 " + $id + "::" + ($texto -replace '%', '%25' -replace "`r?`n", ' ')) }
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

Escribir "Verificacion fase 3 - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - $env:COMPUTERNAME" 'White'
$sufijo = Get-Random -Minimum 100000 -Maximum 999999
$hoy = Get-Date -Format 'yyyy-MM-dd'
$citasCreadas = New-Object System.Collections.Generic.List[int]

# ---------------------------------------------------------------- Sesiones de QA
Seccion 'Sesiones del personal de QA (contrasena + TOTP)'
$recProv = Login-Personal 'qa.recepcion@reservamed.cl'
$recLC = Login-Personal 'qa.recepcion.lc@reservamed.cl'
$medico = Login-Personal 'qa.medico@reservamed.cl'
$admin = Login-Personal 'qa@reservamed.cl'
Revisar 'QA' ($recProv -and $recLC -and $medico -and $admin) 'Sesion de recepcion (2 sedes), medico y admin de QA' 'Falta alguna sesion de QA (ver arriba)'

# Datos de referencia (sa solo lee)
$sedeProv = [int](Sql "SELECT Id FROM dbo.Sedes WHERE Comuna = N'Providencia'")
$sedeLC = [int](Sql "SELECT Id FROM dbo.Sedes WHERE Comuna = N'Las Condes'")
$soto = [int](Sql "SELECT ProfesionalId FROM dbo.Usuarios WHERE Email = N'qa.medico@reservamed.cl'")
$citaProv = [int](Sql "SELECT TOP 1 Id FROM dbo.Citas WHERE SedeId = $sedeProv ORDER BY Id DESC")
$citaLC = [int](Sql "SELECT TOP 1 Id FROM dbo.Citas WHERE SedeId = $sedeLC ORDER BY Id DESC")
$citaSoto = [int](Sql "SELECT TOP 1 Id FROM dbo.Citas WHERE ProfesionalId = $soto ORDER BY Id DESC")
$citaOtro = [int](Sql "SELECT TOP 1 Id FROM dbo.Citas WHERE ProfesionalId <> $soto ORDER BY Id DESC")
$pacConSoto = [int](Sql "SELECT TOP 1 PacienteId FROM dbo.Citas WHERE ProfesionalId = $soto ORDER BY Id DESC")
$pacSinSoto = [int](Sql "SELECT TOP 1 p.Id FROM dbo.Pacientes p WHERE NOT EXISTS (SELECT 1 FROM dbo.Citas c WHERE c.PacienteId = p.Id AND c.ProfesionalId = $soto) ORDER BY p.Id DESC")
Info "Sedes $sedeProv/$sedeLC, agenda QA medico = profesional $soto; citas de referencia $citaProv, $citaLC, $citaSoto, $citaOtro"

# ---------------------------------------------------------------- A3
if ($recProv -and $recLC) {
    Seccion 'A3 - Recepcion solo ve y opera su sede'
    $r = Pedir $recProv 'GET' "/bff/bo/api/agenda/dia?fecha=$hoy"
    $sedes = @($r.Json | ForEach-Object { $_.sedeId } | Select-Object -Unique)
    Revisar 'A3' ($r.Status -eq 200 -and @($sedes | Where-Object { $_ -ne $sedeProv }).Count -eq 0) "Agenda del dia de recepcion Providencia: solo su sede ($(@($r.Json).Count) citas)" "Agenda respondio $($r.Status) con sedes: $($sedes -join ', ')"
    $r = Pedir $recProv 'GET' "/bff/bo/api/agenda/dia?fecha=$hoy&sedeId=$sedeLC"
    Revisar 'A3' ($r.Status -eq 403) 'Pedir la agenda de Las Condes cambiando sedeId: 403' "sedeId ajeno respondio $($r.Status)"
    $r = Pedir $recProv 'GET' "/bff/bo/api/admision/citas/$citaLC"
    Revisar 'A3' (Denegado $r) 'Recepcion Providencia no abre una cita de Las Condes' "Cita de otra sede respondio $($r.Status)"
    $r = Pedir $recProv 'GET' "/bff/bo/api/admision/citas/$citaProv"
    Revisar 'A3' ($r.Status -eq 200) 'Recepcion Providencia abre las citas de su sede' "Cita propia respondio $($r.Status) $($r.Texto)"
    $r = Pedir $recLC 'POST' "/bff/bo/api/citas/$citaProv/cancelar" @{ motivo = 'no deberia poder' }
    Revisar 'A3' (Denegado $r) 'Recepcion Las Condes no puede anular una cita de Providencia' "Anular cita ajena respondio $($r.Status)"
    $r = Pedir $recLC 'GET' "/bff/bo/api/ml/citas/$citaProv/riesgo"
    Revisar 'A3' (Denegado $r) 'Recepcion Las Condes no ve el riesgo de una cita de Providencia' "Riesgo de cita ajena respondio $($r.Status)"
}

# ---------------------------------------------------------------- C5
if ($medico) {
    Seccion 'C5 - El medico solo ve sus pacientes y sus citas'
    $r = Pedir $medico 'GET' "/bff/bo/fhir/Patient/$pacConSoto"
    Revisar 'C5' ($r.Status -eq 200) 'FHIR Patient de un paciente con cita en su agenda: 200' "Paciente propio respondio $($r.Status)"
    $r = Pedir $medico 'GET' "/bff/bo/fhir/Patient/$pacSinSoto"
    Revisar 'C5' ($r.Status -eq 403) 'FHIR Patient de un paciente ajeno: 403' "Paciente ajeno respondio $($r.Status)"
    $r = Pedir $medico 'GET' "/bff/bo/fhir/Appointment/$citaOtro"
    Revisar 'C5' ($r.Status -eq 403) 'FHIR Appointment de otro profesional: 403' "Cita ajena (FHIR) respondio $($r.Status)"
    $r = Pedir $medico 'GET' "/bff/bo/fhir/Appointment/$citaSoto"
    Revisar 'C5' ($r.Status -eq 200) 'FHIR Appointment de su agenda: 200' "Cita propia (FHIR) respondio $($r.Status)"
    $r = Pedir $medico 'GET' "/bff/bo/api/ml/citas/$citaOtro/riesgo"
    Revisar 'C5' (Denegado $r) 'Riesgo ML de una cita de otro profesional: denegado' "Riesgo ajeno respondio $($r.Status)"
    $r = Pedir $medico 'GET' "/bff/bo/api/citas/$citaOtro"
    Revisar 'C5' (Denegado $r) 'Detalle de una cita de otro profesional: denegado' "Cita ajena respondio $($r.Status)"
    $r = Pedir $medico 'GET' "/bff/bo/api/agenda/dia?fecha=$hoy"
    $profs = @($r.Json | ForEach-Object { $_.profesionalId } | Select-Object -Unique)
    Revisar 'C5' ($r.Status -eq 200 -and ($profs.Count -eq 0 -or ($profs.Count -eq 1 -and $profs[0] -eq $soto))) 'La agenda del medico solo trae sus citas' "Agenda del medico: $($r.Status), profesionales $($profs -join ', ')"
}

# ---------------------------------------------------------------- C3
$pacienteQa = $null
if ($recProv) {
    Seccion 'C3 - La huella la firma el lector, no el navegador'
    $cat = (Pedir $null 'GET' '/bff/catalogo').Json
    $fonasa = @($cat.financiadores | Where-Object { $_.tipo -eq 'FONASA' })[0].id
    $rut = Nuevo-Rut
    $r = Pedir $recProv 'POST' '/bff/bo/api/admision/pacientes' @{ rut = $rut; nombres = 'Paciente'; apellidoPaterno = 'Verificacion'; apellidoMaterno = 'Fase Tres'
        fechaNacimiento = '1988-07-21'; sexo = 'F'; email = "f3-$sufijo@prueba.cl"; telefono = $null; comuna = 'Providencia' }
    $pacienteQa = if ($r.Json) { [int]$r.Json.id } else { 0 }
    $p = Pedir $recProv 'PUT' "/bff/bo/api/admision/pacientes/$pacienteQa/prevision" @{ financiadorId = $fonasa; planSaludId = $null; tramoFonasa = 'B'; esCarga = $false; afiliadoRut = $null; afiliadoNombre = $null }
    Revisar 'C3' ($pacienteQa -gt 0 -and $p.Status -eq 204) "Paciente ficticio Fonasa B creado en admision ($rut)" "Crear paciente respondio $($r.Status) $($r.Texto) / prevision $($p.Status) $($p.Texto)"

    # Cita de HOY en Providencia (el bono solo se emite el dia de la atencion). 23:xx:ss no choca con cupos reales.
    $seg = Get-Random -Minimum 1 -Maximum 3000
    $salidaSql = (Sql ("DECLARE @Prof INT, @Prest INT; SELECT TOP 1 @Prof = ProfesionalId, @Prest = PrestacionId FROM dbo.HorariosProfesional WHERE SedeId = $sedeProv ORDER BY Id; " +
        "DECLARE @Ini DATETIME2(0) = DATEADD(SECOND, $seg, DATEADD(HOUR, 23, CAST(CAST(SYSDATETIME() AS DATE) AS DATETIME2(0)))); " +
        "INSERT INTO dbo.Citas (PacienteId, ProfesionalId, SedeId, PrestacionId, FechaHoraInicio, FechaHoraFin, Estado, Canal, OcupaCupo, CreadoEn) OUTPUT INSERTED.Id " +
        "VALUES ($pacienteQa, @Prof, $sedeProv, @Prest, @Ini, DATEADD(MINUTE, 20, @Ini), 'RESERVADA', 'PRESENCIAL', 1, SYSDATETIME())"))
    $cita = 0
    if ($salidaSql -match '^\d+$') { $cita = [int]$salidaSql; $citasCreadas.Add($cita); Info "Cita de prueba de hoy: $cita" }
    else { Falla 'C3' "No se pudo crear la cita de prueba de hoy: $salidaSql" }
}
if ($recProv -and $cita -gt 0) {

    $r = Pedir $recProv 'POST' "/bff/bo/api/admision/citas/$cita/bono" @{ huellaVerificada = $true; derivadorRut = $null }
    Revisar 'C3' ($r.Status -eq 422 -and (Codigo $r) -eq 'HUELLA_REQUERIDA') 'El navegador diciendo huellaVerificada: true ya no emite bono (422)' "huellaVerificada respondio $($r.Status) $($r.Texto)"

    $r = Pedir $recLC 'POST' "/bff/bo/api/admision/citas/$cita/huella/desafio" @{}
    Revisar 'C3' (Denegado $r) 'Recepcion de otra sede no puede pedir el desafio de huella' "Desafio desde otra sede respondio $($r.Status)"
    $r = Pedir $null 'POST' '/bff/bo/lector/leer' @{ desafio = @{ nonce = 'x' * 20; citaId = $cita; rut = $rut; sedeId = $sedeProv }; resultado = 'COINCIDE' }
    Revisar 'C3' ($r.Status -eq 401) 'El lector no firma sin sesion del personal (401)' "Lector sin sesion respondio $($r.Status)"
    if ($medico) {
        $r = Pedir $medico 'POST' '/bff/bo/lector/leer' @{ desafio = @{ nonce = 'x' * 20; citaId = $cita; rut = $rut; sedeId = $sedeProv }; resultado = 'COINCIDE' }
        Revisar 'C3' ($r.Status -eq 403) 'El lector no firma para un medico (403)' "Lector con medico respondio $($r.Status)"
    }

    $d = (Pedir $recProv 'POST' "/bff/bo/api/admision/citas/$cita/huella/desafio" @{}).Json
    $lect = (Pedir $recProv 'POST' '/bff/bo/lector/leer' @{ desafio = @{ nonce = $d.nonce; citaId = $d.citaId; rut = $d.rut; sedeId = $d.sedeId }; resultado = 'COINCIDE' }).Json
    $comprobante = "$($lect.comprobante)"
    Revisar 'C3' ($comprobante -like 'v1.*.*') 'El lector de la sede firmo la lectura (comprobante v1)' "El lector no devolvio comprobante: $comprobante"

    # Comprobante alterado: se cambia el RUT dentro de la lectura y se deja la firma original.
    $partes = $comprobante.Split('.')
    $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($partes[1].Replace('-', '+').Replace('_', '/')).PadRight([math]::Ceiling($partes[1].Length / 4) * 4, '=')))
    $falso = $json.Replace($d.rut, '11111111-1')
    $payloadFalso = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($falso)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $r = Pedir $recProv 'POST' "/bff/bo/api/admision/citas/$cita/bono" @{ comprobanteHuella = "v1.$payloadFalso.$($partes[2])"; derivadorRut = $null }
    Revisar 'C3' ($r.Status -eq 422 -and (Codigo $r) -eq 'HUELLA_INVALIDA') 'Una lectura alterada en el navegador se rechaza (firma invalida)' "Comprobante alterado respondio $($r.Status) $($r.Texto)"

    $r = Pedir $recProv 'POST' "/bff/bo/api/admision/citas/$cita/valorizar" @{}
    $r = Pedir $recProv 'POST' "/bff/bo/api/admision/citas/$cita/bono" @{ comprobanteHuella = $comprobante; derivadorRut = $null }
    Revisar 'C3' ($r.Status -eq 200 -and $r.Json.folio) "Con la lectura firmada se emite el bono (folio $($r.Json.folio))" "Bono con lectura firmada respondio $($r.Status) $($r.Texto)"
    $a = Pedir $recProv 'POST' "/bff/bo/api/admision/citas/$cita/bono/anular" @{}
    $r = Pedir $recProv 'POST' "/bff/bo/api/admision/citas/$cita/bono" @{ comprobanteHuella = $comprobante; derivadorRut = $null }
    Revisar 'C3' ($a.Status -eq 204 -and $r.Status -eq 422 -and (Codigo $r) -eq 'HUELLA_INVALIDA') 'La misma lectura no sirve para un segundo bono' "Reuso respondio anular=$($a.Status), bono=$($r.Status) $($r.Texto)"

    $d = (Pedir $recProv 'POST' "/bff/bo/api/admision/citas/$cita/huella/desafio" @{}).Json
    $lect = (Pedir $recProv 'POST' '/bff/bo/lector/leer' @{ desafio = @{ nonce = $d.nonce; citaId = $d.citaId; rut = $d.rut; sedeId = $d.sedeId }; resultado = 'NO_COINCIDE' }).Json
    $r = Pedir $recProv 'POST' "/bff/bo/api/admision/citas/$cita/bono" @{ comprobanteHuella = $lect.comprobante; derivadorRut = $null }
    Revisar 'C3' ($r.Status -eq 422 -and (Codigo $r) -eq 'HUELLA_NO_COINCIDE') 'Si la huella no coincide no se emite bono' "Huella que no coincide respondio $($r.Status) $($r.Texto)"
}

# ---------------------------------------------------------------- B3
if ($recProv -and $pacienteQa) {
    Seccion 'B3 - Una hora por dia con el mismo profesional, tambien con reservas simultaneas'
    $desde = (Get-Date).AddDays(3).ToString('yyyy-MM-dd'); $hasta = (Get-Date).AddDays(12).ToString('yyyy-MM-dd')
    $grupo = $null
    foreach ($e in @($cat.especialidades)) {
        $dias = (Pedir $null 'GET' "/bff/agenda/disponibilidad?especialidadId=$($e.id)&sedeId=$sedeProv&desde=$desde&hasta=$hasta").Json
        foreach ($dia in @($dias)) { foreach ($p in @($dia.profesionales)) {
            if (-not $grupo -and @($p.horas).Count -ge 4) { $grupo = @{ prof = $p.profesionalId; horas = @($p.horas | Select-Object -First 4) } }
        } }
        if ($grupo) { break }
    }
    if (-not $grupo) { Falla 'B3' 'No hay un profesional con 4 horas libres el mismo dia en Providencia' }
    else {
        $cuerpos = @($grupo.horas | ForEach-Object {
            $ini = if ($_.inicio -is [datetime]) { $_.inicio.ToString('yyyy-MM-ddTHH:mm', [Globalization.CultureInfo]::InvariantCulture) } else { "$($_.inicio)".Substring(0, 16) }
            @{ profesionalId = $grupo.prof; sedeId = $sedeProv; inicio = $ini; modalidadPago = 'EN_CLINICA'; pacienteId = $pacienteQa; canal = 'PRESENCIAL' } })
        $res = Pedir-Simultaneas $recProv '/bff/bo/api/citas' $cuerpos
        $estados = @($res | ForEach-Object { $_.Status })
        foreach ($x in $res) { if ($x.Json -and $x.Json.citaId) { $citasCreadas.Add([int]$x.Json.citaId) } }
        $creadas = @($estados | Where-Object { $_ -in 200, 201 }).Count
        $conflictos = @($estados | Where-Object { $_ -eq 409 }).Count
        Revisar 'B3' ($creadas -eq 1 -and $conflictos -eq 3) "4 reservas simultaneas con el mismo profesional el mismo dia: 1 creada, 3 rechazadas ($($estados -join ', '))" "Respuestas: $($estados -join ', ')"
    }
}

# ---------------------------------------------------------------- M3
if ($admin) {
    Seccion 'M3 - Entradas invalidas: 400 uniforme, nunca 500'
    $r = Pedir $admin 'POST' '/bff/bo/api/admision/pacientes' @{ rut = (Nuevo-Rut); nombres = ('a' * 500); apellidoPaterno = 'Largo'; apellidoMaterno = $null
        fechaNacimiento = '1990-01-01'; sexo = 'F'; email = "largo-$sufijo@prueba.cl"; telefono = $null; comuna = $null }
    $campos = @($r.Json.errores | ForEach-Object { $_.campo })
    Revisar 'M3' ($r.Status -eq 400 -and (Codigo $r) -eq 'VALIDACION' -and $campos -contains 'nombres') 'Nombre de 500 caracteres: 400 VALIDACION con el campo' "Texto largo respondio $($r.Status) $($r.Texto)"
    $r = Pedir $admin 'POST' '/bff/bo/api/citas' '{"profesionalId":-1,"sedeId":0,"inicio":"no-es-fecha","modalidadPago":"GRATIS"}'
    Revisar 'M3' ($r.Status -eq 400 -and (Codigo $r) -eq 'VALIDACION') 'Reserva con tipos invalidos: 400 VALIDACION' "Reserva invalida respondio $($r.Status) $($r.Texto)"
    $r = Pedir $admin 'POST' '/bff/bo/api/agenda/bloqueos' @{}
    Revisar 'M3' ($r.Status -eq 400 -and (Codigo $r) -eq 'VALIDACION') 'Bloqueo de agenda vacio: 400 VALIDACION' "Bloqueo vacio respondio $($r.Status) $($r.Texto)"
    $r = Pedir $admin 'POST' '/bff/bo/api/admision/pacientes' @{ rut = (Nuevo-Rut); nombres = 'Futuro'; apellidoPaterno = 'Prueba'; fechaNacimiento = '3000-01-01'; sexo = 'Z'; email = 'no-es-correo' }
    $campos = @($r.Json.errores | ForEach-Object { $_.campo })
    Revisar 'M3' ($r.Status -eq 400 -and $campos -contains 'fechaNacimiento' -and $campos -contains 'sexo' -and $campos -contains 'email') "Fecha futura, sexo y correo invalidos: 400 con los 3 campos ($($campos -join ', '))" "Respondio $($r.Status) $($r.Texto)"
}

Seccion 'A6 y gate'
Info 'A6 (Webpay: monto, orden y estado; confirmacion idempotente y reversa) y la matriz completa rol x endpoint'
Info 'se prueban en CI (pruebas de integracion DominioTests y MatrizAutorizacionTests).'

# ---------------------------------------------------------------- Limpieza
foreach ($id in $citasCreadas) {
    $r = Pedir $(if ($admin) { $admin } else { $recProv }) 'POST' "/bff/bo/api/citas/$id/cancelar" @{ motivo = 'verificacion fase 3' }
    if ($r.Status -ge 300) { Info "No se pudo anular la cita de prueba $id ($($r.Status))" }
}
if ($citasCreadas.Count) { Info "Citas de prueba anuladas: $($citasCreadas -join ', ')" }
foreach ($n in @($recProv, $recLC, $medico, $admin)) { if ($n) { Pedir $n 'POST' '/bff/bo/auth/salir' @{} | Out-Null } }

# ---------------------------------------------------------------- Resumen
Seccion 'Resumen'
$color = if ($script:fallas -eq 0) { 'Green' } else { 'Yellow' }
Escribir "  $($script:ok) OK, $($script:fallas) fallas" $color
[System.IO.File]::WriteAllLines($salida, $script:lineas, (New-Object System.Text.UTF8Encoding $false))
Escribir "  Resultado guardado en $salida"
if ($CI) {
    Write-Host ("::notice title=Fase 3 ($($script:ok) OK, $($script:fallas) fallas)::" + (($script:okIds | ForEach-Object { $_ }) -join '; '))
    if ($script:fallas -gt 0) { exit 1 }
}
