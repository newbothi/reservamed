using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Integraciones.Pagos;

/// <summary>
/// Cliente de Webpay Plus (API REST v1.2 de Transbank).
/// En Docker usa el ambiente de integración: el pago se hace en la página real de Transbank
/// con tarjetas de prueba, así que nadie puede aprobar su propio pago desde ReservaMed (hallazgo C2).
/// </summary>
public sealed class WebpayTransbank(HttpClient http, IOptions<OpcionesWebpay> opciones, ILogger<WebpayTransbank> log)
    : IPasarelaWebpay
{
    internal const string Ruta = "rswebpaytransaction/api/webpay/v1.2/transactions";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly TimeZoneInfo ZonaChile = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");

    /// <summary>Configura el HttpClient con el dominio y las credenciales del comercio.</summary>
    public static void Configurar(HttpClient http, OpcionesWebpay o)
    {
        http.BaseAddress = new Uri(o.UrlBaseEfectiva.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(o.TimeoutSegundos);
        http.DefaultRequestHeaders.Add("Tbk-Api-Key-Id", o.CodigoComercio);
        http.DefaultRequestHeaders.Add("Tbk-Api-Key-Secret", o.ApiKey);
    }

    public async Task<TransaccionCreada> CrearAsync(string buyOrder, string sessionId, int monto, string returnUrl,
        CancellationToken ct = default)
    {
        var cuerpo = new SolicitudCrear(buyOrder, sessionId, monto, returnUrl);
        var r = await EnviarAsync<RespuestaCrear>(HttpMethod.Post, Ruta, cuerpo, ct);
        if (string.IsNullOrEmpty(r.Token) || string.IsNullOrEmpty(r.Url))
            throw ErrorPasarela("Webpay no devolvió token ni URL de pago.");

        log.LogInformation("[Webpay] Transacción {BuyOrder} creada por ${Monto} ({Modo})", buyOrder, monto, opciones.Value.Modo);
        return new TransaccionCreada(r.Token, r.Url);
    }

    public async Task<ResultadoWebpay> ConfirmarAsync(string token, CancellationToken ct = default)
    {
        var r = await EnviarAsync<RespuestaCommit>(HttpMethod.Put, $"{Ruta}/{Uri.EscapeDataString(token)}", null, ct);
        var autorizada = r.Status == "AUTHORIZED" && r.ResponseCode == 0;
        DateTime? fecha = r.TransactionDate is { } f
            ? DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(f, ZonaChile).DateTime, DateTimeKind.Unspecified)
            : null;

        return new ResultadoWebpay(
            autorizada, r.Status ?? "FAILED", r.BuyOrder ?? "", decimal.ToInt32(r.Amount),
            r.AuthorizationCode, r.CardDetail?.CardNumber, r.PaymentTypeCode, r.InstallmentsNumber ?? 0, fecha);
    }

    public async Task ReversarAsync(string token, int monto, CancellationToken ct = default)
    {
        var r = await EnviarAsync<RespuestaReembolso>(HttpMethod.Post,
            $"{Ruta}/{Uri.EscapeDataString(token)}/refunds", new SolicitudReembolso(monto), ct);
        log.LogWarning("[Webpay] Transacción {Token}… reversada por ${Monto} ({Tipo})", token[..Math.Min(8, token.Length)], monto, r.Type);
    }

    private async Task<T> EnviarAsync<T>(HttpMethod metodo, string ruta, object? cuerpo, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(metodo, ruta);
        if (cuerpo is not null) req.Content = JsonContent.Create(cuerpo, cuerpo.GetType(), options: Json);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogError("[Webpay] Transbank no responde: {Error}", ex.Message);
            throw ErrorPasarela("Webpay no está disponible. Intente nuevamente en unos minutos.", 503);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var error = await LeerErrorAsync(resp, ct);
                log.LogWarning("[Webpay] {Metodo} {Ruta} respondió {Status}: {Error}", metodo, ruta.Split('/')[^1], (int)resp.StatusCode, error);
                throw ErrorPasarela($"Webpay rechazó la operación: {error}");
            }
            return await resp.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw ErrorPasarela("Respuesta vacía de Webpay.");
        }
    }

    private static async Task<string> LeerErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var e = await resp.Content.ReadFromJsonAsync<RespuestaError>(Json, ct);
            if (!string.IsNullOrWhiteSpace(e?.ErrorMessage)) return e.ErrorMessage;
        }
        catch (JsonException) { /* cuerpo no JSON */ }
        return $"HTTP {(int)resp.StatusCode}";
    }

    private static ReglaNegocioException ErrorPasarela(string mensaje, int status = StatusCodes.Status502BadGateway) =>
        new(mensaje, status, "WEBPAY");

    internal sealed record SolicitudCrear(string BuyOrder, string SessionId, int Amount, string ReturnUrl);
    internal sealed record SolicitudReembolso(int Amount);
    internal sealed record RespuestaCrear(string? Token, string? Url);
    internal sealed record RespuestaReembolso(string? Type);
    internal sealed record RespuestaError(string? ErrorMessage);
    internal sealed record DetalleTarjeta(string? CardNumber);
    internal sealed record RespuestaCommit(
        string? Vci, decimal Amount, string? Status, string? BuyOrder, string? SessionId,
        DetalleTarjeta? CardDetail, DateTimeOffset? TransactionDate, string? AuthorizationCode,
        string? PaymentTypeCode, int? ResponseCode, int? InstallmentsNumber);
}

/// <summary>
/// Quita controladores del ensamblado según la configuración. Se usa para que la página
/// del Webpay simulado no exista fuera de Development/Testing (hallazgo C2).
/// </summary>
public sealed class QuitarControladores(params Type[] tipos)
    : Microsoft.AspNetCore.Mvc.ApplicationParts.IApplicationFeatureProvider<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeature>
{
    public void PopulateFeature(IEnumerable<Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPart> parts,
        Microsoft.AspNetCore.Mvc.Controllers.ControllerFeature feature)
    {
        foreach (var tipo in tipos)
            feature.Controllers.Remove(System.Reflection.IntrospectionExtensions.GetTypeInfo(tipo));
    }
}
