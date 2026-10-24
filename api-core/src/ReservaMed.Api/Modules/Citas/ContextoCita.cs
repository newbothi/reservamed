using System.Data;
using Dapper;
using ReservaMed.Api.Common;
using ReservaMed.Api.Integraciones.Bonos;
using ReservaMed.Contracts;

namespace ReservaMed.Api.Modules.Citas;

/// <summary>
/// Toda la información de una cita necesaria para admitir, valorizar y cobrar:
/// paciente, previsión vigente, convenio de su financiador con la sede y arancel.
/// </summary>
public sealed class ContextoCita
{
    public int CitaId { get; set; }
    public string Estado { get; set; } = "";
    public string Canal { get; set; } = "";
    public DateTime FechaHoraInicio { get; set; }
    public DateTime? ExpiraEn { get; set; }
    public byte[] RowVer { get; set; } = [];

    public int PacienteId { get; set; }
    public string PacienteRut { get; set; } = "";
    public string PacienteNombreBono { get; set; } = "";   // APELLIDOS NOMBRES, como aparece en el bono
    public string PacienteEmail { get; set; } = "";

    public int ProfesionalId { get; set; }
    public string ProfesionalRut { get; set; } = "";
    public string ProfesionalNombre { get; set; } = "";
    public string Especialidad { get; set; } = "";

    public int SedeId { get; set; }
    public string Sede { get; set; } = "";
    public string RutInstitucion { get; set; } = "";
    public string RazonSocial { get; set; } = "";

    public int PrestacionId { get; set; }
    public string PrestacionCodigo { get; set; } = "";
    public string PrestacionDescripcion { get; set; } = "";
    public int ValorParticular { get; set; }

    public int? FinanciadorId { get; set; }
    public string? FinanciadorCodigo { get; set; }
    public string? FinanciadorNombre { get; set; }
    public string? FinanciadorTipo { get; set; }
    public string? PlanCodigo { get; set; }
    public string? TramoFonasa { get; set; }
    public bool EsCarga { get; set; }
    public string? AfiliadoRut { get; set; }
    public string? AfiliadoNombre { get; set; }

    public int? ConvenioId { get; set; }
    public string? NumeroConvenio { get; set; }
    public int? ValorConvenio { get; set; }

    /// <summary>Particular si no tiene previsión, si es particular, o si su financiador no tiene convenio con la sede.</summary>
    public bool EsParticular => FinanciadorTipo is null or "PARTICULAR" || ConvenioId is null;

    public string? AvisoParticular =>
        FinanciadorTipo is null or "PARTICULAR" ? null
        : ConvenioId is null ? $"{FinanciadorNombre} no tiene convenio con esta sede: la atención es particular."
        : null;
}

public static class RepositorioContexto
{
    public static async Task<ContextoCita> CargarAsync(IDbConnection cn, int citaId, IDbTransaction? tx = null)
    {
        var c = await cn.QuerySingleOrDefaultAsync<ContextoCita>("""
            SELECT c.Id AS CitaId, c.Estado, c.Canal, c.FechaHoraInicio, c.ExpiraEn, c.RowVer,
                   pa.Id AS PacienteId, pa.Rut AS PacienteRut,
                   UPPER(pa.ApellidoPaterno + ISNULL(' ' + pa.ApellidoMaterno, '') + ' ' + pa.Nombres) AS PacienteNombreBono,
                   pa.Email AS PacienteEmail,
                   pr.Id AS ProfesionalId, pr.Rut AS ProfesionalRut,
                   UPPER(pr.Nombres + ' ' + pr.ApellidoPaterno + ISNULL(' ' + pr.ApellidoMaterno, '')) AS ProfesionalNombre,
                   e.Nombre AS Especialidad,
                   s.Id AS SedeId, s.Nombre AS Sede, s.RutInstitucion, s.RazonSocial,
                   ps.Id AS PrestacionId, ps.Codigo AS PrestacionCodigo, ps.Descripcion AS PrestacionDescripcion, ps.ValorParticular,
                   f.Id AS FinanciadorId, f.Codigo AS FinanciadorCodigo, f.Nombre AS FinanciadorNombre, f.Tipo AS FinanciadorTipo,
                   pl.Codigo AS PlanCodigo, pv.TramoFonasa, ISNULL(pv.EsCarga, 0) AS EsCarga, pv.AfiliadoRut, pv.AfiliadoNombre,
                   cv.Id AS ConvenioId, cv.NumeroConvenio, ac.Valor AS ValorConvenio
            FROM dbo.Citas c
            JOIN dbo.Pacientes pa       ON pa.Id = c.PacienteId
            JOIN dbo.Profesionales pr   ON pr.Id = c.ProfesionalId
            JOIN dbo.Especialidades e   ON e.Id = pr.EspecialidadId
            JOIN dbo.Sedes s            ON s.Id = c.SedeId
            JOIN dbo.Prestaciones ps    ON ps.Id = c.PrestacionId
            LEFT JOIN dbo.Previsiones pv  ON pv.PacienteId = pa.Id AND pv.Vigente = 1
            LEFT JOIN dbo.Financiadores f ON f.Id = pv.FinanciadorId
            LEFT JOIN dbo.PlanesSalud pl  ON pl.Id = pv.PlanSaludId
            LEFT JOIN dbo.Convenios cv    ON cv.FinanciadorId = f.Id AND cv.SedeId = s.Id AND cv.Vigente = 1
            LEFT JOIN dbo.ArancelesConvenio ac ON ac.ConvenioId = cv.Id AND ac.PrestacionId = ps.Id
            WHERE c.Id = @Id
            """, new { Id = citaId }, tx);

        return c ?? throw ReglaNegocioException.NoEncontrado("La cita");
    }

