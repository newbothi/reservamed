using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReservaMed.Api.Common;
using ReservaMed.Api.Integraciones.Ml;

namespace ReservaMed.Api.Modules.Ml;

internal sealed class FilaPrediccion
{
    public int CitaId { get; set; }
    public decimal Probabilidad { get; set; }
    public string Nivel { get; set; } = "";
    public string? Factores { get; set; }
    public string ModeloVersion { get; set; } = "";
    public DateTime CalculadoEn { get; set; }
}

public sealed record RiesgoCitaDto(
    bool Disponible, int CitaId, decimal? Probabilidad, string? Nivel,
    IReadOnlyList<string> Factores, string? ModeloVersion, DateTime? CalculadoEn, string? Motivo);

/// <summary>
/// Puerta de entrada al ml-service para el backoffice. El navegador nunca habla con Python:
/// pasa por la API Core, que aplica autenticación, roles y auditoría.
/// </summary>
[ApiController]
[Route("api/ml")]
public sealed class MlController(ClienteMl ml, ISqlConnectionFactory db, IAuditoria auditoria, IReloj reloj) : ControllerBase
{
    [HttpGet("modelo")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> ModeloActual() =>
        await ml.ModeloActualAsync() is { } m
            ? Ok(m)
            : throw new ReglaNegocioException("Todavía no hay un modelo entrenado.", StatusCodes.Status404NotFound, "SIN_MODELO");

    [HttpGet("modelos")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Historial() => Ok((object?)await ml.HistorialModelosAsync() ?? Array.Empty<object>());

    [HttpPost("modelo/entrenar")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Entrenar()
    {
        var r = await ml.EntrenarAsync();
        string? version = null;
        if (r is { } json && json.TryGetProperty("version", out var v)) version = v.GetString();
        await auditoria.RegistrarAsync("ENTRENAR_MODELO", "ModeloML", version);
        return Ok(r);
    }

    [HttpGet("demanda")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Demanda([FromQuery] int semanas = 4) =>
        Ok(await ml.DemandaAsync(Math.Clamp(semanas, 1, 12)));

    /// <summary>
    /// Riesgo de inasistencia de una cita. Normalmente ya está calculado (lo hace el ml-service
    /// al recibir el evento por RabbitMQ). Si no, se pide en línea; si Python no responde,
    /// se informa "no disponible" sin romper la pantalla.
    /// </summary>
    [HttpGet("citas/{id:int}/riesgo")]
    [Authorize(Roles = Roles.StaffYMedico)]
    public async Task<RiesgoCitaDto> Riesgo(int id)
    {
        await using var cn = db.Crear();
        var fila = await cn.QuerySingleOrDefaultAsync<FilaPrediccion>(
            "SELECT * FROM dbo.PrediccionesCita WHERE CitaId = @Id", new { Id = id });
        if (fila is not null)
            return new RiesgoCitaDto(true, id, fila.Probabilidad, fila.Nivel, Factores(fila.Factores),
                fila.ModeloVersion, fila.CalculadoEn, null);

        try
        {
            var r = await ml.PredecirAsync(id);
            if (r is not { } p) return SinDatos(id, "La cita no existe en el servicio de ML.");
            return new RiesgoCitaDto(true, id,
                p.GetProperty("probabilidad").GetDecimal(), p.GetProperty("nivel").GetString(),
                p.GetProperty("factores").EnumerateArray().Select(f => f.GetString() ?? "").ToList(),
                p.GetProperty("modeloVersion").GetString(), reloj.Ahora, null);
        }
        catch (ReglaNegocioException ex) when (ex.Codigo is "ML_NO_DISPONIBLE" or "ML_ERROR")
        {
            return SinDatos(id, ex.Message);
        }
    }

    private static RiesgoCitaDto SinDatos(int id, string motivo) =>
        new(false, id, null, null, [], null, null, motivo);

    private static IReadOnlyList<string> Factores(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch (JsonException) { return []; }
    }
}
