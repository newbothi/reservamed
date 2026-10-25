using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ReservaMed.Api.Jobs;

namespace ReservaMed.IntegrationTests;

/// <summary>Fase 5 (ASVS V11.1.7): la actividad inusual genera una alerta y un correo a los administradores.</summary>
[Collection("api")]
public sealed class AlertasTests(ApiFactory api)
{
    [Fact]
    public async Task Muchas_cuentas_probadas_desde_una_ip_generan_una_alerta_unica()
    {
        var servicio = api.Services.GetRequiredService<ServicioAlertas>();
        var admin = await api.AdminAsync();
        // Lo que hayan dejado otras pruebas queda revisado: solo un aumento vuelve a alertar.
        await servicio.EvaluarAsync();
        foreach (var previa in (await api.ConToken(admin.AccessToken).GetFromJsonAsync<List<AlertaDto>>("/api/seguridad/alertas?pendientes=true"))!)
            await api.PostAsync($"/api/seguridad/alertas/{previa.Id}/revisada", new { }, admin.AccessToken);

        // Relleno de credenciales: correos distintos, todos inexistentes, desde la misma IP.
        for (var i = 0; i < 10; i++)
            await api.PostAsync("/api/auth/login", new { email = $"no-existe-{Guid.NewGuid():N}@prueba.cl", password = "x", cliente = "PORTAL" });

        var nuevas = await servicio.EvaluarAsync();
        var alerta = Assert.Single(nuevas, a => a.Regla == "CUENTAS_PROBADAS_IP");
        Assert.Equal("ALTA", alerta.Severidad);
        Assert.DoesNotContain("@", alerta.Detalle);   // la alerta no repite los correos probados

        // No se duplica en la misma ventana.
        Assert.DoesNotContain(await servicio.EvaluarAsync(), a => a.Regla == "CUENTAS_PROBADAS_IP" && a.Clave == alerta.Clave);
        Assert.Contains(api.Buzon.Para("admin@reservamed.cl"), c => c.Asunto.Contains("CUENTAS_PROBADAS_IP"));

        var lista = await api.ConToken(admin.AccessToken).GetFromJsonAsync<List<AlertaDto>>("/api/seguridad/alertas?pendientes=true");
        Assert.Contains(lista!, a => a.Id == alerta.Id);

        var revisar = await api.PostAsync($"/api/seguridad/alertas/{alerta.Id}/revisada", new { }, admin.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, revisar.StatusCode);
        var otraVez = await api.PostAsync($"/api/seguridad/alertas/{alerta.Id}/revisada", new { }, admin.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, otraVez.StatusCode);
    }
}
