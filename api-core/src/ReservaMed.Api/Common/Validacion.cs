using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ReservaMed.Api.Modules.Admision;
using ReservaMed.Api.Modules.Agenda;
using ReservaMed.Api.Modules.Auth;
using ReservaMed.Api.Modules.Citas;
using ReservaMed.Api.Modules.Pagos;

namespace ReservaMed.Api.Common;

public sealed record ErrorCampo(string Campo, string Mensaje);

/// <summary>
/// Hallazgo M3: toda solicitud se valida ANTES de llegar a la lógica. Nulos, largos excesivos,
/// fechas absurdas o valores fuera de catálogo responden 400 con el mismo formato siempre:
/// { status: 400, title, codigo: "VALIDACION", errores: [{ campo, mensaje }] }, nunca un 500.
/// Los largos máximos coinciden con las columnas de SQL Server.
/// </summary>
public static class Validacion
{
    public const string Codigo = "VALIDACION";

    public static ObjectResult Respuesta(IReadOnlyList<ErrorCampo> errores) =>
        new(new
        {
            status = StatusCodes.Status400BadRequest,
            title = errores.Count == 1 ? errores[0].Mensaje : "Hay datos no válidos en la solicitud.",
            codigo = Codigo,
            errores
        })
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" }
        };

    /// <summary>JSON mal formado, tipos incorrectos o propiedades obligatorias ausentes (model binding).</summary>
    public static IActionResult DesdeModelState(ActionContext ctx)
    {
        var errores = ctx.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .Select(e => new ErrorCampo(Campo(e.Key),
                e.Key.StartsWith('$') || e.Value!.Errors[0].Exception is not null || string.IsNullOrWhiteSpace(e.Value.Errors[0].ErrorMessage)
                    ? "Formato no válido."
                    : "Dato obligatorio o no válido."))
            .DistinctBy(e => e.Campo)
            .ToList();
        return Respuesta(errores.Count > 0 ? errores : [new ErrorCampo("", "Solicitud no válida.")]);
    }

    /// <summary>"$.fechaNacimiento" o "r.FechaNacimiento" → "fechaNacimiento".</summary>
    public static string Campo(string clave)
    {
        var c = clave.TrimStart('$', '.');
        var punto = c.LastIndexOf('.');
        if (punto >= 0) c = c[(punto + 1)..];
        return c.Length == 0 ? c : char.ToLowerInvariant(c[0]) + c[1..];
    }

    /// <summary>Registra los validadores de este archivo y el filtro que los aplica.</summary>
    public static IServiceCollection AddValidacion(this IServiceCollection s) =>
        s.AddValidatorsFromAssemblyContaining<FiltroValidacion>(ServiceLifetime.Singleton);
}

/// <summary>Aplica el IValidator&lt;T&gt; de cada argumento de la acción, si existe.</summary>
public sealed class FiltroValidacion : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var errores = new List<ErrorCampo>();
        foreach (var arg in ctx.ActionArguments.Values)
        {
            if (arg is null) continue;
            var tipo = typeof(IValidator<>).MakeGenericType(arg.GetType());
            if (ctx.HttpContext.RequestServices.GetService(tipo) is not IValidator v) continue;
            var r = await v.ValidateAsync(new ValidationContext<object>(arg), ctx.HttpContext.RequestAborted);
            errores.AddRange(r.Errors.Select(e => new ErrorCampo(Validacion.Campo(e.PropertyName), e.ErrorMessage)));
        }
        if (errores.Count > 0)
        {
            ctx.Result = Validacion.Respuesta(errores);
            return;
        }
        await next();
    }
}

/* ---------------- Reglas comunes ---------------- */

internal static class Reglas
{
    public static readonly DateTime FechaMinima = new(1900, 1, 1);

    public static IRuleBuilderOptions<T, string?> Texto<T>(this IRuleBuilder<T, string?> r, int max, string nombre) =>
        r.MaximumLength(max).WithMessage($"{nombre}: máximo {max} caracteres.");

    public static IRuleBuilderOptions<T, string?> Obligatorio<T>(this IRuleBuilder<T, string?> r, int max, string nombre) =>
        r.NotEmpty().WithMessage($"{nombre} es obligatorio.").MaximumLength(max).WithMessage($"{nombre}: máximo {max} caracteres.");

    public static IRuleBuilderOptions<T, string?> RutOpcional<T>(this IRuleBuilder<T, string?> r, string nombre = "RUT") =>
        r.MaximumLength(15).WithMessage($"{nombre} no válido.");

