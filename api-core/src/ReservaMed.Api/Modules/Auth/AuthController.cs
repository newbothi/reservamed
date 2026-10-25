using System.Data.Common;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Modules.Auth;

public sealed class OpcionesJwt
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string Key { get; set; } = "";
}

/* ---------- Contratos ---------- */

public static class Clientes
{
    public const string Portal = "PORTAL";
    public const string Backoffice = "BACKOFFICE";
}

public sealed record LoginRequest(string Email, string Password, string? Cliente);
public sealed record TotpRequest(string Desafio, string Codigo);
public sealed record RenovarRequest(string RefreshToken);
public sealed record VerificarCorreoRequest(string Token);

public sealed record UsuarioSesion(int Id, string Rol, string Nombre, string Email, int? PacienteId, int? ProfesionalId, int? SedeId);

public sealed record SesionEmitida(
    string AccessToken, DateTime AccessExpiraUtc, string RefreshToken, DateTime SesionExpiraUtc, UsuarioSesion Usuario);

/// <summary>
/// Estado: OK (sesión emitida), TOTP (falta el código del autenticador) o ENROLAR_TOTP
/// (primer ingreso del personal: registrar el autenticador con el secreto o el QR).
/// </summary>
public sealed record ResultadoLogin(string Estado, SesionEmitida? Sesion, string? Desafio, string? SecretoTotp, string? UriTotp);

public sealed record MensajeResponse(string Mensaje);

public sealed record RegistroPacienteRequest(
    string Rut, string Nombres, string ApellidoPaterno, string? ApellidoMaterno,
    DateTime FechaNacimiento, string Sexo, string Email, string? Telefono, string? Comuna,
    string Password,
    int FinanciadorId, int? PlanSaludId, string? TramoFonasa,
    bool EsCarga, string? AfiliadoRut, string? AfiliadoNombre,
    bool AceptaPrivacidad = false);

internal sealed class UsuarioFila
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Rol { get; set; } = "";
    public string NombreVisible { get; set; } = "";
    public int? PacienteId { get; set; }
    public int? ProfesionalId { get; set; }
    public int? SedeId { get; set; }
    public bool Activo { get; set; }
    public bool EmailVerificado { get; set; }
    public byte[]? TotpSecreto { get; set; }
    public bool TotpActivo { get; set; }
    public long? TotpUltimoPaso { get; set; }
    public int IntentosFallidos { get; set; }
    public int BloqueosConsecutivos { get; set; }
    public DateTime? BloqueadoHasta { get; set; }

    public UsuarioSesion Publico() => new(Id, Rol, NombreVisible, Email, PacienteId, ProfesionalId, SedeId);
}

/* ---------- Tokens ---------- */

public sealed class ServicioJwt(IOptions<OpcionesJwt> opciones, IOptions<OpcionesSeguridad> seguridad)
{
    public const string AudienciaDesafio = "reservamed-desafio";
    private readonly OpcionesJwt _o = opciones.Value;
    private readonly OpcionesSeguridad _s = seguridad.Value;

    private SigningCredentials Firma => new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key)), SecurityAlgorithms.HmacSha256);

    /// <summary>Access token de 15 minutos, atado a una sesión revocable (claim sid).</summary>
    public (string Token, DateTime Expira) Acceso(UsuarioSesion u, Guid sid)
    {
        var expira = DateTime.UtcNow.AddMinutes(_s.AccesoMinutos);
        var claims = new Dictionary<string, object>
        {
            [Claims.Sub] = u.Id.ToString(),
            [Claims.Sid] = sid.ToString(),
            [Claims.Rol] = u.Rol,
            [Claims.Nombre] = u.Nombre,
            [Claims.Email] = u.Email
        };
        if (u.PacienteId is int p) claims[Claims.PacienteId] = p.ToString();
        if (u.ProfesionalId is int m) claims[Claims.ProfesionalId] = m.ToString();
        if (u.SedeId is int s) claims[Claims.SedeId] = s.ToString();

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer, Audience = _o.Audience, Expires = expira, Claims = claims, SigningCredentials = Firma
        });
        return (token, expira);
    }

    /// <summary>
    /// Desafío entre la contraseña y el segundo factor: de un solo uso, 5 minutos y con otra
    /// audiencia, así que no sirve como access token.
    /// </summary>
    public string Desafio(int usuarioId, string paso, string cliente) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = AudienciaDesafio,
            Expires = DateTime.UtcNow.AddMinutes(_s.DesafioMinutos),
            Claims = new Dictionary<string, object>
            {
                [Claims.Sub] = usuarioId.ToString(), ["paso"] = paso, ["cliente"] = cliente, ["jti"] = Guid.NewGuid().ToString()
            },
            SigningCredentials = Firma
        });

    public async Task<(int UsuarioId, string Paso, string Cliente, string Jti, DateTime Expira)?> LeerDesafioAsync(string? desafio)
    {
        if (string.IsNullOrWhiteSpace(desafio)) return null;
        var r = await new JsonWebTokenHandler().ValidateTokenAsync(desafio, new TokenValidationParameters
        {
            ValidIssuer = _o.Issuer,
            ValidAudience = AudienciaDesafio,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key)),
            ClockSkew = TimeSpan.FromSeconds(10)
        });
        if (!r.IsValid) return null;
        string Claim(string n) => r.Claims.TryGetValue(n, out var v) ? v?.ToString() ?? "" : "";
        return int.TryParse(Claim(Claims.Sub), out var id)
            ? (id, Claim("paso"), Claim("cliente"), Claim("jti"), r.SecurityToken.ValidTo)
            : null;
    }
}

