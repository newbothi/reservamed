using ReservaMed.Contracts;
using ReservaMed.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Secretos (C1): sin valores por defecto en el código.
ValidacionSecretos.Exigir(
[
    new("RabbitMq:Clave", builder.Configuration["RabbitMq:Clave"], ValidacionSecretos.LargoMinimoClave, ToleradoEnDesarrollo: true)
], builder.Environment.IsDevelopment());

builder.Services.AddHostedService<ConsumidorNotificaciones>();
builder.Services.AddHostedService<ConsumidorHis>();

builder.Build().Run();
