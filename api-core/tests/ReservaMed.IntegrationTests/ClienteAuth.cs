using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using ReservaMed.Api.Common;
using ReservaMed.Api.Modules.Auth;

namespace ReservaMed.IntegrationTests;

/// <summary>
/// Inicios de sesión reales contra la API (contraseña, TOTP, enrolamiento y registro verificado)
/// para las pruebas. Calcula los códigos TOTP igual que un autenticador del teléfono.
/// </summary>
public static partial class ClienteAuth
{
    public const string ClaveDemo = "Demo1234!";

    private static readonly Dictionary<string, (byte[] Secreto, long UltimoPaso)> Autenticadores = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static SesionEmitida? _admin;

    public static async Task<HttpResponseMessage> PostAsync(this ApiFactory api, string ruta, object cuerpo, string? token = null)
    {
        var c = api.CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await c.PostAsJsonAsync(ruta, cuerpo);
    }

    public static HttpClient ConToken(this ApiFactory api, string token)
    {
        var c = api.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static async Task<SesionEmitida> LoginPacienteAsync(this ApiFactory api, string email, string clave = ClaveDemo)
    {
        var resp = await api.PostAsync("/api/auth/login", new { email, password = clave, cliente = "PORTAL" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = (await resp.Content.ReadFromJsonAsync<ResultadoLogin>())!;
        Assert.Equal("OK", r.Estado);
        return r.Sesion!;
    }

    /// <summary>Personal: contraseña y luego TOTP. El primer ingreso enrola el autenticador.</summary>
    public static async Task<SesionEmitida> LoginPersonalAsync(this ApiFactory api, string email)
    {
        await Candado.WaitAsync();
        try
        {
            var resp = await api.PostAsync("/api/auth/login", new { email, password = ClaveDemo, cliente = "BACKOFFICE" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var r = (await resp.Content.ReadFromJsonAsync<ResultadoLogin>())!;

            if (r.Estado == "ENROLAR_TOTP")
                Autenticadores[email] = (Base32.Decodificar(r.SecretoTotp!), long.MinValue);
            else
                Assert.Equal("TOTP", r.Estado);

            var codigo = await SiguienteCodigoAsync(email);
            var fin = await api.PostAsync("/api/auth/totp", new { desafio = r.Desafio, codigo });
            Assert.Equal(HttpStatusCode.OK, fin.StatusCode);
            var ok = (await fin.Content.ReadFromJsonAsync<ResultadoLogin>())!;
            Assert.Equal("OK", ok.Estado);
            return ok.Sesion!;
        }
        finally
        {
            Candado.Release();
        }
    }

    /// <summary>Sesión de administrador compartida por las pruebas que solo leen (auditoría, etc.).</summary>
    public static async Task<SesionEmitida> AdminAsync(this ApiFactory api) => _admin ??= await api.LoginPersonalAsync("admin@reservamed.cl");

    /// <summary>
    /// Próximo código válido: la API no acepta dos veces el mismo paso de 30 s (anti-repetición),
    /// así que si ya se usó el actual se usa el siguiente (tolerado por desfase) o se espera.
    /// </summary>
    public static async Task<string> SiguienteCodigoAsync(string email)
    {
        var (secreto, ultimo) = Autenticadores[email];
        while (true)
        {
            var actual = Totp.Paso(DateTimeOffset.UtcNow);
            var paso = Math.Max(actual, ultimo + 1);
            if (paso <= actual + 1)
            {
                Autenticadores[email] = (secreto, paso);
                return Totp.Codigo(secreto, paso);
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    public static string CodigoActual(string email) => Totp.Codigo(Autenticadores[email].Secreto, Totp.Paso(DateTimeOffset.UtcNow));

    /// <summary>Registra un paciente nuevo, abre el enlace del correo y devuelve sus credenciales.</summary>
    public static async Task<(string Email, string Clave)> PacienteNuevoAsync(this ApiFactory api, string prefijo)
    {
        var numero = Random.Shared.Next(30_000_000, 39_999_999);
        var rut = $"{numero}-{Rut.CalcularDv(numero)}";
        var email = $"{prefijo}-{numero}@prueba.cl";
        const string clave = "Una-clave-larga-y-unica-2026";

        var resp = await api.PostAsync("/api/auth/registro", Registro(rut, email, clave));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var token = TokenVerificacion(api, email);
        var ok = await api.PostAsync("/api/auth/verificar-correo", new { token });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        return (email, clave);
    }

    public static object Registro(string rut, string email, string clave) => new
    {
        rut, nombres = "Paciente", apellidoPaterno = "Prueba", apellidoMaterno = "Automatizada",
        fechaNacimiento = "1990-05-10", sexo = "F", email, telefono = (string?)null, comuna = "Providencia",
        password = clave, financiadorId = 1, planSaludId = (int?)null, tramoFonasa = "B",
        esCarga = false, afiliadoRut = (string?)null, afiliadoNombre = (string?)null
    };

    public static string TokenVerificacion(ApiFactory api, string email)
    {
        var correo = api.Buzon.Para(email).LastOrDefault(c => c.Asunto.Contains("Active su cuenta"))
            ?? throw new Xunit.Sdk.XunitException($"No llegó el correo de verificación a {email}");
        return TokenEnlace().Match(correo.Cuerpo).Groups[1].Value;
    }

    [GeneratedRegex(@"verificar-correo\?token=([A-Za-z0-9_\-]+)")]
    private static partial Regex TokenEnlace();
}
