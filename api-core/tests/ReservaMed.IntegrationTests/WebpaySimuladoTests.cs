using System.Net;

namespace ReservaMed.IntegrationTests;

/// <summary>
/// C2: en Testing (como en Development) la página simulada existe y sirve para probar el flujo.
/// En Production la API no arranca con Modo=Simulado (ver OpcionesWebpayTests) y el controlador
/// se quita; el job E2E de CI comprueba además que en Docker la ruta responde 404.
/// </summary>
[Collection("api")]
public sealed class WebpaySimuladoTests(ApiFactory api)
{
    [Fact]
    public async Task En_testing_la_pagina_simulada_existe()
    {
        var resp = await api.CreateClient().GetAsync("/webpay-mock/pagar?token_ws=inexistente");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("no encontrada", await resp.Content.ReadAsStringAsync());
    }
}