    public static SolicitudValorizacion SolicitudDesde(ContextoCita c) => new(
        c.FinanciadorCodigo!, c.NumeroConvenio!, c.PacienteRut, c.RutInstitucion, c.ProfesionalRut,
        [new ItemPrestacion(c.PrestacionId, c.PrestacionCodigo, c.PrestacionDescripcion, 1, c.ValorConvenio ?? c.ValorParticular)]);
}

/// <summary>Valorización lista para mostrar: con bono (previsión) o particular.</summary>
public sealed record ResultadoValorizacion(
    bool EsParticular,
    string? Aviso,
    string Financiador,
    string? PlanCodigo,
    string? NumeroConvenio,
    string? AfiliadoRut,
    string? AfiliadoNombre,
    IReadOnlyList<LineaValorizada> Lineas)
{
    public int TotalValor => Lineas.Sum(l => l.Valor);
    public int TotalBonificacion => Lineas.Sum(l => l.Bonificacion);
    public int TotalOtraBonificacion => Lineas.Sum(l => l.OtraBonificacion);
    public int TotalCopago => Lineas.Sum(l => l.Copago);
}

public sealed class ServicioValorizacion(IEmisorBonos emisor)
{
    public async Task<ResultadoValorizacion> ValorizarAsync(ContextoCita c, CancellationToken ct = default)
    {
        if (c.EsParticular)
        {
            var linea = new LineaValorizada(c.PrestacionId, c.PrestacionCodigo, c.PrestacionDescripcion, 1,
                c.ValorParticular, 0, 0, c.ValorParticular);
            return new ResultadoValorizacion(true, c.AvisoParticular, "Particular", null, null, null, null, [linea]);
        }

        var v = await emisor.ValorizarAsync(RepositorioContexto.SolicitudDesde(c), ct);
        return new ResultadoValorizacion(false, null, c.FinanciadorNombre!, v.PlanCodigo, c.NumeroConvenio,
            v.AfiliadoRut, v.AfiliadoNombre, v.Lineas);
    }
}

/// <summary>Arma el payload de los eventos de cita que se publican vía Outbox.</summary>
/// <summary>
/// Publica eventos de cita por la Outbox. A9: el evento lleva solo el id y el estado; nada del
/// paciente. El Worker y el ml-service consultan lo que necesitan con sus propios permisos.
/// </summary>
public static class EventosCita
{
    public static async Task PublicarAsync(IDbConnection cn, IDbTransaction tx, string tipo, int citaId)
    {
        var estado = await cn.ExecuteScalarAsync<string>("SELECT Estado FROM dbo.Citas WHERE Id = @Id", new { Id = citaId }, tx)
                     ?? throw new InvalidOperationException($"La cita {citaId} no existe.");
        await Outbox.AgregarAsync(cn, tx, tipo, new EventoCita(citaId, estado));
    }
}
