using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using ReservaMed.Api.Common;
using ReservaMed.Api.Modules.Citas;
using ReservaMed.Contracts;

namespace ReservaMed.Api.Jobs;

public sealed class OpcionesRabbit
{
    public string Host { get; set; } = "localhost";
    public string Usuario { get; set; } = "";
    public string Clave { get; set; } = "";
}

/// <summary>Publicador hacia RabbitMQ con conexión perezosa y reconexión simple.</summary>
public sealed class PublicadorRabbit(IOptions<OpcionesRabbit> opciones, ILogger<PublicadorRabbit> log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _conexion;
    private IChannel? _canal;

    public async Task PublicarAsync(string routingKey, string json, string messageId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var canal = await CanalAsync(ct);
            var props = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = messageId,
                Type = routingKey
            };
            await canal.BasicPublishAsync(TiposEvento.Exchange, routingKey, mandatory: false, props,
                Encoding.UTF8.GetBytes(json), ct);
        }
        catch
        {
            await CerrarAsync();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IChannel> CanalAsync(CancellationToken ct)
    {
        if (_canal is { IsOpen: true }) return _canal;
        await CerrarAsync();

        var o = opciones.Value;
        var factory = new ConnectionFactory { HostName = o.Host, UserName = o.Usuario, Password = o.Clave, ClientProvidedName = "reservamed-api" };
        _conexion = await factory.CreateConnectionAsync(ct);
        _canal = await _conexion.CreateChannelAsync(cancellationToken: ct);
        await _canal.ExchangeDeclareAsync(TiposEvento.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        log.LogInformation("Conectado a RabbitMQ en {Host}", o.Host);
        return _canal;
    }

    private async Task CerrarAsync()
    {
        try { if (_canal is not null) await _canal.DisposeAsync(); } catch { /* ignorar */ }
        try { if (_conexion is not null) await _conexion.DisposeAsync(); } catch { /* ignorar */ }
        _canal = null;
        _conexion = null;
    }

    public async ValueTask DisposeAsync() => await CerrarAsync();
}

/// <summary>
/// Lee la tabla Outbox y publica los eventos en RabbitMQ.
/// Si RabbitMQ está caído, los eventos esperan en la base y se reintentan: no se pierden.
/// </summary>
public sealed class PublicadorOutbox(ISqlConnectionFactory db, PublicadorRabbit rabbit, ILogger<PublicadorOutbox> log) : BackgroundService
{
    private sealed record Pendiente(long Id, string Tipo, string Payload);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var procesados = 0;
            try
            {
                procesados = await ProcesarLoteAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Outbox: no se pudo publicar ({Error}). Se reintentará.", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            if (procesados == 0) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task<int> ProcesarLoteAsync(CancellationToken ct)
    {
        await using var cn = db.Crear();
        await cn.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        // UPDLOCK + READPAST: si hay varias instancias de la API, cada una toma filas distintas.
        var lote = (await cn.QueryAsync<Pendiente>("""
            SELECT TOP 50 Id, Tipo, Payload FROM dbo.Outbox WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE ProcesadoEn IS NULL AND Intentos < 20
            ORDER BY Id
            """, transaction: tx)).ToList();

        foreach (var e in lote)
        {
            try
            {
                await rabbit.PublicarAsync(e.Tipo, e.Payload, e.Id.ToString(), ct);
                await cn.ExecuteAsync("UPDATE dbo.Outbox SET ProcesadoEn = SYSDATETIME() WHERE Id = @Id", new { e.Id }, tx);
            }
            catch (Exception ex)
            {
                await cn.ExecuteAsync("UPDATE dbo.Outbox SET Intentos = Intentos + 1, UltimoError = LEFT(@Err, 500) WHERE Id = @Id",
                    new { e.Id, Err = ex.Message }, tx);
                await tx.CommitAsync(ct);
                throw;
            }
        }

        await tx.CommitAsync(ct);
        return lote.Count;
    }
}

/// <summary>
/// Libera reservas temporales no pagadas: si el paciente no paga en N minutos, la hora vuelve a quedar libre.
/// </summary>
public sealed class ExpiracionReservasJob(ISqlConnectionFactory db, IReloj reloj, ILogger<ExpiracionReservasJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var cn = db.Crear();
                await cn.OpenAsync(ct);
                await using var tx = await cn.BeginTransactionAsync(ct);

                var ids = (await cn.QueryAsync<int>("""
                    UPDATE dbo.Citas
                    SET Estado = 'EXPIRADA', OcupaCupo = 0, ExpiraEn = NULL
                    OUTPUT INSERTED.Id
                    WHERE Estado = 'PENDIENTE_PAGO' AND ExpiraEn < @Ahora
                    """, new { reloj.Ahora }, tx)).ToList();

                if (ids.Count > 0)
                {
                    await cn.ExecuteAsync("UPDATE dbo.Pagos SET Estado = 'RECHAZADO' WHERE CitaId IN @Ids AND Estado = 'PENDIENTE'",
                        new { Ids = ids }, tx);
                    foreach (var id in ids)
                        await EventosCita.PublicarAsync(cn, tx, TiposEvento.CitaExpirada, id, motivo: "No se completó el pago a tiempo");
                    log.LogInformation("Reservas expiradas: {Ids}", string.Join(", ", ids));
                }
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Job de expiración falló: {Error}", ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

/// <summary>Programa recordatorios 24 horas antes de la cita.</summary>
public sealed class RecordatoriosJob(ISqlConnectionFactory db, IReloj reloj, ILogger<RecordatoriosJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                await using var cn = db.Crear();
                await cn.OpenAsync(ct);
                await using var tx = await cn.BeginTransactionAsync(ct);
                var ahora = reloj.Ahora;

                var ids = (await cn.QueryAsync<int>("""
                    UPDATE dbo.Citas SET RecordatorioEnviado = 1
                    OUTPUT INSERTED.Id
                    WHERE RecordatorioEnviado = 0 AND Estado IN ('RESERVADA','CONFIRMADA')
                      AND FechaHoraInicio > @Ahora AND FechaHoraInicio <= DATEADD(HOUR, 24, @Ahora)
                    """, new { Ahora = ahora }, tx)).ToList();

                foreach (var id in ids)
                    await EventosCita.PublicarAsync(cn, tx, TiposEvento.CitaRecordatorio, id);
                await tx.CommitAsync(ct);

                if (ids.Count > 0) log.LogInformation("Recordatorios programados: {Cantidad}", ids.Count);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Job de recordatorios falló: {Error}", ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
