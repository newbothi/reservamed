using System.Data;
using System.Security.Claims;
using Dapper;

namespace ReservaMed.Api.Common;

/// <summary>Sede, paciente y profesional de una cita: lo que decide quién puede verla.</summary>
public sealed record AlcanceCita(int CitaId, int PacienteId, int ProfesionalId, int SedeId);

/// <summary>
/// Fase 3 (C5, A3): autorización por objeto en un solo lugar. El rol ([Authorize]) dice QUÉ
/// operaciones puede intentar un usuario; esta política dice SOBRE QUÉ datos:
///  - ADMIN: todo.
///  - RECEPCION: solo citas de la sede de su sesión (la que manda el navegador se ignora).
///  - MEDICO: solo sus citas y los pacientes que tienen o tuvieron cita con él.
///  - PACIENTE: solo lo suyo.
/// Una cita ajena responde 404 SIN_ACCESO: no se revela si existe.
/// </summary>
public sealed class PoliticaAcceso(ISqlConnectionFactory db)
{
    public const string CodigoSinAcceso = "SIN_ACCESO";

    public static ReglaNegocioException SinAcceso(string recurso = "La cita") =>
        new($"{recurso} no existe o no tiene acceso a ella.", StatusCodes.Status404NotFound, CodigoSinAcceso);

    public static bool PuedeVerCita(ClaimsPrincipal u, AlcanceCita c) => u.Rol() switch
    {
        Roles.Admin => true,
        Roles.Recepcion => u.SedeId() is int sede && sede == c.SedeId,
        Roles.Medico => u.ProfesionalId() is int prof && prof == c.ProfesionalId,
        Roles.Paciente => u.PacienteId() is int pac && pac == c.PacienteId,
        _ => false
    };

    /// <summary>Carga el alcance de la cita y exige acceso. Inexistente o ajena: 404 SIN_ACCESO.</summary>
    public async Task<AlcanceCita> ExigirCitaAsync(ClaimsPrincipal u, int citaId, IDbConnection? cn = null)
    {
        var alcance = await AlcanceAsync(citaId, cn);
        if (alcance is null || !PuedeVerCita(u, alcance)) throw SinAcceso();
        return alcance;
    }

    public async Task<bool> PuedeVerCitaAsync(ClaimsPrincipal u, int citaId, IDbConnection? cn = null) =>
        await AlcanceAsync(citaId, cn) is { } a && PuedeVerCita(u, a);

    /// <summary>
    /// Datos de un paciente. Recepción y admin atienden a cualquiera (el paciente puede llegar a
    /// cualquier sede); el médico solo a quien tiene o tuvo cita con él; el paciente, a sí mismo.
    /// </summary>
    public async Task<bool> PuedeVerPacienteAsync(ClaimsPrincipal u, int pacienteId, IDbConnection? cn = null)
    {
        switch (u.Rol())
        {
            case Roles.Admin:
            case Roles.Recepcion when u.SedeId() is not null:
                return true;
            case Roles.Paciente:
                return u.PacienteId() == pacienteId;
            case Roles.Medico when u.ProfesionalId() is int prof:
                return await ConConexionAsync(cn, c => c.ExecuteScalarAsync<bool>("""
                    SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.Citas
                                                  WHERE PacienteId = @Pac AND ProfesionalId = @Prof)
                                THEN 1 ELSE 0 END AS BIT)
                    """, new { Pac = pacienteId, Prof = prof }));
            default:
                return false;
        }
    }

    /// <summary>
    /// A3: sede con la que se consulta. Para recepción es siempre la de su sesión; pedir otra es 403.
    /// Para admin y médico se respeta el filtro solicitado (el médico ya se limita a sus citas).
    /// </summary>
    public static int? SedeParaConsulta(ClaimsPrincipal u, int? solicitada)
    {
        if (!u.Es(Roles.Recepcion)) return solicitada;
        var propia = u.SedeId() ?? throw ReglaNegocioException.Prohibido("Su usuario de recepción no tiene sede asignada.");
        if (solicitada is int s && s != propia)
            throw ReglaNegocioException.Prohibido("Solo puede trabajar con la agenda de su sede.");
        return propia;
    }

    /// <summary>A3: recepción solo opera (reserva, admite) en su sede.</summary>
    public static void ExigirSede(ClaimsPrincipal u, int sedeId) => SedeParaConsulta(u, sedeId);

    private async Task<AlcanceCita?> AlcanceAsync(int citaId, IDbConnection? cn) =>
        await ConConexionAsync(cn, c => c.QuerySingleOrDefaultAsync<AlcanceCita>(
            "SELECT Id AS CitaId, PacienteId, ProfesionalId, SedeId FROM dbo.Citas WHERE Id = @Id", new { Id = citaId }));

    private async Task<T> ConConexionAsync<T>(IDbConnection? cn, Func<IDbConnection, Task<T>> f)
    {
        if (cn is not null) return await f(cn);
        await using var propia = db.Crear();
        return await f(propia);
    }
}
