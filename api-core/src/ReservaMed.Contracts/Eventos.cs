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
/// Carga útil común para los eventos de cita. Lleva lo necesario para notificar
/// sin que el consumidor tenga que volver a consultar la base de datos.
/// </summary>
public sealed record EventoCita(
    int CitaId,
    string Estado,
    string PacienteNombre,
    string PacienteEmail,
    string? PacienteTelefono,
    string ProfesionalNombre,
    string Especialidad,
    string Sede,
    string DireccionSede,
    DateTime FechaHoraInicio,
    long? FolioBono = null,
    int? MontoPagado = null,
    string? Motivo = null);
