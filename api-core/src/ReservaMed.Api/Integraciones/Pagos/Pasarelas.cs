using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Integraciones.Pagos;

/* =====================================================================
   POS presencial (máquina Transbank en la caja de admisión)
   ===================================================================== */

public enum MedioPos { POS_DEBITO, POS_CREDITO }

public sealed record CobroPos(int Monto, MedioPos Medio, string UltimosDigitos, int Cuotas);

/// <summary>Lo que imprime el voucher: código de autorización, n° de operación, terminal...</summary>
public sealed record VoucherPos(
    bool Aprobado, string? MotivoRechazo,
    string Terminal, string CodigoComercio,
    string? CodigoAutorizacion, string? NumeroOperacion, string? NumeroUnico,
    string UltimosDigitos, int Monto, int Cuotas, DateTime Fecha);

public interface ITerminalPos
{
    Task<VoucherPos> CobrarAsync(CobroPos cobro, CancellationToken ct = default);
}

public sealed class OpcionesPos
{
    public string Terminal { get; set; } = "S2PCD3267";
    public string CodigoComercio { get; set; } = "597000000000";
}

/// <summary>
/// Simula el POS integrado. Regla de prueba: una tarjeta terminada en 0000 es rechazada.
/// </summary>
public sealed class PosMock(IOptions<OpcionesPos> opciones, IReloj reloj, ILogger<PosMock> log) : ITerminalPos
{
    private readonly OpcionesPos _o = opciones.Value;

    public async Task<VoucherPos> CobrarAsync(CobroPos c, CancellationToken ct = default)
    {
        await Task.Delay(1200, ct); // el paciente inserta la tarjeta y digita la clave
        var ahora = reloj.Ahora;

        if (c.UltimosDigitos == "0000")
            return new VoucherPos(false, "Transacción rechazada por el emisor (fondos insuficientes).",
                _o.Terminal, _o.CodigoComercio, null, null, null, c.UltimosDigitos, c.Monto, c.Cuotas, ahora);

        var autorizacion = Codigo("ABCDEFGHJKLMNPQRSTUVWXYZ0123456789", 6);
        var operacion = Codigo("0123456789", 9);
        var unico = $"{ahora:yyyyMMddHHmmss}{Codigo("0123456789", 12)}";

        log.LogInformation("[POS mock] {Medio} ${Monto} aprobado, autorización {Aut}", c.Medio, c.Monto, autorizacion);
        return new VoucherPos(true, null, _o.Terminal, _o.CodigoComercio, autorizacion, operacion, unico,
            c.UltimosDigitos, c.Monto, c.Cuotas, ahora);
    }

    internal static string Codigo(string alfabeto, int largo) =>
        new(Enumerable.Range(0, largo).Select(_ => alfabeto[RandomNumberGenerator.GetInt32(alfabeto.Length)]).ToArray());
}

/* =====================================================================
   Webpay Plus (pago online)
   Flujo real: crear transacción -> el navegador hace POST del token_ws a la
   URL de Webpay -> el usuario paga -> Webpay redirige a return_url con token_ws
   -> el comercio confirma (commit) la transacción.
   ===================================================================== */

public sealed record TransaccionCreada(string Token, string Url);

public sealed record ResultadoWebpay(
    bool Autorizada, string Estado, string BuyOrder, int Monto,
    string? CodigoAutorizacion, string? UltimosDigitos, string? TipoPago, int Cuotas, DateTime? Fecha);

public interface IPasarelaWebpay
{
    Task<TransaccionCreada> CrearAsync(string buyOrder, string sessionId, int monto, string returnUrl, CancellationToken ct = default);
    Task<ResultadoWebpay> ConfirmarAsync(string token, CancellationToken ct = default);
    Task ReversarAsync(string token, int monto, CancellationToken ct = default);
}

/// <summary>
/// Simulado: página propia (WebpayMockController), solo en Development o Testing (hallazgo C2).
/// Integracion: ambiente de integración de Transbank, con tarjetas de prueba.
/// Produccion: Transbank real.
/// </summary>
public enum ModoWebpay { Simulado, Integracion, Produccion }

public sealed class OpcionesWebpay
{
    public const string CodigoComercioIntegracion = "597055555532";
    public static readonly IReadOnlySet<string> EntornosConSimulador =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Development", "Testing" };

    public ModoWebpay Modo { get; set; } = ModoWebpay.Integracion;
    public string UrlPagoMock { get; set; } = "";
    public string ReturnUrl { get; set; } = "";
    public string CodigoComercio { get; set; } = CodigoComercioIntegracion;
    /// <summary>Tbk-Api-Key-Secret. Va en .env o user-secrets, nunca en appsettings.</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>Solo para pruebas: reemplaza el dominio de Transbank.</summary>
    public string? UrlBase { get; set; }
    public int TimeoutSegundos { get; set; } = 30;

    public string UrlBaseEfectiva => UrlBase ?? (Modo == ModoWebpay.Produccion
        ? "https://webpay3g.transbank.cl/"
        : "https://webpay3gint.transbank.cl/");

