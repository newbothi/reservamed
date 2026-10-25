using Microsoft.Extensions.Configuration;
using ReservaMed.Api.Common;
using ReservaMed.Contracts;

namespace ReservaMed.UnitTests;

/// <summary>F5: TLS interno obligatorio en producción y secretos desde /run/secrets.</summary>
public sealed class TlsInternoTests
{
    [Fact]
    public void Acepta_sql_estricto_con_certificado_fijado_y_ml_https()
    {
        var p = TlsInterno.Revisar("Server=sqlserver;Database=ReservaMed;User Id=rm_api;Encrypt=Strict;ServerCertificate=/certs/sqlserver.crt",
            "https://ml-service:8443");
        Assert.Empty(p);
    }

    [Theory]
    [InlineData("Server=sqlserver;Database=R;Encrypt=False", "https://ml:8443")]
    [InlineData("Server=sqlserver;Database=R;Encrypt=Strict;TrustServerCertificate=True", "https://ml:8443")]
    [InlineData("Server=sqlserver;Database=R;Encrypt=Strict", "https://ml:8443")]
    [InlineData("Server=sqlserver;Database=R;Encrypt=Strict;ServerCertificate=/c.crt", "http://ml:8000")]
    public void Rechaza_conexiones_internas_sin_tls_verificado(string sql, string ml) =>
        Assert.NotEmpty(TlsInterno.Revisar(sql, ml));

    [Fact]
    public void Lee_secretos_de_archivos_con_prioridad_sobre_el_entorno()
    {
        var carpeta = Directory.CreateTempSubdirectory("secretos-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(carpeta, "Jwt__Key"), "desde-archivo\n");
            File.WriteAllText(Path.Combine(carpeta, "ignorado"), "x");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = "desde-entorno" })
                .AgregarSecretosDocker(carpeta)
                .Build();
            Assert.Equal("desde-archivo", config["Jwt:Key"]);
            Assert.Null(config["ignorado"]);
        }
        finally
        {
            Directory.Delete(carpeta, true);
        }
    }

    [Fact]
    public void La_clave_de_sql_se_agrega_a_la_cadena()
    {
        var cadena = SecretosArchivo.CadenaSql("Server=sqlserver;Database=ReservaMed;User Id=rm_api", "Cl@ve;con=raros");
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cadena);
        Assert.Equal("Cl@ve;con=raros", b.Password);
        Assert.Equal("rm_api", b.UserID);
    }
}
