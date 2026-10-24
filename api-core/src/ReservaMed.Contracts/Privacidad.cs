namespace ReservaMed.Contracts;

/// <summary>
/// Hallazgo A9: datos personales que nunca deben quedar completos en logs, mensajes ni
/// registros de envío. Se muestran lo justo para que una persona reconozca el dato.
/// </summary>
public static class Privacidad
{
    /// <summary>"ignacio.perez@demo.cl" → "i***@demo.cl".</summary>
    public static string Correo(string? correo)
    {
        if (string.IsNullOrWhiteSpace(correo)) return "";
        var arroba = correo.IndexOf('@');
        return arroba <= 0 ? "***" : $"{correo[0]}***{correo[arroba..]}";
    }

    /// <summary>"+56 9 1234 5678" → "***5678".</summary>
    public static string Telefono(string? telefono)
    {
        var digitos = new string((telefono ?? "").Where(char.IsDigit).ToArray());
        return digitos.Length < 4 ? "***" : $"***{digitos[^4..]}";
    }

    /// <summary>"12345678-9" → "xx.xxx.678-9" (como la máscara de la base).</summary>
    public static string Rut(string? rut)
    {
        var limpio = (rut ?? "").Replace(".", "").Trim();
        return limpio.Length < 5 ? "***" : $"xx.xxx.{limpio[^5..]}";
    }
}
