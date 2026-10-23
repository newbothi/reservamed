using Dapper;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Modules.Agenda;

/// <summary>Un cupo libre que el paciente puede reservar.</summary>
public sealed record Cupo(
    int ProfesionalId, string Profesional, int EspecialidadId, string Especialidad,
    int SedeId, string Sede, int PrestacionId, string Prestacion, int ValorParticular,
    DateTime Inicio, DateTime Fin);

public sealed record ConsultaDisponibilidad(
    int? EspecialidadId, int? ProfesionalId, int? SedeId, DateTime Desde, DateTime Hasta);

internal sealed class HorarioFila
{
    public int ProfesionalId { get; set; }
    public string Profesional { get; set; } = "";
    public int EspecialidadId { get; set; }
    public string Especialidad { get; set; } = "";
    public int SedeId { get; set; }
    public string Sede { get; set; } = "";
    public int PrestacionId { get; set; }
    public string Prestacion { get; set; } = "";
    public int ValorParticular { get; set; }
    public byte DiaSemana { get; set; }
    public TimeSpan HoraInicio { get; set; }
    public TimeSpan HoraFin { get; set; }
    public int DuracionMinutos { get; set; }
    public DateTime VigenteDesde { get; set; }
    public DateTime? VigenteHasta { get; set; }
}

internal sealed record Ocupado(int ProfesionalId, DateTime Desde, DateTime Hasta);

/// <summary>
/// Calcula la disponibilidad a partir del horario semanal recurrente, descontando
/// citas activas y bloqueos (vacaciones, congresos). Los cupos no se guardan en tablas:
/// se generan al vuelo, igual que en la mayoría de los sistemas de agenda.
/// </summary>
public sealed class ServicioAgenda(ISqlConnectionFactory db, IReloj reloj, IOptions<ReglasNegocio> reglas)
{
    private readonly ReglasNegocio _reglas = reglas.Value;

    public async Task<IReadOnlyList<Cupo>> DisponibilidadAsync(ConsultaDisponibilidad q)
    {
        if (q.EspecialidadId is null && q.ProfesionalId is null)
            throw new ReglaNegocioException("Indique una especialidad o un profesional.");

        var desde = q.Desde.Date;
        var hasta = q.Hasta.Date;
        if (hasta < desde) throw new ReglaNegocioException("El rango de fechas no es válido.");
        if ((hasta - desde).TotalDays > _reglas.DiasMaximosConsulta)
            throw new ReglaNegocioException($"Puede consultar hasta {_reglas.DiasMaximosConsulta} días a la vez.");

        var hastaExclusivo = hasta.AddDays(1);
        var ahora = reloj.Ahora;
        var minimoInicio = ahora.AddMinutes(_reglas.MinutosAnticipacionMinima);

        await using var cn = db.Crear();
        var horarios = (await cn.QueryAsync<HorarioFila>("""
            SELECT h.ProfesionalId,
                   p.Nombres + ' ' + p.ApellidoPaterno AS Profesional,
                   p.EspecialidadId, e.Nombre AS Especialidad,
                   h.SedeId, s.Nombre AS Sede,
                   h.PrestacionId, pr.Descripcion AS Prestacion, pr.ValorParticular,
                   h.DiaSemana, h.HoraInicio, h.HoraFin, h.DuracionMinutos, h.VigenteDesde, h.VigenteHasta
            FROM dbo.HorariosProfesional h
            JOIN dbo.Profesionales p  ON p.Id = h.ProfesionalId AND p.Activo = 1
            JOIN dbo.Especialidades e ON e.Id = p.EspecialidadId
            JOIN dbo.Sedes s          ON s.Id = h.SedeId AND s.Activa = 1
            JOIN dbo.Prestaciones pr  ON pr.Id = h.PrestacionId
            WHERE (@Esp  IS NULL OR p.EspecialidadId = @Esp)
              AND (@Prof IS NULL OR h.ProfesionalId = @Prof)
              AND (@Sede IS NULL OR h.SedeId = @Sede)
              AND h.VigenteDesde <= @Hasta
              AND (h.VigenteHasta IS NULL OR h.VigenteHasta >= @Desde)
            """,
            new { Esp = q.EspecialidadId, Prof = q.ProfesionalId, Sede = q.SedeId, Desde = desde, Hasta = hasta })).ToList();

        if (horarios.Count == 0) return [];

        var ids = horarios.Select(h => h.ProfesionalId).Distinct().ToArray();

        var ocupados = (await cn.QueryAsync<Ocupado>("""
            SELECT ProfesionalId, FechaHoraInicio AS Desde, FechaHoraFin AS Hasta
            FROM dbo.Citas
            WHERE OcupaCupo = 1 AND ProfesionalId IN @Ids
              AND FechaHoraInicio < @HastaEx AND FechaHoraFin > @Desde
            UNION ALL
            SELECT ProfesionalId, Desde, Hasta
            FROM dbo.BloqueosAgenda
            WHERE ProfesionalId IN @Ids AND Desde < @HastaEx AND Hasta > @Desde
            """,
            new { Ids = ids, Desde = desde, HastaEx = hastaExclusivo }))
            .ToLookup(o => o.ProfesionalId);

        var cupos = new List<Cupo>();
        for (var dia = desde; dia < hastaExclusivo; dia = dia.AddDays(1))
        {
            var diaSemana = (byte)(((int)dia.DayOfWeek + 6) % 7 + 1); // lunes = 1 ... domingo = 7

            foreach (var h in horarios.Where(h => h.DiaSemana == diaSemana
                                                 && h.VigenteDesde.Date <= dia
                                                 && (h.VigenteHasta is null || h.VigenteHasta.Value.Date >= dia)))
            {
                var duracion = TimeSpan.FromMinutes(h.DuracionMinutos);
                for (var t = h.HoraInicio; t + duracion <= h.HoraFin; t += duracion)
                {
                    var inicio = dia + t;
                    var fin = inicio + duracion;
                    if (inicio < minimoInicio) continue;
                    if (ocupados[h.ProfesionalId].Any(o => o.Desde < fin && o.Hasta > inicio)) continue;

                    cupos.Add(new Cupo(h.ProfesionalId, h.Profesional, h.EspecialidadId, h.Especialidad,
                        h.SedeId, h.Sede, h.PrestacionId, h.Prestacion, h.ValorParticular, inicio, fin));
                }
            }
        }

        return cupos.OrderBy(c => c.Inicio).ThenBy(c => c.Profesional).ToList();
    }

    /// <summary>Verifica que el cupo pedido existe en el horario y está libre en este momento.</summary>
    public async Task<Cupo> ExigirCupoAsync(int profesionalId, int sedeId, DateTime inicio)
    {
        var cupos = await DisponibilidadAsync(new ConsultaDisponibilidad(null, profesionalId, sedeId, inicio.Date, inicio.Date));
        return cupos.FirstOrDefault(c => c.Inicio == inicio)
            ?? throw ReglaNegocioException.Conflicto("Ese horario ya no está disponible. Elija otro.");
    }
}
