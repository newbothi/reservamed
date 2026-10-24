namespace ReservaMed.Api.Modules.Auth;

/// <summary>Sección "Seguridad" de appsettings. Valores por defecto razonables para una clínica.</summary>
public sealed class OpcionesSeguridad
{
    /// <summary>Vida del token de acceso (A1).</summary>
    public int AccesoMinutos { get; set; } = 15;
    /// <summary>Vida máxima de una sesión de paciente (no se extiende al renovar).</summary>
    public int SesionPortalHoras { get; set; } = 24 * 7;
    /// <summary>Vida máxima de una sesión del personal: un turno.</summary>
    public int SesionBackofficeHoras { get; set; } = 12;
    /// <summary>Tiempo para completar el segundo factor después de la contraseña.</summary>
    public int DesafioMinutos { get; set; } = 5;

    /// <summary>Intentos fallidos antes del primer bloqueo (A2).</summary>
    public int MaxIntentos { get; set; } = 5;
    public int BloqueoBaseMinutos { get; set; } = 1;
    public int BloqueoMaxMinutos { get; set; } = 60;

    /// <summary>Límite de peticiones por IP (las pruebas de integración lo suben).</summary>
    public int LoginPorMinutoPorIp { get; set; } = 10;
    public int RenovarPorMinutoPorIp { get; set; } = 60;

    /// <summary>Política de contraseñas (M2).</summary>
    public int LargoMinimoContrasena { get; set; } = 12;
    public bool VerificarFiltraciones { get; set; } = true;

    /// <summary>Validez del enlace de verificación de correo.</summary>
    public int VerificacionCorreoHoras { get; set; } = 24;

    /// <summary>URL pública del portal para armar enlaces en correos.</summary>
    public string UrlPortal { get; set; } = "https://reservamed.localhost";

    /// <summary>Clave (base64, ≥ 32 bytes) para cifrar las semillas TOTP. Secreto.</summary>
    public string TotpClave { get; set; } = "";

    /// <summary>Minutos de bloqueo para el bloqueo número n (1, 2, 4… hasta el máximo).</summary>
    public int MinutosBloqueo(int bloqueosPrevios) =>
        (int)Math.Min(BloqueoMaxMinutos, BloqueoBaseMinutos * Math.Pow(2, Math.Max(0, bloqueosPrevios)));
}
