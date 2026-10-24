using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ReservaMed.IntegrationTests;

/// <summary>
/// Pruebas base: la API arranca contra SQL Server real, el login funciona, un token firmado con
/// la clave que estuvo publicada es rechazado (C1) y, desde F2, un token bien firmado pero sin
/// sesión activa tampoco sirve (A1).
/// </summary>
[Collection("api")]
public sealed class SeguridadBaseTests(ApiFactory api)
{
    private const string ClaveJwtPublicada = "CAMBIAR-esta-clave-de-desarrollo-ReservaMed-2026-minimo-32-bytes";

    [Fact]
    public async Task Health_responde_200()
    {
        var resp = await api.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Login_de_paciente_entrega_sesion_con_access_y_refresh()
    {
        var s = await api.LoginPacienteAsync("ignacio@demo.cl");

        Assert.Equal("PACIENTE", s.Usuario.Rol);
        Assert.False(string.IsNullOrWhiteSpace(s.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(s.RefreshToken));
    }

    [Fact]
    public async Task Login_con_clave_incorrecta_responde_401()
    {
        var resp = await api.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "sofia@demo.cl", password = "otra-clave", cliente = "PORTAL" });
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
    public async Task Token_bien_firmado_pero_sin_sesion_es_rechazado()
    {
        // Aunque alguien obtuviera la clave de firma, sin una sesión viva en la base no entra (A1).
        var cliente = api.CreateClient();
        cliente.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TokenAdmin(ApiFactory.ClaveJwtPrueba));

        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync("/api/auditoria")).StatusCode);
    }

    [Fact]
    public async Task Sesion_real_de_admin_con_totp_es_aceptada()
    {
        // Control: las pruebas anteriores fallan por la clave o la sesión, no por otra razón.
        var admin = await api.AdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await api.ConToken(admin.AccessToken).GetAsync("/api/auditoria")).StatusCode);
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
