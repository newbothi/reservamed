using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Common;
using ReservaMed.Api.Integraciones.Pagos;

namespace ReservaMed.UnitTests;

/// <summary>Hallazgo C2: el Webpay simulado no puede existir fuera de desarrollo.</summary>
public class OpcionesWebpayTests
{
    private static OpcionesWebpay Simulado() => new()
    {
        Modo = ModoWebpay.Simulado,
        UrlPagoMock = "http://localhost:5173/webpay-mock/pagar",
        ReturnUrl = "http://localhost:5173/pago/retorno"
    };

    private static OpcionesWebpay Integracion() => new()
    {
        Modo = ModoWebpay.Integracion,
        ReturnUrl = "https://reservamed.localhost/pago/retorno",
        ApiKey = "clave-de-prueba"
    };

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Simulado_se_permite_en_desarrollo_y_pruebas(string entorno) =>
        Assert.Empty(Simulado().Revisar(entorno));

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void Simulado_se_rechaza_en_cualquier_otro_entorno(string entorno) =>
        Assert.Contains(Simulado().Revisar(entorno), p => p.Contains("C2"));

    [Fact]
    public void Integracion_valida_no_tiene_problemas() =>
        Assert.Empty(Integracion().Revisar("Production"));

    [Fact]
    public void Integracion_sin_api_key_se_rechaza()
    {
        var o = Integracion();
        o.ApiKey = "";
        Assert.Contains(o.Revisar("Production"), p => p.Contains("Webpay:ApiKey"));
    }

    [Fact]
    public void Produccion_no_acepta_comercio_de_integracion_ni_http()
    {
        var o = Integracion();
        o.Modo = ModoWebpay.Produccion;
        o.ReturnUrl = "http://reservamed.cl/pago/retorno";

        var problemas = o.Revisar("Production");

        Assert.Contains(problemas, p => p.Contains("código de comercio de integración"));
        Assert.Contains(problemas, p => p.Contains("https"));
    }

    [Fact]
    public void Url_base_segun_modo()
    {
        Assert.StartsWith("https://webpay3gint.transbank.cl", Integracion().UrlBaseEfectiva);
        var prod = Integracion();
        prod.Modo = ModoWebpay.Produccion;
        Assert.StartsWith("https://webpay3g.transbank.cl", prod.UrlBaseEfectiva);
    }

    [Fact]
    public void Quitar_controladores_elimina_la_pagina_simulada()
    {
        var feature = new ControllerFeature();
        feature.Controllers.Add(typeof(WebpayMockController).GetTypeInfo());
        feature.Controllers.Add(typeof(OpcionesWebpayTests).GetTypeInfo());

        new QuitarControladores(typeof(WebpayMockController)).PopulateFeature([], feature);

        Assert.DoesNotContain(typeof(WebpayMockController).GetTypeInfo(), feature.Controllers);
        Assert.Single(feature.Controllers);
    }
}

/// <summary>Cliente REST de Transbank contra un servidor falso: rutas, cabeceras, cuerpo y respuestas.</summary>
public class WebpayTransbankTests
{
    private sealed class ServidorFalso(Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string? Cuerpo)> Recibidas { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var cuerpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Recibidas.Add((request, cuerpo));
            return responder(request, cuerpo);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (WebpayTransbank Cliente, ServidorFalso Servidor) Crear(Func<HttpRequestMessage, string?, HttpResponseMessage> r)
    {
        var o = new OpcionesWebpay { Modo = ModoWebpay.Integracion, ApiKey = "secreto-prueba", ReturnUrl = "https://x/retorno" };
        var servidor = new ServidorFalso(r);
        var http = new HttpClient(servidor);
        WebpayTransbank.Configurar(http, o);
        return (new WebpayTransbank(http, Options.Create(o), NullLogger<WebpayTransbank>.Instance), servidor);
    }

