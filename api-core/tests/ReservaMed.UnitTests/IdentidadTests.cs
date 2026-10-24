using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Modules.Auth;

namespace ReservaMed.UnitTests;

/// <summary>TOTP según RFC 6238 (apéndice B): mismos resultados que cualquier autenticador.</summary>
public class TotpTests
{
    private static readonly byte[] SecretoRfc = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    [InlineData(20000000000, "65353130")]
    public void Vectores_del_rfc_6238(long segundos, string esperado) =>
        Assert.Equal(esperado, Totp.Codigo(SecretoRfc, Totp.Paso(DateTimeOffset.FromUnixTimeSeconds(segundos)), digitos: 8));

    [Fact]
    public void Acepta_el_paso_actual_y_uno_de_desfase_pero_no_dos()
    {
        var ahora = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var paso = Totp.Paso(ahora);

        Assert.Equal(paso, Totp.Verificar(SecretoRfc, Totp.Codigo(SecretoRfc, paso), ahora, null));
        Assert.Equal(paso - 1, Totp.Verificar(SecretoRfc, Totp.Codigo(SecretoRfc, paso - 1), ahora, null));
        Assert.Equal(paso + 1, Totp.Verificar(SecretoRfc, Totp.Codigo(SecretoRfc, paso + 1), ahora, null));
        Assert.Null(Totp.Verificar(SecretoRfc, Totp.Codigo(SecretoRfc, paso - 2), ahora, null));
    }

    [Fact]
    public void Un_codigo_ya_usado_no_se_acepta_otra_vez()
    {
        var ahora = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var paso = Totp.Paso(ahora);
        var codigo = Totp.Codigo(SecretoRfc, paso);

        Assert.Null(Totp.Verificar(SecretoRfc, codigo, ahora, ultimoPasoUsado: paso));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public void Formatos_invalidos_se_rechazan(string? codigo) =>
        Assert.Null(Totp.Verificar(SecretoRfc, codigo, DateTimeOffset.UtcNow, null));

    [Fact]
    public void Uri_otpauth_lleva_emisor_cuenta_y_secreto()
    {
        var uri = Totp.UriOtpAuth(SecretoRfc, "dra.soto@reservamed.cl");
        Assert.StartsWith("otpauth://totp/ReservaMed:dra.soto%40reservamed.cl?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", uri);
        Assert.Contains("issuer=ReservaMed", uri);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_rfc_4648(string texto, string esperado)
    {
        Assert.Equal(esperado, Base32.Codificar(Encoding.ASCII.GetBytes(texto)));
        Assert.Equal(texto, Encoding.ASCII.GetString(Base32.Decodificar(esperado)));
    }
}

public class CifradoSecretosTests
{
    private static string Clave() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Cifra_y_descifra()
    {
        var c = new CifradoSecretos(Clave());
        var secreto = RandomNumberGenerator.GetBytes(20);
        var cifrado = c.Cifrar(secreto);

        Assert.NotEqual(secreto, cifrado[29..]);
        Assert.Equal(secreto, c.Descifrar(cifrado));
        Assert.NotEqual(cifrado, c.Cifrar(secreto));    // nonce aleatorio
    }

    [Fact]
    public void Manipular_el_dato_o_usar_otra_clave_falla()
    {
        var c = new CifradoSecretos(Clave());
        var cifrado = c.Cifrar(RandomNumberGenerator.GetBytes(20));
        var alterado = (byte[])cifrado.Clone();
        alterado[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => c.Descifrar(alterado));
        Assert.ThrowsAny<CryptographicException>(() => new CifradoSecretos(Clave()).Descifrar(cifrado));
    }

    [Fact]
    public void Clave_corta_detiene_el_arranque() =>
        Assert.Throws<InvalidOperationException>(() => new CifradoSecretos(Convert.ToBase64String(new byte[16])));
}

public class PoliticaContrasenasTests
{
    private sealed class Filtraciones(int? n) : IVerificadorFiltraciones
    {
        public Task<int?> AparicionesAsync(string contrasena, CancellationToken ct = default) => Task.FromResult(n);
    }

    private static PoliticaContrasenas Politica(int? filtrada = 0) =>
        new(Options.Create(new OpcionesSeguridad()), new Filtraciones(filtrada));

    [Fact]
    public async Task Acepta_una_frase_larga() =>
        Assert.Empty(await Politica().RevisarAsync("caballo-bateria-grapa-correcta", "ana.perez@mail.cl", "Ana Pérez"));

    [Theory]
    [InlineData("Corta-123")]                 // < 12
    [InlineData("aaaaaaaaaaaaaaaa")]          // repetitiva
    [InlineData("password1234")]              // común
    [InlineData("ana.perez-segura-2026")]     // contiene el correo
    [InlineData("Gonzalez-segura-2026")]      // contiene el apellido
    public async Task Rechaza_contrasenas_debiles(string clave) =>
        Assert.NotEmpty(await Politica().RevisarAsync(clave, "ana.perez@mail.cl", "Ana Gonzalez"));

    [Fact]
    public async Task Rechaza_una_contrasena_filtrada() =>
        Assert.Contains(await Politica(filtrada: 3_861_493).RevisarAsync("una-clave-filtrada-larga", "x@y.cl", "X Y"),
            e => e.Contains("filtraciones"));

    [Fact]
    public async Task Si_el_servicio_de_filtraciones_no_responde_aplica_solo_la_politica_local() =>
        Assert.Empty(await Politica(filtrada: null).RevisarAsync("caballo-bateria-grapa-correcta", "x@y.cl", "X Y"));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(6, 60)]
    [InlineData(20, 60)]
    public void Bloqueo_progresivo_hasta_una_hora(int previos, int minutos) =>
        Assert.Equal(minutos, new OpcionesSeguridad().MinutosBloqueo(previos));
}
