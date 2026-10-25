using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
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
    new("Seguridad:TotpClave", builder.Configuration["Seguridad:TotpClave"], ValidacionSecretos.LargoMinimoClaveJwt),
    new("Huella:Clave", builder.Configuration["Huella:Clave"], ValidacionSecretos.LargoMinimoClaveJwt),
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
builder.Services.Configure<OpcionesSeguridad>(builder.Configuration.GetSection("Seguridad"));
builder.Services.Configure<OpcionesCorreo>(builder.Configuration.GetSection("Correo"));
var seguridad = builder.Configuration.GetSection("Seguridad").Get<OpcionesSeguridad>() ?? new OpcionesSeguridad();
builder.Services.Configure<OpcionesConexionRabbit>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<OpcionesWebpay>(builder.Configuration.GetSection("Webpay"));
builder.Services.Configure<OpcionesPos>(builder.Configuration.GetSection("Pos"));
builder.Services.Configure<OpcionesHuella>(builder.Configuration.GetSection("Huella"));

// ---------- Infraestructura ----------
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<IReloj, RelojChile>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditoria, ServicioAuditoria>();
// F4 (M6): alerta si la auditoría falla y registro de accesos denegados por rol.
builder.Services.AddSingleton<EstadoAuditoria>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, AuditarDenegaciones>();
builder.Services.AddSingleton(TimeProvider.System);
// F3 (C5, A3): autorización por objeto y sede en un solo lugar.
builder.Services.AddSingleton<PoliticaAcceso>();
// F3 (C3): comprobantes firmados del lector de huella.
builder.Services.AddSingleton<VerificadorHuella>();
// F3 (M3): validación declarativa de cada solicitud.
builder.Services.AddValidacion();

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
// ---------- Identidad y sesión (F2: A1 A2 A7 M1 M2) ----------
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ServicioJwt>();
builder.Services.AddScoped<ServicioSesiones>();
builder.Services.AddSingleton(new CifradoSecretos(seguridad.TotpClave));
builder.Services.AddScoped<PoliticaContrasenas>();
builder.Services.AddScoped<ServicioBloqueos>();
builder.Services.AddScoped<FiltroReautenticacion>();
if (seguridad.VerificarFiltraciones)
    builder.Services.AddHttpClient<IVerificadorFiltraciones, VerificadorHibp>(http =>
    {
        http.BaseAddress = new Uri("https://api.pwnedpasswords.com/");
        http.Timeout = TimeSpan.FromSeconds(3);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ReservaMed/2.0");
    });
else
    builder.Services.AddSingleton<IVerificadorFiltraciones, SinVerificacionFiltraciones>();
if (string.Equals(builder.Configuration["Correo:Modo"], "Smtp", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IEnviadorCorreo, CorreoSmtp>();
else
    builder.Services.AddSingleton<IEnviadorCorreo, CorreoEnLog>();

// Límite por IP en login, TOTP, registro y renovación (A2, A8). El bloqueo por cuenta está en AuthController.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "sin-ip";
    o.AddPolicy(PoliticasLimite.Login, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = seguridad.LoginPorMinutoPorIp, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(PoliticasLimite.Renovar, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = seguridad.RenovarPorMinutoPorIp, Window = TimeSpan.FromMinutes(1) }));
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "application/problem+json";
        await ctx.HttpContext.Response.WriteAsJsonAsync(new
        {
            status = 429, title = "Demasiadas solicitudes. Espere un minuto e intente nuevamente.", codigo = "LIMITE"
        }, ct);
    };
});
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
builder.Services.AddControllers(o => o.Filters.Add<FiltroValidacion>())
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = Validacion.DesdeModelState)
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .ConfigureApplicationPartManager(m =>
    {
        if (!webpaySimulado) m.FeatureProviders.Add(new QuitarControladores(typeof(WebpayMockController)));
    });
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ManejadorErrores>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<SaludAuditoria>("auditoria");

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
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        // A1: un access token solo vale mientras su sesión siga activa (cerrar sesión, desactivar
        // la cuenta o detectar reuso de refresh la revocan en el acto).
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var sid = ctx.Principal?.FindFirst(Claims.Sid)?.Value;
                var sesiones = ctx.HttpContext.RequestServices.GetRequiredService<ServicioSesiones>();
                if (!Guid.TryParse(sid, out var id) || !await sesiones.EstaActivaAsync(id))
                    ctx.Fail("Sesión revocada o inexistente.");
            }
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
app.UseRateLimiter();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

/// <summary>Visible para las pruebas de integración (WebApplicationFactory).</summary>
public partial class Program;
