using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Modules.Catalogo;

public sealed record EspecialidadDto(int Id, string Nombre);
public sealed record SedeDto(int Id, string Nombre, string Direccion, string Comuna);
public sealed record ProfesionalDto(int Id, string Rut, string NombreCompleto, int EspecialidadId, string Especialidad);
public sealed record PlanDto(int Id, string Codigo, string Nombre);

public sealed class FinanciadorDto
{
    public int Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "";
    public List<PlanDto> Planes { get; set; } = [];
}

/// <summary>Datos de referencia públicos (especialidades, sedes, previsiones).</summary>
[ApiController]
[Route("api/catalogo")]
[AllowAnonymous]
public sealed class CatalogoController(ISqlConnectionFactory db) : ControllerBase
{
    [HttpGet("especialidades")]
    public async Task<IEnumerable<EspecialidadDto>> Especialidades()
    {
        await using var cn = db.Crear();
        return await cn.QueryAsync<EspecialidadDto>("SELECT Id, Nombre FROM dbo.Especialidades ORDER BY Nombre");
    }

    [HttpGet("sedes")]
    public async Task<IEnumerable<SedeDto>> Sedes()
    {
        await using var cn = db.Crear();
        return await cn.QueryAsync<SedeDto>(
            "SELECT Id, Nombre, Direccion, Comuna FROM dbo.Sedes WHERE Activa = 1 ORDER BY Nombre");
    }

    [HttpGet("profesionales")]
    public async Task<IEnumerable<ProfesionalDto>> Profesionales([FromQuery] int? especialidadId)
    {
        await using var cn = db.Crear();
        return await cn.QueryAsync<ProfesionalDto>("""
            SELECT p.Id, p.Rut,
                   p.Nombres + ' ' + p.ApellidoPaterno + ISNULL(' ' + p.ApellidoMaterno, '') AS NombreCompleto,
                   p.EspecialidadId, e.Nombre AS Especialidad
            FROM dbo.Profesionales p
            JOIN dbo.Especialidades e ON e.Id = p.EspecialidadId
            WHERE p.Activo = 1 AND (@Esp IS NULL OR p.EspecialidadId = @Esp)
            ORDER BY p.ApellidoPaterno
            """, new { Esp = especialidadId });
    }

    [HttpGet("financiadores")]
    public async Task<IEnumerable<FinanciadorDto>> Financiadores()
    {
        await using var cn = db.Crear();
        var fins = (await cn.QueryAsync<FinanciadorDto>(
            "SELECT Id, Codigo, Nombre, Tipo FROM dbo.Financiadores ORDER BY CASE Tipo WHEN 'FONASA' THEN 0 WHEN 'ISAPRE' THEN 1 ELSE 2 END, Nombre"))
            .ToList();
        var planes = await cn.QueryAsync<(int FinanciadorId, int Id, string Codigo, string Nombre)>(
            "SELECT FinanciadorId, Id, Codigo, Nombre FROM dbo.PlanesSalud ORDER BY Nombre");

        foreach (var f in fins)
            f.Planes = planes.Where(p => p.FinanciadorId == f.Id).Select(p => new PlanDto(p.Id, p.Codigo, p.Nombre)).ToList();
        return fins;
    }
}
