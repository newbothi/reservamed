using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ReservaMed.Api.Integraciones.Pagos;

/// <summary>
/// Página de pago simulada, al estilo del formulario de Webpay.
/// Sólo para desarrollo: en producción el navegador va al dominio de Transbank.
/// </summary>
[Route("webpay-mock")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class WebpayMockController(WebpayMock webpay) : ControllerBase
{
    private static readonly CultureInfo Chile = new("es-CL");

    [HttpGet("pagar")]
    [HttpPost("pagar")]
    public IActionResult Pagar([FromForm(Name = "token_ws")] string? tokenForm, [FromQuery(Name = "token_ws")] string? tokenQuery)
    {
        var token = tokenForm ?? tokenQuery;
        var tx = token is null ? null : WebpayMock.Buscar(token);
        if (tx is null) return Content(Pagina("<p>Transacción no encontrada o expirada.</p>"), "text/html; charset=utf-8");

        var monto = tx.Monto.ToString("C0", Chile);
        var html = $"""
            <div class="caja">
              <div class="marca">webpay <span>simulado</span></div>
              <p class="dato">Comercio: <b>ReservaMed</b></p>
              <p class="dato">Orden de compra: <b>{WebUtility.HtmlEncode(tx.BuyOrder)}</b></p>
              <p class="monto">{monto}</p>
              <form method="post" action="procesar">
                <input type="hidden" name="token_ws" value="{tx.Token}">
                <label>Tipo de tarjeta
                  <select name="tipo_pago">
                    <option value="VD">Débito (Redcompra)</option>
                    <option value="VN">Crédito</option>
                  </select>
                </label>
                <label>Número de tarjeta (prueba)
                  <input name="tarjeta" value="4051 8856 0044 6623" maxlength="19">
                </label>
                <p class="ayuda">Tarjeta terminada en 0000 = rechazo del emisor.</p>
                <div class="acciones">
                  <button name="accion" value="aprobar" class="ok">Pagar</button>
                  <button name="accion" value="anular" class="sec">Anular compra y volver</button>
                </div>
              </form>
            </div>
            """;
        return Content(Pagina(html), "text/html; charset=utf-8");
    }

    [HttpPost("procesar")]
    public IActionResult Procesar(
        [FromForm(Name = "token_ws")] string token,
        [FromForm(Name = "accion")] string accion,
        [FromForm(Name = "tarjeta")] string? tarjeta,
        [FromForm(Name = "tipo_pago")] string? tipoPago)
    {
        var tx = WebpayMock.Buscar(token);
        if (tx is null) return NotFound();

        var digitos = new string((tarjeta ?? "").Where(char.IsDigit).ToArray());
        var ultimos = digitos.Length >= 4 ? digitos[^4..] : "0000";
        var accionFinal = accion == "aprobar" && ultimos == "0000" ? "rechazar" : accion;

        webpay.Procesar(tx, accionFinal, ultimos, tipoPago == "VN" ? "VN" : "VD");

        // Webpay devuelve token_ws cuando hubo intento de pago y TBK_TOKEN cuando el usuario anuló.
        var separador = tx.ReturnUrl.Contains('?') ? '&' : '?';
        var destino = accionFinal == "anular"
            ? $"{tx.ReturnUrl}{separador}TBK_TOKEN={tx.Token}&TBK_ORDEN_COMPRA={Uri.EscapeDataString(tx.BuyOrder)}"
            : $"{tx.ReturnUrl}{separador}token_ws={tx.Token}";
        return Redirect(destino);
    }

    private static string Pagina(string cuerpo) => $$"""
        <!doctype html><html lang="es"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Webpay (simulado)</title>
        <style>
          body{margin:0;font-family:system-ui,sans-serif;background:#f1f1f4;display:grid;place-items:center;min-height:100vh;color:#222}
          .caja{background:#fff;border-radius:12px;padding:28px;width:min(380px,92vw);box-shadow:0 8px 30px rgba(0,0,0,.08)}
          .marca{font-weight:800;font-size:26px;color:#6b196b}.marca span{font-size:12px;color:#999;font-weight:500}
          .dato{margin:6px 0;font-size:14px;color:#555}.monto{font-size:32px;font-weight:700;margin:14px 0}
          label{display:block;font-size:13px;margin:12px 0 4px;color:#444}
          input,select{width:100%;box-sizing:border-box;padding:10px;border:1px solid #ccc;border-radius:8px;font-size:15px;margin-top:4px}
          .ayuda{font-size:12px;color:#888}.acciones{display:flex;flex-direction:column;gap:8px;margin-top:16px}
          button{padding:12px;border-radius:8px;border:0;font-size:15px;cursor:pointer}
          .ok{background:#6b196b;color:#fff}.sec{background:transparent;color:#6b196b;text-decoration:underline}
        </style></head><body>{{cuerpo}}</body></html>
        """;
}
