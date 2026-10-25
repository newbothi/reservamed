using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Jobs;

/// <summary>Umbrales de las alertas (sección "Alertas" de appsettings). Ventanas en minutos.</summary>
public sealed class OpcionesAlertas
{
    public bool Activas { get; set; } = true;
    public int MinutosEntreRevisiones { get; set; } = 1;
    public int VentanaMinutos { get; set; } = 15;
    /// <summary>Intentos fallidos (contraseña o TOTP) desde una misma IP.</summary>
    public int FallosPorIp { get; set; } = 20;
    /// <summary>Cuentas distintas probadas desde una misma IP (relleno de credenciales).</summary>
    public int CuentasPorIp { get; set; } = 8;
    /// <summary>Cuentas bloqueadas en total (ataque distribuido).</summary>
    public int CuentasBloqueadas { get; set; } = 5;
    /// <summary>Accesos denegados de un mismo usuario o IP.</summary>
    public int AccesosDenegados { get; set; } = 15;
    /// <summary>Pacientes distintos consultados por una misma persona del personal en una hora.</summary>
    public int PacientesPorHora { get; set; } = 40;
}

public sealed record AlertaDto(long Id, string Regla, string Severidad, string Clave, int Conteo, string Detalle,
    DateTime DesdeEn, DateTime CreadaEn, DateTime? RevisadaEn, string? RevisadaPor);

/// <summary>
/// ASVS V11.1.7 / V7 (monitoreo): reglas sobre la auditoría que detectan actividad inusual.
/// Cada regla agrupa por IP o usuario dentro de una ventana; si supera el umbral se crea una
/// alerta (una por regla y clave por ventana) y se avisa por correo a los administradores activos.
/// La clave nunca contiene datos de pacientes: solo IP o el correo del usuario del personal.
/// </summary>
public sealed class ServicioAlertas(ISqlConnectionFactory db, IEnviadorCorreo correo, IReloj reloj,
    IOptions<OpcionesAlertas> opciones, ILogger<ServicioAlertas> log)
{
    private readonly OpcionesAlertas _o = opciones.Value;

    private sealed record Hallazgo(string Clave, int Conteo);

    private sealed record Regla(string Nombre, string Severidad, int Minutos, int Umbral, string Sql, Func<Hallazgo, string> Detalle);

    private IEnumerable<Regla> Reglas() =>
    [
        new("FALLOS_LOGIN_IP", "ALTA", _o.VentanaMinutos, _o.FallosPorIp, """
            SELECT COALESCE(Ip, '(sin IP)') AS Clave, COUNT(1) AS Conteo FROM dbo.Auditoria
            WHERE Fecha >= @Desde AND Accion IN ('LOGIN_FALLIDO', 'TOTP_FALLIDO', 'LOGIN_BLOQUEADO')
            GROUP BY COALESCE(Ip, '(sin IP)') HAVING COUNT(1) >= @Umbral
            """, h => $"{h.Conteo} ingresos fallidos desde la IP {h.Clave} en {_o.VentanaMinutos} min (fuerza bruta)."),

        new("CUENTAS_PROBADAS_IP", "ALTA", _o.VentanaMinutos, _o.CuentasPorIp, """
            SELECT COALESCE(Ip, '(sin IP)') AS Clave, COUNT(DISTINCT Detalle) AS Conteo FROM dbo.Auditoria
            WHERE Fecha >= @Desde AND Accion = 'LOGIN_FALLIDO'
            GROUP BY COALESCE(Ip, '(sin IP)') HAVING COUNT(DISTINCT Detalle) >= @Umbral
            """, h => $"{h.Conteo} cuentas distintas probadas desde la IP {h.Clave} en {_o.VentanaMinutos} min (relleno de credenciales)."),

        new("CUENTAS_BLOQUEADAS", "ALTA", _o.VentanaMinutos, _o.CuentasBloqueadas, """
            SELECT 'todas' AS Clave, COUNT(DISTINCT RecursoId) AS Conteo FROM dbo.Auditoria
            WHERE Fecha >= @Desde AND Accion = 'CUENTA_BLOQUEADA'
            HAVING COUNT(DISTINCT RecursoId) >= @Umbral
            """, h => $"{h.Conteo} cuentas bloqueadas en {_o.VentanaMinutos} min (posible ataque distribuido)."),

        new("ACCESOS_DENEGADOS", "MEDIA", _o.VentanaMinutos, _o.AccesosDenegados, """
            SELECT COALESCE(Email, Ip, '(anónimo)') AS Clave, COUNT(1) AS Conteo FROM dbo.Auditoria
            WHERE Fecha >= @Desde AND Accion = 'ACCESO_DENEGADO'
            GROUP BY COALESCE(Email, Ip, '(anónimo)') HAVING COUNT(1) >= @Umbral
            """, h => $"{h.Conteo} accesos denegados de {h.Clave} en {_o.VentanaMinutos} min (exploración de permisos)."),

        new("CONSULTA_MASIVA_PACIENTES", "ALTA", 60, _o.PacientesPorHora, """
            SELECT COALESCE(Email, CAST(UsuarioId AS NVARCHAR(20))) AS Clave, COUNT(DISTINCT RecursoId) AS Conteo FROM dbo.Auditoria
            WHERE Fecha >= @Desde AND Accion IN ('VER_PACIENTE', 'FHIR_PATIENT') AND Rol <> 'PACIENTE'
            GROUP BY COALESCE(Email, CAST(UsuarioId AS NVARCHAR(20))) HAVING COUNT(DISTINCT RecursoId) >= @Umbral
            """, h => $"{h.Clave} consultó {h.Conteo} pacientes distintos en una hora (posible extracción de datos)."),
    ];

    /// <summary>Evalúa todas las reglas y devuelve las alertas NUEVAS (ya guardadas y avisadas).</summary>
    public async Task<IReadOnlyList<AlertaDto>> EvaluarAsync(CancellationToken ct = default)
    {
        var ahora = reloj.Ahora;
        var nuevas = new List<AlertaDto>();
        await using var cn = db.Crear();
        await cn.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        // Una sola evaluación a la vez (varias instancias o la prueba junto al job).
        await cn.ExecuteAsync("EXEC sp_getapplock @Resource = 'alertas-seguridad', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000",
            transaction: tx);

        foreach (var r in Reglas())
        {
            var desde = ahora.AddMinutes(-r.Minutos);
            var hallazgos = await cn.QueryAsync<Hallazgo>(r.Sql, new { Desde = desde, r.Umbral }, tx);
            foreach (var h in hallazgos)
            {
                var detalle = r.Detalle(h);
                var id = await cn.ExecuteScalarAsync<long?>("""
                    -- Una alerta por regla y clave en la ventana; si ya se revisó, solo se vuelve a alertar si la actividad creció.
                    IF NOT EXISTS (SELECT 1 FROM dbo.AlertasSeguridad WHERE Regla = @Regla AND Clave = @Clave AND CreadaEn >= @Desde
                                     AND (RevisadaEn IS NULL OR Conteo >= @Conteo))
                        INSERT INTO dbo.AlertasSeguridad (Regla, Severidad, Clave, Conteo, Detalle, DesdeEn, CreadaEn)
                        OUTPUT INSERTED.Id
                        VALUES (@Regla, @Severidad, @Clave, @Conteo, @Detalle, @Desde, @Ahora);
                    """, new { Regla = r.Nombre, r.Severidad, Clave = Recortar(h.Clave, 150), h.Conteo, Detalle = Recortar(detalle, 300), Desde = desde, Ahora = ahora }, tx);
                if (id is long nuevo)
                    nuevas.Add(new AlertaDto(nuevo, r.Nombre, r.Severidad, h.Clave, h.Conteo, detalle, desde, ahora, null, null));
            }
        }
        List<string> admins = nuevas.Count == 0 ? [] :
            (await cn.QueryAsync<string>("SELECT Email FROM dbo.Usuarios WHERE Rol = 'ADMIN' AND Activo = 1", transaction: tx)).ToList();
        await tx.CommitAsync(ct);

        foreach (var a in nuevas)
        {
            log.LogWarning("Alerta de seguridad {Regla} ({Severidad}): {Detalle}", a.Regla, a.Severidad, a.Detalle);
            foreach (var para in admins)
                await correo.EnviarAsync(new Correo(para, $"[ReservaMed] Alerta de seguridad {a.Severidad}: {a.Regla}",
                    $"{a.Detalle}\n\nDetectada el {a.CreadaEn:dd-MM-yyyy HH:mm}. Revísela en el backoffice, sección Seguridad."), ct);
        }
        return nuevas;
    }

    private static string Recortar(string s, int max) => s.Length <= max ? s : s[..max];
}

