using System.Security.Cryptography;

namespace ReservaMed.Api.Modules.Auth;

/// <summary>
/// Cifra secretos que deben poder leerse (la semilla TOTP) con AES-256-GCM.
/// Formato: [1 byte versión][12 bytes nonce][16 bytes tag][datos]. Si la base se filtra,
/// las semillas no sirven sin la clave, que vive fuera de la base (Totp:Clave).
/// </summary>
public sealed class CifradoSecretos
{
    private const byte Version = 1;
    private readonly byte[] _clave;

    public CifradoSecretos(string claveBase64)
    {
        _clave = ClaveDesde(claveBase64);
    }

    public static byte[] ClaveDesde(string texto)
    {
        // Acepta base64 estándar o URL-safe (lo que genera configurar-secretos.ps1).
        var b64 = texto.Trim().Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        var bytes = Convert.FromBase64String(b64);
        if (bytes.Length < 32) throw new InvalidOperationException("Totp:Clave debe tener al menos 32 bytes (base64).");
        return SHA256.HashData(bytes);   // normaliza a 32 bytes exactos
    }

    public byte[] Cifrar(ReadOnlySpan<byte> datos)
    {
        var salida = new byte[1 + 12 + 16 + datos.Length];
        salida[0] = Version;
        var nonce = salida.AsSpan(1, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_clave, 16);
        aes.Encrypt(nonce, datos, salida.AsSpan(29), salida.AsSpan(13, 16));
        return salida;
    }

    public byte[] Descifrar(ReadOnlySpan<byte> cifrado)
    {
        if (cifrado.Length < 29 || cifrado[0] != Version) throw new CryptographicException("Formato de secreto cifrado desconocido.");
        var datos = new byte[cifrado.Length - 29];
        using var aes = new AesGcm(_clave, 16);
        aes.Decrypt(cifrado.Slice(1, 12), cifrado[29..], cifrado.Slice(13, 16), datos);
        return datos;
    }
}
