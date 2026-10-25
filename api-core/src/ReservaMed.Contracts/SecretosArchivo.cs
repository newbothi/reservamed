using System.Data.Common;
using Microsoft.Extensions.Configuration;

namespace ReservaMed.Contracts;

/// <summary>
/// F5 (ASVS V6.4.1): en Docker los secretos llegan como archivos en /run/secrets (Docker secrets),
/// no como variables de entorno: no aparecen en "docker inspect", en /proc/1/environ ni en los
/// volcados del proceso. Cada archivo se llama como la clave de configuración ("Jwt__Key").
/// </summary>
public static class SecretosArchivo
{
    public const string Carpeta = "/run/secrets";

    /// <summary>Agrega /run/secrets (si existe) con prioridad sobre las variables de entorno.</summary>
    public static IConfigurationBuilder AgregarSecretosDocker(this IConfigurationBuilder config, string carpeta = Carpeta)
    {
        if (!Directory.Exists(carpeta)) return config;
        var valores = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var archivo in Directory.EnumerateFiles(carpeta))
        {
            var nombre = Path.GetFileName(archivo);
            if (!nombre.Contains("__", StringComparison.Ordinal)) continue;   // solo claves de configuración
            valores[nombre.Replace("__", ":", StringComparison.Ordinal)] = File.ReadAllText(archivo).Trim();
        }
        return config.AddInMemoryCollection(valores);
    }

    /// <summary>La contraseña de SQL va aparte (Sql:Clave) para que la cadena de conexión no sea secreta.</summary>
    public static string CadenaSql(string cadena, string? clave)
    {
        if (string.IsNullOrEmpty(clave)) return cadena;
        var b = new DbConnectionStringBuilder { ConnectionString = cadena };
        b["Password"] = clave;
        return b.ConnectionString;
    }
}
