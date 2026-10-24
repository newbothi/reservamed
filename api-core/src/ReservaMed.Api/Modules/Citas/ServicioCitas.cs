using System.Security.Claims;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Common;
using ReservaMed.Api.Integraciones.Bonos;
using ReservaMed.Api.Integraciones.Pagos;
using ReservaMed.Api.Modules.Agenda;
using ReservaMed.Contracts;

namespace ReservaMed.Api.Modules.Citas;

public enum ModalidadPago { ONLINE, EN_CLINICA }

public sealed record ReservarRequest(int ProfesionalId, int SedeId, DateTime Inicio, ModalidadPago ModalidadPago, int? PacienteId, string? Canal);

public sealed record ReservaCreada(int CitaId, string Estado, DateTime Inicio, DateTime? ExpiraEn);

public sealed record CancelarRequest(string? Motivo);

public sealed class CitaResumenDto
{
    public int Id { get; set; }
    public DateTime FechaHoraInicio { get; set; }
    public DateTime FechaHoraFin { get; set; }
    public string Estado { get; set; } = "";
    public string Canal { get; set; } = "";
    public DateTime? ExpiraEn { get; set; }
    public int ProfesionalId { get; set; }
    public string Profesional { get; set; } = "";
    public string Especialidad { get; set; } = "";
    public int SedeId { get; set; }
    public string Sede { get; set; } = "";
    public string DireccionSede { get; set; } = "";
    public string Prestacion { get; set; } = "";
    public long? FolioBono { get; set; }
    public int? MontoPagado { get; set; }
    public string? MedioPago { get; set; }
}

