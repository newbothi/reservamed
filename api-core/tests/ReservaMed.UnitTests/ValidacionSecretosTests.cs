using ReservaMed.Contracts;
using static ReservaMed.Contracts.ValidacionSecretos;

namespace ReservaMed.UnitTests;

/// <summary>Hallazgo C1: la aplicación no arranca con secretos ausentes, cortos o ya publicados.</summary>
public class ValidacionSecretosTests
{
    private const string ClaveBuena = "k3Qz8vT1mN6pR0sW4yB7cE2fH5jL9aD-xG";

    [Fact]
    public void Acepta_secretos_validos() =>
        Assert.Empty(Revisar([new Requisito("Jwt:Key", ClaveBuena, LargoMinimoClaveJwt)], esDesarrollo: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rechaza_secreto_ausente(string? valor)
    {
        var problemas = Revisar([new Requisito("Jwt:Key", valor, LargoMinimoClaveJwt)], esDesarrollo: false);
        Assert.Contains(problemas, p => p.Contains("Falta"));
    }

    [Theory]
    [InlineData("CAMBIAR-esta-clave-de-desarrollo-ReservaMed-2026-minimo-32-bytes")]
    [InlineData("dev-ml-key-cambiar")]
    [InlineData("ReservaMed_2026!")]
    [InlineData("guest")]
    [InlineData("CAMBIAR-cualquier-otra-cosa-larga-de-mas-de-32")]
    public void Rechaza_valores_por_defecto_publicados(string valor)
    {
        var problemas = Revisar([new Requisito("Secreto", valor, 4)], esDesarrollo: false);
        Assert.Contains(problemas, p => p.Contains("por defecto"));
    }

    [Fact]
    public void Rechaza_secreto_corto()
    {
        var problemas = Revisar([new Requisito("Jwt:Key", "corta-pero-no-default", LargoMinimoClaveJwt)], esDesarrollo: false);
        Assert.Contains(problemas, p => p.Contains("al menos"));
    }

    [Fact]
    public void En_desarrollo_tolera_solo_lo_marcado_como_tolerable()
    {
        var requisitos = new[]
        {
            new Requisito("RabbitMq:Clave", "guest", LargoMinimoClave, ToleradoEnDesarrollo: true),
            new Requisito("Jwt:Key", null, LargoMinimoClaveJwt)
        };

        var problemas = Revisar(requisitos, esDesarrollo: true);

        Assert.Single(problemas);
        Assert.Contains("Jwt:Key", problemas[0]);
    }

    [Fact]
    public void Fuera_de_desarrollo_no_tolera_guest()
    {
        var problemas = Revisar([new Requisito("RabbitMq:Clave", "guest", LargoMinimoClave, ToleradoEnDesarrollo: true)], esDesarrollo: false);
        Assert.NotEmpty(problemas);
    }

    [Fact]
    public void Exigir_lanza_sin_revelar_el_valor()
    {
        const string secreto = "dev-ml-key-cambiar";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Exigir([new Requisito("Ml:ApiKey", secreto, LargoMinimoApiKey)], esDesarrollo: false));

        Assert.Contains("Ml:ApiKey", ex.Message);
        Assert.DoesNotContain(secreto, ex.Message);
    }
}