/* ---------- Controlador ---------- */

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    ISqlConnectionFactory db,
    ServicioJwt jwt,
    ServicioSesiones sesiones,
    CifradoSecretos cifrado,
    PoliticaContrasenas politica,
    IEnviadorCorreo correo,
    IAuditoria auditoria,
    IMemoryCache cache,
    IReloj reloj,
    ServicioBloqueos bloqueos,
    IOptions<OpcionesSeguridad> opciones) : ControllerBase
{
    private const string MensajeCredenciales = "Correo o contraseña incorrectos.";
    private readonly OpcionesSeguridad _o = opciones.Value;

    /// <summary>
    /// M1: si el correo no existe se verifica igual contra un hash BCrypt ficticio, para que el
    /// tiempo de respuesta no revele qué correos tienen cuenta.
    /// </summary>
    private static readonly Lazy<string> HashFicticio = new(() => Contrasenas.Hash(Guid.NewGuid().ToString()));

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(PoliticasLimite.Login)]
    public async Task<ResultadoLogin> Login(LoginRequest req)
    {
        var cliente = req.Cliente?.ToUpperInvariant() == Clientes.Backoffice ? Clientes.Backoffice : Clientes.Portal;
        var email = req.Email?.Trim().ToLowerInvariant() ?? "";

        await using var cn = db.Crear();
        await cn.OpenAsync();
        var u = await cn.QuerySingleOrDefaultAsync<UsuarioFila>("SELECT * FROM dbo.Usuarios WHERE Email = @Email", new { Email = email });

        var claveOk = Contrasenas.Verificar(req.Password ?? "", u?.PasswordHash ?? HashFicticio.Value);

        if (u is not null && u.BloqueadoHasta > DateTime.UtcNow)
        {
            await auditoria.RegistrarAsync("LOGIN_BLOQUEADO", "Usuario", u.Id, email);
            throw Bloqueada();
        }
        if (u is null || !claveOk || !u.Activo)
        {
            if (u is not null && !claveOk) await RegistrarFalloAsync(cn, u);
            await auditoria.RegistrarAsync("LOGIN_FALLIDO", "Usuario", u?.Id, email);
            throw new ReglaNegocioException(MensajeCredenciales, StatusCodes.Status401Unauthorized, "CREDENCIALES");
        }

        // La cuenta existe y la clave es correcta: recién aquí se pueden dar mensajes específicos.
        var esPersonal = u.Rol != Roles.Paciente;
        if (cliente == Clientes.Portal && esPersonal)
            throw new ReglaNegocioException("Esta cuenta es del personal de la clínica: ingrese por el backoffice.", StatusCodes.Status403Forbidden, "CLIENTE");
        if (cliente == Clientes.Backoffice && !esPersonal)
            throw new ReglaNegocioException("Los pacientes ingresan por el portal web.", StatusCodes.Status403Forbidden, "CLIENTE");

        if (!u.EmailVerificado)
        {
            await EnviarVerificacionAsync(cn, u.Id, u.Email);
            throw new ReglaNegocioException(
                "Debe activar su cuenta con el enlace que enviamos a su correo. Le enviamos uno nuevo.",
                StatusCodes.Status403Forbidden, "CORREO_NO_VERIFICADO");
        }

        await ReiniciarIntentosAsync(cn, u.Id);

        // ASVS V2.1.3: los hashes del formato antiguo se reemplazan ahora que se conoce la contraseña.
        if (Contrasenas.EsLegado(u.PasswordHash))
            await cn.ExecuteAsync("UPDATE dbo.Usuarios SET PasswordHash = @H WHERE Id = @Id",
                new { H = Contrasenas.Hash(req.Password!), u.Id });

        if (!esPersonal) return new ResultadoLogin("OK", await EmitirAsync(u.Publico(), cliente), null, null, null);

        // A2: el personal clínico siempre usa segundo factor.
        if (u.TotpActivo)
            return new ResultadoLogin("TOTP", null, jwt.Desafio(u.Id, "TOTP", cliente), null, null);

        // Primer ingreso: se genera la semilla (cifrada en la base) y se muestra una sola vez.
        var secreto = Totp.NuevoSecreto();
        await cn.ExecuteAsync("UPDATE dbo.Usuarios SET TotpSecreto = @S, TotpUltimoPaso = NULL WHERE Id = @Id",
            new { S = cifrado.Cifrar(secreto), u.Id });
        await auditoria.RegistrarAsync("TOTP_ENROLAMIENTO_INICIADO", "Usuario", u.Id);
        return new ResultadoLogin("ENROLAR_TOTP", null, jwt.Desafio(u.Id, "ENROLAR", cliente),
            Base32.Codificar(secreto), Totp.UriOtpAuth(secreto, u.Email));
    }

    /// <summary>Segundo paso del personal: código del autenticador (o activación en el primer ingreso).</summary>
    [HttpPost("totp")]
    [AllowAnonymous]
    [EnableRateLimiting(PoliticasLimite.Login)]
    public async Task<ResultadoLogin> VerificarTotp(TotpRequest req)
    {
        var d = await jwt.LeerDesafioAsync(req.Desafio)
            ?? throw new ReglaNegocioException("La verificación expiró. Ingrese nuevamente.", StatusCodes.Status401Unauthorized, "DESAFIO");

        // Un desafío sirve una sola vez.
        if (cache.TryGetValue($"desafio:{d.Jti}", out _))
            throw new ReglaNegocioException("La verificación expiró. Ingrese nuevamente.", StatusCodes.Status401Unauthorized, "DESAFIO");

        await using var cn = db.Crear();
        await cn.OpenAsync();
        var u = await cn.QuerySingleOrDefaultAsync<UsuarioFila>("SELECT * FROM dbo.Usuarios WHERE Id = @Id", new { Id = d.UsuarioId });
        if (u is null || !u.Activo || u.TotpSecreto is null)
            throw new ReglaNegocioException("La verificación expiró. Ingrese nuevamente.", StatusCodes.Status401Unauthorized, "DESAFIO");
        if (u.BloqueadoHasta > DateTime.UtcNow) throw Bloqueada();

        var secreto = cifrado.Descifrar(u.TotpSecreto);
        var paso = Totp.Verificar(secreto, req.Codigo, DateTimeOffset.UtcNow, u.TotpUltimoPaso);
        if (paso is null)
        {
            await RegistrarFalloAsync(cn, u);
            await auditoria.RegistrarAsync("TOTP_FALLIDO", "Usuario", u.Id);
            throw new ReglaNegocioException("Código incorrecto.", StatusCodes.Status401Unauthorized, "TOTP");
        }

        cache.Set($"desafio:{d.Jti}", true, d.Expira.AddMinutes(1) - DateTime.UtcNow);
        var enrolando = d.Paso == "ENROLAR";
        await cn.ExecuteAsync("""
            UPDATE dbo.Usuarios SET TotpUltimoPaso = @Paso, TotpActivo = 1,
                   IntentosFallidos = 0, BloqueosConsecutivos = 0, BloqueadoHasta = NULL
            WHERE Id = @Id
            """, new { Paso = paso, u.Id });
        if (enrolando) await auditoria.RegistrarAsync("TOTP_ACTIVADO", "Usuario", u.Id);

        return new ResultadoLogin("OK", await EmitirAsync(u.Publico(), d.Cliente), null, null, null);
    }

    /// <summary>Rotación del refresh token. El anterior queda inutilizable.</summary>
    [HttpPost("renovar")]
    [AllowAnonymous]
    [EnableRateLimiting(PoliticasLimite.Renovar)]
    public async Task<SesionEmitida> Renovar(RenovarRequest req)
    {
        var r = await sesiones.RenovarAsync(req.RefreshToken ?? "")
            ?? throw new ReglaNegocioException("La sesión expiró. Ingrese nuevamente.", StatusCodes.Status401Unauthorized, "SESION");

        await using var cn = db.Crear();
        var u = await cn.QuerySingleAsync<UsuarioFila>("SELECT * FROM dbo.Usuarios WHERE Id = @Id", new { Id = r.UsuarioId });
        var (acceso, expira) = jwt.Acceso(u.Publico(), r.Tokens.SesionId);
        return new SesionEmitida(acceso, expira, r.Tokens.Refresh, r.Tokens.SesionExpiraUtc, u.Publico());
    }

    [HttpPost("cerrar")]
    [Authorize]
    public async Task<IActionResult> Cerrar()
    {
        if (Guid.TryParse(User.FindFirstValue(Claims.Sid), out var sid))
            await sesiones.RevocarAsync(sid, "Cierre de sesión");
        await auditoria.RegistrarAsync("LOGOUT", "Usuario", User.UsuarioId());
        return NoContent();
    }

    [HttpGet("yo")]
    [Authorize]
    public UsuarioSesion Yo() => new(
        User.UsuarioId(), User.Rol(), User.FindFirstValue(Claims.Nombre) ?? "", User.FindFirstValue(Claims.Email) ?? "",
        User.PacienteId(), User.ProfesionalId(), User.SedeId());

    /* ---------- Registro con correo verificado (A7, B4, M2) ---------- */

    /// <summary>
    /// Respuesta siempre igual (202) exista o no la cuenta: no revela qué RUT o correos están
    /// registrados. Si ya existían, el aviso llega por correo al titular.
    /// </summary>
    [HttpPost("registro")]
    [AllowAnonymous]
    [EnableRateLimiting(PoliticasLimite.Login)]
    [ProducesResponseType<MensajeResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Registro(RegistroPacienteRequest r, CancellationToken ct)
    {
        var rut = Rut.Exigir(r.Rut);
        var email = r.Email?.Trim().ToLowerInvariant() ?? "";
        var neutro = new MensajeResponse(
            $"Si los datos son correctos, enviamos un correo a {email} con el enlace para activar su cuenta. Revise también la carpeta de spam.");

        if (string.IsNullOrWhiteSpace(r.Nombres) || string.IsNullOrWhiteSpace(r.ApellidoPaterno))
            throw new ReglaNegocioException("Nombre y apellido paterno son obligatorios.");
        if (!email.Contains('@') || email.Length > 150)
            throw new ReglaNegocioException("El correo no es válido.");
        if (r.Sexo is not ("M" or "F" or "X"))
            throw new ReglaNegocioException("Sexo debe ser M, F o X.");
        if (r.FechaNacimiento.Date >= reloj.Hoy || r.FechaNacimiento.Year < 1900)
            throw new ReglaNegocioException("La fecha de nacimiento no es válida.");

        var errores = await politica.RevisarAsync(r.Password, email, $"{r.Nombres} {r.ApellidoPaterno}", ct);
        if (errores.Count > 0) throw new ReglaNegocioException(string.Join(" ", errores), StatusCodes.Status422UnprocessableEntity, "CONTRASENA");

        string? afiliadoRut = null;
        if (r.EsCarga)
        {
            afiliadoRut = Rut.Exigir(r.AfiliadoRut, "RUT del titular");
            if (string.IsNullOrWhiteSpace(r.AfiliadoNombre))
                throw new ReglaNegocioException("Indique el nombre del titular del plan.");
        }

        // El hash se calcula siempre: así crear o no crear la cuenta tarda lo mismo.
        var hash = Contrasenas.Hash(r.Password);

        await using var cn = db.Crear();
        await cn.OpenAsync(ct);

        var fin = await cn.QuerySingleOrDefaultAsync<(string Tipo, int Id)>(
            "SELECT Tipo, Id FROM dbo.Financiadores WHERE Id = @Id", new { Id = r.FinanciadorId });
        if (fin.Id == 0) throw new ReglaNegocioException("Seleccione su previsión de salud.");
        if (fin.Tipo == "FONASA" && r.TramoFonasa is not ("A" or "B" or "C" or "D"))
            throw new ReglaNegocioException("Indique su tramo Fonasa (A, B, C o D).");

        int? planId = r.PlanSaludId;
        if (fin.Tipo == "ISAPRE")
        {
            var planValido = planId is not null && await cn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.PlanesSalud WHERE Id = @P AND FinanciadorId = @F", new { P = planId, F = fin.Id }) == 1;
            if (!planValido) throw new ReglaNegocioException("Seleccione un plan válido de su Isapre.");
        }
        if (fin.Tipo == "FONASA")
            planId = await cn.ExecuteScalarAsync<int?>("SELECT TOP 1 Id FROM dbo.PlanesSalud WHERE FinanciadorId = @F", new { F = fin.Id });
        if (fin.Tipo == "PARTICULAR") planId = null;

        // ¿Ya existe una cuenta con ese correo o con ese RUT?
        var cuentaCorreo = await cn.QuerySingleOrDefaultAsync<string>("SELECT Email FROM dbo.Usuarios WHERE Email = @E", new { E = email });
        var ficha = await cn.QuerySingleOrDefaultAsync<(int Id, string Email, int? UsuarioId, string? EmailCuenta)>("""
            SELECT p.Id, p.Email, u.Id AS UsuarioId, u.Email AS EmailCuenta
            FROM dbo.Pacientes p LEFT JOIN dbo.Usuarios u ON u.PacienteId = p.Id
            WHERE p.Rut = @Rut
            """, new { Rut = rut });

        if (cuentaCorreo is not null || ficha.UsuarioId is not null)
        {
            var titular = cuentaCorreo ?? ficha.EmailCuenta!;
            await correo.EnviarAsync(new Correo(titular, "Intento de registro en ReservaMed",
                "Alguien intentó crear una cuenta en ReservaMed con su correo o su RUT. Si fue usted, ya tiene una cuenta: " +
                $"ingrese en {_o.UrlPortal}/login. Si no fue usted, puede ignorar este mensaje; su cuenta no cambió."), ct);
            await auditoria.RegistrarAsync("REGISTRO_DUPLICADO", "Usuario", null, $"rut={rut[..^4]}****");
            return Accepted(neutro);
        }

        if (ficha.Id != 0 && !string.Equals(ficha.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            // Hay ficha clínica con ese RUT pero con otro correo: no se vincula (podría ser un tercero).
            await correo.EnviarAsync(new Correo(ficha.Email, "Intento de registro con su RUT",
                "Alguien intentó crear una cuenta en ReservaMed con su RUT y otro correo. No vinculamos su ficha. " +
                "Si fue usted, regístrese con el correo que tiene en la clínica o actualícelo en admisión."), ct);
            await auditoria.RegistrarAsync("REGISTRO_RUT_OTRO_CORREO", "Paciente", ficha.Id);
            return Accepted(neutro);
        }

        await using var tx = await cn.BeginTransactionAsync(ct);
        try
        {
            var pacienteId = ficha.Id;
            if (pacienteId == 0)
            {
                pacienteId = await cn.ExecuteScalarAsync<int>("""
                    INSERT INTO dbo.Pacientes (Rut, Nombres, ApellidoPaterno, ApellidoMaterno, FechaNacimiento, Sexo, Email, Telefono, Comuna)
                    OUTPUT INSERTED.Id
                    VALUES (@Rut, @Nombres, @ApellidoPaterno, @ApellidoMaterno, @FechaNacimiento, @Sexo, @Email, @Telefono, @Comuna);
                    """,
                    new
                    {
                        Rut = rut, Nombres = r.Nombres.Trim(), ApellidoPaterno = r.ApellidoPaterno.Trim(),
                        ApellidoMaterno = r.ApellidoMaterno?.Trim(), FechaNacimiento = r.FechaNacimiento.Date,
                        r.Sexo, Email = email, r.Telefono, r.Comuna
                    }, tx);

                await cn.ExecuteAsync("""
                    INSERT INTO dbo.Previsiones (PacienteId, FinanciadorId, PlanSaludId, TramoFonasa, EsCarga, AfiliadoRut, AfiliadoNombre)
                    VALUES (@PacienteId, @FinanciadorId, @PlanSaludId, @Tramo, @EsCarga, @AfiliadoRut, @AfiliadoNombre);
                    """,
                    new
                    {
                        PacienteId = pacienteId, FinanciadorId = fin.Id, PlanSaludId = planId,
                        Tramo = fin.Tipo == "FONASA" ? r.TramoFonasa : null,
                        EsCarga = r.EsCarga && fin.Tipo != "PARTICULAR",
                        AfiliadoRut = afiliadoRut,
                        AfiliadoNombre = r.EsCarga ? r.AfiliadoNombre!.Trim().ToUpperInvariant() : null
                    }, tx);
            }

            var usuarioId = await cn.ExecuteScalarAsync<int>("""
                INSERT INTO dbo.Usuarios (Email, PasswordHash, Rol, NombreVisible, PacienteId, EmailVerificado, PrivacidadAceptadaEn)
                OUTPUT INSERTED.Id
                VALUES (@Email, @Hash, 'PACIENTE', @Nombre, @PacienteId, 0, SYSUTCDATETIME());
                """,
                new { Email = email, Hash = hash, Nombre = $"{r.Nombres.Trim()} {r.ApellidoPaterno.Trim()}", PacienteId = pacienteId }, tx);

            await tx.CommitAsync(ct);
            await EnviarVerificacionAsync(cn, usuarioId, email);
            await auditoria.RegistrarAsync("REGISTRO", "Usuario", usuarioId);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (SqlErrores.EsDuplicado(ex))
        {
            // Carrera con otro registro igual: misma respuesta neutra.
        }
        return Accepted(neutro);
    }

    [HttpPost("verificar-correo")]
    [AllowAnonymous]
    [EnableRateLimiting(PoliticasLimite.Login)]
    public async Task<MensajeResponse> VerificarCorreo(VerificarCorreoRequest req)
    {
        byte[] hash;
        try { hash = ServicioSesiones.Hash(req.Token ?? ""); }
        catch (FormatException) { throw EnlaceInvalido(); }

        await using var cn = db.Crear();
        var usuarioId = await cn.ExecuteScalarAsync<int?>("""
            UPDATE dbo.TokensUsuario SET UsadoEn = SYSUTCDATETIME()
            OUTPUT INSERTED.UsuarioId
            WHERE Hash = @Hash AND Tipo = 'VERIFICAR_CORREO' AND UsadoEn IS NULL AND ExpiraEn > SYSUTCDATETIME()
            """, new { Hash = hash }) ?? throw EnlaceInvalido();

        await cn.ExecuteAsync("UPDATE dbo.Usuarios SET EmailVerificado = 1 WHERE Id = @Id", new { Id = usuarioId });
        await auditoria.RegistrarAsync("CORREO_VERIFICADO", "Usuario", usuarioId);
        return new MensajeResponse("Su cuenta quedó activa. Ya puede ingresar.");
    }

    /* ---------- Apoyo ---------- */

    private async Task<SesionEmitida> EmitirAsync(UsuarioSesion u, string cliente)
    {
        var t = await sesiones.CrearAsync(u.Id, cliente, Ip);
        var (acceso, expira) = jwt.Acceso(u, t.SesionId);
        await auditoria.RegistrarAsync("LOGIN", "Usuario", u.Id, cliente);
        return new SesionEmitida(acceso, expira, t.Refresh, t.SesionExpiraUtc, u);
    }

    private Task RegistrarFalloAsync(DbConnection cn, UsuarioFila u) =>
        bloqueos.RegistrarFalloAsync(cn, u.Id, u.IntentosFallidos, u.BloqueosConsecutivos);

    private static Task ReiniciarIntentosAsync(DbConnection cn, int id) => ServicioBloqueos.ReiniciarAsync(cn, id);

    private async Task EnviarVerificacionAsync(DbConnection cn, int usuarioId, string email)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await cn.ExecuteAsync("""
            INSERT INTO dbo.TokensUsuario (Hash, UsuarioId, Tipo, CreadoEn, ExpiraEn)
            VALUES (@Hash, @Id, 'VERIFICAR_CORREO', SYSUTCDATETIME(), DATEADD(HOUR, @Horas, SYSUTCDATETIME()))
            """, new { Hash = ServicioSesiones.Hash(token), Id = usuarioId, Horas = _o.VerificacionCorreoHoras });
        await correo.EnviarAsync(new Correo(email, "Active su cuenta de ReservaMed",
            $"Para activar su cuenta abra este enlace (válido por {_o.VerificacionCorreoHoras} horas):\n\n" +
            $"{_o.UrlPortal}/verificar-correo?token={token}\n\nSi usted no creó esta cuenta, ignore este correo."));
    }

    private static ReglaNegocioException Bloqueada() => ServicioBloqueos.Bloqueada();

    private static ReglaNegocioException EnlaceInvalido() => new(
        "El enlace no es válido o ya venció. Ingrese con su correo y contraseña para recibir uno nuevo.",
        StatusCodes.Status400BadRequest, "ENLACE");
}

/// <summary>Nombres de las políticas de límite de peticiones por IP (Program.cs).</summary>
public static class PoliticasLimite
{
    public const string Login = "login";
    public const string Renovar = "renovar";
}
