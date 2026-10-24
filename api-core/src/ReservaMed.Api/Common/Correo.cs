using System.Net.Mail;
using System.Text.RegularExpressions;

namespace ReservaMed.Api.Common;

public sealed record Correo(string Para, string Asunto, string Cuerpo);

/// <summary>Envío de correos transaccionales (verificación de cuenta, avisos de seguridad).</summary>
public interface IEnviadorCorreo
{
    Task EnviarAsync(Correo correo, CancellationToken ct = default);
}

public sealed class OpcionesCorreo
{
    /// <summary>Log (desarrollo: el correo aparece en la consola) o Smtp (Docker: Mailpit).</summary>
    public string Modo { get; set; } = "Log";
    public string Host { get; set; } = "localhost";
    public int Puerto { get; set; } = 1025;
    public string Remitente { get; set; } = "ReservaMed <no-responder@reservamed.cl>";
}

/// <summary>Desarrollo: muestra el correo en el log. Enmascara la dirección (A9 se completa en F4).</summary>
public sealed partial class CorreoEnLog(ILogger<CorreoEnLog> log) : IEnviadorCorreo
{
    public Task EnviarAsync(Correo c, CancellationToken ct = default)
    {
        log.LogInformation("Correo a {Para}: {Asunto}\n{Cuerpo}", Enmascarar(c.Para), c.Asunto, c.Cuerpo);
        return Task.CompletedTask;
    }

    public static string Enmascarar(string email) => Usuario().Replace(email, m => m.Value[0] + "***");

    [GeneratedRegex(@"^[^@]+")]
    private static partial Regex Usuario();
}

/// <summary>SMTP sin autenticación hacia un relay interno (Mailpit en la red de Docker).</summary>
public sealed class CorreoSmtp(Microsoft.Extensions.Options.IOptions<OpcionesCorreo> opciones, ILogger<CorreoSmtp> log) : IEnviadorCorreo
{
    public async Task EnviarAsync(Correo c, CancellationToken ct = default)
    {
        var o = opciones.Value;
        using var cliente = new SmtpClient(o.Host, o.Puerto) { DeliveryMethod = SmtpDeliveryMethod.Network };
        using var mensaje = new MailMessage { From = new MailAddress(o.Remitente), Subject = c.Asunto, Body = c.Cuerpo };
        mensaje.To.Add(c.Para);
        try
        {
            await cliente.SendMailAsync(mensaje, ct);
        }
        catch (SmtpException ex)
        {
            // Un fallo de correo no debe revelar nada al usuario ni botar el registro.
            log.LogError("No se pudo enviar el correo '{Asunto}': {Error}", c.Asunto, ex.Message);
        }
    }
}
