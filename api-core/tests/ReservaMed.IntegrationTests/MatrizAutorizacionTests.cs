using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ReservaMed.IntegrationTests;

/// <summary>
/// Gate de la fase 3: matriz rol × endpoint. Cada endpoint protegido se llama con cada rol
/// (anónimo, dos pacientes, recepción de cada sede, médico y admin) sobre datos de distintas
/// sedes, profesionales y pacientes. Lo esperado sale de la regla, no de lo que hace el código:
///  - ADMIN ve todo; RECEPCION solo su sede (A3); MEDICO solo sus citas y pacientes (C5);
///  - PACIENTE solo lo suyo; anónimo, nada protegido.
/// "Denegado" = 401, 403 o 404 con código SIN_ACCESO. "Permitido" = cualquier otra respuesta
/// (las operaciones se hacen sobre citas anuladas para que, una vez autorizadas, la regla de
/// negocio responda 422 y no se modifique nada).
/// </summary>
[Collection("api")]
public sealed class MatrizAutorizacionTests(ApiFactory api)
{
    public enum Rol { Anonimo, PacienteDueno, OtroPaciente, RecepcionProvidencia, RecepcionLasCondes, Medico, Admin }

    private const int SedeProvidencia = 1, SedeLasCondes = 2;

    /// <summary>Cita de prueba: paciente dueño, sede y profesional.</summary>
    private sealed record CitaPrueba(int Id, int Sede, int Profesional);

    private sealed class Escenario
    {
        public Dictionary<Rol, string?> Tokens { get; } = [];
        public int PacienteDueno, OtroPaciente, ProfesionalMedico;
        public string RutDueno = "";
        public Dictionary<string, CitaPrueba> Citas { get; } = [];
    }

    private static Escenario? _escenario;
    private static readonly SemaphoreSlim Candado = new(1, 1);

    /* ------------------------------------------------------------------ casos */

    private sealed record Caso(string Nombre, string Metodo, Func<Escenario, string> Ruta, Func<Escenario, object?>? Cuerpo,
        Func<Escenario, Rol, bool> Permitido);

    private static readonly Rol[] Staff = [Rol.RecepcionProvidencia, Rol.RecepcionLasCondes, Rol.Admin];
    private static readonly Rol[] StaffYMedico = [.. Staff, Rol.Medico];
    private static readonly Rol[] Pacientes = [Rol.PacienteDueno, Rol.OtroPaciente];

    private static bool VeCita(Escenario e, Rol r, CitaPrueba c) => r switch
    {
        Rol.Admin => true,
        Rol.RecepcionProvidencia => c.Sede == SedeProvidencia,
        Rol.RecepcionLasCondes => c.Sede == SedeLasCondes,
        Rol.Medico => c.Profesional == e.ProfesionalMedico,
        Rol.PacienteDueno => true,   // todas las citas de prueba son del dueño
        _ => false
    };

    /// <summary>Endpoints sobre una cita: el rol debe estar en <paramref name="roles"/> y además ver la cita.</summary>
    private static IEnumerable<Caso> SobreCita(string nombre, string metodo, string plantilla, Rol[] roles, object? cuerpo = null)
    {
        foreach (var clave in new[] { "Providencia", "LasCondes", "DelMedico" })
            yield return new Caso($"{nombre} [{clave}]", metodo,
                e => plantilla.Replace("{id}", e.Citas[clave].Id.ToString()),
                cuerpo is null ? null : _ => cuerpo,
                (e, r) => roles.Contains(r) && VeCita(e, r, e.Citas[clave]));
    }

    private static Caso Solo(string nombre, string metodo, string ruta, Rol[] roles, object? cuerpo = null) =>
        new(nombre, metodo, _ => ruta, cuerpo is null ? null : _ => cuerpo, (_, r) => roles.Contains(r));

    private static readonly Rol[] Autenticados = [.. Pacientes, .. StaffYMedico];
    private static readonly Rol[] Todos = [Rol.Anonimo, .. Autenticados];

