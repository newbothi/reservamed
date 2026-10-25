using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Modules.Auth;

/// <summary>
/// Hash de contraseñas. ASVS V2.1.3: BCrypt solo considera los primeros 72 bytes, así que una
/// contraseña larga se "truncaba" en silencio. Ahora se hace SHA-384 de la contraseña completa
/// (64 caracteres en Base64, siempre bajo 72 bytes) y eso va a BCrypt (costo 11, el mismo de los hashes antiguos: así el tiempo de verificación no distingue cuentas, M1).
/// Formato nuevo: "$rm2$" + hash BCrypt. Los hashes antiguos (BCrypt directo) se siguen
/// aceptando y se reemplazan por el formato nuevo en el siguiente ingreso correcto.
/// </summary>
public static class Contrasenas
{
    private const string Prefijo = "$rm2$";
    private const int Costo = 11;

    public static string Hash(string contrasena) => Prefijo + BCrypt.Net.BCrypt.HashPassword(Prehash(contrasena), Costo);

    public static bool Verificar(string contrasena, string hash)
    {
        try
        {
            return hash.StartsWith(Prefijo, StringComparison.Ordinal)
                ? BCrypt.Net.BCrypt.Verify(Prehash(contrasena), hash[Prefijo.Length..])
                : BCrypt.Net.BCrypt.Verify(contrasena, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    /// <summary>¿Es un hash del formato antiguo (truncaba a 72 bytes)?</summary>
    public static bool EsLegado(string hash) => !hash.StartsWith(Prefijo, StringComparison.Ordinal);

    private static string Prehash(string contrasena) =>
        Convert.ToBase64String(SHA384.HashData(Encoding.UTF8.GetBytes(contrasena)));
}

/// <summary>A2: bloqueo progresivo por cuenta (1, 2, 4… minutos hasta 60) tras varios intentos fallidos.</summary>
public sealed class ServicioBloqueos(IOptions<OpcionesSeguridad> opciones, IAuditoria auditoria)
{
    private readonly OpcionesSeguridad _o = opciones.Value;

    public async Task RegistrarFalloAsync(System.Data.IDbConnection cn, int usuarioId, int intentosFallidos, int bloqueosConsecutivos)
    {
        var intentos = intentosFallidos + 1;
        if (intentos < _o.MaxIntentos)
        {
            await cn.ExecuteAsync("UPDATE dbo.Usuarios SET IntentosFallidos = @N WHERE Id = @Id", new { N = intentos, Id = usuarioId });
            return;
        }
        var minutos = _o.MinutosBloqueo(bloqueosConsecutivos);
        await cn.ExecuteAsync("""
            UPDATE dbo.Usuarios SET IntentosFallidos = 0, BloqueosConsecutivos = BloqueosConsecutivos + 1, BloqueadoHasta = @Hasta
            WHERE Id = @Id
            """, new { Hasta = DateTime.UtcNow.AddMinutes(minutos), Id = usuarioId });
        await auditoria.RegistrarAsync("CUENTA_BLOQUEADA", "Usuario", usuarioId, $"{minutos} min");
    }

    public static Task ReiniciarAsync(System.Data.IDbConnection cn, int usuarioId) => cn.ExecuteAsync(
        "UPDATE dbo.Usuarios SET IntentosFallidos = 0, BloqueosConsecutivos = 0, BloqueadoHasta = NULL WHERE Id = @Id", new { Id = usuarioId });

    public static ReglaNegocioException Bloqueada() => new(
        "Demasiados intentos fallidos. Por seguridad, espere unos minutos antes de volver a intentar.",
        StatusCodes.Status429TooManyRequests, "BLOQUEADA");
}
