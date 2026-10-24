using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using ReservaMed.Api.Common;
using ReservaMed.Api.Integraciones.Bonos;
using ReservaMed.Api.Integraciones.Ml;
using ReservaMed.Api.Integraciones.Pagos;
using ReservaMed.Api.Jobs;
using ReservaMed.Api.Modules.Admision;
using ReservaMed.Api.Modules.Agenda;
using ReservaMed.Api.Modules.Auth;
using ReservaMed.Api.Modules.Citas;
using ReservaMed.Api.Modules.Pagos;
using ReservaMed.Contracts;

var builder = WebApplication.CreateBuilder(args);

// ---------- Secretos (C1): sin valores por defecto en el código ----------
ValidacionSecretos.Exigir(
[
    new("Jwt:Key", builder.Configuration["Jwt:Key"], ValidacionSecretos.LargoMinimoClaveJwt),
    new("Ml:ApiKey", builder.Configuration["Ml:ApiKey"], ValidacionSecretos.LargoMinimoApiKey),
    new("RabbitMq:Clave", builder.Configuration["RabbitMq:Clave"], ValidacionSecretos.LargoMinimoClave, ToleradoEnDesarrollo: true)
], builder.Environment.IsDevelopment());

// ---------- Webpay (C2): el simulador no existe fuera de Development/Testing ----------
var opcionesWebpay = builder.Configuration.GetSection("Webpay").Get<OpcionesWebpay>() ?? new OpcionesWebpay();
var problemasWebpay = opcionesWebpay.Revisar(builder.Environment.EnvironmentName);
if (problemasWebpay.Count > 0)
    throw new InvalidOperationException("Configuración de Webpay inválida:" + Environment.NewLine +
        string.Join(Environment.NewLine, problemasWebpay.Select(p => " - " + p)));
var webpaySimulado = opcionesWebpay.Modo == ModoWebpay.Simulado;

// ---------- RabbitMQ (M9): AMQPS obligatorio en producción ----------
var problemasRabbit = (builder.Configuration.GetSection("RabbitMq").Get<OpcionesConexionRabbit>() ?? new()).Revisar(builder.Environment.IsProduction());
if (problemasRabbit.Count > 0)
    throw new InvalidOperationException(string.Join(Environment.NewLine, problemasRabbit));

// ---------- Configuración ----------
builder.Services.Configure<ReglasNegocio>(builder.Configuration.GetSection("Reglas"));
builder.Services.Configure<OpcionesJwt>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<OpcionesConexionRabbit>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<OpcionesWebpay>(builder.Configuration.GetSection("Webpay"));
builder.Services.Configure<OpcionesPos>(builder.Configuration.GetSection("Pos"));

// ---------- Infraestructura ----------
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<IReloj, RelojChile>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditoria, ServicioAuditoria>();

// ---------- Integraciones externas (mocks intercambiables por clientes reales) ----------
builder.Services.AddSingleton<IEmisorBonos, ImedMock>();
builder.Services.AddSingleton<ITerminalPos, PosMock>();
if (webpaySimulado)
{
    builder.Services.AddSingleton<WebpayMock>();
    builder.Services.AddSingleton<IPasarelaWebpay>(sp => sp.GetRequiredService<WebpayMock>());
}
else
{
    builder.Services.AddHttpClient<IPasarelaWebpay, WebpayTransbank>((sp, http) =>
        WebpayTransbank.Configurar(http, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpcionesWebpay>>().Value));
}

// ---------- Machine learning (ml-service en Python, sólo red interna) ----------
builder.Services.Configure<OpcionesMl>(builder.Configuration.GetSection("Ml"));
builder.Services.AddHttpClient<ClienteMl>((sp, http) =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpcionesMl>>().Value;
    http.BaseAddress = new Uri(o.Url.TrimEnd('/') + "/");
    http.DefaultRequestHeaders.Add("X-Api-Key", o.ApiKey);
    http.Timeout = TimeSpan.FromMinutes(4); // el límite real lo pone cada llamada
});

// ---------- Módulos ----------
builder.Services.AddSingleton<ServicioJwt>();
builder.Services.AddScoped<ServicioAgenda>();
builder.Services.AddScoped<ServicioCitas>();
builder.Services.AddScoped<ServicioValorizacion>();
builder.Services.AddScoped<ServicioAdmision>();
builder.Services.AddScoped<ServicioPagosWeb>();

// ---------- Mensajería y procesos en segundo plano ----------
builder.Services.AddSingleton<PublicadorRabbit>();
builder.Services.AddHostedService<PublicadorOutbox>();
builder.Services.AddHostedService<ExpiracionReservasJob>();
builder.Services.AddHostedService<RecordatoriosJob>();

// ---------- Web ----------
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .ConfigureApplicationPartManager(m =>
    {
        if (!webpaySimulado) m.FeatureProviders.Add(new QuitarControladores(typeof(WebpayMockController)));
    });
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ManejadorErrores>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

// A4: solo NGINX y el BFF (IPs fijas en Docker) pueden informar la IP del cliente.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    ProxiesConfiables.Configurar(o, builder.Configuration.GetSection("Proxy:IpsConfiables").Get<string[]>()));

builder.Services.AddCors(o => o.AddPolicy("dev", p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origenes").Get<string[]>() ?? Array.Empty<string>())
    .AllowAnyHeader()
    .AllowAnyMethod()));

// ---------- Autenticación JWT ----------
var jwt = builder.Configuration.GetSection("Jwt").Get<OpcionesJwt>()!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // conservar "sub", "rol", etc. sin renombrarlos
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            RoleClaimType = Claims.Rol,
            NameClaimType = Claims.Nombre,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseCors("dev");
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

/// <summary>Visible para las pruebas de integración (WebApplicationFactory).</summary>
public partial class Program;
