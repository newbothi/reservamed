using System.Data;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ReservaMed.Api.Common;

/// <summary>
/// Registro de accesos y modificaciones a datos sensibles.
/// La Ley 20.584 exige confidencialidad de la información clínica y la Ley 19.628 (y la 21.719)
/// regula el tratamiento de datos personales: saber quién vio qué es parte de cumplirlas.
/// </summary>
public interface IAuditoria
{
    Task RegistrarAsync(string accion, string recurso, object? recursoId, string? detalle = null);
}

/// <summary>
/// M6: si la auditoría falla, no se ignora. Queda un log CRÍTICO y /health pasa a "Unhealthy"
/// durante 15 minutos, para que el monitoreo (o el orquestador) avise.
/// </summary>
public sealed class EstadoAuditoria
{
    private long _ultimaFallaTicks;
    public DateTimeOffset? UltimaFalla => _ultimaFallaTicks == 0 ? null : new DateTimeOffset(Interlocked.Read(ref _ultimaFallaTicks), TimeSpan.Zero);
    public void RegistrarFalla() => Interlocked.Exchange(ref _ultimaFallaTicks, DateTimeOffset.UtcNow.UtcTicks);
}

public sealed class SaludAuditoria(EstadoAuditoria estado) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(estado.UltimaFalla is { } f && f > DateTimeOffset.UtcNow.AddMinutes(-15)
            ? HealthCheckResult.Unhealthy("La auditoría no se pudo escribir en los últimos 15 minutos.")
            : HealthCheckResult.Healthy());
}

/// <summary>
/// M6: escribe por dbo.Auditoria_Registrar, que encadena cada fila con el hash de la anterior
/// (SHA-256). La API no tiene permiso para modificar ni borrar la tabla, y un trigger lo impide
/// a cualquiera.
/// </summary>
public sealed class ServicioAuditoria(
    ISqlConnectionFactory db,
    IHttpContextAccessor http,
    IReloj reloj,
    EstadoAuditoria estado,
    ILogger<ServicioAuditoria> log) : IAuditoria
{
    public static readonly EventId EventoFalla = new(9100, "AUDITORIA_FALLIDA");

    public async Task RegistrarAsync(string accion, string recurso, object? recursoId, string? detalle = null)
    {
        var ctx = http.HttpContext;
        var user = ctx?.User;
        int? usuarioId = int.TryParse(user?.FindFirst(Claims.Sub)?.Value, out var id) ? id : null;

        try
        {
            await using var cn = db.Crear();
            await cn.ExecuteAsync("dbo.Auditoria_Registrar", new
            {
                Fecha = reloj.Ahora,
                UsuarioId = usuarioId,
                Email = user?.FindFirst(Claims.Email)?.Value,
                Rol = user?.FindFirst(Claims.Rol)?.Value,
                Accion = accion,
                Recurso = recurso,
                RecursoId = recursoId?.ToString(),
                Ip = ctx?.Connection.RemoteIpAddress?.ToString(),
                Detalle = detalle is { Length: > 500 } ? detalle[..500] : detalle
            }, commandType: CommandType.StoredProcedure);
        }
        catch (Exception ex)
        {
            // La operación principal ya ocurrió: no se revierte, pero la falla se ALERTA.
            estado.RegistrarFalla();
            log.LogCritical(EventoFalla, ex, "AUDITORÍA NO REGISTRADA: {Accion} {Recurso} {Id}. /health queda en Unhealthy.",
                accion, recurso, recursoId);
        }
    }
}

/// <summary>
/// M6: los intentos de acceso denegados por rol (403) también quedan en la auditoría.
/// Los denegados por objeto (404 SIN_ACCESO) los registra ManejadorErrores.
/// </summary>
public sealed class AuditarDenegaciones : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defecto = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext ctx, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden)
            await ctx.RequestServices.GetRequiredService<IAuditoria>()
                .RegistrarAsync("ACCESO_DENEGADO", "Ruta", null, $"{ctx.Request.Method} {RutaPlantilla(ctx)} (rol)");
        await _defecto.HandleAsync(next, ctx, policy, result);
    }

    /// <summary>La plantilla ("api/citas/{id:int}"), nunca la ruta real con datos.</summary>
    public static string RutaPlantilla(HttpContext ctx) =>
        (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(sin ruta)";
}
