using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Modules.Auditoria;

public sealed class RegistroAuditoriaDto
{
    public long Id { get; set; }
    public DateTime Fecha { get; set; }
    public string? Email { get; set; }
    public string? Rol { get; set; }
    public string Accion { get; set; } = "";
    public string Recurso { get; set; } = "";
    public string? RecursoId { get; set; }
    public string? Ip { get; set; }
    public string? Detalle { get; set; }
}

[ApiController]
[Route("api/auditoria")]
[Authorize(Roles = Roles.Admin)]
public sealed class AuditoriaController(ISqlConnectionFactory db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<RegistroAuditoriaDto>> Listar(
        [FromQuery] string? accion, [FromQuery] string? email, [FromQuery] string? recurso, [FromQuery] string? recursoId,
        [FromQuery] int top = 200)
    {
        await using var cn = db.Crear();
        return await cn.QueryAsync<RegistroAuditoriaDto>("""
            SELECT TOP (@Top) Id, Fecha, Email, Rol, Accion, Recurso, RecursoId, Ip, Detalle
            FROM dbo.Auditoria
            WHERE (@Accion IS NULL OR Accion = @Accion)
              AND (@Email IS NULL OR Email LIKE '%' + @Email + '%')
              AND (@Recurso IS NULL OR Recurso = @Recurso)
              AND (@RecursoId IS NULL OR RecursoId = @RecursoId)
            ORDER BY Id DESC
            """, new { Top = Math.Clamp(top, 1, 1000), Accion = accion, Email = email, Recurso = recurso, RecursoId = recursoId });
    }
}
