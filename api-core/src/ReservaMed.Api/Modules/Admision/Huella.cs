using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Modules.Admision;

/// <summary>Un lector biométrico instalado en el mesón de una sede.</summary>
public sealed class LectorHuella
{
    public string Id { get; set; } = "";
    public int SedeId { get; set; }
}

/// <summary>Sección "Huella" de la configuración.</summary>
public sealed class OpcionesHuella
{
    /// <summary>Clave maestra de los lectores. Cada lector firma con HMAC(maestra, "lector:" + Id).</summary>
    public string Clave { get; set; } = "";
    /// <summary>Vida del desafío y del comprobante: la huella se captura en el momento.</summary>
    public int VigenciaSegundos { get; set; } = 120;
    public List<LectorHuella> Lectores { get; set; } = [];
}

public sealed record DesafioHuellaDto(string Nonce, int CitaId, string Rut, int SedeId, DateTime ExpiraUtc);

/// <summary>Lo que el lector afirma haber leído, firmado por él.</summary>
public sealed record LecturaHuella(
    [property: JsonPropertyName("lector")] string Lector,
    [property: JsonPropertyName("sedeId")] int SedeId,
    [property: JsonPropertyName("citaId")] int CitaId,
    [property: JsonPropertyName("rut")] string Rut,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("emitido")] long Emitido,
    [property: JsonPropertyName("resultado")] string Resultado);

/// <summary>
/// Hallazgo C3: antes el navegador mandaba "huellaVerificada: true" y la API le creía, así que se
/// podía emitir un bono sin el paciente presente. Ahora la verificación la afirma el LECTOR:
///  1. La API entrega un desafío de un solo uso (nonce) para esa cita, ese usuario y ese RUT.
///  2. El lector de la sede captura la huella y firma {lector, sede, cita, rut, nonce, hora, resultado}
///     con su clave (en producción, la del dispositivo o la de I-Med; aquí, el simulador del BFF).
///  3. La API valida la firma, que el lector sea de la sede de la cita, que cita, RUT y nonce
///     coincidan, que sea reciente y que el nonce no se haya usado. El navegador solo transporta
///     el comprobante: no puede fabricarlo ni reusarlo para otra cita o paciente.
/// </summary>
public sealed class VerificadorHuella(IOptions<OpcionesHuella> opciones, TimeProvider tiempo)
{
    public const string Coincide = "COINCIDE";
    public const string NoCoincide = "NO_COINCIDE";

    private readonly OpcionesHuella _o = opciones.Value;
    private readonly ConcurrentDictionary<string, Pendiente> _desafios = new();

    private sealed record Pendiente(int CitaId, int UsuarioId, string Rut, int SedeId, DateTimeOffset Expira);

    public DesafioHuellaDto CrearDesafio(int citaId, int usuarioId, string rut, int sedeId)
    {
        var ahora = tiempo.GetUtcNow();
        foreach (var (k, v) in _desafios)
            if (v.Expira < ahora) _desafios.TryRemove(k, out _);

        var nonce = Base64Url(RandomNumberGenerator.GetBytes(18));
        var expira = ahora.AddSeconds(_o.VigenciaSegundos);
        _desafios[nonce] = new Pendiente(citaId, usuarioId, rut, sedeId, expira);
        return new DesafioHuellaDto(nonce, citaId, rut, sedeId, expira.UtcDateTime);
    }

    /// <summary>Valida el comprobante y consume su desafío. Lanza 422 si no sirve.</summary>
    public void Exigir(string? comprobante, int citaId, int usuarioId, string rut, int sedeId)
    {
        if (string.IsNullOrWhiteSpace(comprobante))
            throw new ReglaNegocioException("Falta la lectura de huella del beneficiario.", codigo: "HUELLA_REQUERIDA");

        var lectura = Leer(comprobante) ?? throw Invalido();

        // Un desafío sirve una sola vez (también si la huella no coincidió: hay que leer de nuevo).
        if (!_desafios.TryRemove(lectura.Nonce, out var d)) throw Invalido();

        var ahora = tiempo.GetUtcNow();
        var emitido = DateTimeOffset.FromUnixTimeSeconds(lectura.Emitido);
        var lector = _o.Lectores.FirstOrDefault(l => l.Id == lectura.Lector);
        var valido =
            d.Expira >= ahora &&
            d.CitaId == citaId && d.UsuarioId == usuarioId && d.Rut == rut && d.SedeId == sedeId &&
            lectura.CitaId == citaId && lectura.Rut == rut && lectura.SedeId == sedeId &&
            lector is not null && lector.SedeId == sedeId &&
            emitido >= ahora.AddSeconds(-_o.VigenciaSegundos) && emitido <= ahora.AddSeconds(30);
        if (!valido) throw Invalido();

        if (lectura.Resultado != Coincide)
            throw new ReglaNegocioException("La huella no coincide con el beneficiario. Repita la lectura.", codigo: "HUELLA_NO_COINCIDE");
    }

    /// <summary>Verifica formato y firma. Devuelve null si el comprobante no es auténtico.</summary>
    private LecturaHuella? Leer(string comprobante)
    {
        var partes = comprobante.Split('.');
        if (partes.Length != 3 || partes[0] != "v1" || comprobante.Length > 2000) return null;
        try
        {
            var json = Convert.FromBase64String(DeBase64Url(partes[1]));
            var lectura = JsonSerializer.Deserialize<LecturaHuella>(json);
            if (lectura is null || _o.Lectores.All(l => l.Id != lectura.Lector)) return null;

            var esperada = Firma(ClaveLector(_o.Clave, lectura.Lector), $"v1.{partes[1]}");
            var recibida = Convert.FromBase64String(DeBase64Url(partes[2]));
            return CryptographicOperations.FixedTimeEquals(esperada, recibida) ? lectura : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Firma una lectura como lo hace un lector (lo usan las pruebas; el simulador está en el BFF).</summary>
    public static string Firmar(string claveMaestra, LecturaHuella lectura)
    {
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(lectura));
        return $"v1.{payload}.{Base64Url(Firma(ClaveLector(claveMaestra, lectura.Lector), $"v1.{payload}"))}";
    }

    private static byte[] ClaveLector(string maestra, string lectorId) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(maestra), Encoding.UTF8.GetBytes($"lector:{lectorId}"));

    private static byte[] Firma(byte[] clave, string texto) => HMACSHA256.HashData(clave, Encoding.UTF8.GetBytes(texto));

    private static ReglaNegocioException Invalido() =>
        new("El comprobante del lector de huella no es válido o ya se usó. Repita la lectura.", codigo: "HUELLA_INVALIDA");

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DeBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }
}
