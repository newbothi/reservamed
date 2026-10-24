using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using ReservaMed.Api.Integraciones.Pagos;
using ReservaMed.Api.Modules.Admision;

namespace ReservaMed.IntegrationTests;

/// <summary>
/// Fase 3, reglas de dominio: huella firmada por el lector (C3), confirmación de Webpay validada
/// e idempotente (A6), validación de entrada sin errores 500 (M3) y regla atómica de reserva (B3).
/// </summary>
[Collection("api")]
public sealed class DominioTests(ApiFactory api)
{
    private static readonly TimeZoneInfo Chile = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
    private static DateTime HoyChile => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Chile).Date;

    /* =====================================================================
       C3: la huella la afirma el lector, no el navegador
       ===================================================================== */

    private sealed record CitaHoy(int Id, string Rut, int Sede);

    /// <summary>Cita de HOY en Providencia para un paciente Fonasa nuevo (el bono solo se emite el día de la atención).</summary>
    private async Task<CitaHoy> CitaDeHoyAsync()
    {
        var (email, clave) = await api.PacienteNuevoAsync("huella");
        var paciente = (await api.LoginPacienteAsync(email, clave)).Usuario.PacienteId!.Value;

        await using var cn = new SqlConnection(api.CadenaApi);
        await cn.OpenAsync();
        // 23:xx:ss con segundos: nunca coincide con un cupo real (los cupos empiezan en el segundo 0).
        var inicio = HoyChile.AddHours(23).AddSeconds(Random.Shared.Next(1, 3000));
        await using var cmd = new SqlCommand("""
            DECLARE @Prof INT, @Prest INT;
            SELECT TOP 1 @Prof = ProfesionalId, @Prest = PrestacionId FROM dbo.HorariosProfesional WHERE SedeId = 1 ORDER BY Id;
            INSERT INTO dbo.Citas (PacienteId, ProfesionalId, SedeId, PrestacionId, FechaHoraInicio, FechaHoraFin, Estado, Canal, OcupaCupo, CreadoEn)
            OUTPUT INSERTED.Id
            VALUES (@Pac, @Prof, 1, @Prest, @Inicio, DATEADD(MINUTE, 20, @Inicio), 'RESERVADA', 'PRESENCIAL', 1, SYSDATETIME());
            """, cn);
        cmd.Parameters.AddWithValue("@Pac", paciente);
        cmd.Parameters.AddWithValue("@Inicio", inicio);
        var id = (int)(await cmd.ExecuteScalarAsync())!;

        await using var rut = new SqlCommand("SELECT Rut FROM dbo.Pacientes WHERE Id = @Id", cn);
        rut.Parameters.AddWithValue("@Id", paciente);
        return new CitaHoy(id, (string)(await rut.ExecuteScalarAsync())!, 1);
    }

    private async Task<HttpClient> RecepcionProvidenciaAsync() =>
        api.ConToken((await api.LoginPersonalAsync("recepcion@reservamed.cl")).AccessToken);

    private static async Task<DesafioHuellaDto> DesafioAsync(HttpClient c, int citaId)
    {
        var r = await c.PostAsJsonAsync($"/api/admision/citas/{citaId}/huella/desafio", new { });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<DesafioHuellaDto>())!;
    }

    private static string Lectura(DesafioHuellaDto d, string lector = "LECTOR-PROV-01", string resultado = VerificadorHuella.Coincide,
        int? citaId = null, string? rut = null, long? emitido = null, string clave = ApiFactory.ClaveHuellaPrueba) =>
        VerificadorHuella.Firmar(clave, new LecturaHuella(lector, d.SedeId, citaId ?? d.CitaId, rut ?? d.Rut, d.Nonce,
            emitido ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), resultado));

    private static async Task<(HttpStatusCode Status, string? Codigo)> EmitirAsync(HttpClient c, int citaId, object cuerpo)
    {
        var r = await c.PostAsJsonAsync($"/api/admision/citas/{citaId}/bono", cuerpo);
        var texto = await r.Content.ReadAsStringAsync();
        string? codigo = null;
        try { codigo = JsonDocument.Parse(texto).RootElement.TryGetProperty("codigo", out var p) ? p.GetString() : null; }
        catch (JsonException) { }
        return (r.StatusCode, codigo);
    }

    [Fact]
    public async Task Bono_sin_lectura_del_lector_se_rechaza_aunque_el_navegador_diga_huellaVerificada()
    {
        var cita = await CitaDeHoyAsync();
        var c = await RecepcionProvidenciaAsync();

        // El cuerpo antiguo (v1) ya no sirve: la API ignora "huellaVerificada".
        var r = await EmitirAsync(c, cita.Id, new { huellaVerificada = true });
        Assert.Equal(((HttpStatusCode)422, "HUELLA_REQUERIDA"), r);
    }

    [Fact]
    public async Task Bono_con_lectura_firmada_se_emite_y_el_comprobante_no_se_puede_reusar()
    {
        var cita = await CitaDeHoyAsync();
        var c = await RecepcionProvidenciaAsync();
        var comprobante = Lectura(await DesafioAsync(c, cita.Id));

        var ok = await EmitirAsync(c, cita.Id, new { comprobanteHuella = comprobante });
        Assert.Equal(HttpStatusCode.OK, ok.Status);

        // Se anula el bono y se intenta emitir otro con el mismo comprobante: ya se consumió.
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api/admision/citas/{cita.Id}/bono/anular", new { })).StatusCode);
        Assert.Equal(((HttpStatusCode)422, "HUELLA_INVALIDA"), await EmitirAsync(c, cita.Id, new { comprobanteHuella = comprobante }));
    }

    public static TheoryData<string> LecturasFalsas => new()
    {
        "firma de otra clave", "lector de otra sede", "otra cita", "otro RUT", "lectura vieja", "lector inexistente", "basura"
    };

    [Theory]
    [MemberData(nameof(LecturasFalsas))]
    public async Task Lectura_falsificada_o_ajena_se_rechaza(string caso)
    {
        var cita = await CitaDeHoyAsync();
        var otra = await CitaDeHoyAsync();
        var c = await RecepcionProvidenciaAsync();
        var d = await DesafioAsync(c, cita.Id);

        var comprobante = caso switch
        {
            "firma de otra clave" => Lectura(d, clave: "una-clave-que-no-es-la-del-lector-1234567890"),
            "lector de otra sede" => Lectura(d, lector: "LECTOR-LC-01"),
            "otra cita" => Lectura(d, citaId: otra.Id),
            "otro RUT" => Lectura(d, rut: otra.Rut),
            "lectura vieja" => Lectura(d, emitido: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()),
            "lector inexistente" => Lectura(d, lector: "LECTOR-PIRATA"),
            _ => "v1.eyJ4IjoxfQ.AAAA"
        };

        Assert.Equal(((HttpStatusCode)422, "HUELLA_INVALIDA"), await EmitirAsync(c, cita.Id, new { comprobanteHuella = comprobante }));
    }

    [Fact]
    public async Task Huella_que_no_coincide_no_emite_bono()
    {
        var cita = await CitaDeHoyAsync();
        var c = await RecepcionProvidenciaAsync();
        var d = await DesafioAsync(c, cita.Id);

        Assert.Equal(((HttpStatusCode)422, "HUELLA_NO_COINCIDE"),
            await EmitirAsync(c, cita.Id, new { comprobanteHuella = Lectura(d, resultado: VerificadorHuella.NoCoincide) }));
    }

    /* =====================================================================
       A6: Webpay. Lo que se confirma debe coincidir con lo registrado; una sola vez.
       ===================================================================== */

    /// <summary>Pasarela de pruebas: responde lo que la prueba indique y cuenta las llamadas.</summary>
    private sealed class PasarelaFalsa : IPasarelaWebpay
    {
        public readonly ConcurrentDictionary<string, (string BuyOrder, int Monto)> Creadas = new();
        public Func<string, int, ResultadoWebpay> Respuesta { get; set; } =
            (bo, monto) => new ResultadoWebpay(true, "AUTHORIZED", bo, monto, "1213", "6623", "VD", 0, DateTime.UtcNow);
        public int Confirmaciones;
        public readonly ConcurrentBag<string> Reversos = [];

        public Task<TransaccionCreada> CrearAsync(string buyOrder, string sessionId, int monto, string returnUrl, CancellationToken ct = default)
        {
            var token = "tok" + Guid.NewGuid().ToString("N");
            Creadas[token] = (buyOrder, monto);
            return Task.FromResult(new TransaccionCreada(token, "https://webpay.prueba/pagar"));
        }

        public async Task<ResultadoWebpay> ConfirmarAsync(string token, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Confirmaciones);
            await Task.Delay(300, ct);   // Transbank tarda: deja tiempo a una confirmación simultánea
            var (bo, monto) = Creadas[token];
            return Respuesta(bo, monto);
        }

        public Task ReversarAsync(string token, int monto, CancellationToken ct = default)
        {
            Reversos.Add(token);
            return Task.CompletedTask;
        }
    }

    private async Task<(HttpClient Paciente, int CitaId, string Token, PasarelaFalsa Pasarela)> PagoIniciadoAsync()
    {
        var pasarela = new PasarelaFalsa();
        var conPasarela = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IPasarelaWebpay>(pasarela)));

        // Paciente particular: sin bono, el monto es el valor particular.
        var particular = (await api.CreateClient().GetFromJsonAsync<List<JsonElement>>("/api/catalogo/financiadores"))!
            .First(f => f.GetProperty("tipo").GetString() == "PARTICULAR").GetProperty("id").GetInt32();
        var (email, clave) = await api.PacienteNuevoAsync("webpay", particular, null);
        var token = (await api.LoginPacienteAsync(email, clave)).AccessToken;
        var http = conPasarela.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var cupo = (await CuposAsync()).First();
        var r = await http.PostAsJsonAsync("/api/citas", new
        {
            profesionalId = cupo.ProfesionalId, sedeId = cupo.SedeId, inicio = cupo.Inicio, modalidadPago = "ONLINE"
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var citaId = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("citaId").GetInt32();

        var ini = await http.PostAsJsonAsync("/api/pagos/webpay/iniciar", new { citaId });
        Assert.True(ini.IsSuccessStatusCode, await ini.Content.ReadAsStringAsync());
        var tokenWs = (await ini.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        return (http, citaId, tokenWs, pasarela);
    }

    private async Task<string> EstadoAsync(string tabla, string columna, object valor)
    {
        await using var cn = new SqlConnection(api.CadenaApi);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT Estado FROM dbo.{tabla} WHERE {columna} = @V", cn);
        cmd.Parameters.AddWithValue("@V", valor);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData("monto distinto")]
    [InlineData("orden de compra distinta")]
    [InlineData("estado no autorizado")]
    public async Task Confirmacion_inconsistente_no_confirma_y_reversa(string caso)
    {
        var (http, citaId, token, pasarela) = await PagoIniciadoAsync();
        pasarela.Respuesta = caso switch
        {
            "monto distinto" => (bo, m) => new ResultadoWebpay(true, "AUTHORIZED", bo, m - 1, "1213", "6623", "VD", 0, DateTime.UtcNow),
            "orden de compra distinta" => (_, m) => new ResultadoWebpay(true, "AUTHORIZED", "RM-OTRA", m, "1213", "6623", "VD", 0, DateTime.UtcNow),
            _ => (bo, m) => new ResultadoWebpay(true, "REVERSED", bo, m, "1213", "6623", "VD", 0, DateTime.UtcNow)
        };

        var r = await (await http.PostAsJsonAsync("/api/pagos/webpay/confirmar", new { token })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(r.GetProperty("aprobado").GetBoolean());
        Assert.Contains(token, pasarela.Reversos);
        Assert.Equal("ANULADO", await EstadoAsync("Pagos", "TokenWebpay", token));
        Assert.Equal("PENDIENTE_PAGO", await EstadoAsync("Citas", "Id", citaId));
    }

    [Fact]
    public async Task Confirmar_dos_veces_a_la_vez_cobra_y_confirma_una_sola_vez()
    {
        var (http, citaId, token, pasarela) = await PagoIniciadoAsync();

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            http.PostAsJsonAsync("/api/pagos/webpay/confirmar", new { token })));

        Assert.All(respuestas, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        Assert.Equal(1, pasarela.Confirmaciones);
        Assert.Empty(pasarela.Reversos);
        Assert.Equal("CONFIRMADA", await EstadoAsync("Citas", "Id", citaId));
        Assert.Equal("AUTORIZADO", await EstadoAsync("Pagos", "TokenWebpay", token));
    }

    /* =====================================================================
       B3: una hora por día con el mismo profesional, también con reservas simultáneas
       ===================================================================== */

    private sealed record CupoDto(int ProfesionalId, int SedeId, DateTime Inicio);

    private async Task<List<CupoDto>> CuposAsync()
    {
        var desde = DateTime.Today.AddDays(3).ToString("yyyy-MM-dd");
        var hasta = DateTime.Today.AddDays(17).ToString("yyyy-MM-dd");
        var cupos = new List<CupoDto>();
        for (var esp = 1; esp <= 5; esp++)
            cupos.AddRange((await api.CreateClient().GetFromJsonAsync<List<CupoDto>>(
                $"/api/agenda/disponibilidad?especialidadId={esp}&desde={desde}&hasta={hasta}"))!);
        // Orden aleatorio: pruebas distintas no compiten por el mismo cupo.
        return cupos.OrderBy(_ => Random.Shared.Next()).ToList();
    }

    [Fact]
    public async Task Reservas_simultaneas_del_mismo_paciente_con_el_mismo_profesional_el_mismo_dia_solo_una_pasa()
    {
        var (email, clave) = await api.PacienteNuevoAsync("simultaneo");
        var c = api.ConToken((await api.LoginPacienteAsync(email, clave)).AccessToken);

        // Cuatro horas distintas del mismo profesional, mismo día y misma sede.
        var grupo = (await CuposAsync())
            .GroupBy(x => (x.ProfesionalId, x.SedeId, x.Inicio.Date))
            .First(g => g.Count() >= 4)
            .Take(4)
            .ToList();

        var respuestas = await Task.WhenAll(grupo.Select(cupo => c.PostAsJsonAsync("/api/citas", new
        {
            profesionalId = cupo.ProfesionalId, sedeId = cupo.SedeId, inicio = cupo.Inicio, modalidadPago = "EN_CLINICA"
        })));

        Assert.Equal(1, respuestas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, respuestas.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    /* =====================================================================
       M3: entradas inválidas responden 400 uniforme, nunca 500
       ===================================================================== */

    [Fact]
    public async Task Textos_demasiado_largos_responden_400_con_el_campo()
    {
        var admin = api.ConToken((await api.AdminAsync()).AccessToken);
        var r = await admin.PostAsJsonAsync("/api/admision/pacientes", new
        {
            rut = "11111111-1", nombres = new string('a', 500), apellidoPaterno = "Prueba", apellidoMaterno = (string?)null,
            fechaNacimiento = "1990-01-01", sexo = "F", email = "largo@prueba.cl", telefono = (string?)null, comuna = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var cuerpo = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDACION", cuerpo.GetProperty("codigo").GetString());
        Assert.Contains(cuerpo.GetProperty("errores").EnumerateArray(), e => e.GetProperty("campo").GetString() == "nombres");
    }

    public static TheoryData<string, string, string> EntradasInvalidas => new()
    {
        { "POST", "/api/auth/login", "{\"email\":null,\"password\":null}" },
        { "POST", "/api/auth/login", "{\"email\":\"" + new string('x', 5000) + "@a.cl\",\"password\":\"x\"}" },
        { "POST", "/api/auth/login", "{ esto no es json" },
        { "POST", "/api/auth/totp", "{\"desafio\":\"x\",\"codigo\":\"12ab\"}" },
        { "POST", "/api/auth/registro", "{\"rut\":\"1-9\",\"nombres\":\"" + new string('n', 1000) + "\"}" },
        { "POST", "/api/auth/registro", "{\"fechaNacimiento\":\"3000-01-01\"}" },
        { "POST", "/api/auth/verificar-correo", "{\"token\":null}" },
        { "POST", "/api/auth/renovar", "{\"refreshToken\":\"" + new string('r', 5000) + "\"}" },
        { "POST", "/api/citas", "{\"profesionalId\":-1,\"sedeId\":0,\"inicio\":\"no-es-fecha\",\"modalidadPago\":\"GRATIS\"}" },
    };

    [Theory]
    [MemberData(nameof(EntradasInvalidas))]
    public async Task Entrada_invalida_responde_400_uniforme(string metodo, string ruta, string json)
    {
        var c = ruta == "/api/citas" ? api.ConToken((await api.AdminAsync()).AccessToken) : api.CreateClient();
        var r = await c.SendAsync(new HttpRequestMessage(new HttpMethod(metodo), ruta)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var cuerpo = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDACION", cuerpo.GetProperty("codigo").GetString());
        Assert.True(cuerpo.GetProperty("errores").GetArrayLength() > 0);
    }
}
