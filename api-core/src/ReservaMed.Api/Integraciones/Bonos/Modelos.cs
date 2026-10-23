namespace ReservaMed.Api.Integraciones.Bonos;

/// <summary>Una prestación a valorizar (código de arancel, cantidad y valor convenido).</summary>
public sealed record ItemPrestacion(int PrestacionId, string Codigo, string Descripcion, int Cantidad, int ValorUnitario);

public sealed record SolicitudValorizacion(
    string FinanciadorCodigo,
    string NumeroConvenio,
    string BeneficiarioRut,
    string RutInstitucion,
    string RutProfesional,
    IReadOnlyList<ItemPrestacion> Items);

public sealed record LineaValorizada(
    int PrestacionId, string Codigo, string Descripcion, int Cantidad,
    int Valor, int Bonificacion, int OtraBonificacion, int Copago);

/// <summary>Respuesta del financiador: cuánto bonifica y cuánto paga el paciente (copago).</summary>
public sealed record Valorizacion(
    string PlanCodigo,
    string AfiliadoRut,
    string AfiliadoNombre,
    IReadOnlyList<LineaValorizada> Lineas)
{
    public int TotalValor => Lineas.Sum(l => l.Valor);
    public int TotalBonificacion => Lineas.Sum(l => l.Bonificacion);
    public int TotalOtraBonificacion => Lineas.Sum(l => l.OtraBonificacion);
    public int TotalCopago => Lineas.Sum(l => l.Copago);
}

public enum TipoFirma { HUELLA, CLAVE_WEB }

public sealed record SolicitudEmision(
    SolicitudValorizacion Valorizacion,
    TipoFirma TipoFirma,
    bool FirmaVerificada,          // resultado del lector de huella o de la clave del financiador
    string RutEmisor,
    string? DerivadorRut);

public sealed record BonoEmitido(
    long Folio,
    string CodigoAuditoria,
    DateTime FechaEmision,
    DateTime ValidoHasta,
    Valorizacion Valorizacion);

/// <summary>
/// Puerta de entrada al sistema de bonos electrónicos (en Chile, normalmente I-Med),
/// que a su vez consulta a Fonasa o a la Isapre del paciente.
/// </summary>
public interface IEmisorBonos
{
    Task<Valorizacion> ValorizarAsync(SolicitudValorizacion solicitud, CancellationToken ct = default);
    Task<BonoEmitido> EmitirAsync(SolicitudEmision solicitud, CancellationToken ct = default);
    Task AnularAsync(long folio, string motivo, CancellationToken ct = default);
}
