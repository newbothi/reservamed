namespace ReservaMed.Contracts;

/// <summary>
/// Validación de secretos al arrancar (hallazgo C1).
/// La aplicación se niega a iniciar si falta un secreto, si es demasiado corto
/// o si es uno de los valores por defecto que alguna vez estuvieron en el repositorio.
/// </summary>
public static class ValidacionSecretos
{
    /// <summary>Valores que estuvieron publicados en el código y por lo tanto se consideran conocidos.</summary>
    public static readonly IReadOnlySet<string> ValoresComprometidos = new HashSet<string>(StringComparer.Ordinal)
    {
        "CAMBIAR-esta-clave-de-desarrollo-ReservaMed-2026-minimo-32-bytes",
        "dev-ml-key-cambiar",
        "ReservaMed_2026!",
        "guest"
    };

    public const int LargoMinimoClaveJwt = 32;
    public const int LargoMinimoApiKey = 24;
    public const int LargoMinimoClave = 16;

    /// <param name="Nombre">Nombre de la clave de configuración (ej. Jwt:Key).</param>
    /// <param name="Valor">Valor leído.</param>
    /// <param name="LargoMinimo">Largo mínimo exigido.</param>
    /// <param name="ToleradoEnDesarrollo">
    /// true para credenciales que en un PC de desarrollo pueden ser las de fábrica
    /// (ej. guest/guest de un RabbitMQ local, que solo acepta conexiones desde localhost).
    /// </param>
    public sealed record Requisito(string Nombre, string? Valor, int LargoMinimo, bool ToleradoEnDesarrollo = false);

    /// <summary>Devuelve los problemas encontrados (lista vacía si todo está bien).</summary>
    public static IReadOnlyList<string> Revisar(IEnumerable<Requisito> requisitos, bool esDesarrollo)
    {
        var problemas = new List<string>();
        foreach (var r in requisitos)
        {
            if (esDesarrollo && r.ToleradoEnDesarrollo) continue;

            if (string.IsNullOrWhiteSpace(r.Valor))
                problemas.Add($"Falta el secreto '{r.Nombre}'.");
            else if (ValoresComprometidos.Contains(r.Valor) || r.Valor.StartsWith("CAMBIAR", StringComparison.OrdinalIgnoreCase))
                problemas.Add($"El secreto '{r.Nombre}' usa un valor por defecto conocido; genere uno nuevo.");
            else if (r.Valor.Length < r.LargoMinimo)
                problemas.Add($"El secreto '{r.Nombre}' debe tener al menos {r.LargoMinimo} caracteres.");
        }
        return problemas;
    }

    /// <summary>Lanza una excepción con instrucciones si hay problemas. No muestra los valores.</summary>
    public static void Exigir(IEnumerable<Requisito> requisitos, bool esDesarrollo)
    {
        var problemas = Revisar(requisitos, esDesarrollo);
        if (problemas.Count == 0) return;

        throw new InvalidOperationException(
            "Configuración de secretos inválida:" + Environment.NewLine +
            string.Join(Environment.NewLine, problemas.Select(p => " - " + p)) + Environment.NewLine +
            "En desarrollo ejecute scripts/configurar-secretos.ps1; en Docker defina los valores en el archivo .env.");
    }
}
