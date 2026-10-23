using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ReservaMed.IntegrationTests;

/// <summary>
/// Pruebas base de la fase 0: la API arranca contra SQL Server real, el login funciona
/// y un token firmado con la clave que estuvo publicada en el repositorio es rechazado (C1).
/// </summary>
[Collection("api")]
public sealed class SeguridadBaseTests(ApiFactory api)
{
    private const string ClaveJwtPublicada = "CAMBIAR-esta-clave-de-desarrollo-ReservaMed-2026-minimo-32-bytes";

    private sealed record Sesion(string Token, string Rol);

    [Fact]
    public async Task Health_responde_200()
    {
        var resp = await api.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Login_con_usuario_demo_entrega_token()
    {
        var resp = await api.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "admin@reservamed.cl", password = "Demo1234!" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sesion = await resp.Content.ReadFromJsonAsync<Sesion>();
        Assert.Equal("ADMIN", sesion!.Rol);
        Assert.False(string.IsNullOrWhiteSpace(sesion.Token));
    }

    [Fact]
    public async Task Login_con_clave_incorrecta_responde_401()
    {
        var resp = await api.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "admin@reservamed.cl", password = "otra-clave" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Token_firmado_con_la_clave_publicada_es_rechazado()
    {
        var cliente = api.CreateClient();
        cliente.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TokenAdmin(ClaveJwtPublicada));

        var resp = await cliente.GetAsync("/api/auditoria");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Token_firmado_con_la_clave_configurada_es_aceptado()
    {
        // Control: confirma que la prueba anterior falla por la clave y no por otra razón.
        var cliente = api.CreateClient();
        cliente.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TokenAdmin(ApiFactory.ClaveJwtPrueba));

        var resp = await cliente.GetAsync("/api/auditoria");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Endpoint_protegido_sin_token_responde_401()
    {
        var resp = await api.CreateClient().GetAsync("/api/auditoria");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private static string TokenAdmin(string clave) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "reservamed-core",
            Audience = "reservamed",
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "1",
                ["rol"] = "ADMIN",
                ["nombre"] = "Atacante",
                ["email"] = "admin@reservamed.cl"
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(clave)), SecurityAlgorithms.HmacSha256)
        });
}
