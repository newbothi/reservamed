using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace ReservaMed.Api.Common;

/// <summary>
/// Hallazgo A4: X-Forwarded-For solo se acepta si la conexión viene de un proxy conocido
/// (NGINX o el BFF, con IP fija en la red de Docker). Cualquier otro cliente que envíe la
/// cabecera la ve ignorada: no puede falsear su IP en la auditoría ni evadir límites.
/// </summary>
public static class ProxiesConfiables
{
    public static void Configurar(ForwardedHeadersOptions o, IEnumerable<string>? ips)
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;   // solo el salto inmediato: NGINX o el BFF reescriben la cabecera completa
        o.RequireHeaderSymmetry = false;

        // Los valores por defecto (127.0.0.1 y ::1) se mantienen para desarrollo local.
        foreach (var texto in ips ?? [])
        {
            if (!IPAddress.TryParse(texto, out var ip))
                throw new InvalidOperationException($"Proxy:IpsConfiables contiene un valor que no es una IP: '{texto}'.");
            o.KnownProxies.Add(ip);
        }
    }
}
