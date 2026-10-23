using Dapper;

namespace ReservaMed.Api.Common;

/// <summary>
/// Registro de accesos y modificaciones a datos sensibles.
/// La Ley 20.584 exige confidencialidad de la información clínica y la Ley 19.628
/// regula el tratamiento de datos personales: saber quién vio qué es parte de cumplirlas.
/// </summary>
public interface IAuditoria
{
    Task RegistrarAsync(string accion, string recurso, object? recursoId, string? detalle = null);
}

public sealed class ServicioAuditoria(
    ISqlConnectionFactory db,
    IHttpContextAccessor http,
    IReloj reloj,
    ILogger<ServicioAuditoria> log) : IAuditoria
{
    public async Task RegistrarAsync(string accion, string recurso, object? recursoId, string? detalle = null)
    {
        var ctx = http.HttpContext;
        var user = ctx?.User;
        int? usuarioId = int.TryParse(user?.FindFirst(Claims.Sub)?.Value, out var id) ? id : null;

        try
        {
            await using var cn = db.Crear();
            await cn.ExecuteAsync("""
                INSERT INTO dbo.Auditoria (Fecha, UsuarioId, Email, Rol, Accion, Recurso, RecursoId, Ip, Detalle)
                VALUES (@Fecha, @UsuarioId, @Email, @Rol, @Accion, @Recurso, @RecursoId, @Ip, @Detalle);
                """,
                new
                {
                    Fecha = reloj.Ahora,
                    UsuarioId = usuarioId,
                    Email = user?.FindFirst(Claims.Email)?.Value,
                    Rol = user?.FindFirst(Claims.Rol)?.Value,
                    Accion = accion,
                    Recurso = recurso,
                    RecursoId = recursoId?.ToString(),
                    Ip = ctx?.Connection.RemoteIpAddress?.ToString(),
                    Detalle = detalle
                });
        }
        catch (Exception ex)
        {
            // La auditoría no debe botar la operación principal, pero sí quedar en el log.
            log.LogError(ex, "No se pudo registrar auditoría {Accion} {Recurso} {Id}", accion, recurso, recursoId);
        }
    }
}
