using ReservaMed.Contracts;
using ReservaMed.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AgregarSecretosDocker();   // F5 (V6.4.1): secretos como archivos en Docker

// Secretos (C1): sin valores por defecto en el código.
ValidacionSecretos.Exigir(
[
    new("RabbitMq:Clave", builder.Configuration["RabbitMq:Clave"], ValidacionSecretos.LargoMinimoClave, ToleradoEnDesarrollo: true)
], builder.Environment.IsDevelopment());

// M9: AMQPS obligatorio en producción.
var problemasRabbit = (builder.Configuration.GetSection("RabbitMq").Get<OpcionesConexionRabbit>() ?? new()).Revisar(builder.Environment.IsProduction());
if (problemasRabbit.Count > 0)
    throw new InvalidOperationException(string.Join(Environment.NewLine, problemasRabbit));

builder.Services.AddHostedService<ConsumidorNotificaciones>();
builder.Services.AddHostedService<ConsumidorHis>();
builder.Services.AddHostedService<RetencionNotificaciones>();   // A9: retención limitada

builder.Build().Run();