    public static IRuleBuilderOptions<T, string?> Correo<T>(this IRuleBuilder<T, string?> r) =>
        r.NotEmpty().WithMessage("El correo es obligatorio.")
         .MaximumLength(150).WithMessage("Correo: máximo 150 caracteres.")
         .EmailAddress().WithMessage("Correo no válido.");

    public static IRuleBuilderOptions<T, DateTime> Nacimiento<T>(this IRuleBuilder<T, DateTime> r) =>
        r.Must(f => f >= FechaMinima && f.Date <= DateTime.Today).WithMessage("Fecha de nacimiento no válida.");

    public static IRuleBuilderOptions<T, string?> Tramo<T>(this IRuleBuilder<T, string?> r) =>
        r.Must(t => t is null or "A" or "B" or "C" or "D").WithMessage("Tramo Fonasa no válido (A, B, C o D).");

    public static IRuleBuilderOptions<T, string?> Sexo<T>(this IRuleBuilder<T, string?> r) =>
        r.Must(s => s is "M" or "F" or "X").WithMessage("Sexo debe ser M, F o X.");
}

/* ---------------- Identidad ---------------- */

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).Obligatorio(150, "El correo");
        RuleFor(x => x.Password).Obligatorio(128, "La contraseña");
        RuleFor(x => x.Cliente).Must(c => c is null or "PORTAL" or "BACKOFFICE").WithMessage("Cliente no válido.");
    }
}

public sealed class TotpRequestValidator : AbstractValidator<TotpRequest>
{
    public TotpRequestValidator()
    {
        RuleFor(x => x.Desafio).Obligatorio(2000, "La verificación");
        RuleFor(x => x.Codigo).NotEmpty().Matches(@"^\d{6}$").WithMessage("El código tiene 6 dígitos.");
    }
}

public sealed class RenovarRequestValidator : AbstractValidator<RenovarRequest>
{
    public RenovarRequestValidator() => RuleFor(x => x.RefreshToken).Obligatorio(200, "El token");
}

public sealed class VerificarCorreoRequestValidator : AbstractValidator<VerificarCorreoRequest>
{
    public VerificarCorreoRequestValidator() => RuleFor(x => x.Token).Obligatorio(200, "El token");
}

public sealed class RegistroPacienteRequestValidator : AbstractValidator<RegistroPacienteRequest>
{
    public RegistroPacienteRequestValidator()
    {
        RuleFor(x => x.Rut).NotEmpty().WithMessage("El RUT es obligatorio.").RutOpcional();
        RuleFor(x => x.Nombres).Obligatorio(80, "Nombres");
        RuleFor(x => x.ApellidoPaterno).Obligatorio(60, "Apellido paterno");
        RuleFor(x => x.ApellidoMaterno).Texto(60, "Apellido materno");
        RuleFor(x => x.FechaNacimiento).Nacimiento();
        RuleFor(x => x.Sexo).Sexo();
        RuleFor(x => x.Email).Correo();
        RuleFor(x => x.Telefono).Texto(20, "Teléfono");
        RuleFor(x => x.Comuna).Texto(80, "Comuna");
        // El mínimo y la calidad de la contraseña los revisa PoliticaContrasenas (M2, 422).
        RuleFor(x => x.Password).Obligatorio(128, "La contraseña");
        RuleFor(x => x.FinanciadorId).GreaterThan(0).WithMessage("Seleccione su previsión.");
        RuleFor(x => x.PlanSaludId).GreaterThan(0).When(x => x.PlanSaludId is not null).WithMessage("Plan no válido.");
        RuleFor(x => x.TramoFonasa).Tramo();
        RuleFor(x => x.AfiliadoRut).RutOpcional("RUT del titular");
        RuleFor(x => x.AfiliadoNombre).Texto(150, "Nombre del titular");
    }
}

/* ---------------- Citas y pagos ---------------- */

public sealed class ReservarRequestValidator : AbstractValidator<ReservarRequest>
{
    public ReservarRequestValidator()
    {
        RuleFor(x => x.ProfesionalId).GreaterThan(0).WithMessage("Profesional no válido.");
        RuleFor(x => x.SedeId).GreaterThan(0).WithMessage("Sede no válida.");
        RuleFor(x => x.Inicio).Must(i => i > Reglas.FechaMinima).WithMessage("Indique la hora.");
        RuleFor(x => x.ModalidadPago).IsInEnum().WithMessage("Modalidad de pago no válida.");
        RuleFor(x => x.PacienteId).GreaterThan(0).When(x => x.PacienteId is not null).WithMessage("Paciente no válido.");
        RuleFor(x => x.Canal).Must(c => c is null or "WEB" or "PRESENCIAL" or "TELEFONO").WithMessage("Canal no válido.");
    }
}

