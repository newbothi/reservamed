using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ReservaMed.Api.Modules.Auth;

/// <summary>¿Cuántas veces apareció esta contraseña en filtraciones conocidas?</summary>
public interface IVerificadorFiltraciones
{
    /// <returns>Número de apariciones, o null si el servicio no respondió (no bloquea el registro).</returns>
    Task<int?> AparicionesAsync(string contrasena, CancellationToken ct = default);
}

/// <summary>
/// Have I Been Pwned con k-anonimato (hallazgo M2): solo se envían los 5 primeros caracteres del
/// SHA-1; la respuesta trae todos los sufijos con ese prefijo y la comparación se hace aquí.
/// La cabecera Add-Padding hace que todas las respuestas tengan tamaño similar.
/// </summary>
public sealed class VerificadorHibp(HttpClient http, ILogger<VerificadorHibp> log) : IVerificadorFiltraciones
{
    public async Task<int?> AparicionesAsync(string contrasena, CancellationToken ct = default)
    {
        var sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(contrasena)));
        var (prefijo, sufijo) = (sha1[..5], sha1[5..]);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"range/{prefijo}");
            req.Headers.Add("Add-Padding", "true");
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            foreach (var linea in (await resp.Content.ReadAsStringAsync(ct)).Split('\n'))
            {
                var partes = linea.Trim().Split(':');
                if (partes.Length == 2 && partes[0].Equals(sufijo, StringComparison.OrdinalIgnoreCase))
                    return int.TryParse(partes[1], out var n) ? n : 1;
            }
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("No se pudo consultar contraseñas filtradas ({Error}); se aplica solo la política local.", ex.Message);
            return null;
        }
    }
}

public sealed class SinVerificacionFiltraciones : IVerificadorFiltraciones
{
    public Task<int?> AparicionesAsync(string contrasena, CancellationToken ct = default) => Task.FromResult<int?>(null);
}

/// <summary>Reglas de contraseña para cuentas nuevas (M2). NIST 800-63B: largo antes que complejidad.</summary>
public sealed class PoliticaContrasenas(IOptions<OpcionesSeguridad> opciones, IVerificadorFiltraciones filtraciones)
{
    private static readonly HashSet<string> Comunes = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456789012", "contraseña123", "contrasena123", "password1234", "qwertyuiop12", "reservamed123",
        "chile1234567", "santiago1234", "123456789abc", "abcdefghijkl", "aaaaaaaaaaaa", "000000000000"
    };

    public async Task<IReadOnlyList<string>> RevisarAsync(string? contrasena, string email, string nombres, CancellationToken ct = default)
    {
        var o = opciones.Value;
        var p = contrasena ?? "";
        var errores = new List<string>();

        if (p.Length < o.LargoMinimoContrasena) errores.Add($"La contraseña debe tener al menos {o.LargoMinimoContrasena} caracteres.");
        if (p.Length > 128) errores.Add("La contraseña no puede superar 128 caracteres.");
        if (p.Distinct().Count() < 5) errores.Add("La contraseña repite demasiado los mismos caracteres.");
        if (Comunes.Contains(p)) errores.Add("Esa contraseña es demasiado común.");

        var usuario = email.Split('@')[0];
        if (usuario.Length >= 4 && p.Contains(usuario, StringComparison.OrdinalIgnoreCase))
            errores.Add("La contraseña no debe contener su correo.");
        foreach (var n in nombres.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(n => n.Length >= 4))
            if (p.Contains(n, StringComparison.OrdinalIgnoreCase)) { errores.Add("La contraseña no debe contener su nombre."); break; }

        if (errores.Count == 0 && o.VerificarFiltraciones && await filtraciones.AparicionesAsync(p, ct) is > 0)
            errores.Add("Esa contraseña aparece en filtraciones de datos conocidas. Elija otra.");

        return errores;
    }
}
