using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReservaMed.Api.Modules.Auth;

namespace ReservaMed.IntegrationTests;

/// <summary>Fase 5: brechas ASVS de la propia cuenta (V2.1.3, V2.1.6, V2.2.3, V3.3.3, V3.3.4, V3.7.1, V8.3.2).</summary>
[Collection("api")]
public sealed class CuentaTests(ApiFactory api)
{
    private async Task<HttpStatusCode> YoAsync(string token) =>
        (await api.ConToken(token).GetAsync("/api/auth/yo")).StatusCode;

    private static async Task<string?> CodigoAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.TryGetProperty("codigo", out var c) ? c.GetString() : null;

    [Fact]
    public async Task Cambiar_clave_exige_la_actual_cierra_las_demas_sesiones_y_avisa_por_correo()
    {
        var (email, clave) = await api.PacienteNuevoAsync("cambio-clave");
        var otra = await api.LoginPacienteAsync(email, clave);
        var actual = await api.LoginPacienteAsync(email, clave);

        var mal = await api.PostAsync("/api/cuenta/clave", new { actual = "no-es-la-clave", nueva = "Otra-clave-larga-y-unica-2026" }, actual.AccessToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mal.StatusCode);
        Assert.Equal("CLAVE_ACTUAL", await CodigoAsync(mal));
        Assert.Equal(HttpStatusCode.OK, await YoAsync(actual.AccessToken));   // la sesión sigue viva

        var debil = await api.PostAsync("/api/cuenta/clave", new { actual = clave, nueva = "corta" }, actual.AccessToken);
        Assert.Equal("CONTRASENA", await CodigoAsync(debil));

        const string nueva = "Otra-clave-larga-y-unica-2026";
        var ok = await api.PostAsync("/api/cuenta/clave", new { actual = clave, nueva }, actual.AccessToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, (await ok.Content.ReadFromJsonAsync<ResultadoCambioClave>())!.SesionesCerradas);

        Assert.Equal(HttpStatusCode.OK, await YoAsync(actual.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await YoAsync(otra.AccessToken));
        Assert.Contains(api.Buzon.Para(email), c => c.Asunto.Contains("contraseña", StringComparison.OrdinalIgnoreCase) && c.Asunto.Contains("cambió"));

        var viejo = await api.PostAsync("/api/auth/login", new { email, password = clave, cliente = "PORTAL" });
        Assert.Equal(HttpStatusCode.Unauthorized, viejo.StatusCode);
        await api.LoginPacienteAsync(email, nueva);
    }

    [Fact]
    public async Task Contrasenas_largas_cuentan_completas_mas_alla_de_72_bytes()
    {
        var (email, clave) = await api.PacienteNuevoAsync("clave-larga");
        var s = await api.LoginPacienteAsync(email, clave);
        var larga = "Frase-de-paso-muy-larga-" + new string('x', 70) + "-final-A";
        var r = await api.PostAsync("/api/cuenta/clave", new { actual = clave, nueva = larga }, s.AccessToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        // Igual en los primeros 72 bytes, distinta al final: con BCrypt directo habría entrado.
        var casi = larga[..^1] + "B";
        var login = await api.PostAsync("/api/auth/login", new { email, password = casi, cliente = "PORTAL" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        await api.LoginPacienteAsync(email, larga);
    }

    [Fact]
    public void Hash_nuevo_y_legado_se_verifican()
    {
        var nuevo = Contrasenas.Hash("Una-clave-cualquiera-2026");
        Assert.StartsWith("$rm2$", nuevo);
        Assert.True(Contrasenas.Verificar("Una-clave-cualquiera-2026", nuevo));
        Assert.False(Contrasenas.Verificar("Una-clave-cualquiera-2027", nuevo));
        Assert.False(Contrasenas.EsLegado(nuevo));

        var legado = BCrypt.Net.BCrypt.HashPassword("Demo1234!", 11);
        Assert.True(Contrasenas.Verificar("Demo1234!", legado));
        Assert.True(Contrasenas.EsLegado(legado));
        Assert.False(Contrasenas.Verificar("x", "no-es-un-hash"));
    }

    [Fact]
    public async Task El_usuario_ve_sus_sesiones_y_cierra_otra()
    {
        var (email, clave) = await api.PacienteNuevoAsync("sesiones");
        var otra = await api.LoginPacienteAsync(email, clave);
        var actual = await api.LoginPacienteAsync(email, clave);

        var lista = (await api.ConToken(actual.AccessToken).GetFromJsonAsync<List<SesionActiva>>("/api/cuenta/sesiones"))!;
        Assert.Equal(2, lista.Count);
        Assert.Single(lista, x => x.Actual);
        var ajena = lista.Single(x => !x.Actual);

        var cerrar = await api.PostAsync($"/api/cuenta/sesiones/{ajena.Id}/cerrar", new { }, actual.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, cerrar.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await YoAsync(otra.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await YoAsync(actual.AccessToken));

        // La sesión de otro usuario no se puede cerrar (ni se revela si existe).
        var (email2, clave2) = await api.PacienteNuevoAsync("sesiones-otro");
        var tercero = await api.LoginPacienteAsync(email2, clave2);
        var sidTercero = (await api.ConToken(tercero.AccessToken).GetFromJsonAsync<List<SesionActiva>>("/api/cuenta/sesiones"))!.Single().Id;
        var intento = await api.PostAsync($"/api/cuenta/sesiones/{sidTercero}/cerrar", new { }, actual.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, intento.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await YoAsync(tercero.AccessToken));
    }

    [Fact]
    public async Task Acciones_de_administracion_exigen_reautenticar_con_totp()
    {
        var admin = await api.LoginPersonalAsync("admin@reservamed.cl");
        var (email, clave) = await api.PacienteNuevoAsync("reaut");
        var paciente = await api.LoginPacienteAsync(email, clave);
        var ruta = $"/api/usuarios/{paciente.Usuario.Id}/desactivar";

        var sin = await api.ConToken(admin.AccessToken).PostAsync(ruta, null);
        Assert.Equal(HttpStatusCode.Forbidden, sin.StatusCode);
        Assert.Equal("REAUTENTICAR", await CodigoAsync(sin));

        var mal = await api.PostAsync("/api/cuenta/reautenticar", new { codigo = "000000" }, admin.AccessToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mal.StatusCode);

        await api.ReautenticarAsync("admin@reservamed.cl", admin.AccessToken);
        var con = await api.ConToken(admin.AccessToken).PostAsync(ruta, null);
        Assert.Equal(HttpStatusCode.NoContent, con.StatusCode);

        // La reautenticación es de ESA sesión: otra sesión del mismo admin sigue sin poder.
        var otraSesion = await api.LoginPersonalAsync("admin@reservamed.cl");
        var otra = await api.ConToken(otraSesion.AccessToken).PostAsync($"/api/usuarios/{paciente.Usuario.Id}/activar", null);
        Assert.Equal(HttpStatusCode.Forbidden, otra.StatusCode);
    }

    [Fact]
    public async Task El_paciente_exporta_sus_datos()
    {
        var (email, clave) = await api.PacienteNuevoAsync("exportar");
        var s = await api.LoginPacienteAsync(email, clave);

        var r = await api.ConToken(s.AccessToken).GetAsync("/api/cuenta/datos");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("attachment", r.Content.Headers.ContentDisposition?.ToString() ?? r.Headers.ToString());
        var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(email, json.GetProperty("paciente").GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("citas").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("accesosASuFicha").ValueKind);
        Assert.False(json.GetProperty("cuenta").GetProperty("privacidadAceptadaEn").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Registro_exige_aceptar_la_politica_de_privacidad()
    {
        var numero = Random.Shared.Next(30_000_000, 39_999_999);
        var rut = $"{numero}-{ReservaMed.Api.Common.Rut.CalcularDv(numero)}";
        var cuerpo = JsonSerializer.SerializeToNode(ClienteAuth.Registro(rut, $"sin-privacidad-{numero}@prueba.cl", "Una-clave-larga-y-unica-2026"))!;
        cuerpo["aceptaPrivacidad"] = false;
        var r = await api.CreateClient().PostAsJsonAsync("/api/auth/registro", cuerpo);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("privacidad", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Solicitar_eliminacion_cierra_la_cuenta_y_borra_el_contacto()
    {
        var (email, clave) = await api.PacienteNuevoAsync("eliminar");
        var s = await api.LoginPacienteAsync(email, clave);

        var sinConfirmar = await api.PostAsync("/api/cuenta/eliminacion", new { clave, confirmacion = "si" }, s.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, sinConfirmar.StatusCode);
        var malaClave = await api.PostAsync("/api/cuenta/eliminacion", new { clave = "otra", confirmacion = "ELIMINAR" }, s.AccessToken);
        Assert.Equal("CLAVE_ACTUAL", await CodigoAsync(malaClave));

        var ok = await api.PostAsync("/api/cuenta/eliminacion", new { clave, confirmacion = "ELIMINAR" }, s.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await YoAsync(s.AccessToken));
        var login = await api.PostAsync("/api/auth/login", new { email, password = clave, cliente = "PORTAL" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Contains(api.Buzon.Para(email), c => c.Asunto.Contains("eliminación"));

        await using var cn = new Microsoft.Data.SqlClient.SqlConnection(api.CadenaApi);
        await cn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("""
            SELECT COUNT(1) FROM dbo.Pacientes p JOIN dbo.SolicitudesDatos d ON d.PacienteId = p.Id
            WHERE p.Id = @Id AND p.Email LIKE 'eliminado-%@reservamed.invalid' AND p.Telefono IS NULL
              AND d.Tipo = 'ELIMINAR' AND d.Estado = 'PENDIENTE'
            """, cn);
        cmd.Parameters.AddWithValue("@Id", s.Usuario.PacienteId!.Value);
        Assert.Equal(1, (int)(await cmd.ExecuteScalarAsync())!);
    }
}