public sealed class AlertasSeguridadJob(IServiceProvider sp, IOptions<OpcionesAlertas> opciones, ILogger<AlertasSeguridadJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opciones.Value.Activas) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, opciones.Value.MinutosEntreRevisiones)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await sp.GetRequiredService<ServicioAlertas>().EvaluarAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Job de alertas de seguridad falló: {Error}", ex.Message);
            }
        }
    }
}

[ApiController]
[Route("api/seguridad/alertas")]
[Authorize(Roles = Roles.Admin)]
public sealed class AlertasController(ISqlConnectionFactory db, IAuditoria auditoria, IReloj reloj) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<AlertaDto>> Listar([FromQuery] bool pendientes = false)
    {
        await using var cn = db.Crear();
        return await cn.QueryAsync<AlertaDto>("""
            SELECT TOP 200 a.Id, a.Regla, a.Severidad, a.Clave, a.Conteo, a.Detalle, a.DesdeEn, a.CreadaEn, a.RevisadaEn,
                   u.Email AS RevisadaPor
            FROM dbo.AlertasSeguridad a LEFT JOIN dbo.Usuarios u ON u.Id = a.RevisadaPor
            WHERE (@Pendientes = 0 OR a.RevisadaEn IS NULL)
            ORDER BY a.Id DESC
            """, new { Pendientes = pendientes });
    }

    [HttpPost("{id:long}/revisada")]
    public async Task<IActionResult> MarcarRevisada(long id)
    {
        await using var cn = db.Crear();
        var filas = await cn.ExecuteAsync(
            "UPDATE dbo.AlertasSeguridad SET RevisadaEn = @Ahora, RevisadaPor = @U WHERE Id = @Id AND RevisadaEn IS NULL",
            new { reloj.Ahora, U = User.UsuarioId(), Id = id });
        if (filas == 0) throw ReglaNegocioException.NoEncontrado("La alerta pendiente");
        await auditoria.RegistrarAsync("REVISAR_ALERTA", "AlertaSeguridad", id);
        return NoContent();
    }
}
