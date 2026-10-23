using System.Text.RegularExpressions;

namespace ReservaMed.Api.Common;

/// <summary>
/// RUT/RUN chileno: validación con módulo 11 y normalización a formato 12345678-9.
/// </summary>
public static partial class Rut
{
    [GeneratedRegex(@"^(\d{7,8})-([\dK])$")]
    private static partial Regex Formato();

    /// <summary>Devuelve el RUT normalizado (sin puntos, con guion, DV en mayúscula) o null si no es válido.</summary>
    public static string? Normalizar(string? entrada)
    {
        if (string.IsNullOrWhiteSpace(entrada)) return null;

        var limpio = entrada.Replace(".", "").Replace(" ", "").Trim().ToUpperInvariant();
        if (!limpio.Contains('-') && limpio.Length >= 2)
            limpio = limpio[..^1] + "-" + limpio[^1];

        var m = Formato().Match(limpio);
        if (!m.Success) return null;

        var numero = int.Parse(m.Groups[1].Value);
        return CalcularDv(numero) == m.Groups[2].Value ? $"{numero}-{m.Groups[2].Value}" : null;
    }

    public static bool EsValido(string? entrada) => Normalizar(entrada) is not null;

    public static string CalcularDv(int numero)
    {
        int suma = 0, multiplicador = 2;
        while (numero > 0)
        {
            suma += numero % 10 * multiplicador;
            numero /= 10;
            multiplicador = multiplicador == 7 ? 2 : multiplicador + 1;
        }
        var resto = 11 - suma % 11;
        return resto switch { 11 => "0", 10 => "K", _ => resto.ToString() };
    }

    /// <summary>Exige un RUT válido o lanza error de negocio.</summary>
    public static string Exigir(string? entrada, string campo = "RUT") =>
        Normalizar(entrada) ?? throw new ReglaNegocioException($"El {campo} ingresado no es válido.", codigo: "RUT_INVALIDO");
}
