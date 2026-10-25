using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace ReservaMed.Api.Common;

/// <summary>Crea conexiones a SQL Server. Dapper trabaja sobre estas conexiones.</summary>
public interface ISqlConnectionFactory
{
    SqlConnection Crear();
}

public sealed class SqlConnectionFactory(IConfiguration config) : ISqlConnectionFactory
{
    // F5: la contraseña llega aparte (Sql:Clave, Docker secret); la cadena fija el certificado del servidor.
    private readonly string _cadena = ReservaMed.Contracts.SecretosArchivo.CadenaSql(
        config.GetConnectionString("ReservaMed") ?? throw new InvalidOperationException("Falta ConnectionStrings:ReservaMed"),
        config["Sql:Clave"]);

    public SqlConnection Crear() => new(_cadena);
}

/// <summary>
/// Hora oficial de Chile. El servidor puede correr en UTC (Docker), pero la agenda
/// de una clínica siempre se maneja en hora local.
/// </summary>
public interface IReloj
{
    DateTime Ahora { get; }
    DateTime Hoy { get; }
}

public sealed class RelojChile : IReloj
{
    private static readonly TimeZoneInfo ZonaChile =
        TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");

    public DateTime Ahora =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ZonaChile), DateTimeKind.Unspecified);

    public DateTime Hoy => Ahora.Date;
}

/// <summary>Opciones de reglas de negocio (sección "Reglas" de appsettings).</summary>
public sealed class ReglasNegocio
{
    public int MinutosReservaTemporal { get; set; } = 10;
    public int HorasMinimasParaAnular { get; set; } = 2;
    public int MinutosAnticipacionMinima { get; set; } = 60;
    public int DiasMaximosConsulta { get; set; } = 21;
    /// <summary>A8: reservas con pago online sin pagar que puede tener un paciente a la vez.</summary>
    public int MaxReservasPendientesPago { get; set; } = 2;
}

/// <summary>Error de negocio que se devuelve al cliente como ProblemDetails.</summary>
public sealed class ReglaNegocioException(string mensaje, int status = StatusCodes.Status422UnprocessableEntity, string? codigo = null)
    : Exception(mensaje)
{
    public int Status { get; } = status;
    public string? Codigo { get; } = codigo;

    public static ReglaNegocioException NoEncontrado(string que) =>
        new($"{que} no existe.", StatusCodes.Status404NotFound, "NO_ENCONTRADO");

    public static ReglaNegocioException Conflicto(string mensaje) =>
        new(mensaje, StatusCodes.Status409Conflict, "CONFLICTO");

    public static ReglaNegocioException Prohibido(string mensaje = "No tiene permiso para esta operación.") =>
        new(mensaje, StatusCodes.Status403Forbidden, "PROHIBIDO");
}

/// <summary>Patrón Outbox: guarda el evento en la misma transacción que el cambio.</summary>
public static class Outbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task AgregarAsync(IDbConnection cn, IDbTransaction tx, string tipo, object payload) =>
        cn.ExecuteAsync(
            "INSERT INTO dbo.Outbox (Tipo, Payload) VALUES (@Tipo, @Payload);",
            new { Tipo = tipo, Payload = JsonSerializer.Serialize(payload, Json) },
            tx);
}

public static class SqlErrores
{
    /// <summary>2601/2627 = violación de índice único o clave primaria.</summary>
    public static bool EsDuplicado(SqlException ex) => ex.Number is 2601 or 2627;
}
