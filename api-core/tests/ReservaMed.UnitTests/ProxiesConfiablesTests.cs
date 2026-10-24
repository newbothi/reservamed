using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Common;

namespace ReservaMed.UnitTests;

/// <summary>Hallazgo A4: la IP del cliente solo se toma de X-Forwarded-For si la envía un proxy conocido.</summary>
public class ProxiesConfiablesTests
{
    private const string IpNginx = "172.30.1.10";
    private const string IpFalsa = "203.0.113.66";

    private static async Task<IPAddress?> IpVistaPorLaApi(string ipConexion, string? xff)
    {
        var opciones = new ForwardedHeadersOptions();
        ProxiesConfiables.Configurar(opciones, [IpNginx]);
        IPAddress? vista = null;
        var middleware = new ForwardedHeadersMiddleware(
            ctx => { vista = ctx.Connection.RemoteIpAddress; return Task.CompletedTask; },
            NullLoggerFactory.Instance, Options.Create(opciones));

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ipConexion);
        if (xff is not null) ctx.Request.Headers["X-Forwarded-For"] = xff;

        await middleware.Invoke(ctx);
        return vista;
    }

    [Fact]
    public async Task Cliente_directo_no_puede_falsear_su_ip()
    {
        var vista = await IpVistaPorLaApi("198.51.100.7", IpFalsa);
        Assert.Equal(IPAddress.Parse("198.51.100.7"), vista);
    }

    [Fact]
    public async Task Desde_nginx_se_toma_la_ip_informada()
    {
        var vista = await IpVistaPorLaApi(IpNginx, "198.51.100.7");
        Assert.Equal(IPAddress.Parse("198.51.100.7"), vista);
    }

    [Fact]
    public async Task Solo_se_confia_en_el_ultimo_salto()
    {
        // El atacante antepone una IP falsa; NGINX la reemplaza, pero aunque llegara, solo vale el último valor.
        var vista = await IpVistaPorLaApi(IpNginx, $"{IpFalsa}, 198.51.100.7");
        Assert.Equal(IPAddress.Parse("198.51.100.7"), vista);
    }

    [Fact]
    public void Ip_invalida_en_configuracion_detiene_el_arranque() =>
        Assert.Throws<InvalidOperationException>(() =>
            ProxiesConfiables.Configurar(new ForwardedHeadersOptions(), ["nginx"]));

    [Fact]
    public void Sin_configuracion_solo_confia_en_loopback()
    {
        var o = new ForwardedHeadersOptions();
        ProxiesConfiables.Configurar(o, null);
        Assert.All(o.KnownProxies, ip => Assert.True(IPAddress.IsLoopback(ip)));
        Assert.Equal(1, o.ForwardLimit);
    }
}
