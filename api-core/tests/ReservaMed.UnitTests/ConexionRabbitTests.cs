using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ReservaMed.Contracts;

namespace ReservaMed.UnitTests;

/// <summary>Hallazgo M9: AMQP cifrado y certificado del broker validado contra la CA local.</summary>
public class ConexionRabbitTests
{
    private static X509Certificate2 CrearCa(string nombre)
    {
        using var clave = RSA.Create(2048);
        var req = new CertificateRequest($"CN={nombre}", clave, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 Emitir(X509Certificate2 ca, string host)
    {
        using var clave = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", clave, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        return req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(8));
    }

    [Fact]
    public void Acepta_certificado_firmado_por_la_ca_local()
    {
        using var ca = CrearCa("ReservaMed CA prueba");
        using var hoja = Emitir(ca, "rabbitmq");
        var cb = ValidacionCertificados.ConfiarEnCa(ca);

        // Sin la CA en el sistema, .NET reporta error de cadena: la validación propia la resuelve.
        Assert.True(cb(this, hoja, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Rechaza_certificado_de_otra_ca()
    {
        using var ca = CrearCa("ReservaMed CA prueba");
        using var otra = CrearCa("CA del atacante");
        using var hoja = Emitir(otra, "rabbitmq");

        Assert.False(ValidacionCertificados.ConfiarEnCa(ca)(this, hoja, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Rechaza_nombre_que_no_coincide_aunque_la_ca_sea_la_correcta()
    {
        using var ca = CrearCa("ReservaMed CA prueba");
        using var hoja = Emitir(ca, "otro-host");

        Assert.False(ValidacionCertificados.ConfiarEnCa(ca)(this, hoja, null,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Rechaza_si_no_hay_certificado()
    {
        using var ca = CrearCa("ReservaMed CA prueba");
        Assert.False(ValidacionCertificados.ConfiarEnCa(ca)(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Fact]
    public void Produccion_exige_tls()
    {
        Assert.NotEmpty(new OpcionesConexionRabbit { Tls = false }.Revisar(esProduccion: true));
        Assert.Empty(new OpcionesConexionRabbit { Tls = false }.Revisar(esProduccion: false));
    }

    [Fact]
    public void Fabrica_usa_5671_vhost_y_ssl_con_tls()
    {
        var f = FabricaRabbit.Crear(new OpcionesConexionRabbit
        {
            Host = "rabbitmq", Usuario = "rm_api", Clave = "x", VirtualHost = "reservamed", Tls = true
        }, "prueba");

        Assert.Equal(5671, f.Port);
        Assert.Equal("reservamed", f.VirtualHost);
        Assert.True(f.Ssl.Enabled);
        Assert.Equal("rabbitmq", f.Ssl.ServerName);
    }

    [Fact]
    public void Sin_tls_usa_5672()
    {
        var f = FabricaRabbit.Crear(new OpcionesConexionRabbit { Host = "localhost", Usuario = "guest", Clave = "guest" }, "dev");
        Assert.Equal(5672, f.Port);
        Assert.False(f.Ssl.Enabled);
    }
}
