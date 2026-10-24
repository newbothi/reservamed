using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Modules.Auth;

public sealed record TokensSesion(Guid SesionId, string Refresh, DateTime SesionExpiraUtc);

/// <summary>
/// Sesiones revocables con refresh tokens rotativos (hallazgo A1).
/// - Cada inicio de sesión crea una fila en dbo.Sesiones; el access token lleva su id (sid).
/// - El refresh token es de un solo uso: renovar entrega uno nuevo y marca el anterior.
/// - Si llega un refresh ya usado, alguien lo copió: se revoca la sesión completa.
/// - Desactivar un usuario o cerrar sesión revoca; la API rechaza el access token en el acto.
/// Fechas en UTC.
/// </summary>
public sealed class ServicioSesiones(ISqlConnectionFactory db, IMemoryCache cache, IOptions<OpcionesSeguridad> opciones,
    ILogger<ServicioSesiones> log)
{
    private static readonly TimeSpan CacheValidez = TimeSpan.FromSeconds(30);
    private readonly OpcionesSeguridad _o = opciones.Value;

    public static byte[] Hash(string token) => SHA256.HashData(Convert.FromBase64String(Base64Url(token, decodificar: true)));

    private static string NuevoToken() => Base64Url(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    private static string Base64Url(string s, bool decodificar = false)
    {
        if (!decodificar) return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var b = s.Replace('-', '+').Replace('_', '/');
        return b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
    }

    public async Task<TokensSesion> CrearAsync(int usuarioId, string cliente, string? ip)
    {
        var ahora = DateTime.UtcNow;
        var expira = ahora.AddHours(cliente == "BACKOFFICE" ? _o.SesionBackofficeHoras : _o.SesionPortalHoras);
        var sid = Guid.NewGuid();
        var refresh = NuevoToken();

        await using var cn = db.Crear();
        await cn.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        await cn.ExecuteAsync("""
            INSERT INTO dbo.Sesiones (Id, UsuarioId, Cliente, CreadaEn, UltimoUso, ExpiraEn, Ip)
            VALUES (@Sid, @UsuarioId, @Cliente, @Ahora, @Ahora, @Expira, @Ip);
            INSERT INTO dbo.TokensRefresco (Hash, SesionId, CreadoEn, ExpiraEn) VALUES (@Hash, @Sid, @Ahora, @Expira);
            """, new { Sid = sid, UsuarioId = usuarioId, Cliente = cliente, Ahora = ahora, Expira = expira, Ip = ip, Hash = Hash(refresh) }, tx);
        await tx.CommitAsync();
        return new TokensSesion(sid, refresh, expira);
    }

    private sealed record FilaRefresco(Guid SesionId, DateTime ExpiraEn, DateTime? UsadoEn, DateTime SesionExpira,
        DateTime? RevocadaEn, int UsuarioId, bool Activo);

    /// <summary>Rotación: consume el refresh y entrega uno nuevo. Null si no es válido.</summary>
    public async Task<(TokensSesion Tokens, int UsuarioId)?> RenovarAsync(string refresh)
    {
        byte[] hash;
        try { hash = Hash(refresh); } catch (FormatException) { return null; }

        var ahora = DateTime.UtcNow;
        await using var cn = db.Crear();
        await cn.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();

        var f = await cn.QuerySingleOrDefaultAsync<FilaRefresco>("""
            SELECT t.SesionId, t.ExpiraEn, t.UsadoEn, s.ExpiraEn AS SesionExpira, s.RevocadaEn, s.UsuarioId, u.Activo
            FROM dbo.TokensRefresco t WITH (UPDLOCK, ROWLOCK)
            JOIN dbo.Sesiones s ON s.Id = t.SesionId
            JOIN dbo.Usuarios u ON u.Id = s.UsuarioId
            WHERE t.Hash = @Hash
            """, new { Hash = hash }, tx);

        if (f is null) return null;

        if (f.UsadoEn is not null)
        {
            // Reuso: el token ya se había canjeado. Lo tiene otra persona (o se repitió una
            // petición): por seguridad se cierra la sesión para ambos.
            await RevocarAsync(cn, tx, f.SesionId, "Reuso de refresh token");
            await tx.CommitAsync();
            log.LogWarning("Refresh token reutilizado: sesión {Sid} revocada.", f.SesionId);
            return null;
        }
        if (f.RevocadaEn is not null || f.ExpiraEn <= ahora || f.SesionExpira <= ahora || !f.Activo) return null;

        var nuevo = NuevoToken();
        await cn.ExecuteAsync("""
            UPDATE dbo.TokensRefresco SET UsadoEn = @Ahora WHERE Hash = @Hash;
            INSERT INTO dbo.TokensRefresco (Hash, SesionId, CreadoEn, ExpiraEn) VALUES (@Nuevo, @Sid, @Ahora, @Expira);
            UPDATE dbo.Sesiones SET UltimoUso = @Ahora WHERE Id = @Sid;
            """, new { Ahora = ahora, Hash = hash, Nuevo = Hash(nuevo), Sid = f.SesionId, Expira = f.SesionExpira }, tx);
        await tx.CommitAsync();
        return (new TokensSesion(f.SesionId, nuevo, f.SesionExpira), f.UsuarioId);
    }

    public async Task RevocarAsync(Guid sid, string motivo)
    {
        await using var cn = db.Crear();
        await cn.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        await RevocarAsync(cn, tx, sid, motivo);
        await tx.CommitAsync();
    }

    public async Task<int> RevocarTodasAsync(int usuarioId, string motivo)
    {
        await using var cn = db.Crear();
        var sids = (await cn.QueryAsync<Guid>(
            "SELECT Id FROM dbo.Sesiones WHERE UsuarioId = @U AND RevocadaEn IS NULL", new { U = usuarioId })).ToList();
        await cn.ExecuteAsync("""
            UPDATE dbo.Sesiones SET RevocadaEn = SYSUTCDATETIME(), MotivoRevocacion = @Motivo
            WHERE UsuarioId = @U AND RevocadaEn IS NULL
            """, new { U = usuarioId, Motivo = motivo });
        foreach (var sid in sids) cache.Remove(Clave(sid));
        return sids.Count;
    }

    private async Task RevocarAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction tx, Guid sid, string motivo)
    {
        await cn.ExecuteAsync("""
            UPDATE dbo.Sesiones SET RevocadaEn = SYSUTCDATETIME(), MotivoRevocacion = @Motivo
            WHERE Id = @Sid AND RevocadaEn IS NULL
            """, new { Sid = sid, Motivo = motivo }, tx);
        cache.Remove(Clave(sid));
    }

    /// <summary>
    /// ¿Sigue vigente la sesión de este access token? Se consulta en cada petición autenticada,
    /// con caché de 30 s; revocar desde esta instancia borra la caché al momento.
    /// </summary>
    public async Task<bool> EstaActivaAsync(Guid sid)
    {
        if (cache.TryGetValue(Clave(sid), out bool activa)) return activa;
        await using var cn = db.Crear();
        activa = await cn.ExecuteScalarAsync<int>("""
            SELECT COUNT(1) FROM dbo.Sesiones s JOIN dbo.Usuarios u ON u.Id = s.UsuarioId
            WHERE s.Id = @Sid AND s.RevocadaEn IS NULL AND s.ExpiraEn > SYSUTCDATETIME() AND u.Activo = 1
            """, new { Sid = sid }) == 1;
        cache.Set(Clave(sid), activa, CacheValidez);
        return activa;
    }

    private static string Clave(Guid sid) => $"sesion:{sid}";
}
