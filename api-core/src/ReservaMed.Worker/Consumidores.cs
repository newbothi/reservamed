using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ReservaMed.Contracts;

namespace ReservaMed.Worker;

/// <summary>
/// Base para consumidores de RabbitMQ: declara exchange, cola con dead-letter y
/// reintenta la conexión si el broker no está disponible.
/// </summary>
public abstract class ConsumidorBase(IConfiguration config, ILogger log) : BackgroundService
{
    protected const string ExchangeMuertos = "reservamed.dlx";
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected abstract string Cola { get; }
    protected abstract string[] RoutingKeys { get; }
    protected abstract Task ProcesarAsync(string routingKey, string json, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumirAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("RabbitMQ no disponible ({Error}). Reintentando en 5 s...", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ConsumirAsync(CancellationToken ct)
    {
        var opciones = config.GetSection("RabbitMq").Get<OpcionesConexionRabbit>() ?? new OpcionesConexionRabbit();
        if (string.IsNullOrEmpty(opciones.Usuario) || string.IsNullOrEmpty(opciones.Clave))
            throw new InvalidOperationException("Faltan RabbitMq:Usuario o RabbitMq:Clave.");
        var factory = FabricaRabbit.Crear(opciones, $"reservamed-worker-{Cola}");

        await using var conexion = await factory.CreateConnectionAsync(ct);
        await using var canal = await conexion.CreateChannelAsync(cancellationToken: ct);

        await canal.ExchangeDeclareAsync(TiposEvento.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        // Exchange "direct" de muertos: cada cola deja sus mensajes fallidos en SU propia .dlq
        // (un fanout los duplicaría en todas las colas de muertos).
        await canal.ExchangeDeclareAsync(ExchangeMuertos, ExchangeType.Direct, durable: true, cancellationToken: ct);

        // Los mensajes que fallan van a una cola de "muertos" para revisión manual.
        await canal.QueueDeclareAsync($"{Cola}.dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await canal.QueueBindAsync($"{Cola}.dlq", ExchangeMuertos, routingKey: Cola, cancellationToken: ct);

        await canal.QueueDeclareAsync(Cola, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = ExchangeMuertos,
                ["x-dead-letter-routing-key"] = Cola
            },
            cancellationToken: ct);
        foreach (var rk in RoutingKeys)
            await canal.QueueBindAsync(Cola, TiposEvento.Exchange, rk, cancellationToken: ct);

        await canal.BasicQosAsync(0, 10, false, ct);

        var consumidor = new AsyncEventingBasicConsumer(canal);
        consumidor.ReceivedAsync += async (_, ea) =>
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            try
            {
                await ProcesarAsync(ea.RoutingKey, json, ct);
                await canal.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error procesando {RoutingKey}; el mensaje va a {Cola}.dlq", ea.RoutingKey, Cola);
                await canal.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            }
        };

        await canal.BasicConsumeAsync(Cola, autoAck: false, consumer: consumidor, cancellationToken: ct);
        log.LogInformation("Escuchando cola {Cola} ({Keys})", Cola, string.Join(", ", RoutingKeys));

        // Mantener vivo mientras la conexión esté abierta.
        while (!ct.IsCancellationRequested && conexion.IsOpen)
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }
}

/// <summary>
/// Envía correos y SMS (simulados) por cada evento de cita y deja registro en dbo.Notificaciones.
/// </summary>
public sealed class ConsumidorNotificaciones(IConfiguration config, ILogger<ConsumidorNotificaciones> log)
    : ConsumidorBase(config, log)
{
    private static readonly CultureInfo Chile = new("es-CL");
    private static readonly TimeZoneInfo ZonaChile = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
    private readonly string _cadena = config.GetConnectionString("ReservaMed")!;

    protected override string Cola => "reservamed.notificaciones";
    protected override string[] RoutingKeys => ["cita.*"];

    protected override async Task ProcesarAsync(string routingKey, string json, CancellationToken ct)
    {
        var e = JsonSerializer.Deserialize<EventoCita>(json, Json)
                ?? throw new InvalidOperationException("Mensaje vacío");

        var cuando = e.FechaHoraInicio.ToString("dddd d 'de' MMMM 'a las' HH:mm", Chile);
        var (asunto, cuerpo) = routingKey switch
        {
            TiposEvento.CitaReservada => ("Su hora fue reservada",
                $"Hola {e.PacienteNombre}: reservó {e.Especialidad} con {e.ProfesionalNombre} el {cuando} en {e.Sede} ({e.DireccionSede}). " +
                "Llegue 15 minutos antes con su cédula de identidad para emitir el bono en admisión."),
            TiposEvento.CitaConfirmada => ("Su hora está confirmada y pagada",
                $"Hola {e.PacienteNombre}: su hora de {e.Especialidad} del {cuando} quedó confirmada. " +
                $"Bono folio {e.FolioBono?.ToString() ?? "-"}, copago pagado {Pesos(e.MontoPagado)}. Sólo preséntese en admisión."),
            TiposEvento.CitaCancelada => ("Su hora fue anulada",
                $"Hola {e.PacienteNombre}: su hora de {e.Especialidad} del {cuando} fue anulada." +
                (e.MontoPagado is > 0 ? $" Se reversó el cargo de {Pesos(e.MontoPagado)}." : "")),
            TiposEvento.CitaExpirada => ("Su reserva expiró",
                $"Hola {e.PacienteNombre}: no se completó el pago de su hora del {cuando}, por lo que se liberó. Puede reservar nuevamente."),
            TiposEvento.CitaAdmitida => ("Admisión registrada",
                $"Hola {e.PacienteNombre}: quedó admitido para su atención de {e.Especialidad}. Espere a ser llamado."),
            TiposEvento.CitaRecordatorio => ("Recordatorio de su hora de mañana",
                $"Hola {e.PacienteNombre}: le recordamos su hora de {e.Especialidad} con {e.ProfesionalNombre} el {cuando} en {e.Sede}. " +
                "Si no puede asistir, anúlela para liberar el cupo."),
            _ => ("Actualización de su cita", $"Su cita del {cuando} cambió a estado {e.Estado}.")
        };

        var ahora = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ZonaChile);
        await using var cn = new SqlConnection(_cadena);
        await cn.ExecuteAsync("""
            INSERT INTO dbo.Notificaciones (CitaId, Evento, Canal, Destino, Asunto, Cuerpo, EnviadaEn)
            VALUES (@CitaId, @Evento, 'EMAIL', @Email, @Asunto, @Cuerpo, @Ahora);
            """, new { e.CitaId, Evento = routingKey, Email = e.PacienteEmail, Asunto = asunto, Cuerpo = cuerpo, Ahora = ahora });

        log.LogInformation("📧 EMAIL a {Email}: {Asunto}", e.PacienteEmail, asunto);

        // SMS de recordatorio según el riesgo de inasistencia que calculó el ml-service:
        // un SMS cuesta dinero, así que se envía a los pacientes con riesgo MEDIO o ALTO
        // (o a todos si todavía no hay predicción). A los de riesgo ALTO se les pide confirmar.
        if (routingKey == TiposEvento.CitaRecordatorio && !string.IsNullOrWhiteSpace(e.PacienteTelefono))
        {
            var riesgo = await cn.ExecuteScalarAsync<string?>(
                "SELECT Nivel FROM dbo.PrediccionesCita WHERE CitaId = @CitaId", new { e.CitaId });
            if (riesgo == "BAJO")
            {
                log.LogInformation("Cita {CitaId}: riesgo BAJO, sólo email (sin SMS).", e.CitaId);
                return;
            }
            var sms = riesgo == "ALTO"
                ? $"ReservaMed: el {e.FechaHoraInicio:dd/MM} a las {e.FechaHoraInicio:HH:mm} tiene hora de {e.Especialidad} en {e.Sede}. ¿Asistirá? Si no, anúlela en el portal para liberar el cupo."
                : $"ReservaMed: hora {e.Especialidad} {e.FechaHoraInicio:dd/MM HH:mm} en {e.Sede}.";
            await cn.ExecuteAsync("""
                INSERT INTO dbo.Notificaciones (CitaId, Evento, Canal, Destino, Asunto, Cuerpo, EnviadaEn)
                VALUES (@CitaId, @Evento, 'SMS', @Tel, 'Recordatorio', @Sms, @Ahora);
                """, new { e.CitaId, Evento = routingKey, Tel = e.PacienteTelefono, Sms = sms, Ahora = ahora });
            log.LogInformation("📱 SMS a {Telefono}: {Sms}", e.PacienteTelefono, sms);
        }
    }

    private static string Pesos(int? monto) => (monto ?? 0).ToString("C0", Chile);
}

/// <summary>
/// Integración simulada con el HIS (ficha clínica): al admitir un paciente se envía
/// el Appointment en formato FHIR para que el médico lo vea en su agenda clínica.
/// </summary>
public sealed class ConsumidorHis(IConfiguration config, ILogger<ConsumidorHis> log) : ConsumidorBase(config, log)
{
    protected override string Cola => "reservamed.his";
    protected override string[] RoutingKeys => [TiposEvento.CitaAdmitida, TiposEvento.CitaCancelada];

    protected override Task ProcesarAsync(string routingKey, string json, CancellationToken ct)
    {
        var e = JsonSerializer.Deserialize<EventoCita>(json, Json)!;
        var status = routingKey == TiposEvento.CitaAdmitida ? "arrived" : "cancelled";
        log.LogInformation("🏥 [HIS mock] PUT Appointment/{Id} status={Status} paciente={Paciente} profesional={Profesional}",
            e.CitaId, status, e.PacienteNombre, e.ProfesionalNombre);
        return Task.CompletedTask;
    }
}