    private static readonly List<Caso> Casos =
    [
        // ---------- Citas ----------
        .. SobreCita("GET cita", "GET", "/api/citas/{id}", Autenticados),
        .. SobreCita("Anular cita", "POST", "/api/citas/{id}/cancelar", [.. Pacientes, .. Staff], new { motivo = "matriz" }),
        .. SobreCita("Marcar atendida", "POST", "/api/citas/{id}/atendida", [Rol.Medico, Rol.Admin]),
        .. SobreCita("Marcar no asistió", "POST", "/api/citas/{id}/no-asistio", StaffYMedico),
        .. SobreCita("Riesgo ML", "GET", "/api/ml/citas/{id}/riesgo", StaffYMedico),
        .. SobreCita("FHIR Appointment", "GET", "/fhir/Appointment/{id}", StaffYMedico),
        // ---------- Admisión ----------
        .. SobreCita("Admisión: ver cita", "GET", "/api/admision/citas/{id}", Staff),
        .. SobreCita("Admisión: valorizar", "POST", "/api/admision/citas/{id}/valorizar", Staff, new { }),
        .. SobreCita("Admisión: desafío de huella", "POST", "/api/admision/citas/{id}/huella/desafio", Staff, new { }),
        .. SobreCita("Admisión: emitir bono", "POST", "/api/admision/citas/{id}/bono", Staff, new { comprobanteHuella = (string?)null }),
        .. SobreCita("Admisión: anular bono", "POST", "/api/admision/citas/{id}/bono/anular", Staff, new { }),
        .. SobreCita("Admisión: cobrar", "POST", "/api/admision/citas/{id}/cobrar", Staff, new { medio = "EFECTIVO" }),
        .. SobreCita("Admisión: admitir", "POST", "/api/admision/citas/{id}/admitir", Staff, new { }),
        // ---------- Pago online (solo el paciente dueño; la cita va en el cuerpo) ----------
        .. new[] { "Providencia", "LasCondes", "DelMedico" }.Select(k => new Caso($"Webpay: iniciar [{k}]", "POST",
            _ => "/api/pagos/webpay/iniciar", e => new { citaId = e.Citas[k].Id }, (e, r) => Pacientes.Contains(r) && VeCita(e, r, e.Citas[k]))),

        // ---------- Agenda por sede (A3) ----------
        Solo("Agenda del día (sede propia)", "GET", "/api/agenda/dia?fecha=2030-01-07", StaffYMedico),
        new("Agenda del día de Providencia", "GET", _ => "/api/agenda/dia?fecha=2030-01-07&sedeId=1", null,
            (_, r) => r is Rol.Admin or Rol.Medico or Rol.RecepcionProvidencia),
        new("Agenda del día de Las Condes", "GET", _ => "/api/agenda/dia?fecha=2030-01-07&sedeId=2", null,
            (_, r) => r is Rol.Admin or Rol.Medico or Rol.RecepcionLasCondes),
        new("Reservar en Providencia", "POST", _ => "/api/citas", e => Reserva(e, SedeProvidencia),
            (_, r) => r is Rol.PacienteDueno or Rol.OtroPaciente or Rol.Admin or Rol.RecepcionProvidencia),
        new("Reservar en Las Condes", "POST", _ => "/api/citas", e => Reserva(e, SedeLasCondes),
            (_, r) => r is Rol.PacienteDueno or Rol.OtroPaciente or Rol.Admin or Rol.RecepcionLasCondes),

        // ---------- Pacientes (C5) ----------
        new("FHIR Patient con cita del médico", "GET", e => $"/fhir/Patient/{e.PacienteDueno}", null,
            (_, r) => StaffYMedico.Contains(r)),
        new("FHIR Patient sin cita del médico", "GET", e => $"/fhir/Patient/{e.OtroPaciente}", null,
            (_, r) => Staff.Contains(r)),
        new("FHIR Practitioner", "GET", e => $"/fhir/Practitioner/{e.ProfesionalMedico}", null, (_, r) => StaffYMedico.Contains(r)),
        new("Admisión: buscar paciente por RUT", "POST", _ => "/api/admision/pacientes/buscar", e => new { rut = e.RutDueno }, (_, r) => Staff.Contains(r)),
        new("Admisión: actualizar previsión", "PUT", e => $"/api/admision/pacientes/{e.PacienteDueno}/prevision",
            _ => new { financiadorId = 0 }, (_, r) => Staff.Contains(r)),
        Solo("Admisión: crear paciente", "POST", "/api/admision/pacientes", Staff, new { }),

        // ---------- Propios del paciente ----------
        Solo("Mis citas", "GET", "/api/citas/mias", Pacientes),
        Solo("Webpay: confirmar", "POST", "/api/pagos/webpay/confirmar", Pacientes, new { token = "inexistente" }),

        // ---------- Administración ----------
        Solo("Auditoría", "GET", "/api/auditoria?top=1", [Rol.Admin]),
        Solo("Integridad de la auditoría", "GET", "/api/auditoria/integridad", [Rol.Admin]),
        Solo("Usuarios", "GET", "/api/usuarios", [Rol.Admin]),
        Solo("Modelo ML", "GET", "/api/ml/modelo", [Rol.Admin]),
        Solo("Demanda ML", "GET", "/api/ml/demanda", [Rol.Admin]),
        Solo("Historial de modelos ML", "GET", "/api/ml/modelos", [Rol.Admin]),
        Solo("Entrenar modelo ML", "POST", "/api/ml/modelo/entrenar", [Rol.Admin], new { }),
        Solo("Desactivar usuario", "POST", "/api/usuarios/999999/desactivar", [Rol.Admin], new { }),
        Solo("Activar usuario", "POST", "/api/usuarios/999999/activar", [Rol.Admin], new { }),
        Solo("Restablecer TOTP", "POST", "/api/usuarios/999999/restablecer-totp", [Rol.Admin], new { }),
        Solo("Bloqueos de agenda", "POST", "/api/agenda/bloqueos", [Rol.Admin], new { }),

        // ---------- Públicos ----------
        Solo("Disponibilidad", "GET", "/api/agenda/disponibilidad?especialidadId=1&desde=2030-01-07&hasta=2030-01-08", Todos),
        Solo("Catálogo: sedes", "GET", "/api/catalogo/sedes", Todos),
        Solo("Catálogo: especialidades", "GET", "/api/catalogo/especialidades", Todos),
        Solo("Catálogo: profesionales", "GET", "/api/catalogo/profesionales", Todos),
        Solo("Catálogo: financiadores", "GET", "/api/catalogo/financiadores", Todos),
    ];

