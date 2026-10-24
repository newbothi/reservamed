using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace ReservaMed.IntegrationTests;

/// <summary>
/// Levanta SQL Server real en Docker (Testcontainers), ejecuta los scripts de db/ y arranca la API
/// en memoria. La comparten todas las pruebas de la colección "api".
/// RabbitMQ y el ml-service no se levantan: la API debe funcionar igual sin ellos.
/// </summary>
public sealed partial class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Secretos SOLO de prueba: largos, aleatorios y distintos de cualquier valor publicado.
    public const string ClaveJwtPrueba = "pruebas-integracion-7Gq2xV9mK4tR8wZ1nB6cY3hL5sJ0dF";
    private const string ApiKeyMlPrueba = "ml-pruebas-integracion-Xp4Lk8Wq2Zr6";
    private const string ClaveRabbitPrueba = "rabbit-pruebas-Nf7Tq3Vb";
    // A5: la API de pruebas entra como rm_api (no sa), así las pruebas verifican los permisos mínimos.
    private const string ClaveSqlApi = "Rm1!pruebas-Api-9xQ2";
    private static readonly Dictionary<string, string> VariablesUsuarios = new()
    {
        ["API_PASS"] = ClaveSqlApi,
        ["WORKER_PASS"] = "Rm1!pruebas-Worker-4kT8",
        ["ML_PASS"] = "Rm1!pruebas-Ml-6pZ3",
        ["ML_SEED_PASS"] = "Rm1!pruebas-Seed-2wJ7",
    };

    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    /// <summary>Cadena con la que corre la API (usuario rm_api).</summary>
    public string CadenaApi { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();
        await EjecutarScriptsAsync();

        var cs = new SqlConnectionStringBuilder(_sql.GetConnectionString())
        {
            InitialCatalog = "ReservaMed",
            UserID = "rm_api",
            Password = ClaveSqlApi
        }.ConnectionString;

        // Variables de entorno: la API las lee en Program.cs antes de construir el host,
        // así que se definen antes de que la fábrica la arranque.
        CadenaApi = cs;
        Environment.SetEnvironmentVariable("ConnectionStrings__ReservaMed", cs);
        Environment.SetEnvironmentVariable("Jwt__Key", ClaveJwtPrueba);
        Environment.SetEnvironmentVariable("Ml__ApiKey", ApiKeyMlPrueba);
        Environment.SetEnvironmentVariable("Ml__Url", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("RabbitMq__Host", "127.0.0.1");
        Environment.SetEnvironmentVariable("RabbitMq__Usuario", "pruebas");
        Environment.SetEnvironmentVariable("RabbitMq__Clave", ClaveRabbitPrueba);
        // Testing permite el simulador de Webpay; Production lo rechaza al arrancar (C2).
        Environment.SetEnvironmentVariable("Webpay__Modo", "Simulado");
        Environment.SetEnvironmentVariable("Webpay__UrlPagoMock", "http://localhost/webpay-mock/pagar");
        Environment.SetEnvironmentVariable("Webpay__ReturnUrl", "http://localhost/pago/retorno");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Testing");   // ni Development ni Production: valida secretos en modo estricto

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _sql.DisposeAsync();
    }

    /// <summary>Ejecuta db/01..04 separando los lotes por GO y reemplazando $(VARIABLE), igual que sqlcmd.</summary>
    private async Task EjecutarScriptsAsync()
    {
        var carpetaDb = BuscarCarpetaDb();
        await using var cn = new SqlConnection(_sql.GetConnectionString());
        await cn.OpenAsync();

        foreach (var archivo in Directory.GetFiles(carpetaDb, "0*.sql").Order())
        {
            var texto = await File.ReadAllTextAsync(archivo);
            foreach (var (nombre, valor) in VariablesUsuarios)
                texto = texto.Replace($"$({nombre})", valor);
            foreach (var lote in SeparadorGo().Split(texto).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                await using var cmd = new SqlCommand(lote, cn) { CommandTimeout = 120 };
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    private static string BuscarCarpetaDb()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db")))
            dir = dir.Parent;
        return dir is null
            ? throw new DirectoryNotFoundException("No se encontró la carpeta db/ del repositorio.")
            : Path.Combine(dir.FullName, "db");
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SeparadorGo();
}

[CollectionDefinition("api")]
public sealed class ColeccionApi : ICollectionFixture<ApiFactory>;