    [Fact]
    public async Task Crear_envia_credenciales_y_cuerpo_en_snake_case()
    {
        var (cliente, servidor) = Crear((_, _) => Json(HttpStatusCode.OK, """{"token":"tok123","url":"https://webpay3gint.transbank.cl/webpayserver/initTransaction"}"""));

        var tx = await cliente.CrearAsync("RM-1-ABC", "u7", 15000, "https://x/retorno");

        Assert.Equal("tok123", tx.Token);
        Assert.StartsWith("https://webpay3gint.transbank.cl", tx.Url);
        var (req, cuerpo) = Assert.Single(servidor.Recibidas);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://webpay3gint.transbank.cl/rswebpaytransaction/api/webpay/v1.2/transactions", req.RequestUri!.ToString());
        Assert.Equal(OpcionesWebpay.CodigoComercioIntegracion, req.Headers.GetValues("Tbk-Api-Key-Id").Single());
        Assert.Equal("secreto-prueba", req.Headers.GetValues("Tbk-Api-Key-Secret").Single());
        using var doc = JsonDocument.Parse(cuerpo!);
        Assert.Equal("RM-1-ABC", doc.RootElement.GetProperty("buy_order").GetString());
        Assert.Equal(15000, doc.RootElement.GetProperty("amount").GetInt32());
        Assert.Equal("https://x/retorno", doc.RootElement.GetProperty("return_url").GetString());
    }

    [Fact]
    public async Task Confirmar_autorizada_mapea_tarjeta_y_fecha_en_hora_chilena()
    {
        var (cliente, servidor) = Crear((_, _) => Json(HttpStatusCode.OK, """
            {"vci":"TSY","amount":15000,"status":"AUTHORIZED","buy_order":"RM-1-ABC","session_id":"u7",
             "card_detail":{"card_number":"6623"},"accounting_date":"1023","transaction_date":"2026-10-23T15:30:00.000Z",
             "authorization_code":"1213","payment_type_code":"VD","response_code":0,"installments_number":0}
            """));

        var r = await cliente.ConfirmarAsync("tok123");

        Assert.True(r.Autorizada);
        Assert.Equal(15000, r.Monto);
        Assert.Equal("6623", r.UltimosDigitos);
        Assert.Equal("VD", r.TipoPago);
        Assert.Equal(new DateTime(2026, 10, 23, 12, 30, 0), r.Fecha);   // UTC-3 en octubre
        Assert.Equal(HttpMethod.Put, servidor.Recibidas[0].Req.Method);
        Assert.EndsWith("/transactions/tok123", servidor.Recibidas[0].Req.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Confirmar_rechazada_no_queda_autorizada()
    {
        var (cliente, _) = Crear((_, _) => Json(HttpStatusCode.OK,
            """{"amount":15000,"status":"FAILED","buy_order":"RM-1","response_code":-1}"""));

        var r = await cliente.ConfirmarAsync("tok");

        Assert.False(r.Autorizada);
        Assert.Equal("FAILED", r.Estado);
    }

    [Fact]
    public async Task Error_de_transbank_se_traduce_a_error_de_negocio()
    {
        var (cliente, _) = Crear((_, _) => Json(HttpStatusCode.UnprocessableEntity,
            """{"error_message":"Invalid status '6' for transaction while authorizing"}"""));

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => cliente.ConfirmarAsync("tok"));

        Assert.Equal("WEBPAY", ex.Codigo);
        Assert.Equal(502, ex.Status);
        Assert.Contains("Invalid status", ex.Message);
    }

    [Fact]
    public async Task Reversar_envia_monto_al_endpoint_de_reembolso()
    {
        var (cliente, servidor) = Crear((_, _) => Json(HttpStatusCode.OK, """{"type":"REVERSED"}"""));

        await cliente.ReversarAsync("tok123", 15000);

        var (req, cuerpo) = Assert.Single(servidor.Recibidas);
        Assert.EndsWith("/transactions/tok123/refunds", req.RequestUri!.AbsolutePath);
        Assert.Equal(15000, JsonDocument.Parse(cuerpo!).RootElement.GetProperty("amount").GetInt32());
    }
}
