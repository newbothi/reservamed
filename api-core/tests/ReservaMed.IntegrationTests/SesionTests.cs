using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using ReservaMed.Api.Modules.Auth;

namespace ReservaMed.IntegrationTests;

/// <summary>
/// Gate de la fase 2: un token robado o revocado no sirve (A1), el personal usa segundo factor
/// (A2), el registro exige correo verificado sin revelar cuentas (A7, B4) y hay bloqueo (A2, M1).
/// </summary>
[Collection("api")]
public sealed class SesionTests(ApiFactory api)
{
    private async Task<HttpStatusCode> YoAsync(string token) =>
        (await api.ConToken(token).GetAsync("/api/auth/yo")).StatusCode;

    private async Task<HttpResponseMessage> RenovarAsync(string refresh) =>
        await api.PostAsync("/api/auth/renovar", new { refreshToken = refresh });

    /* ---------- A1: sesión, rotación y revocación ---------- */

    [Fact]
    public async Task Access_token_dura_15_minutos_y_lleva_la_sesion()
    {
        var s = await api.LoginPacienteAsync("ignacio@demo.cl");

        var minutos = (s.AccessExpiraUtc - DateTime.UtcNow).TotalMinutes;
        Assert.InRange(minutos, 14, 15.1);
        Assert.Equal(HttpStatusCode.OK, await YoAsync(s.AccessToken));
    }

