using System.Security.Claims;

namespace ReservaMed.Api.Common;

/// <summary>Nombres de los claims del JWT emitido por la API Core.</summary>
public static class Claims
{
    public const string Sub = "sub";
    public const string Rol = "rol";
    public const string Nombre = "nombre";
    public const string Email = "email";
    public const string PacienteId = "pacienteId";
    public const string ProfesionalId = "profesionalId";
    public const string SedeId = "sedeId";
}

public static class Roles
{
    public const string Paciente = "PACIENTE";
    public const string Recepcion = "RECEPCION";
    public const string Medico = "MEDICO";
    public const string Admin = "ADMIN";

    public const string Staff = "RECEPCION,ADMIN";
    public const string StaffYMedico = "RECEPCION,ADMIN,MEDICO";
}

public static class UsuarioActual
{
    public static int UsuarioId(this ClaimsPrincipal u) =>
        int.Parse(u.FindFirstValue(Claims.Sub) ?? throw ReglaNegocioException.Prohibido("Sesión inválida."));

    public static string Rol(this ClaimsPrincipal u) => u.FindFirstValue(Claims.Rol) ?? "";

    public static bool Es(this ClaimsPrincipal u, string rol) => u.Rol() == rol;

    public static int? PacienteId(this ClaimsPrincipal u) => Entero(u, Claims.PacienteId);
    public static int? ProfesionalId(this ClaimsPrincipal u) => Entero(u, Claims.ProfesionalId);
    public static int? SedeId(this ClaimsPrincipal u) => Entero(u, Claims.SedeId);

    private static int? Entero(ClaimsPrincipal u, string claim) =>
        int.TryParse(u.FindFirstValue(claim), out var v) ? v : null;
}
