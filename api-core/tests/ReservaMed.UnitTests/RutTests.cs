using ReservaMed.Api.Common;

namespace ReservaMed.UnitTests;

/// <summary>RUT chileno: módulo 11, formatos aceptados y rechazo de inválidos.</summary>
public class RutTests
{
    [Theory]
    [InlineData("19876321-7", "19876321-7")]
    [InlineData("19.876.321-7", "19876321-7")]
    [InlineData("198763217", "19876321-7")]
    [InlineData(" 19876321-7 ", "19876321-7")]
    [InlineData("11223344-k", "11223344-K")]
    [InlineData("11.223.344-K", "11223344-K")]
    [InlineData("9876543-3", "9876543-3")]
    public void Normaliza_rut_valido(string entrada, string esperado) =>
        Assert.Equal(esperado, Rut.Normalizar(entrada));

    [Theory]
    [InlineData("19876321-8")]   // dígito verificador incorrecto
    [InlineData("11223344-0")]
    [InlineData("123-4")]        // muy corto
    [InlineData("abcdefgh-1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rechaza_rut_invalido(string? entrada)
    {
        Assert.Null(Rut.Normalizar(entrada));
        Assert.False(Rut.EsValido(entrada));
    }

    [Theory]
    [InlineData(19876321, "7")]
    [InlineData(11223344, "K")]
    [InlineData(76543210, "3")]
    public void Calcula_digito_verificador(int numero, string dv) =>
        Assert.Equal(dv, Rut.CalcularDv(numero));

    [Fact]
    public void Exigir_lanza_error_de_negocio_con_rut_invalido()
    {
        var ex = Assert.Throws<ReglaNegocioException>(() => Rut.Exigir("12345678-0"));
        Assert.Equal("RUT_INVALIDO", ex.Codigo);
    }
}