    [Fact]
    public async Task Renovar_rota_el_refresh_y_el_anterior_ya_no_sirve()
    {
        var s = await api.LoginPacienteAsync("martina@demo.cl");

        var r1 = await RenovarAsync(s.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var nueva = (await r1.Content.ReadFromJsonAsync<SesionEmitida>())!;
        Assert.NotEqual(s.RefreshToken, nueva.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, await YoAsync(nueva.AccessToken));
    }

    [Fact]
    public async Task Reusar_un_refresh_robado_revoca_la_sesion_completa()
    {
        var s = await api.LoginPacienteAsync("martina@demo.cl");
        var legitima = (await (await RenovarAsync(s.RefreshToken)).Content.ReadFromJsonAsync<SesionEmitida>())!;

        // El atacante usa el refresh viejo que copió.
        var robo = await RenovarAsync(s.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, robo.StatusCode);

        // Resultado: la sesión entera queda cerrada, también para la copia legítima.
        Assert.Equal(HttpStatusCode.Unauthorized, await YoAsync(legitima.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RenovarAsync(legitima.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Cerrar_sesion_invalida_access_y_refresh_al_instante()
    {
        var s = await api.LoginPacienteAsync("ignacio@demo.cl");
        Assert.Equal(HttpStatusCode.OK, await YoAsync(s.AccessToken));

        var cerrar = await api.ConToken(s.AccessToken).PostAsync("/api/auth/cerrar", null);
        Assert.Equal(HttpStatusCode.NoContent, cerrar.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await YoAsync(s.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RenovarAsync(s.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Desactivar_usuario_corta_sus_sesiones_en_el_acto()
    {
        var (email, clave) = await api.PacienteNuevoAsync("desactivar");
        var s = await api.LoginPacienteAsync(email, clave);
        var admin = await api.AdminAsync();
        var id = s.Usuario.Id;
        await api.ReautenticarAsync("admin@reservamed.cl", admin.AccessToken);

        var resp = await api.ConToken(admin.AccessToken).PostAsync($"/api/usuarios/{id}/desactivar", null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await YoAsync(s.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RenovarAsync(s.RefreshToken)).StatusCode);
        var login = await api.PostAsync("/api/auth/login", new { email, password = clave, cliente = "PORTAL" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Refresh_inventado_no_sirve()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await RenovarAsync("no-es-un-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RenovarAsync(Convert.ToBase64String(new byte[32]))).StatusCode);
    }

    /* ---------- A2: segundo factor del personal ---------- */

    [Fact]
    public async Task Personal_no_entra_solo_con_contrasena()
    {
        var resp = await api.PostAsync("/api/auth/login", new { email = "recepcion.lc@reservamed.cl", password = ClienteAuth.ClaveDemo, cliente = "BACKOFFICE" });
        var r = (await resp.Content.ReadFromJsonAsync<ResultadoLogin>())!;

        Assert.Contains(r.Estado, new[] { "TOTP", "ENROLAR_TOTP" });
        Assert.Null(r.Sesion);
        // El desafío no sirve como access token (otra audiencia).
        Assert.Equal(HttpStatusCode.Unauthorized, await YoAsync(r.Desafio!));
    }

    [Fact]
    public async Task Enrolamiento_y_luego_codigo_en_cada_ingreso()
    {
        var primera = await api.LoginPersonalAsync("dra.soto@reservamed.cl");     // enrola
        Assert.Equal("MEDICO", primera.Usuario.Rol);
        var segunda = await api.LoginPersonalAsync("dra.soto@reservamed.cl");     // pide código
        Assert.Equal(HttpStatusCode.OK, await YoAsync(segunda.AccessToken));
    }

    [Fact]
    public async Task Codigo_totp_incorrecto_o_repetido_se_rechaza_y_el_desafio_es_de_un_solo_uso()
    {
        await api.LoginPersonalAsync("recepcion@reservamed.cl");   // asegura enrolamiento

        async Task<ResultadoLogin> Paso1()
        {
            var resp = await api.PostAsync("/api/auth/login", new { email = "recepcion@reservamed.cl", password = ClienteAuth.ClaveDemo, cliente = "BACKOFFICE" });
            return (await resp.Content.ReadFromJsonAsync<ResultadoLogin>())!;
        }

        var d1 = await Paso1();
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostAsync("/api/auth/totp", new { desafio = d1.Desafio, codigo = "000000" })).StatusCode);

        var codigo = await ClienteAuth.SiguienteCodigoAsync("recepcion@reservamed.cl");
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/auth/totp", new { desafio = d1.Desafio, codigo })).StatusCode);

        // Mismo desafío otra vez: rechazado.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostAsync("/api/auth/totp", new { desafio = d1.Desafio, codigo })).StatusCode);
        // Mismo código con un desafío nuevo: rechazado (anti-repetición).
        var d2 = await Paso1();
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostAsync("/api/auth/totp", new { desafio = d2.Desafio, codigo })).StatusCode);
    }

    [Fact]
    public async Task Portal_y_backoffice_no_se_mezclan()
    {
        var personal = await api.PostAsync("/api/auth/login", new { email = "admin@reservamed.cl", password = ClienteAuth.ClaveDemo, cliente = "PORTAL" });
        Assert.Equal(HttpStatusCode.Forbidden, personal.StatusCode);
        var paciente = await api.PostAsync("/api/auth/login", new { email = "ignacio@demo.cl", password = ClienteAuth.ClaveDemo, cliente = "BACKOFFICE" });
        Assert.Equal(HttpStatusCode.Forbidden, paciente.StatusCode);
    }

    /* ---------- A2 / M1: bloqueo y enumeración ---------- */

    [Fact]
    public async Task Cinco_fallos_bloquean_la_cuenta_aunque_despues_la_clave_sea_correcta()
    {
        var (email, clave) = await api.PacienteNuevoAsync("bloqueo");
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await api.PostAsync("/api/auth/login", new { email, password = "incorrecta-" + i, cliente = "PORTAL" })).StatusCode);

        var bloqueada = await api.PostAsync("/api/auth/login", new { email, password = clave, cliente = "PORTAL" });
        Assert.Equal((HttpStatusCode)429, bloqueada.StatusCode);
    }

    [Fact]
    public async Task Correo_inexistente_y_clave_incorrecta_responden_igual()
    {
        var a = await api.PostAsync("/api/auth/login", new { email = "nadie-existe@prueba.cl", password = "x", cliente = "PORTAL" });
        var b = await api.PostAsync("/api/auth/login", new { email = "ignacio@demo.cl", password = "x-incorrecta", cliente = "PORTAL" });

        Assert.Equal(a.StatusCode, b.StatusCode);
        // Mismo cuerpo salvo el traceId, que identifica cada solicitud.
        Assert.Equal(SinTraza(await a.Content.ReadAsStringAsync()), SinTraza(await b.Content.ReadAsStringAsync()));
    }

    /* ---------- A7 / B4 / M2: registro ---------- */

    [Fact]
    public async Task Registro_exige_verificar_el_correo_antes_de_entrar()
    {
        var numero = Random.Shared.Next(40_000_000, 49_999_999);
        var email = $"verificar-{numero}@prueba.cl";
        const string clave = "Otra-clave-larga-y-unica-2026";
        var resp = await api.PostAsync("/api/auth/registro",
            ClienteAuth.Registro($"{numero}-{ReservaMed.Api.Common.Rut.CalcularDv(numero)}", email, clave));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var antes = await api.PostAsync("/api/auth/login", new { email, password = clave, cliente = "PORTAL" });
        Assert.Equal(HttpStatusCode.Forbidden, antes.StatusCode);
        Assert.Contains("CORREO_NO_VERIFICADO", await antes.Content.ReadAsStringAsync());

        var token = ClienteAuth.TokenVerificacion(api, email);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/auth/verificar-correo", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/auth/verificar-correo", new { token })).StatusCode);   // un solo uso

        await api.LoginPacienteAsync(email, clave);
    }

    [Fact]
    public async Task Registro_con_correo_o_rut_existente_responde_igual_y_avisa_al_titular()
    {
        var (email, clave) = await api.PacienteNuevoAsync("duplicado");
        var nuevo = await api.PostAsync("/api/auth/registro", ClienteAuth.Registro("11223344-K", $"otro-{Guid.NewGuid():N}@prueba.cl", "Clave-larga-distinta-2026"));
        var repetido = await api.PostAsync("/api/auth/registro", ClienteAuth.Registro("11223344-K", email, "Clave-larga-distinta-2026"));

        Assert.Equal(HttpStatusCode.Accepted, nuevo.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, repetido.StatusCode);
        Assert.Contains(api.Buzon.Para(email), c => c.Asunto.Contains("Intento de registro"));
        // La cuenta original sigue funcionando con su clave.
        await api.LoginPacienteAsync(email, clave);
    }

    [Theory]
    [InlineData("corta-1")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("123456789012")]
    public async Task Registro_rechaza_contrasenas_debiles(string clave)
    {
        var numero = Random.Shared.Next(50_000_000, 59_999_999);
        var resp = await api.PostAsync("/api/auth/registro",
            ClienteAuth.Registro($"{numero}-{ReservaMed.Api.Common.Rut.CalcularDv(numero)}", $"debil-{numero}@prueba.cl", clave));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    /* ---------- A8: acaparamiento ---------- */

    [Fact]
    public async Task No_se_pueden_acumular_mas_de_dos_reservas_sin_pagar()
    {
        var (email, clave) = await api.PacienteNuevoAsync("acaparar");
        var s = await api.LoginPacienteAsync(email, clave);
        var c = api.ConToken(s.AccessToken);

        var desde = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd");
        var hasta = DateTime.Today.AddDays(14).ToString("yyyy-MM-dd");
        var cupos = new List<CupoDto>();
        for (var esp = 1; esp <= 5; esp++)
            cupos.AddRange((await api.CreateClient().GetFromJsonAsync<List<CupoDto>>(
                $"/api/agenda/disponibilidad?especialidadId={esp}&desde={desde}&hasta={hasta}"))!);
        // Tres cupos de profesionales distintos (regla: una hora por día con el mismo profesional).
        var elegidos = cupos.GroupBy(x => x.ProfesionalId).Select(g => g.Last()).Take(3).ToList();
        Assert.Equal(3, elegidos.Count);

        var estados = new List<HttpStatusCode>();
        foreach (var cupo in elegidos)
            estados.Add((await c.PostAsJsonAsync("/api/citas", new
            {
                profesionalId = cupo.ProfesionalId, sedeId = cupo.SedeId, inicio = cupo.Inicio, modalidadPago = "ONLINE"
            })).StatusCode);

        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Conflict }, estados);
    }

    private static string SinTraza(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"traceId\":\"[^\"]*\"", "");

    private sealed record CupoDto(int ProfesionalId, int SedeId, DateTime Inicio);
}