public sealed class CancelarRequestValidator : AbstractValidator<CancelarRequest>
{
    public CancelarRequestValidator() => RuleFor(x => x.Motivo).Texto(200, "Motivo");
}

public sealed class IniciarPagoRequestValidator : AbstractValidator<IniciarPagoRequest>
{
    public IniciarPagoRequestValidator() => RuleFor(x => x.CitaId).GreaterThan(0).WithMessage("Cita no válida.");
}

public sealed class ConfirmarPagoRequestValidator : AbstractValidator<ConfirmarPagoRequest>
{
    public ConfirmarPagoRequestValidator() => RuleFor(x => x.Token).Obligatorio(64, "El token de Webpay");
}

public sealed class BloqueoRequestValidator : AbstractValidator<BloqueoRequest>
{
    public BloqueoRequestValidator()
    {
        RuleFor(x => x.ProfesionalId).GreaterThan(0).WithMessage("Profesional no válido.");
        RuleFor(x => x.Motivo).Obligatorio(150, "El motivo");
        RuleFor(x => x).Must(x => x.Hasta > x.Desde && x.Desde > Reglas.FechaMinima && (x.Hasta - x.Desde).TotalDays <= 366)
            .OverridePropertyName("hasta").WithMessage("El rango del bloqueo no es válido (máximo un año).");
    }
}

/* ---------------- Admisión ---------------- */

public sealed class BuscarPacienteRequestValidator : AbstractValidator<BuscarPacienteRequest>
{
    public BuscarPacienteRequestValidator() => RuleFor(x => x.Rut).NotEmpty().WithMessage("Ingrese el RUT.").RutOpcional();
}

public sealed class CrearPacienteRequestValidator : AbstractValidator<CrearPacienteRequest>
{
    public CrearPacienteRequestValidator()
    {
        RuleFor(x => x.Rut).NotEmpty().WithMessage("El RUT es obligatorio.").RutOpcional();
        RuleFor(x => x.Nombres).Obligatorio(80, "Nombres");
        RuleFor(x => x.ApellidoPaterno).Obligatorio(60, "Apellido paterno");
        RuleFor(x => x.ApellidoMaterno).Texto(60, "Apellido materno");
        RuleFor(x => x.FechaNacimiento).Nacimiento();
        RuleFor(x => x.Sexo).Sexo();
        RuleFor(x => x.Email).Correo();
        RuleFor(x => x.Telefono).Texto(20, "Teléfono");
        RuleFor(x => x.Comuna).Texto(80, "Comuna");
    }
}

public sealed class PrevisionRequestValidator : AbstractValidator<PrevisionRequest>
{
    public PrevisionRequestValidator()
    {
        RuleFor(x => x.FinanciadorId).GreaterThan(0).WithMessage("Financiador no válido.");
        RuleFor(x => x.PlanSaludId).GreaterThan(0).When(x => x.PlanSaludId is not null).WithMessage("Plan no válido.");
        RuleFor(x => x.TramoFonasa).Tramo();
        RuleFor(x => x.AfiliadoRut).RutOpcional("RUT del titular");
        RuleFor(x => x.AfiliadoNombre).Texto(150, "Nombre del titular");
    }
}

public sealed class EmitirBonoRequestValidator : AbstractValidator<EmitirBonoRequest>
{
    public EmitirBonoRequestValidator()
    {
        RuleFor(x => x.ComprobanteHuella).Texto(2000, "Comprobante de huella");
        RuleFor(x => x.DerivadorRut).RutOpcional("RUT del médico derivador");
    }
}

public sealed class CobrarRequestValidator : AbstractValidator<CobrarRequest>
{
    public CobrarRequestValidator()
    {
        RuleFor(x => x.Medio).IsInEnum().WithMessage("Medio de pago no válido.");
        RuleFor(x => x.UltimosDigitos).Matches(@"^\d{4}$").When(x => x.UltimosDigitos is not null)
            .WithMessage("Indique los últimos 4 dígitos de la tarjeta.");
        RuleFor(x => x.Cuotas).InclusiveBetween(0, 12).When(x => x.Cuotas is not null).WithMessage("Cuotas: de 1 a 12.");
    }
}
