using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace ReservaMed.Api.Common;

/// <summary>Convierte excepciones en respuestas ProblemDetails (RFC 9457).</summary>
public sealed class ManejadorErrores(ILogger<ManejadorErrores> log, IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var (status, titulo, codigo) = ex switch
        {
            ReglaNegocioException r => (r.Status, r.Message, r.Codigo),
            SqlException s when SqlErrores.EsDuplicado(s) =>
                (StatusCodes.Status409Conflict, "El registro ya existe o el cupo ya fue tomado.", "CONFLICTO"),
            // M3, defensa en profundidad: si un dato más largo que la columna escapó a la validación,
            // es un error del cliente (400), no del servidor.
            SqlException s when s.Number is 2628 or 8152 =>
                (StatusCodes.Status400BadRequest, "Uno de los datos es demasiado largo.", Validacion.Codigo),
            _ => (StatusCodes.Status500InternalServerError, "Ocurrió un error inesperado.", "ERROR_INTERNO")
        };

        if (status >= 500)
            log.LogError(ex, "Error no controlado en {Ruta}", ctx.Request.Path);

        ctx.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = ctx,
            Exception = ex,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = titulo,
                Extensions = { ["codigo"] = codigo }
            }
        });
    }
}
