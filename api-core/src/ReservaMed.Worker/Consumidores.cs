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
/// A9: el evento trae solo el id; los datos de contacto se leen aquí con el usuario rm_worker
/// (solo las columnas necesarias). En logs y en el registro de envío el destino va enmascarado
/// y el nombre del paciente no se guarda.
/// </summary>
public sealed class ConsumidorNotificaciones(IConfiguration config, ILogger<ConsumidorNotificaciones> log)
    : ConsumidorBase(config, log)
{
    private static readonly CultureInfo Chile = new("es-CL");
    private static readonly TimeZoneInfo ZonaChile = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
    private readonly string _cadena = config.GetConnectionString("ReservaMed")!;

    protected override string Cola => "reservamed.notificaciones";
    protected override string[] RoutingKeys => ["cita.*"];

    private sealed class DatosCita
    {
        public int CitaId { get; set; }
        public string Estado { get; set; } = "";
        public string PacienteNombre { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Telefono { get; set; }
        public string ProfesionalNombre { get; set; } = "";
        public string Especialidad { get; set; } = "";
        public string Sede { get; set; } = "";
        public string DireccionSede { get; set; } = "";
        public DateTime FechaHoraInicio { get; set; }
        public long? Folio { get; set; }
        public int? MontoPagado { get; set; }
        public int? MontoReversado { get; set; }
    }

    protected override async Task ProcesarAsync(string routingKey, string json, CancellationToken ct)
    {
        var evento = JsonSerializer.Deserialize<EventoCita>(json, Json)
                     ?? throw new InvalidOperationException("Mensaje vacío");

        await using var cn = new SqlConnection(_cadena);
        var e = await cn.QuerySingleOrDefaultAsync<DatosCita>("""
            SELECT c.Id AS CitaId, c.Estado, pa.Nombres AS PacienteNombre, pa.Email, pa.Telefono,
                   pr.Nombres + ' ' + pr.ApellidoPaterno AS ProfesionalNombre, es.Nombre AS Especialidad,
                   s.Nombre AS Sede, s.Direccion + ', ' + s.Comuna AS DireccionSede, c.FechaHoraInicio,
                   (SELECT TOP 1 b.Folio FROM dbo.Bonos b WHERE b.CitaId = c.Id ORDER BY b.Id DESC) AS Folio,
                   (SELECT SUM(p.Monto) FROM dbo.Pagos p WHERE p.CitaId = c.Id AND p.Estado = 'AUTORIZADO') AS MontoPagado,
                   (SELECT SUM(p.Monto) FROM dbo.Pagos p
                     WHERE p.CitaId = c.Id AND p.Medio = 'WEBPAY' AND p.Estado = 'ANULADO' AND p.BonoId IS NOT NULL) AS MontoReversado
            FROM dbo.Citas c
            JOIN dbo.Pacientes pa      ON pa.Id = c.PacienteId
            JOIN dbo.Profesionales pr  ON pr.Id = c.ProfesionalId
            JOIN dbo.Especialidades es ON es.Id = pr.EspecialidadId
            JOIN dbo.Sedes s           ON s.Id = c.SedeId
            WHERE c.Id = @Id
            """, new { Id = evento.CitaId });
        if (e is null)
        {
            log.LogWarning("Cita {CitaId} no existe; se descarta el evento {Evento}.", evento.CitaId, routingKey);
            return;
        }

        var cuando = e.FechaHoraInicio.ToString("dddd d 'de' MMMM 'a las' HH:mm", Chile);
        // El saludo lleva el nombre solo en el correo enviado; el registro guarda "{paciente}".
        string Saludo(string nombre) => $"Hola {nombre}: ";
        Func<string, (string Asunto, string Cuerpo)> plantilla = nombre => routingKey switch
        {
            TiposEvento.CitaReservada => ("Su hora fue reservada",
                Saludo(nombre) + $"reservó {e.Especialidad} con {e.ProfesionalNombre} el {cuando} en {e.Sede} ({e.DireccionSede}). " +
                "Llegue 15 minutos antes con su cédula de identidad para emitir el bono en admisión."),
            TiposEvento.CitaConfirmada => ("Su hora está confirmada y pagada",
                Saludo(nombre) + $"su hora de {e.Especialidad} del {cuando} quedó confirmada. " +
                $"Bono folio {e.Folio?.ToString() ?? "-"}, copago pagado {Pesos(e.MontoPagado)}. Sólo preséntese en admisión."),
            TiposEvento.CitaCancelada => ("Su hora fue anulada",
                Saludo(nombre) + $"su hora de {e.Especialidad} del {cuando} fue anulada." +
                (e.MontoReversado is > 0 ? $" Se reversó el cargo de {Pesos(e.MontoReversado)}." : "")),
            TiposEvento.CitaExpirada => ("Su reserva expiró",
                Saludo(nombre) + $"no se completó el pago de su hora del {cuando}, por lo que se liberó. Puede reservar nuevamente."),
            TiposEvento.CitaAdmitida => ("Admisión registrada",
                Saludo(nombre) + $"quedó admitido para su atención de {e.Especialidad}. Espere a ser llamado."),
            TiposEvento.CitaRecordatorio => ("Recordatorio de su hora de mañana",
                Saludo(nombre) + $"le recordamos su hora de {e.Especialidad} con {e.ProfesionalNombre} el {cuando} en {e.Sede}. " +
                "Si no puede asistir, anúlela para liberar el cupo."),
            _ => ("Actualización de su cita", $"Su cita del {cuando} cambió a estado {e.Estado}.")
        };

        var (asunto, _) = plantilla(e.PacienteNombre);   // lo que se enviaría al paciente
        var (_, cuerpoRegistro) = plantilla("{paciente}");  // lo que queda guardado

        var ahora = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ZonaChile);
        await cn.ExecuteAsync("""
            INSERT INTO dbo.Notificaciones (CitaId, Evento, Canal, Destino, Asunto, Cuerpo, EnviadaEn)
            VALUES (@CitaId, @Evento, 'EMAIL', @Destino, @Asunto, @Cuerpo, @Ahora);
            """, new { e.CitaId, Evento = routingKey, Destino = Privacidad.Correo(e.Email), Asunto = asunto, Cuerpo = cuerpoRegistro, Ahora = ahora });

        log.LogInformation("📧 EMAIL a {Destino}: {Asunto} (cita {CitaId})", Privacidad.Correo(e.Email), asunto, e.CitaId);

        // SMS de recordatorio según el riesgo de inasistencia que calculó el ml-service:
        // un SMS cuesta dinero, así que se envía a los pacientes con riesgo MEDIO o ALTO
        // (o a todos si todavía no hay predicción). A los de riesgo ALTO se les pide confirmar.
        if (routingKey == TiposEvento.CitaRecordatorio && !string.IsNullOrWhiteSpace(e.Telefono))
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
                VALUES (@CitaId, @Evento, 'SMS', @Destino, 'Recordatorio', @Sms, @Ahora);
                """, new { e.CitaId, Evento = routingKey, Destino = Privacidad.Telefono(e.Telefono), Sms = sms, Ahora = ahora });
            log.LogInformation("📱 SMS a {Destino} (cita {CitaId})", Privacidad.Telefono(e.Telefono), e.CitaId);
        }
    }

    private static string Pesos(int? monto) => (monto ?? 0).ToString("C0", Chile);
}

/// <summary>
/// Integración simulada con el HIS (ficha clínica): al admitir un paciente se envía
/// el Appointment en formato FHIR para que el médico lo vea en su agenda clínica.
/// A9: el log identifica la cita por id, sin nombres.
/// </summary>
public sealed class ConsumidorHis(IConfiguration config, ILogger<ConsumidorHis> log) : ConsumidorBase(config, log)
{
    protected override string Cola => "reservamed.his";
    protected override string[] RoutingKeys => [TiposEvento.CitaAdmitida, TiposEvento.CitaCancelada];

    protected override Task ProcesarAsync(string routingKey, string json, CancellationToken ct)
    {
        var e = JsonSerializer.Deserialize<EventoCita>(json, Json)!;
        var status = routingKey == TiposEvento.CitaAdmitida ? "arrived" : "cancelled";
        log.LogInformation("🏥 [HIS mock] PUT Appointment/{Id} status={Status}", e.CitaId, status);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A9: retención limitada. Los registros de envío se borran pasado el plazo (por defecto 90 días).
/// </summary>
public sealed class RetencionNotificaciones(IConfiguration config, ILogger<RetencionNotificaciones> log) : BackgroundService
{
    private readonly string _cadena = config.GetConnectionString("ReservaMed")!;
    private readonly int _dias = config.GetValue("Privacidad:RetencionNotificacionesDias", 90);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                await using var cn = new SqlConnection(_cadena);
                var borradas = await cn.ExecuteAsync(
                    "DELETE FROM dbo.Notificaciones WHERE EnviadaEn < DATEADD(DAY, -@Dias, SYSDATETIME())", new { Dias = _dias });
                if (borradas > 0) log.LogInformation("Retención: {Cantidad} notificaciones de más de {Dias} días eliminadas.", borradas, _dias);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Retención de notificaciones falló: {Error}", ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
