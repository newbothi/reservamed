using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RabbitMQ.Client;

namespace ReservaMed.Contracts;

/// <summary>Sección "RabbitMq" de la configuración (API y Worker).</summary>
public sealed class OpcionesConexionRabbit
{
    public string Host { get; set; } = "localhost";
    public int? Puerto { get; set; }
    public string Usuario { get; set; } = "";
    public string Clave { get; set; } = "";
    public string VirtualHost { get; set; } = "/";
    /// <summary>AMQPS (hallazgo M9). Obligatorio en Production.</summary>
    public bool Tls { get; set; }
    /// <summary>CA que firmó el certificado del broker (certs/ca.pem montado en el contenedor).</summary>
    public string? CaCert { get; set; }

    public IReadOnlyList<string> Revisar(bool esProduccion)
    {
        var problemas = new List<string>();
        if (esProduccion && !Tls)
            problemas.Add("RabbitMq:Tls debe ser true en producción: AMQP sin cifrar expone los eventos (hallazgo M9).");
        if (Tls && !string.IsNullOrEmpty(CaCert) && !File.Exists(CaCert))
            problemas.Add($"RabbitMq:CaCert no existe: {CaCert}. Genere los certificados (scripts/generar-certificados).");
        return problemas;
    }
}

public static class FabricaRabbit
{
    public static ConnectionFactory Crear(OpcionesConexionRabbit o, string nombreCliente)
    {
        var f = new ConnectionFactory
        {
            HostName = o.Host,
            Port = o.Puerto ?? (o.Tls ? 5671 : 5672),
            UserName = o.Usuario,
            Password = o.Clave,
            VirtualHost = o.VirtualHost,
            ClientProvidedName = nombreCliente
        };
        if (o.Tls)
        {
            f.Ssl = new SslOption
            {
                Enabled = true,
                ServerName = o.Host,
                Version = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateValidationCallback = string.IsNullOrEmpty(o.CaCert) ? null : ValidacionCertificados.ConfiarEnCa(o.CaCert)
            };
        }
        return f;
    }
}

/// <summary>
/// Valida el certificado del servidor contra una CA propia (la de mkcert u openssl), sin
/// instalarla en el sistema. El nombre del host debe coincidir siempre.
/// </summary>
public static class ValidacionCertificados
{
    public static RemoteCertificateValidationCallback ConfiarEnCa(string rutaCa) =>
        ConfiarEnCa(X509CertificateLoader.LoadCertificateFromFile(rutaCa));

    public static RemoteCertificateValidationCallback ConfiarEnCa(X509Certificate2 ca) =>
        (_, certificado, _, errores) =>
        {
            if (certificado is null) return false;
            if (errores.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) return false;
            if (errores.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
            if (errores == SslPolicyErrors.None) return true;

            using var hoja = certificado as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificado.GetRawCertData());
            using var cadena = new X509Chain();
            cadena.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            cadena.ChainPolicy.CustomTrustStore.Add(ca);
            cadena.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return cadena.Build(hoja);
        };
}
