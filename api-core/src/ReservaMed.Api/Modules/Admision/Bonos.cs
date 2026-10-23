using System.Data;
using Dapper;
using ReservaMed.Api.Integraciones.Bonos;
using ReservaMed.Api.Modules.Citas;

namespace ReservaMed.Api.Modules.Admision;

public sealed class BonoDetalleDto
{
    public string Codigo { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public int Cantidad { get; set; }
    public int Valor { get; set; }
    public int Bonificacion { get; set; }
    public int OtraBonificacion { get; set; }
    public int Copago { get; set; }
}

/// <summary>Bono tal como se imprime: financiador, convenio, beneficiario, prestador, prestaciones y montos.</summary>
public sealed class BonoDto
{
    public int Id { get; set; }
    public long Folio { get; set; }
    public int CitaId { get; set; }
    public string Financiador { get; set; } = "";
    public string NumeroConvenio { get; set; } = "";
    public string? PlanCodigo { get; set; }
    public string BeneficiarioRut { get; set; } = "";
    public string BeneficiarioNombre { get; set; } = "";
    public DateTime BeneficiarioFechaNacimiento { get; set; }
    public string BeneficiarioSexo { get; set; } = "";
    public string AfiliadoRut { get; set; } = "";
    public string AfiliadoNombre { get; set; } = "";
    public string RutInstitucion { get; set; } = "";
    public string NombreInstitucion { get; set; } = "";
    public string RutProfesional { get; set; } = "";
    public string NombreProfesional { get; set; } = "";
    public string? DerivadorRut { get; set; }
    public string Canal { get; set; } = "";
    public string TipoFirma { get; set; } = "";
    public string CodigoAuditoria { get; set; } = "";
    public string RutEmisor { get; set; } = "";
    public DateTime FechaEmision { get; set; }
    public DateTime ValidoHasta { get; set; }
    public string Estado { get; set; } = "";
    public int TotalValor { get; set; }
    public int TotalBonificacion { get; set; }
    public int TotalOtraBonificacion { get; set; }
    public int TotalCopago { get; set; }
    public List<BonoDetalleDto> Detalles { get; set; } = [];
}

public static class RepositorioBonos
{
    public static async Task<int> InsertarAsync(
        IDbConnection cn, IDbTransaction tx, ContextoCita c, BonoEmitido b,
        string canal, TipoFirma firma, string estado, int? usuarioId, string? derivadorRut)
    {
        var v = b.Valorizacion;
        var bonoId = await cn.ExecuteScalarAsync<int>("""
            INSERT INTO dbo.Bonos (Folio, CitaId, FinanciadorId, ConvenioId, NumeroConvenio, PlanCodigo,
                BeneficiarioPacienteId, BeneficiarioRut, BeneficiarioNombre, AfiliadoRut, AfiliadoNombre,
                RutInstitucion, NombreInstitucion, ProfesionalId, RutProfesional, NombreProfesional, DerivadorRut,
                Canal, TipoFirma, CodigoAuditoria, RutEmisor, FechaEmision, ValidoHasta, Estado,
                TotalValor, TotalBonificacion, TotalOtraBonificacion, TotalCopago, EmitidoPorUsuarioId)
            OUTPUT INSERTED.Id
            VALUES (@Folio, @CitaId, @FinanciadorId, @ConvenioId, @NumeroConvenio, @PlanCodigo,
                @PacienteId, @PacienteRut, @PacienteNombre, @AfiliadoRut, @AfiliadoNombre,
                @RutInstitucion, @RazonSocial, @ProfesionalId, @ProfesionalRut, @ProfesionalNombre, @DerivadorRut,
                @Canal, @TipoFirma, @CodigoAuditoria, @RutInstitucion, @FechaEmision, @ValidoHasta, @Estado,
                @TotalValor, @TotalBonificacion, @TotalOtraBonificacion, @TotalCopago, @UsuarioId);
            """,
            new
            {
                b.Folio, c.CitaId, c.FinanciadorId, c.ConvenioId, c.NumeroConvenio, v.PlanCodigo,
                c.PacienteId, c.PacienteRut, PacienteNombre = c.PacienteNombreBono, v.AfiliadoRut, v.AfiliadoNombre,
                c.RutInstitucion, c.RazonSocial, c.ProfesionalId, c.ProfesionalRut, c.ProfesionalNombre,
                DerivadorRut = derivadorRut,
                Canal = canal, TipoFirma = firma.ToString(), b.CodigoAuditoria, b.FechaEmision, b.ValidoHasta,
                Estado = estado, v.TotalValor, v.TotalBonificacion, v.TotalOtraBonificacion, v.TotalCopago,
                UsuarioId = usuarioId
            }, tx);

        foreach (var l in v.Lineas)
        {
            await cn.ExecuteAsync("""
                INSERT INTO dbo.BonoDetalles (BonoId, PrestacionId, Codigo, Descripcion, Cantidad, Valor, Bonificacion, OtraBonificacion, Copago)
                VALUES (@BonoId, @PrestacionId, @Codigo, @Descripcion, @Cantidad, @Valor, @Bonificacion, @OtraBonificacion, @Copago);
                """,
                new { BonoId = bonoId, l.PrestacionId, l.Codigo, l.Descripcion, l.Cantidad, l.Valor, l.Bonificacion, l.OtraBonificacion, l.Copago },
                tx);
        }
        return bonoId;
    }

    /// <summary>Bono no anulado de la cita (si existe).</summary>
    public static async Task<BonoDto?> ActivoDeCitaAsync(IDbConnection cn, int citaId, IDbTransaction? tx = null)
    {
        var bono = await cn.QuerySingleOrDefaultAsync<BonoDto>("""
            SELECT b.*, f.Nombre AS Financiador,
                   pa.FechaNacimiento AS BeneficiarioFechaNacimiento, pa.Sexo AS BeneficiarioSexo
            FROM dbo.Bonos b
            JOIN dbo.Financiadores f ON f.Id = b.FinanciadorId
            JOIN dbo.Pacientes pa    ON pa.Id = b.BeneficiarioPacienteId
            WHERE b.CitaId = @CitaId AND b.Estado <> 'ANULADO'
            """, new { CitaId = citaId }, tx);
        if (bono is null) return null;

        bono.Detalles = (await cn.QueryAsync<BonoDetalleDto>(
            "SELECT Codigo, Descripcion, Cantidad, Valor, Bonificacion, OtraBonificacion, Copago FROM dbo.BonoDetalles WHERE BonoId = @Id",
            new { bono.Id }, tx)).ToList();
        return bono;
    }
}
