using Microsoft.Data.SqlClient;

namespace ReservaMed.Api.Common;

/// <summary>
/// F5 (ASVS V9.2.2 / V1.9.1): la API no arranca en producción si alguna conexión interna quedaría
/// en texto plano o confiaría en cualquier certificado.
/// </summary>
public static class TlsInterno
{
    public static IReadOnlyList<string> Revisar(string? cadenaSql, string? urlMl)
    {
        var problemas = new List<string>();
        if (string.IsNullOrWhiteSpace(cadenaSql))
            problemas.Add("Falta ConnectionStrings:ReservaMed.");
        else
        {
            var b = new SqlConnectionStringBuilder(cadenaSql);
            if (b.Encrypt != SqlConnectionEncryptOption.Strict && b.Encrypt != SqlConnectionEncryptOption.Mandatory)
                problemas.Add("SQL Server: use Encrypt=Strict.");
            if (b.TrustServerCertificate)
                problemas.Add("SQL Server: TrustServerCertificate=True acepta cualquier certificado; fije ServerCertificate.");
            if (string.IsNullOrEmpty(b.ServerCertificate) && string.IsNullOrEmpty(b.HostNameInCertificate))
                problemas.Add("SQL Server: indique ServerCertificate (certs/sqlserver.crt).");
        }
        if (urlMl is not null && !urlMl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            problemas.Add($"Ml:Url debe ser https:// (es {urlMl}).");
        return problemas;
    }
}
