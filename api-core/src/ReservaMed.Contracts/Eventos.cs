namespace ReservaMed.Contracts;

/// <summary>
/// Nombres de los eventos de dominio. Se usan como routing key en RabbitMQ
/// (exchange de tipo topic "reservamed.eventos").
/// </summary>
public static class TiposEvento
{
    public const string Exchange = "reservamed.eventos";

    public const string CitaReservada    = "cita.reservada";
    public const string CitaConfirmada   = "cita.confirmada";
    public const string CitaCancelada    = "cita.cancelada";
    public const string CitaExpirada     = "cita.expirada";
    public const string CitaAdmitida     = "cita.admitida";
    public const string CitaRecordatorio = "cita.recordatorio";
}

/// <summary>
/// Carga útil de los eventos de cita. Hallazgo A9: SOLO identificadores. Antes viajaban el
/// nombre, correo y teléfono del paciente por la Outbox y RabbitMQ (y quedaban en las colas de
/// muertos); ahora cada consumidor consulta en la base lo que necesita, con sus propios permisos.
/// </summary>
public sealed record EventoCita(int CitaId, string Estado, int Version = 2);
