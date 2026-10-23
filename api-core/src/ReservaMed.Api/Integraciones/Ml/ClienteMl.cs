using System.Net;
using System.Text.Json;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Integraciones.Ml;

public sealed class OpcionesMl
{
    public string Url { get; set; } = "http://localhost:8000";
    public string ApiKey { get; set; } = "";
    /// <summary>Tiempo máximo para predicciones y consultas. El entrenamiento tiene su propio límite.</summary>
    public int TimeoutSegundos { get; set; } = 3;
}

/// <summary>
/// Cliente HTTP hacia el ml-service (Python/FastAPI), que sólo es accesible en la red interna.
/// Las respuestas se reenvían como JSON tal cual: la API Core no duplica los modelos de Python.
/// Si el servicio no responde se lanza un error 503 controlado; quien llama decide si eso
/// bloquea (pantalla de admin) o se ignora (predicción opcional).
/// </summary>
public sealed class ClienteMl(HttpClient http, Microsoft.Extensions.Options.IOptions<OpcionesMl> opciones, ILogger<ClienteMl> log)
{
    private readonly TimeSpan _corto = TimeSpan.FromSeconds(opciones.Value.TimeoutSegundos);
    private static readonly TimeSpan Entrenamiento = TimeSpan.FromMinutes(3);

    public Task<JsonElement?> ModeloActualAsync(CancellationToken ct = default) =>
        EnviarAsync(HttpMethod.Get, "modelos/actual", null, _corto, ct);

    public Task<JsonElement?> HistorialModelosAsync(CancellationToken ct = default) =>
        EnviarAsync(HttpMethod.Get, "modelos", null, _corto, ct);

    public Task<JsonElement?> EntrenarAsync(CancellationToken ct = default) =>
        EnviarAsync(HttpMethod.Post, "modelos/entrenar", null, Entrenamiento, ct);

    public Task<JsonElement?> DemandaAsync(int semanas, CancellationToken ct = default) =>
        EnviarAsync(HttpMethod.Get, $"predicciones/demanda?semanas={semanas}", null, TimeSpan.FromSeconds(30), ct);

    public Task<JsonElement?> PredecirAsync(int citaId, CancellationToken ct = default) =>
        EnviarAsync(HttpMethod.Post, "predicciones/inasistencia", new { citaId, guardar = true }, _corto, ct);

    private async Task<JsonElement?> EnviarAsync(HttpMethod metodo, string ruta, object? cuerpo, TimeSpan limite, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limite);
        using var req = new HttpRequestMessage(metodo, ruta);
        if (cuerpo is not null) req.Content = JsonContent.Create(cuerpo);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("ml-service no responde en {Ruta}: {Error}", ruta, ex.Message);
            throw new ReglaNegocioException("El servicio de machine learning no está disponible en este momento.",
                StatusCodes.Status503ServiceUnavailable, "ML_NO_DISPONIBLE");
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;

            var texto = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var detalle = Detalle(texto) ?? $"Error {(int)resp.StatusCode} del servicio de ML.";
                var status = resp.StatusCode switch
                {
                    HttpStatusCode.UnprocessableEntity => StatusCodes.Status422UnprocessableEntity,
                    HttpStatusCode.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
                    _ => StatusCodes.Status502BadGateway
                };
                throw new ReglaNegocioException(detalle, status, "ML_ERROR");
            }
            return JsonDocument.Parse(texto).RootElement.Clone();
        }
    }

    /// <summary>FastAPI devuelve los errores como {"detail": "..."}.</summary>
    private static string? Detalle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
