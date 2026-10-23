using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Modules.Auth;

public sealed class OpcionesJwt
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string Key { get; set; } = "";
    public int HorasValidez { get; set; } = 8;
}

public sealed record LoginRequest(string Email, string Password);

public sealed record SesionResponse(
    string Token, DateTime ExpiraUtc, string Rol, string Nombre,
    int? PacienteId, int? ProfesionalId, int? SedeId);

public sealed record RegistroPacienteRequest(
    string Rut, string Nombres, string ApellidoPaterno, string? ApellidoMaterno,
    DateTime FechaNacimiento, string Sexo, string Email, string? Telefono, string? Comuna,
    string Password,
    int FinanciadorId, int? PlanSaludId, string? TramoFonasa,
    bool EsCarga, string? AfiliadoRut, string? AfiliadoNombre);

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
}

public sealed class ServicioJwt(Microsoft.Extensions.Options.IOptions<OpcionesJwt> opciones)
{
    private readonly OpcionesJwt _o = opciones.Value;

    internal SesionResponse Emitir(UsuarioFila u)
    {
        var expira = DateTime.UtcNow.AddHours(_o.HorasValidez);
        var claims = new Dictionary<string, object>
        {
            [Claims.Sub] = u.Id.ToString(),
            [Claims.Rol] = u.Rol,
            [Claims.Nombre] = u.NombreVisible,
            [Claims.Email] = u.Email
        };
        if (u.PacienteId is int p) claims[Claims.PacienteId] = p.ToString();
        if (u.ProfesionalId is int m) claims[Claims.ProfesionalId] = m.ToString();
        if (u.SedeId is int s) claims[Claims.SedeId] = s.ToString();

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            Expires = expira,
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key)), SecurityAlgorithms.HmacSha256)
        });

        return new SesionResponse(token, expira, u.Rol, u.NombreVisible, u.PacienteId, u.ProfesionalId, u.SedeId);
    }
}

[ApiController]
[Route("api/auth")]
public sealed class AuthController(ISqlConnectionFactory db, ServicioJwt jwt, IAuditoria auditoria, IReloj reloj) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<SesionResponse>> Login(LoginRequest req)
    {
        await using var cn = db.Crear();
        var u = await cn.QuerySingleOrDefaultAsync<UsuarioFila>(
            "SELECT * FROM dbo.Usuarios WHERE Email = @Email",
            new { Email = req.Email?.Trim().ToLowerInvariant() });

        // Mismo mensaje para usuario inexistente o clave incorrecta: no revelar qué falló.
        if (u is null || !u.Activo || !BCrypt.Net.BCrypt.Verify(req.Password ?? "", u.PasswordHash))
        {
            await auditoria.RegistrarAsync("LOGIN_FALLIDO", "Usuario", null, req.Email);
            throw new ReglaNegocioException("Correo o contraseña incorrectos.", StatusCodes.Status401Unauthorized, "CREDENCIALES");
        }

        return jwt.Emitir(u);
    }

    /// <summary>Registro de pacientes desde el portal web.</summary>
    [HttpPost("registro")]
    [AllowAnonymous]
    public async Task<ActionResult<SesionResponse>> Registro(RegistroPacienteRequest r)
    {
        var rut = Rut.Exigir(r.Rut);
        var email = r.Email?.Trim().ToLowerInvariant() ?? "";

        if (string.IsNullOrWhiteSpace(r.Nombres) || string.IsNullOrWhiteSpace(r.ApellidoPaterno))
            throw new ReglaNegocioException("Nombre y apellido paterno son obligatorios.");
        if (!email.Contains('@'))
            throw new ReglaNegocioException("El correo no es válido.");
        if ((r.Password ?? "").Length < 8)
            throw new ReglaNegocioException("La contraseña debe tener al menos 8 caracteres.");
        if (r.Sexo is not ("M" or "F" or "X"))
            throw new ReglaNegocioException("Sexo debe ser M, F o X.");
        if (r.FechaNacimiento.Date >= reloj.Hoy)
            throw new ReglaNegocioException("La fecha de nacimiento no es válida.");

        string? afiliadoRut = null;
        if (r.EsCarga)
        {
            afiliadoRut = Rut.Exigir(r.AfiliadoRut, "RUT del titular");
            if (string.IsNullOrWhiteSpace(r.AfiliadoNombre))
                throw new ReglaNegocioException("Indique el nombre del titular del plan.");
        }

        await using var cn = db.Crear();
        await cn.OpenAsync();

        var fin = await cn.QuerySingleOrDefaultAsync<(string Tipo, int Id)>(
            "SELECT Tipo, Id FROM dbo.Financiadores WHERE Id = @Id", new { Id = r.FinanciadorId });
        if (fin.Id == 0) throw new ReglaNegocioException("Seleccione su previsión de salud.");

        if (fin.Tipo == "FONASA" && r.TramoFonasa is not ("A" or "B" or "C" or "D"))
            throw new ReglaNegocioException("Indique su tramo Fonasa (A, B, C o D).");

        int? planId = r.PlanSaludId;
        if (fin.Tipo == "ISAPRE")
        {
            var planValido = planId is not null && await cn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.PlanesSalud WHERE Id = @P AND FinanciadorId = @F",
                new { P = planId, F = fin.Id }) == 1;
            if (!planValido) throw new ReglaNegocioException("Seleccione un plan válido de su Isapre.");
        }
        if (fin.Tipo == "FONASA")
            planId = await cn.ExecuteScalarAsync<int?>(
                "SELECT TOP 1 Id FROM dbo.PlanesSalud WHERE FinanciadorId = @F", new { F = fin.Id });
        if (fin.Tipo == "PARTICULAR") planId = null;

        await using var tx = await cn.BeginTransactionAsync();
        try
        {
            var pacienteId = await cn.ExecuteScalarAsync<int>("""
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

            var nombre = $"{r.Nombres.Trim()} {r.ApellidoPaterno.Trim()}";
            var usuarioId = await cn.ExecuteScalarAsync<int>("""
                INSERT INTO dbo.Usuarios (Email, PasswordHash, Rol, NombreVisible, PacienteId)
                OUTPUT INSERTED.Id
                VALUES (@Email, @Hash, 'PACIENTE', @Nombre, @PacienteId);
                """,
                new { Email = email, Hash = BCrypt.Net.BCrypt.HashPassword(r.Password, 11), Nombre = nombre, PacienteId = pacienteId }, tx);

            await tx.CommitAsync();

            return jwt.Emitir(new UsuarioFila
            {
                Id = usuarioId, Email = email, Rol = Roles.Paciente, NombreVisible = nombre,
                PacienteId = pacienteId, Activo = true
            });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (SqlErrores.EsDuplicado(ex))
        {
            throw ReglaNegocioException.Conflicto("Ya existe una cuenta con ese RUT o correo.");
        }
    }
}