    /// <summary>Problemas de configuración. La API no arranca si hay alguno.</summary>
    public IReadOnlyList<string> Revisar(string entorno)
    {
        var problemas = new List<string>();
        if (!Uri.TryCreate(ReturnUrl, UriKind.Absolute, out var retorno))
            problemas.Add("Webpay:ReturnUrl debe ser una URL absoluta.");

        if (Modo == ModoWebpay.Simulado)
        {
            if (!EntornosConSimulador.Contains(entorno))
                problemas.Add($"Webpay:Modo=Simulado no está permitido en el entorno '{entorno}': cualquiera podría aprobar su propio pago (hallazgo C2).");
            if (!Uri.TryCreate(UrlPagoMock, UriKind.Absolute, out _))
                problemas.Add("Webpay:UrlPagoMock debe ser una URL absoluta.");
            return problemas;
        }

        if (string.IsNullOrWhiteSpace(CodigoComercio)) problemas.Add("Falta Webpay:CodigoComercio.");
        if (string.IsNullOrWhiteSpace(ApiKey)) problemas.Add("Falta el secreto Webpay:ApiKey.");

        if (Modo == ModoWebpay.Produccion)
        {
            if (CodigoComercio == CodigoComercioIntegracion)
                problemas.Add("Webpay:Modo=Produccion con el código de comercio de integración.");
            if (retorno is not null && retorno.Scheme != Uri.UriSchemeHttps)
                problemas.Add("Webpay:ReturnUrl debe usar https en producción.");
            if (UrlBase is not null)
                problemas.Add("Webpay:UrlBase solo se permite en pruebas.");
        }
        return problemas;
    }
}

/// <summary>
/// Webpay simulado. Guarda las transacciones en memoria (una sola instancia de la API).
/// La página de pago simulada está en WebpayMockController.
/// </summary>
public sealed class WebpayMock(IOptions<OpcionesWebpay> opciones, IReloj reloj, ILogger<WebpayMock> log) : IPasarelaWebpay
{
    internal sealed class Tx
    {
        public required string Token { get; init; }
        public required string BuyOrder { get; init; }
        public required string SessionId { get; init; }
        public required int Monto { get; init; }
        public required string ReturnUrl { get; init; }
        public string Estado { get; set; } = "INITIALIZED";   // INITIALIZED, AUTHORIZED, FAILED, ABORTED, REVERSED
        public bool Confirmada { get; set; }
        public string? UltimosDigitos { get; set; }
        public string? TipoPago { get; set; }
        public int Cuotas { get; set; }
        public string? CodigoAutorizacion { get; set; }
        public DateTime? Fecha { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Tx> Transacciones = new();
    private readonly OpcionesWebpay _o = opciones.Value;

    internal static Tx? Buscar(string token) => Transacciones.GetValueOrDefault(token);

    public Task<TransaccionCreada> CrearAsync(string buyOrder, string sessionId, int monto, string returnUrl, CancellationToken ct = default)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Transacciones[token] = new Tx { Token = token, BuyOrder = buyOrder, SessionId = sessionId, Monto = monto, ReturnUrl = returnUrl };
        log.LogInformation("[Webpay mock] Transacción {BuyOrder} creada por ${Monto}", buyOrder, monto);
        return Task.FromResult(new TransaccionCreada(token, _o.UrlPagoMock));
    }

    public Task<ResultadoWebpay> ConfirmarAsync(string token, CancellationToken ct = default)
    {
        var tx = Buscar(token) ?? throw new ReglaNegocioException("Transacción Webpay no encontrada.", 404, "WEBPAY");
        lock (tx)
        {
            if (tx.Confirmada)
                throw new ReglaNegocioException("La transacción ya fue confirmada.", 422, "WEBPAY");
            if (tx.Estado == "INITIALIZED")
                throw new ReglaNegocioException("El pago aún no ha sido procesado por el usuario.", 422, "WEBPAY");
            tx.Confirmada = true;
        }

        return Task.FromResult(new ResultadoWebpay(
            tx.Estado == "AUTHORIZED", tx.Estado, tx.BuyOrder, tx.Monto,
            tx.CodigoAutorizacion, tx.UltimosDigitos, tx.TipoPago, tx.Cuotas, tx.Fecha));
    }

    public Task ReversarAsync(string token, int monto, CancellationToken ct = default)
    {
        if (Buscar(token) is { } tx) tx.Estado = "REVERSED";
        log.LogWarning("[Webpay mock] Transacción {Token} reversada por ${Monto}", token[..8], monto);
        return Task.CompletedTask;
    }

    /// <summary>Lo usa la página simulada cuando el usuario aprueba, rechaza o anula.</summary>
    internal void Procesar(Tx tx, string accion, string ultimosDigitos, string tipoPago)
    {
        lock (tx)
        {
            if (tx.Estado != "INITIALIZED") return;
            tx.Fecha = reloj.Ahora;
            tx.UltimosDigitos = ultimosDigitos;
            tx.TipoPago = tipoPago;
            switch (accion)
            {
                case "aprobar":
                    tx.Estado = "AUTHORIZED";
                    tx.CodigoAutorizacion = PosMock.Codigo("0123456789", 6);
                    break;
                case "rechazar":
                    tx.Estado = "FAILED";
                    break;
                default:
                    tx.Estado = "ABORTED";
                    break;
            }
        }
    }
}
