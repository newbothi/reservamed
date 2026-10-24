using System.Security.Cryptography;
using System.Text;

namespace ReservaMed.Api.Modules.Auth;

/// <summary>
/// TOTP (RFC 6238) con HMAC-SHA1, 6 dígitos y pasos de 30 s: lo que usan Google Authenticator,
/// Microsoft Authenticator y similares. Implementado aquí (pocas líneas) y probado con los
/// vectores del RFC, para no depender de un paquete externo en algo tan sensible.
/// </summary>
public static class Totp
{
    public const int Digitos = 6;
    public const int PasoSegundos = 30;

    public static byte[] NuevoSecreto() => RandomNumberGenerator.GetBytes(20);   // 160 bits, recomendado por el RFC

    public static long Paso(DateTimeOffset instante) => instante.ToUnixTimeSeconds() / PasoSegundos;

    public static string Codigo(byte[] secreto, long paso, int digitos = Digitos)
    {
        Span<byte> contador = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { contador[i] = (byte)(paso & 0xFF); paso >>= 8; }
        var hmac = HMACSHA1.HashData(secreto, contador);
        var offset = hmac[^1] & 0x0F;
        var binario = ((hmac[offset] & 0x7F) << 24) | (hmac[offset + 1] << 16) | (hmac[offset + 2] << 8) | hmac[offset + 3];
        var modulo = (int)Math.Pow(10, digitos);
        return (binario % modulo).ToString(new string('0', digitos));
    }

    /// <summary>
    /// Verifica el código aceptando ±1 paso (desfase de reloj del teléfono) y nunca un paso igual
    /// o anterior al último aceptado (un código interceptado no se puede reutilizar).
    /// Devuelve el paso aceptado o null.
    /// </summary>
    public static long? Verificar(byte[] secreto, string? codigo, DateTimeOffset ahora, long? ultimoPasoUsado)
    {
        if (codigo is null) return null;
        codigo = codigo.Replace(" ", "");
        if (codigo.Length != Digitos || !codigo.All(char.IsAsciiDigit)) return null;

        var actual = Paso(ahora);
        for (var delta = -1; delta <= 1; delta++)
        {
            var paso = actual + delta;
            if (ultimoPasoUsado is long u && paso <= u) continue;
            var esperado = Encoding.ASCII.GetBytes(Codigo(secreto, paso));
            if (CryptographicOperations.FixedTimeEquals(esperado, Encoding.ASCII.GetBytes(codigo))) return paso;
        }
        return null;
    }

    public static string UriOtpAuth(byte[] secreto, string cuenta, string emisor = "ReservaMed") =>
        $"otpauth://totp/{Uri.EscapeDataString(emisor)}:{Uri.EscapeDataString(cuenta)}" +
        $"?secret={Base32.Codificar(secreto)}&issuer={Uri.EscapeDataString(emisor)}&algorithm=SHA1&digits={Digitos}&period={PasoSegundos}";
}

/// <summary>Base32 (RFC 4648) sin relleno, el formato de los secretos TOTP.</summary>
public static class Base32
{
    private const string Alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Codificar(ReadOnlySpan<byte> datos)
    {
        var sb = new StringBuilder((datos.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in datos)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alfabeto[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alfabeto[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decodificar(string texto)
    {
        texto = texto.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var salida = new List<byte>(texto.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in texto)
        {
            var v = Alfabeto.IndexOf(c);
            if (v < 0) throw new FormatException("Base32 inválido.");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { salida.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return salida.ToArray();
    }
}