public sealed class ServicioCitas(
    ISqlConnectionFactory db,
    ServicioAgenda agenda,
    IReloj reloj,
    IOptions<ReglasNegocio> reglas,
    IEmisorBonos emisor,
    IPasarelaWebpay webpay,
    IAuditoria auditoria)
{
    private readonly ReglasNegocio _reglas = reglas.Value;

    public async Task<ReservaCreada> ReservarAsync(ClaimsPrincipal usuario, ReservarRequest r)
    {
        // Quién es el paciente: el propio usuario, o el que indique recepción.
        int pacienteId;
        string canal;
        if (usuario.Es(Roles.Paciente))
        {
            pacienteId = usuario.PacienteId() ?? throw ReglaNegocioException.Prohibido();
            canal = "WEB";
        }
        else if (usuario.Es(Roles.Recepcion) || usuario.Es(Roles.Admin))
        {
            pacienteId = r.PacienteId ?? throw new ReglaNegocioException("Indique el paciente.");
            canal = r.Canal is "TELEFONO" ? "TELEFONO" : "PRESENCIAL";
        }
        else throw ReglaNegocioException.Prohibido();

        // Pago online sólo tiene sentido en el portal; recepción reserva y se paga en caja.
        var modalidad = canal == "WEB" ? r.ModalidadPago : ModalidadPago.EN_CLINICA;

        var cupo = await agenda.ExigirCupoAsync(r.ProfesionalId, r.SedeId, r.Inicio);
        var ahora = reloj.Ahora;

        await using var cn = db.Crear();
        await cn.OpenAsync();

        // Regla: un paciente no puede tener dos citas activas el mismo día con el mismo profesional.
        var duplicada = await cn.ExecuteScalarAsync<int>("""
            SELECT COUNT(1) FROM dbo.Citas
            WHERE PacienteId = @PacienteId AND ProfesionalId = @ProfesionalId AND OcupaCupo = 1
              AND CAST(FechaHoraInicio AS DATE) = CAST(@Inicio AS DATE)
            """, new { PacienteId = pacienteId, r.ProfesionalId, r.Inicio });
        if (duplicada > 0)
            throw ReglaNegocioException.Conflicto("Ya tiene una hora con este profesional ese día.");

        // Regla: máximo 3 citas futuras pendientes por paciente (evita acaparar horas).
        var futuras = await cn.ExecuteScalarAsync<int>("""
            SELECT COUNT(1) FROM dbo.Citas
            WHERE PacienteId = @PacienteId AND OcupaCupo = 1 AND FechaHoraInicio > @Ahora
              AND Estado IN ('PENDIENTE_PAGO','RESERVADA','CONFIRMADA')
            """, new { PacienteId = pacienteId, Ahora = ahora });
        if (futuras >= 3 && canal == "WEB")
            throw new ReglaNegocioException("Tiene 3 horas pendientes. Anule alguna antes de reservar otra.");

        // A8: cada reserva con pago online bloquea el cupo mientras se paga. Un bot con varias
        // cuentas podría acaparar horas encadenando reservas que nunca paga: se limita por cuenta.
        if (modalidad == ModalidadPago.ONLINE)
        {
            var pendientes = await cn.ExecuteScalarAsync<int>("""
                SELECT COUNT(1) FROM dbo.Citas
                WHERE PacienteId = @PacienteId AND Estado = 'PENDIENTE_PAGO' AND OcupaCupo = 1
                """, new { PacienteId = pacienteId });
            if (pendientes >= _reglas.MaxReservasPendientesPago)
                throw new ReglaNegocioException(
                    $"Tiene {pendientes} reservas esperando pago. Pague o anule alguna antes de reservar otra.",
                    StatusCodes.Status409Conflict, "PENDIENTES_PAGO");
        }

        var estado = modalidad == ModalidadPago.ONLINE ? "PENDIENTE_PAGO" : "RESERVADA";
        DateTime? expira = modalidad == ModalidadPago.ONLINE ? ahora.AddMinutes(_reglas.MinutosReservaTemporal) : null;

        await using var tx = await cn.BeginTransactionAsync();
        int citaId;
        try
        {
            citaId = await cn.ExecuteScalarAsync<int>("""
                INSERT INTO dbo.Citas (PacienteId, ProfesionalId, SedeId, PrestacionId, FechaHoraInicio, FechaHoraFin,
                                       Estado, Canal, OcupaCupo, ExpiraEn, CreadoPorUsuarioId, CreadoEn)
                OUTPUT INSERTED.Id
                VALUES (@PacienteId, @ProfesionalId, @SedeId, @PrestacionId, @Inicio, @Fin,
                        @Estado, @Canal, 1, @Expira, @UsuarioId, @Ahora);
                """,
                new
                {
                    PacienteId = pacienteId, cupo.ProfesionalId, cupo.SedeId, cupo.PrestacionId,
                    cupo.Inicio, cupo.Fin, Estado = estado, Canal = canal, Expira = expira,
                    UsuarioId = usuario.UsuarioId(), Ahora = ahora
                }, tx);
        }
        catch (SqlException ex) when (SqlErrores.EsDuplicado(ex))
        {
            // Dos personas apretaron "reservar" sobre el mismo cupo: gana la primera inserción.
            throw ReglaNegocioException.Conflicto("Otra persona acaba de tomar esa hora. Elija otra, por favor.");
        }

        if (estado == "RESERVADA")
            await EventosCita.PublicarAsync(cn, tx, TiposEvento.CitaReservada, citaId);

        await tx.CommitAsync();
        await auditoria.RegistrarAsync("RESERVAR_CITA", "Cita", citaId, $"canal={canal} pago={modalidad}");
        return new ReservaCreada(citaId, estado, cupo.Inicio, expira);
    }

    public async Task<IEnumerable<CitaResumenDto>> DelPacienteAsync(int pacienteId)
    {
        await using var cn = db.Crear();
        return await cn.QueryAsync<CitaResumenDto>(SqlResumen + """
             WHERE c.PacienteId = @PacienteId AND c.Estado <> 'EXPIRADA'
             ORDER BY c.FechaHoraInicio DESC
            """, new { PacienteId = pacienteId });
    }

    public async Task<CitaResumenDto> ObtenerAsync(ClaimsPrincipal usuario, int citaId)
    {
        await using var cn = db.Crear();
        var cita = await cn.QuerySingleOrDefaultAsync<CitaResumenDto>(SqlResumen + " WHERE c.Id = @Id",
            new { Id = citaId }) ?? throw ReglaNegocioException.NoEncontrado("La cita");

        await ExigirAccesoAsync(cn, usuario, citaId);
        return cita;
    }

    public async Task CancelarAsync(ClaimsPrincipal usuario, int citaId, string? motivo)
    {
        await using var cn = db.Crear();
        await cn.OpenAsync();
        await ExigirAccesoAsync(cn, usuario, citaId);

        var c = await RepositorioContexto.CargarAsync(cn, citaId);
        var ahora = reloj.Ahora;

        if (c.Estado is not ("PENDIENTE_PAGO" or "RESERVADA" or "CONFIRMADA"))
            throw new ReglaNegocioException($"Una cita en estado {c.Estado} no se puede anular.");

        if (usuario.Es(Roles.Paciente) && c.FechaHoraInicio - ahora < TimeSpan.FromHours(_reglas.HorasMinimasParaAnular))
            throw new ReglaNegocioException(
                $"Las horas se pueden anular por la web hasta {_reglas.HorasMinimasParaAnular} horas antes. Llame a la clínica.");

        // Si se pagó online, se anula el bono y se reversa el cargo Webpay.
        var pagoWeb = await cn.QuerySingleOrDefaultAsync<(int Id, string? Token, int Monto, int? BonoId)>("""
            SELECT TOP 1 Id, TokenWebpay, Monto, BonoId FROM dbo.Pagos
            WHERE CitaId = @Id AND Medio = 'WEBPAY' AND Estado = 'AUTORIZADO'
            """, new { Id = citaId });

        await using var tx = await cn.BeginTransactionAsync();
        var filas = await cn.ExecuteAsync("""
            UPDATE dbo.Citas
            SET Estado = 'CANCELADA', OcupaCupo = 0, ExpiraEn = NULL, MotivoCancelacion = @Motivo
            WHERE Id = @Id AND RowVer = @RowVer
            """, new { Id = citaId, c.RowVer, Motivo = motivo ?? (usuario.Es(Roles.Paciente) ? "Anulada por el paciente" : "Anulada por la clínica") }, tx);
        if (filas == 0)
            throw ReglaNegocioException.Conflicto("La cita fue modificada por otra persona. Recargue e intente de nuevo.");

        long? folio = null;
        if (pagoWeb.Id != 0)
        {
            await cn.ExecuteAsync("UPDATE dbo.Pagos SET Estado = 'ANULADO' WHERE Id = @Id", new { pagoWeb.Id }, tx);
            if (pagoWeb.BonoId is int bonoId)
                folio = await cn.ExecuteScalarAsync<long>(
                    "UPDATE dbo.Bonos SET Estado = 'ANULADO' OUTPUT INSERTED.Folio WHERE Id = @Id", new { Id = bonoId }, tx);
        }
        // Libera también pagos Webpay que quedaron a medio camino.
        await cn.ExecuteAsync("UPDATE dbo.Pagos SET Estado = 'ANULADO' WHERE CitaId = @Id AND Estado = 'PENDIENTE'", new { Id = citaId }, tx);

        await EventosCita.PublicarAsync(cn, tx, TiposEvento.CitaCancelada, citaId, folio, pagoWeb.Id != 0 ? pagoWeb.Monto : null, motivo);
        await tx.CommitAsync();

        // Llamadas externas después del commit: si fallan, el estado interno ya es consistente
        // y quedan en el log para gestión manual (en producción, se reintentarían con una cola).
        if (pagoWeb.Token is not null) await webpay.ReversarAsync(pagoWeb.Token, pagoWeb.Monto);
        if (folio is long f) await emisor.AnularAsync(f, "Cita anulada");

        await auditoria.RegistrarAsync("CANCELAR_CITA", "Cita", citaId, motivo);
    }

    /// <summary>El médico marca la atención como realizada (o que el paciente no llegó).</summary>
    public async Task CerrarAtencionAsync(ClaimsPrincipal usuario, int citaId, bool asistio)
    {
        await using var cn = db.Crear();
        await cn.OpenAsync();
        var c = await RepositorioContexto.CargarAsync(cn, citaId);

        if (usuario.Es(Roles.Medico) && usuario.ProfesionalId() != c.ProfesionalId)
            throw ReglaNegocioException.Prohibido();

        var (desde, hacia) = asistio
            ? (new[] { "ADMITIDA" }, "ATENDIDA")
            : (new[] { "RESERVADA", "CONFIRMADA" }, "NO_ASISTIO");
        if (!desde.Contains(c.Estado))
            throw new ReglaNegocioException($"No se puede pasar de {c.Estado} a {hacia}.");

        var filas = await cn.ExecuteAsync(
            "UPDATE dbo.Citas SET Estado = @Hacia WHERE Id = @Id AND RowVer = @RowVer",
            new { Hacia = hacia, Id = citaId, c.RowVer });
        if (filas == 0) throw ReglaNegocioException.Conflicto("La cita fue modificada por otra persona.");

        await auditoria.RegistrarAsync(asistio ? "CITA_ATENDIDA" : "CITA_NO_ASISTIO", "Cita", citaId);
    }

    private static async Task ExigirAccesoAsync(System.Data.IDbConnection cn, ClaimsPrincipal usuario, int citaId)
    {
        if (usuario.Es(Roles.Recepcion) || usuario.Es(Roles.Admin)) return;

        var (pacienteId, profesionalId) = await cn.QuerySingleOrDefaultAsync<(int PacienteId, int ProfesionalId)>(
            "SELECT PacienteId, ProfesionalId FROM dbo.Citas WHERE Id = @Id", new { Id = citaId });

        var permitido = usuario.Es(Roles.Paciente) ? usuario.PacienteId() == pacienteId
                      : usuario.Es(Roles.Medico) && usuario.ProfesionalId() == profesionalId;
        if (!permitido) throw ReglaNegocioException.NoEncontrado("La cita"); // 404 para no revelar que existe
    }

    private const string SqlResumen = """
        SELECT c.Id, c.FechaHoraInicio, c.FechaHoraFin, c.Estado, c.Canal, c.ExpiraEn,
               pr.Id AS ProfesionalId, pr.Nombres + ' ' + pr.ApellidoPaterno AS Profesional, e.Nombre AS Especialidad,
               s.Id AS SedeId, s.Nombre AS Sede, s.Direccion + ', ' + s.Comuna AS DireccionSede,
               ps.Descripcion AS Prestacion,
               b.Folio AS FolioBono, pg.Monto AS MontoPagado, pg.Medio AS MedioPago
        FROM dbo.Citas c
        JOIN dbo.Profesionales pr ON pr.Id = c.ProfesionalId
        JOIN dbo.Especialidades e ON e.Id = pr.EspecialidadId
        JOIN dbo.Sedes s          ON s.Id = c.SedeId
        JOIN dbo.Prestaciones ps  ON ps.Id = c.PrestacionId
        LEFT JOIN dbo.Bonos b     ON b.CitaId = c.Id AND b.Estado <> 'ANULADO'
        OUTER APPLY (SELECT TOP 1 Monto, Medio FROM dbo.Pagos p
                     WHERE p.CitaId = c.Id AND p.Estado = 'AUTORIZADO' ORDER BY p.Id DESC) pg
        """;
}
