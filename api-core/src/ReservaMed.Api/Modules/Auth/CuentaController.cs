using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ReservaMed.Api.Common;
using ReservaMed.Contracts;

namespace ReservaMed.Api.Modules.Auth;

public sealed record CambiarClaveRequest(string Actual, string Nueva);
public sealed record ReautenticarRequest(string Codigo);
public sealed record SolicitarEliminacionRequest(string Clave, string Confirmacion);
public sealed record ResultadoCambioClave(int SesionesCerradas);

/// <summary>
/// Fase 5 (brechas ASVS nivel 2): lo que cada usuario puede hacer con su propia cuenta.
///  - V2.1.5/V2.1.6 cambiar la contraseña indicando la actual; V2.2.3 aviso por correo;
///    V3.3.3 se cierran las demás sesiones.
///  - V3.3.4 ver sus sesiones abiertas y cerrar cualquiera.
///  - V3.7.1 el personal confirma su identidad con TOTP antes de acciones sensibles.
///  - V8.3.2 (Ley 21.719) el paciente exporta sus datos y puede pedir eliminar su cuenta.
/// </summary>
[ApiController]
[Route("api/cuenta")]
[Authorize]
public sealed class CuentaController(
    ISqlConnectionFactory db,
    ServicioSesiones sesiones,
    ServicioBloqueos bloqueos,
    PoliticaContrasenas politica,
    CifradoSecretos cifrado,
    IEnviadorCorreo correo,
    IAuditoria auditoria,
    IReloj reloj) : ControllerBase
{
    private sealed class FilaCuenta
    {
        public int Id { get; set; }
        public string Email { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string NombreVisible { get; set; } = "";
        public int? PacienteId { get; set; }
        public int IntentosFallidos { get; set; }
        public int BloqueosConsecutivos { get; set; }
        public DateTime? BloqueadoHasta { get; set; }
        public byte[]? TotpSecreto { get; set; }
        public bool TotpActivo { get; set; }
        public long? TotpUltimoPaso { get; set; }
    }

    private Guid? SidActual => Guid.TryParse(User.FindFirstValue(Claims.Sid), out var s) ? s : null;

    private async Task<FilaCuenta> CargarAsync(System.Data.IDbConnection cn)
    {
        var u = await cn.QuerySingleAsync<FilaCuenta>("SELECT * FROM dbo.Usuarios WHERE Id = @Id", new { Id = User.UsuarioId() });
        if (u.BloqueadoHasta > DateTime.UtcNow) throw ServicioBloqueos.Bloqueada();
        return u;
    }

    /// <summary>422 (no 401): la sesión sigue siendo válida, solo la contraseña indicada no lo es.</summary>
    private async Task ExigirClaveAsync(System.Data.IDbConnection cn, FilaCuenta u, string clave)
    {
        if (Contrasenas.Verificar(clave, u.PasswordHash)) return;
        await bloqueos.RegistrarFalloAsync(cn, u.Id, u.IntentosFallidos, u.BloqueosConsecutivos);
        await auditoria.RegistrarAsync("CLAVE_ACTUAL_INCORRECTA", "Usuario", u.Id);
        throw new ReglaNegocioException("La contraseña actual no es correcta.", StatusCodes.Status422UnprocessableEntity, "CLAVE_ACTUAL");
    }

    /* ---------------- Contraseña ---------------- */

    [HttpPost("clave")]
    public async Task<ResultadoCambioClave> CambiarClave(CambiarClaveRequest r, CancellationToken ct)
    {
        await using var cn = db.Crear();
        await cn.OpenAsync(ct);
        var u = await CargarAsync(cn);
        await ExigirClaveAsync(cn, u, r.Actual);

        if (r.Nueva == r.Actual)
            throw new ReglaNegocioException("La contraseña nueva debe ser distinta de la actual.", codigo: "CONTRASENA");
        var errores = await politica.RevisarAsync(r.Nueva, u.Email, u.NombreVisible, ct);
        if (errores.Count > 0) throw new ReglaNegocioException(string.Join(" ", errores), codigo: "CONTRASENA");

        await cn.ExecuteAsync("UPDATE dbo.Usuarios SET PasswordHash = @H WHERE Id = @Id", new { H = Contrasenas.Hash(r.Nueva), u.Id });
        await ServicioBloqueos.ReiniciarAsync(cn, u.Id);
        var cerradas = await sesiones.RevocarTodasAsync(u.Id, "Cambio de contraseña", excepto: SidActual);
        await auditoria.RegistrarAsync("CAMBIO_CLAVE", "Usuario", u.Id, $"{cerradas} sesiones cerradas");

        await correo.EnviarAsync(new Correo(u.Email, "Su contraseña de ReservaMed cambió",
            $"La contraseña de su cuenta se cambió el {reloj.Ahora:dd-MM-yyyy 'a las' HH:mm} y se cerraron las demás sesiones abiertas.\n\n" +
            "Si no fue usted, comuníquese de inmediato con la clínica para bloquear la cuenta."));
        return new ResultadoCambioClave(cerradas);
    }

    /* ---------------- Sesiones ---------------- */

    [HttpGet("sesiones")]
    public async Task<IReadOnlyList<SesionActiva>> Sesiones()
    {
        var lista = await sesiones.ListarAsync(User.UsuarioId());
        foreach (var s in lista) s.Actual = s.Id == SidActual;
        return lista;
    }

    [HttpPost("sesiones/{id:guid}/cerrar")]
    public async Task<IActionResult> CerrarSesion(Guid id)
    {
        if (!await sesiones.RevocarPropiaAsync(User.UsuarioId(), id, "Cerrada por el usuario"))
            throw ReglaNegocioException.NoEncontrado("La sesión");
        await auditoria.RegistrarAsync("CERRAR_SESION_REMOTA", "Usuario", User.UsuarioId());
        return NoContent();
    }

    /* ---------------- Reautenticación (V3.7.1) ---------------- */

    public static readonly TimeSpan VentanaReautenticacion = TimeSpan.FromMinutes(5);

    [HttpPost("reautenticar")]
    [Authorize(Roles = Roles.StaffYMedico)]
    public async Task<IActionResult> Reautenticar(ReautenticarRequest r)
    {
        var sid = SidActual ?? throw ReglaNegocioException.Prohibido();
        await using var cn = db.Crear();
        await cn.OpenAsync();
        var u = await CargarAsync(cn);
        if (!u.TotpActivo || u.TotpSecreto is null) throw ReglaNegocioException.Prohibido("Su cuenta no tiene autenticador activo.");

        var paso = Totp.Verificar(cifrado.Descifrar(u.TotpSecreto), r.Codigo, DateTimeOffset.UtcNow, u.TotpUltimoPaso);
        if (paso is null)
        {
            await bloqueos.RegistrarFalloAsync(cn, u.Id, u.IntentosFallidos, u.BloqueosConsecutivos);
            await auditoria.RegistrarAsync("REAUTENTICACION_FALLIDA", "Usuario", u.Id);
            throw new ReglaNegocioException("Código incorrecto.", StatusCodes.Status422UnprocessableEntity, "TOTP");
        }
        await cn.ExecuteAsync("UPDATE dbo.Usuarios SET TotpUltimoPaso = @P WHERE Id = @Id", new { P = paso, u.Id });
        await ServicioBloqueos.ReiniciarAsync(cn, u.Id);
        await sesiones.MarcarReautenticadaAsync(sid);
        await auditoria.RegistrarAsync("REAUTENTICACION", "Usuario", u.Id);
        return NoContent();
    }

    /* ---------------- Datos del paciente (V8.3.2, Ley 21.719) ---------------- */

    /// <summary>Todos los datos personales del paciente y quién accedió a su ficha, en JSON.</summary>
    [HttpGet("datos")]
    [Authorize(Roles = Roles.Paciente)]
    public async Task<IActionResult> ExportarDatos()
    {
        var pacienteId = User.PacienteId() ?? throw ReglaNegocioException.Prohibido();
        await using var cn = db.Crear();
        object paciente = await cn.QuerySingleAsync("""
            SELECT Rut, Nombres, ApellidoPaterno, ApellidoMaterno, FechaNacimiento, Sexo, Email, Telefono, Comuna, CreadoEn
            FROM dbo.Pacientes WHERE Id = @Id
            """, new { Id = pacienteId });
        object cuenta = await cn.QuerySingleAsync(
            "SELECT Email, CreadoEn, PrivacidadAceptadaEn FROM dbo.Usuarios WHERE Id = @Id", new { Id = User.UsuarioId() });
        var prevision = await cn.QueryAsync("""
            SELECT f.Nombre AS Financiador, pl.Nombre AS PlanSalud, pv.TramoFonasa, pv.EsCarga, pv.AfiliadoRut, pv.AfiliadoNombre
            FROM dbo.Previsiones pv
            JOIN dbo.Financiadores f ON f.Id = pv.FinanciadorId
            LEFT JOIN dbo.PlanesSalud pl ON pl.Id = pv.PlanSaludId
            WHERE pv.PacienteId = @Id
            """, new { Id = pacienteId });
        var citas = await cn.QueryAsync("""
            SELECT c.Id, c.FechaHoraInicio, c.Estado, c.Canal, e.Nombre AS Especialidad,
                   pr.Nombres + ' ' + pr.ApellidoPaterno AS Profesional, s.Nombre AS Sede
            FROM dbo.Citas c
            JOIN dbo.Profesionales pr ON pr.Id = c.ProfesionalId
            JOIN dbo.Especialidades e ON e.Id = pr.EspecialidadId
            JOIN dbo.Sedes s ON s.Id = c.SedeId
            WHERE c.PacienteId = @Id ORDER BY c.FechaHoraInicio DESC
            """, new { Id = pacienteId });
        var bonos = await cn.QueryAsync("""
            SELECT b.Folio, b.CitaId, b.Estado, b.TotalCopago FROM dbo.Bonos b
            JOIN dbo.Citas c ON c.Id = b.CitaId WHERE c.PacienteId = @Id
            """, new { Id = pacienteId });
        var pagos = await cn.QueryAsync("""
            SELECT p.CitaId, p.Medio, p.Monto, p.Estado, p.UltimosDigitos, p.CreadoEn FROM dbo.Pagos p
            JOIN dbo.Citas c ON c.Id = p.CitaId WHERE c.PacienteId = @Id
            """, new { Id = pacienteId });
        // Ley 20.584: el paciente tiene derecho a saber quién accedió a su información (por rol).
        var accesos = await cn.QueryAsync("""
            SELECT a.Fecha, a.Rol, a.Accion FROM dbo.Auditoria a
            WHERE (a.Recurso = 'Paciente' AND a.RecursoId = CAST(@Id AS VARCHAR(30)))
               OR (a.Recurso = 'Cita' AND a.RecursoId IN (SELECT CAST(Id AS VARCHAR(30)) FROM dbo.Citas WHERE PacienteId = @Id))
            ORDER BY a.Id DESC
            """, new { Id = pacienteId });

        await cn.ExecuteAsync("""
            INSERT INTO dbo.SolicitudesDatos (UsuarioId, PacienteId, Tipo, Estado, CreadaEn, ResueltaEn)
            VALUES (@U, @P, 'EXPORTAR', 'ATENDIDA', @Ahora, @Ahora)
            """, new { U = User.UsuarioId(), P = pacienteId, reloj.Ahora });
        await auditoria.RegistrarAsync("EXPORTAR_DATOS", "Paciente", pacienteId);

        Response.Headers.ContentDisposition = $"attachment; filename=\"mis-datos-reservamed-{reloj.Ahora:yyyyMMdd}.json\"";
        return Ok(new
        {
            generadoEn = reloj.Ahora,
            paciente = Fila(paciente), cuenta = Fila(cuenta),
            prevision = Filas(prevision), citas = Filas(citas), bonos = Filas(bonos), pagos = Filas(pagos),
            accesosASuFicha = Filas(accesos)
        });
    }

    /// <summary>Filas de Dapper con nombres en camelCase, como el resto de la API.</summary>
    private static Dictionary<string, object?> Fila(object fila) =>
        ((IDictionary<string, object?>)fila).ToDictionary(kv => char.ToLowerInvariant(kv.Key[0]) + kv.Key[1..], kv => kv.Value);

    private static List<Dictionary<string, object?>> Filas(IEnumerable<object> filas) => filas.Select(Fila).ToList();

    /// <summary>
    /// Solicitud de eliminación. La ficha clínica se conserva el plazo que exige la Ley 20.584
    /// (y por eso no se borra en el acto); la cuenta se cierra de inmediato y sus datos de contacto
    /// se eliminan. La solicitud queda PENDIENTE para que la clínica complete el proceso.
    /// </summary>
    [HttpPost("eliminacion")]
    [Authorize(Roles = Roles.Paciente)]
    public async Task<IActionResult> SolicitarEliminacion(SolicitarEliminacionRequest r)
    {
        var pacienteId = User.PacienteId() ?? throw ReglaNegocioException.Prohibido();
        await using var cn = db.Crear();
        await cn.OpenAsync();
        var u = await CargarAsync(cn);
        await ExigirClaveAsync(cn, u, r.Clave);

        var futuras = await cn.ExecuteScalarAsync<int>("""
            SELECT COUNT(1) FROM dbo.Citas WHERE PacienteId = @Id AND FechaHoraInicio > @Ahora
              AND Estado IN ('PENDIENTE_PAGO', 'RESERVADA', 'CONFIRMADA')
            """, new { Id = pacienteId, reloj.Ahora });
        if (futuras > 0)
            throw new ReglaNegocioException($"Tiene {futuras} hora(s) por venir. Anúlelas antes de cerrar su cuenta.", codigo: "CITAS_FUTURAS");

        // Aviso al correo real ANTES de borrarlo.
        await correo.EnviarAsync(new Correo(u.Email, "Recibimos su solicitud de eliminación de datos",
            "Cerramos su cuenta de ReservaMed y eliminamos sus datos de contacto. Su ficha clínica se conserva por el plazo legal " +
            "(Ley 20.584) y luego se elimina. Si no hizo esta solicitud, comuníquese de inmediato con la clínica."));

        var anonimo = $"eliminado-{u.Id}@reservamed.invalid";
        await using var tx = await cn.BeginTransactionAsync();
        await cn.ExecuteAsync("""
            UPDATE dbo.Usuarios SET Activo = 0, Email = @Anonimo, PasswordHash = @Hash, TotpSecreto = NULL WHERE Id = @U;
            UPDATE dbo.Pacientes SET Email = @Anonimo, Telefono = NULL WHERE Id = @P;
            INSERT INTO dbo.SolicitudesDatos (UsuarioId, PacienteId, Tipo, Estado, CreadaEn, Nota)
            VALUES (@U, @P, 'ELIMINAR', 'PENDIENTE', @Ahora, N'Cuenta cerrada y contacto eliminado; ficha clínica en retención legal.');
            """, new { Anonimo = anonimo, Hash = Contrasenas.Hash(Guid.NewGuid().ToString()), U = u.Id, P = pacienteId, reloj.Ahora }, tx);
        await tx.CommitAsync();
        await sesiones.RevocarTodasAsync(u.Id, "Solicitud de eliminación de datos");
        await auditoria.RegistrarAsync("SOLICITUD_ELIMINACION", "Paciente", pacienteId, Privacidad.Correo(u.Email));
        return NoContent();
    }
}

/// <summary>
/// ASVS V3.7.1: la acción exige que el usuario haya confirmado su TOTP en los últimos 5 minutos
/// (POST /api/cuenta/reautenticar). Si no, 403 con código REAUTENTICAR y el backoffice pide el código.
/// </summary>
public sealed class RequiereReautenticacionAttribute() : TypeFilterAttribute(typeof(FiltroReautenticacion));

public sealed class FiltroReautenticacion(ServicioSesiones sesiones) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var ok = Guid.TryParse(ctx.HttpContext.User.FindFirstValue(Claims.Sid), out var sid)
                 && await sesiones.ReautenticadaHaceMenosDeAsync(sid, CuentaController.VentanaReautenticacion);
        if (!ok)
        {
            ctx.Result = new ObjectResult(new
            {
                status = StatusCodes.Status403Forbidden,
                title = "Confirme su identidad con el código de su autenticador para continuar.",
                codigo = "REAUTENTICAR"
            }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
        await next();
    }
}