    /// <summary>Reserva en una hora pasada: si el rol está autorizado, la agenda la rechaza (422) y no crea nada.</summary>
    private static object Reserva(Escenario e, int sede) => new
    {
        profesionalId = e.Citas["Providencia"].Profesional, sedeId = sede, inicio = "2001-01-01T10:00:00",
        modalidadPago = "EN_CLINICA", pacienteId = e.PacienteDueno
    };

    public static IEnumerable<object[]> Matriz() =>
        from c in Casos from r in Enum.GetValues<Rol>() select new object[] { c.Nombre, r };

    [Theory]
    [MemberData(nameof(Matriz))]
    public async Task Rol_por_endpoint(string caso, Rol rol)
    {
        var e = await EscenarioAsync();
        var c = Casos.Single(x => x.Nombre == caso);

        var req = new HttpRequestMessage(new HttpMethod(c.Metodo), c.Ruta(e));
        if (e.Tokens[rol] is { } token) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (c.Cuerpo is not null) req.Content = JsonContent.Create(c.Cuerpo(e));
        var resp = await api.CreateClient().SendAsync(req);

        var cuerpo = await resp.Content.ReadAsStringAsync();
        var denegado = resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                       || (resp.StatusCode == HttpStatusCode.NotFound && cuerpo.Contains("\"SIN_ACCESO\""));
        var esperado = c.Permitido(e, rol);

        Assert.True(esperado != denegado,
            $"{caso} con {rol}: se esperaba {(esperado ? "PERMITIDO" : "DENEGADO")} y respondió {(int)resp.StatusCode} {Recortar(cuerpo)}");
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    private static string Recortar(string s) => s.Length > 300 ? s[..300] : s;

    /* ------------------------------------------------------------------ datos */

    private sealed record CupoDto(int ProfesionalId, int SedeId, DateTime Inicio);

    private async Task<Escenario> EscenarioAsync()
    {
        if (_escenario is not null) return _escenario;
        await Candado.WaitAsync();
        try
        {
            return _escenario ??= await CrearEscenarioAsync();
        }
        finally
        {
            Candado.Release();
        }
    }

    private async Task<Escenario> CrearEscenarioAsync()
    {
        var e = new Escenario();
        var admin = await api.AdminAsync();
        var medico = await api.LoginPersonalAsync("dra.soto@reservamed.cl");
        e.ProfesionalMedico = medico.Usuario.ProfesionalId!.Value;

        var (emailA, claveA) = await api.PacienteNuevoAsync("matriz-a");
        var (emailB, claveB) = await api.PacienteNuevoAsync("matriz-b");
        var a = await api.LoginPacienteAsync(emailA, claveA);
        var b = await api.LoginPacienteAsync(emailB, claveB);
        e.PacienteDueno = a.Usuario.PacienteId!.Value;
        e.OtroPaciente = b.Usuario.PacienteId!.Value;

        var adminHttp = api.ConToken(admin.AccessToken);
        e.RutDueno = await RutDeAsync(e.PacienteDueno);

        e.Tokens[Rol.Anonimo] = null;
        e.Tokens[Rol.PacienteDueno] = a.AccessToken;
        e.Tokens[Rol.OtroPaciente] = b.AccessToken;
        e.Tokens[Rol.RecepcionProvidencia] = (await api.LoginPersonalAsync("recepcion@reservamed.cl")).AccessToken;
        e.Tokens[Rol.RecepcionLasCondes] = (await api.LoginPersonalAsync("recepcion.lc@reservamed.cl")).AccessToken;
        e.Tokens[Rol.Medico] = medico.AccessToken;
        e.Tokens[Rol.Admin] = admin.AccessToken;

        // Cupos futuros: uno en cada sede de otro profesional y uno del médico.
        var desde = DateTime.Today.AddDays(3).ToString("yyyy-MM-dd");
        var hasta = DateTime.Today.AddDays(17).ToString("yyyy-MM-dd");
        var cupos = new List<CupoDto>();
        for (var esp = 1; esp <= 5; esp++)
            cupos.AddRange((await api.CreateClient().GetFromJsonAsync<List<CupoDto>>(
                $"/api/agenda/disponibilidad?especialidadId={esp}&desde={desde}&hasta={hasta}"))!);
        cupos.AddRange((await api.CreateClient().GetFromJsonAsync<List<CupoDto>>(
            $"/api/agenda/disponibilidad?profesionalId={e.ProfesionalMedico}&desde={desde}&hasta={hasta}"))!);

        var elegidos = new Dictionary<string, CupoDto>
        {
            ["Providencia"] = cupos.Last(c => c.SedeId == SedeProvidencia && c.ProfesionalId != e.ProfesionalMedico),
            ["LasCondes"] = cupos.Last(c => c.SedeId == SedeLasCondes && c.ProfesionalId != e.ProfesionalMedico),
            ["DelMedico"] = cupos.Last(c => c.ProfesionalId == e.ProfesionalMedico),
        };

        // El admin reserva para el dueño y luego anula: citas reales, en estado final, que nadie puede alterar.
        foreach (var (clave, cupo) in elegidos)
        {
            var r = await adminHttp.PostAsJsonAsync("/api/citas", new
            {
                profesionalId = cupo.ProfesionalId, sedeId = cupo.SedeId, inicio = cupo.Inicio,
                modalidadPago = "EN_CLINICA", pacienteId = e.PacienteDueno, canal = "TELEFONO"
            });
            Assert.True(r.IsSuccessStatusCode, $"No se pudo crear la cita {clave}: {await r.Content.ReadAsStringAsync()}");
            var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("citaId").GetInt32();
            var anular = await adminHttp.PostAsJsonAsync($"/api/citas/{id}/cancelar", new { motivo = "Escenario de la matriz" });
            Assert.Equal(HttpStatusCode.NoContent, anular.StatusCode);
            e.Citas[clave] = new CitaPrueba(id, cupo.SedeId, cupo.ProfesionalId);
        }
        return e;
    }

    private async Task<string> RutDeAsync(int pacienteId)
    {
        await using var cn = new Microsoft.Data.SqlClient.SqlConnection(api.CadenaApi);
        await cn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT Rut FROM dbo.Pacientes WHERE Id = @Id", cn);
        cmd.Parameters.AddWithValue("@Id", pacienteId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>La matriz cubre todos los endpoints de la API (salvo autenticación y el Webpay simulado).</summary>
    [Fact]
    public void La_matriz_cubre_todos_los_endpoints_protegidos()
    {
        var rutasMatriz = Casos.Select(c => Normalizar(c.Metodo, c.Ruta(Ficticio()))).ToHashSet();
        var faltantes = EndpointsDeLaApi()
            .Where(x => !x.Ruta.StartsWith("/api/auth") && !x.Ruta.StartsWith("/webpay-mock"))
            .Where(x => !rutasMatriz.Contains(x.Metodo + " " + x.Ruta))
            .Select(x => x.Metodo + " " + x.Ruta)
            .ToList();
        Assert.True(faltantes.Count == 0, "Endpoints sin caso en la matriz: " + string.Join(", ", faltantes));
    }

    private static Escenario Ficticio()
    {
        var e = new Escenario { PacienteDueno = 1, OtroPaciente = 2, ProfesionalMedico = 3, RutDueno = "1-9" };
        foreach (var k in new[] { "Providencia", "LasCondes", "DelMedico" }) e.Citas[k] = new CitaPrueba(1, 1, 1);
        return e;
    }

    /// <summary>"GET /api/citas/12?x=1" → "GET /api/citas/{n}".</summary>
    private static string Normalizar(string metodo, string ruta)
    {
        var sinConsulta = ruta.Split('?')[0];
        var partes = sinConsulta.Split('/').Select(p => int.TryParse(p, out _) || p == "1-9" ? "{n}" : p);
        return $"{metodo} {string.Join('/', partes)}";
    }

    private IEnumerable<(string Metodo, string Ruta)> EndpointsDeLaApi()
    {
        var fuentes = api.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        foreach (var ep in fuentes.Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>())
        {
            var metodos = ep.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [];
            var ruta = "/" + string.Join('/', ep.RoutePattern.PathSegments.Select(s =>
                s.IsSimple && s.Parts[0] is Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart l ? l.Content : "{n}"));
            if (ruta is "/health" or "/openapi/{n}") continue;
            foreach (var m in metodos) yield return (m, ruta);
        }
    }
}
