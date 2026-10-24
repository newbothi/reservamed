using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Modules.Agenda;

public sealed class CitaAgendaDto
{
    public int Id { get; set; }
    public DateTime FechaHoraInicio { get; set; }
    public DateTime FechaHoraFin { get; set; }
    public string Estado { get; set; } = "";
    public string Canal { get; set; } = "";
    public int PacienteId { get; set; }
    public string PacienteRut { get; set; } = "";
    public string PacienteNombre { get; set; } = "";
    public string? Prevision { get; set; }
    public int ProfesionalId { get; set; }
    public string Profesional { get; set; } = "";
    public string Especialidad { get; set; } = "";
    public int SedeId { get; set; }
    public string Sede { get; set; } = "";
    public long? FolioBono { get; set; }
    public int? MontoPagado { get; set; }
    // Riesgo de inasistencia calculado por el ml-service (NULL si aún no hay modelo)
    public decimal? RiesgoProbabilidad { get; set; }
    public string? RiesgoNivel { get; set; }
    public string? RiesgoFactores { get; set; }
}

public sealed record BloqueoRequest(int ProfesionalId, DateTime Desde, DateTime Hasta, string Motivo);

[ApiController]
[Route("api/agenda")]
public sealed class AgendaController(ServicioAgenda agenda, ISqlConnectionFactory db, IAuditoria auditoria) : ControllerBase
{
    /// <summary>Cupos libres. Público: cualquiera puede ver horas disponibles.</summary>
    [HttpGet("disponibilidad")]
    [AllowAnonymous]
    public Task<IReadOnlyList<Cupo>> Disponibilidad(
        [FromQuery] int? especialidadId, [FromQuery] int? profesionalId, [FromQuery] int? sedeId,
        [FromQuery] DateTime desde, [FromQuery] DateTime hasta) =>
        agenda.DisponibilidadAsync(new ConsultaDisponibilidad(especialidadId, profesionalId, sedeId, desde, hasta));

    /// <summary>
    /// Agenda del día para recepción (por sede) o para el médico (sólo sus citas).
    /// </summary>
    [HttpGet("dia")]
    [Authorize(Roles = Roles.StaffYMedico)]
    public async Task<IEnumerable<CitaAgendaDto>> Dia([FromQuery] DateTime fecha, [FromQuery] int? sedeId)
    {
        int? profesionalId = null;
        if (User.Es(Roles.Medico))
            profesionalId = User.ProfesionalId() ?? throw ReglaNegocioException.Prohibido();
        // A3: recepción ve solo la agenda de su sede, venga lo que venga en sedeId.
        sedeId = PoliticaAcceso.SedeParaConsulta(User, sedeId);

        await using var cn = db.Crear();
        var citas = await cn.QueryAsync<CitaAgendaDto>("""
            SELECT c.Id, c.FechaHoraInicio, c.FechaHoraFin, c.Estado, c.Canal,
                   pa.Id AS PacienteId, pa.Rut AS PacienteRut,
                   pa.Nombres + ' ' + pa.ApellidoPaterno + ISNULL(' ' + pa.ApellidoMaterno, '') AS PacienteNombre,
                   f.Nombre + ISNULL(' ' + CASE WHEN f.Tipo = 'FONASA' THEN 'tramo ' + pv.TramoFonasa ELSE pl.Codigo END, '') AS Prevision,
                   pr.Id AS ProfesionalId, pr.Nombres + ' ' + pr.ApellidoPaterno AS Profesional,
                   e.Nombre AS Especialidad, s.Id AS SedeId, s.Nombre AS Sede,
                   b.Folio AS FolioBono,
                   (SELECT SUM(Monto) FROM dbo.Pagos pg WHERE pg.CitaId = c.Id AND pg.Estado = 'AUTORIZADO') AS MontoPagado,
                   pc.Probabilidad AS RiesgoProbabilidad, pc.Nivel AS RiesgoNivel, pc.Factores AS RiesgoFactores
            FROM dbo.Citas c
            JOIN dbo.Pacientes pa       ON pa.Id = c.PacienteId
            JOIN dbo.Profesionales pr   ON pr.Id = c.ProfesionalId
            JOIN dbo.Especialidades e   ON e.Id = pr.EspecialidadId
            JOIN dbo.Sedes s            ON s.Id = c.SedeId
            LEFT JOIN dbo.Previsiones pv ON pv.PacienteId = pa.Id AND pv.Vigente = 1
            LEFT JOIN dbo.Financiadores f ON f.Id = pv.FinanciadorId
            LEFT JOIN dbo.PlanesSalud pl ON pl.Id = pv.PlanSaludId
            LEFT JOIN dbo.Bonos b       ON b.CitaId = c.Id AND b.Estado <> 'ANULADO'
            LEFT JOIN dbo.PrediccionesCita pc ON pc.CitaId = c.Id
            WHERE c.FechaHoraInicio >= @Dia AND c.FechaHoraInicio < DATEADD(DAY, 1, @Dia)
              AND (@Sede IS NULL OR c.SedeId = @Sede)
              AND (@Prof IS NULL OR c.ProfesionalId = @Prof)
              AND c.Estado NOT IN ('EXPIRADA')
            ORDER BY c.FechaHoraInicio, pr.ApellidoPaterno
            """, new { Dia = fecha.Date, Sede = sedeId, Prof = profesionalId });

        await auditoria.RegistrarAsync("VER_AGENDA", "Agenda", fecha.ToString("yyyy-MM-dd"),
            $"sede={sedeId?.ToString() ?? "todas"} profesional={profesionalId?.ToString() ?? "todos"}");
        return citas;
    }

    /// <summary>Bloquea la agenda de un profesional (vacaciones, congreso, licencia).</summary>
    [HttpPost("bloqueos")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> CrearBloqueo(BloqueoRequest r)
    {
        if (r.Hasta <= r.Desde) throw new ReglaNegocioException("El rango del bloqueo no es válido.");
        if (string.IsNullOrWhiteSpace(r.Motivo)) throw new ReglaNegocioException("Indique el motivo.");

        await using var cn = db.Crear();
        var citasAfectadas = await cn.ExecuteScalarAsync<int>("""
            SELECT COUNT(1) FROM dbo.Citas
            WHERE ProfesionalId = @ProfesionalId AND OcupaCupo = 1
              AND FechaHoraInicio < @Hasta AND FechaHoraFin > @Desde
            """, r);

        var id = await cn.ExecuteScalarAsync<int>("""
            INSERT INTO dbo.BloqueosAgenda (ProfesionalId, Desde, Hasta, Motivo)
            OUTPUT INSERTED.Id VALUES (@ProfesionalId, @Desde, @Hasta, @Motivo);
            """, r);

        await auditoria.RegistrarAsync("CREAR_BLOQUEO", "Agenda", id, r.Motivo);
        // Las citas ya tomadas no se anulan solas: recepción debe reagendarlas.
        return Ok(new { id, citasAfectadas });
    }
}
