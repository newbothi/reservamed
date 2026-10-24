using System.Security.Claims;
using Dapper;
using Microsoft.Data.SqlClient;
using ReservaMed.Api.Common;
using ReservaMed.Api.Integraciones.Bonos;
using ReservaMed.Api.Integraciones.Pagos;
using ReservaMed.Api.Modules.Citas;
using ReservaMed.Contracts;

namespace ReservaMed.Api.Modules.Admision;

public sealed class PrevisionDto
{
    public string Financiador { get; set; } = "";
    public string Tipo { get; set; } = "";
    public string? PlanCodigo { get; set; }
    public string? PlanNombre { get; set; }
    public string? TramoFonasa { get; set; }
    public bool EsCarga { get; set; }
    public string? AfiliadoRut { get; set; }
    public string? AfiliadoNombre { get; set; }
}

public sealed class PacienteAdmisionDto
{
    public int Id { get; set; }
    public string Rut { get; set; } = "";
    public string NombreCompleto { get; set; } = "";
    public DateTime FechaNacimiento { get; set; }
    public int Edad { get; set; }
    public string Sexo { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Telefono { get; set; }
    public PrevisionDto? Prevision { get; set; }
    public List<CitaResumenDto> ProximasCitas { get; set; } = [];
}

public sealed class PagoDto
{
    public int Id { get; set; }
    public string Medio { get; set; } = "";
    public int Monto { get; set; }
    public string Estado { get; set; } = "";
    public string? CodigoAutorizacion { get; set; }
    public string? NumeroOperacion { get; set; }
    public string? NumeroUnico { get; set; }
    public string? Terminal { get; set; }
    public string? UltimosDigitos { get; set; }
    public int? Cuotas { get; set; }
    public string? BuyOrder { get; set; }
    public DateTime? FechaPago { get; set; }
}

public sealed record CitaAdmisionDto(
    int CitaId, string Estado, DateTime FechaHoraInicio, string Sede, string Profesional, string Especialidad,
    string Prestacion, string PacienteRut, string PacienteNombre, string? Financiador, string? PlanCodigo,
    string? TramoFonasa, bool EsCarga, bool EsParticular, string? AvisoParticular, string? NumeroConvenio,
    BonoDto? Bono, IReadOnlyList<PagoDto> Pagos);

/// <summary>C3: <paramref name="ComprobanteHuella"/> lo firma el lector biométrico, no el navegador.</summary>
public sealed record EmitirBonoRequest(string? ComprobanteHuella, string? DerivadorRut);

public enum MedioCaja { POS_DEBITO, POS_CREDITO, EFECTIVO }

public sealed record BuscarPacienteRequest(string Rut);

public sealed record CobrarRequest(MedioCaja Medio, string? UltimosDigitos, int? Cuotas);

public sealed record CrearPacienteRequest(
    string Rut, string Nombres, string ApellidoPaterno, string? ApellidoMaterno, DateTime FechaNacimiento,
    string Sexo, string Email, string? Telefono, string? Comuna);

public sealed record PrevisionRequest(int FinanciadorId, int? PlanSaludId, string? TramoFonasa, bool EsCarga, string? AfiliadoRut, string? AfiliadoNombre);

public sealed class ServicioAdmision(
    ISqlConnectionFactory db,
    IReloj reloj,
    IEmisorBonos emisor,
    ITerminalPos pos,
    ServicioValorizacion valorizacion,
    PoliticaAcceso politica,
    VerificadorHuella huellas,
    IAuditoria auditoria,
    ILogger<ServicioAdmision> log)
{
    public async Task<PacienteAdmisionDto> BuscarPacienteAsync(string rutEntrada)
    {
        var rut = Rut.Exigir(rutEntrada);
        await using var cn = db.Crear();

        var p = await cn.QuerySingleOrDefaultAsync<PacienteAdmisionDto>("""
            SELECT Id, Rut, Nombres + ' ' + ApellidoPaterno + ISNULL(' ' + ApellidoMaterno, '') AS NombreCompleto,
                   FechaNacimiento, Sexo, Email, Telefono
            FROM dbo.Pacientes WHERE Rut = @Rut
            """, new { Rut = rut }) ?? throw ReglaNegocioException.NoEncontrado("El paciente");

        var hoy = reloj.Hoy;
        p.Edad = hoy.Year - p.FechaNacimiento.Year - (hoy < p.FechaNacimiento.AddYears(hoy.Year - p.FechaNacimiento.Year) ? 1 : 0);

        p.Prevision = await cn.QuerySingleOrDefaultAsync<PrevisionDto>("""
            SELECT f.Nombre AS Financiador, f.Tipo, pl.Codigo AS PlanCodigo, pl.Nombre AS PlanNombre,
                   pv.TramoFonasa, pv.EsCarga, pv.AfiliadoRut, pv.AfiliadoNombre
            FROM dbo.Previsiones pv
            JOIN dbo.Financiadores f ON f.Id = pv.FinanciadorId
            LEFT JOIN dbo.PlanesSalud pl ON pl.Id = pv.PlanSaludId
            WHERE pv.PacienteId = @Id AND pv.Vigente = 1
            """, new { p.Id });

        p.ProximasCitas = (await cn.QueryAsync<CitaResumenDto>("""
            SELECT c.Id, c.FechaHoraInicio, c.FechaHoraFin, c.Estado, c.Canal, c.ExpiraEn,
                   pr.Id AS ProfesionalId, pr.Nombres + ' ' + pr.ApellidoPaterno AS Profesional, e.Nombre AS Especialidad,
                   s.Id AS SedeId, s.Nombre AS Sede, s.Direccion AS DireccionSede, ps.Descripcion AS Prestacion,
                   b.Folio AS FolioBono
            FROM dbo.Citas c
            JOIN dbo.Profesionales pr ON pr.Id = c.ProfesionalId
            JOIN dbo.Especialidades e ON e.Id = pr.EspecialidadId
            JOIN dbo.Sedes s ON s.Id = c.SedeId
            JOIN dbo.Prestaciones ps ON ps.Id = c.PrestacionId
            LEFT JOIN dbo.Bonos b ON b.CitaId = c.Id AND b.Estado <> 'ANULADO'
            WHERE c.PacienteId = @Id AND c.FechaHoraInicio >= @Hoy AND c.OcupaCupo = 1
            ORDER BY c.FechaHoraInicio
            """, new { p.Id, Hoy = hoy })).ToList();

        await auditoria.RegistrarAsync("VER_PACIENTE", "Paciente", p.Id, "Búsqueda por RUT en admisión");
        return p;
    }

    public async Task<int> CrearPacienteAsync(CrearPacienteRequest r)
    {
        var rut = Rut.Exigir(r.Rut);
        if (string.IsNullOrWhiteSpace(r.Nombres) || string.IsNullOrWhiteSpace(r.ApellidoPaterno))
            throw new ReglaNegocioException("Nombre y apellido paterno son obligatorios.");
        if (r.Sexo is not ("M" or "F" or "X")) throw new ReglaNegocioException("Sexo debe ser M, F o X.");

        await using var cn = db.Crear();
        try
        {
            var id = await cn.ExecuteScalarAsync<int>("""
                INSERT INTO dbo.Pacientes (Rut, Nombres, ApellidoPaterno, ApellidoMaterno, FechaNacimiento, Sexo, Email, Telefono, Comuna)
                OUTPUT INSERTED.Id
                VALUES (@Rut, @Nombres, @ApellidoPaterno, @ApellidoMaterno, @FechaNacimiento, @Sexo, @Email, @Telefono, @Comuna);
                """, new { Rut = rut, r.Nombres, r.ApellidoPaterno, r.ApellidoMaterno, FechaNacimiento = r.FechaNacimiento.Date,
                           r.Sexo, Email = r.Email.Trim().ToLowerInvariant(), r.Telefono, r.Comuna });
            await auditoria.RegistrarAsync("CREAR_PACIENTE", "Paciente", id);
            return id;
        }
        catch (SqlException ex) when (SqlErrores.EsDuplicado(ex))
        {
            throw ReglaNegocioException.Conflicto("Ya existe un paciente con ese RUT.");
        }
    }

    /// <summary>Recepción actualiza la previsión (cambio de Isapre, de tramo, paso a carga, etc.).</summary>
    public async Task ActualizarPrevisionAsync(int pacienteId, PrevisionRequest r)
    {
        string? afiliadoRut = r.EsCarga ? Rut.Exigir(r.AfiliadoRut, "RUT del titular") : null;

        await using var cn = db.Crear();
        await cn.OpenAsync();
        var tipo = await cn.ExecuteScalarAsync<string?>("SELECT Tipo FROM dbo.Financiadores WHERE Id = @Id", new { Id = r.FinanciadorId })
                   ?? throw new ReglaNegocioException("Financiador no válido.");
        if (tipo == "FONASA" && r.TramoFonasa is not ("A" or "B" or "C" or "D"))
            throw new ReglaNegocioException("Indique el tramo Fonasa.");

        var planId = tipo switch
        {
            "FONASA" => await cn.ExecuteScalarAsync<int?>("SELECT TOP 1 Id FROM dbo.PlanesSalud WHERE FinanciadorId = @F", new { F = r.FinanciadorId }),
            "ISAPRE" => r.PlanSaludId ?? throw new ReglaNegocioException("Indique el plan de la Isapre."),
            _ => (int?)null
        };

        await using var tx = await cn.BeginTransactionAsync();
        await cn.ExecuteAsync("UPDATE dbo.Previsiones SET Vigente = 0 WHERE PacienteId = @Id AND Vigente = 1", new { Id = pacienteId }, tx);
        await cn.ExecuteAsync("""
            INSERT INTO dbo.Previsiones (PacienteId, FinanciadorId, PlanSaludId, TramoFonasa, EsCarga, AfiliadoRut, AfiliadoNombre, ActualizadaEn)
            VALUES (@PacienteId, @FinanciadorId, @PlanId, @Tramo, @EsCarga, @AfiliadoRut, @AfiliadoNombre, @Ahora);
            """, new
            {
                PacienteId = pacienteId, r.FinanciadorId, PlanId = planId,
                Tramo = tipo == "FONASA" ? r.TramoFonasa : null,
                EsCarga = r.EsCarga && tipo != "PARTICULAR", AfiliadoRut = afiliadoRut,
                AfiliadoNombre = r.EsCarga ? r.AfiliadoNombre?.Trim().ToUpperInvariant() : null,
                Ahora = reloj.Ahora
            }, tx);
        await tx.CommitAsync();
        await auditoria.RegistrarAsync("ACTUALIZAR_PREVISION", "Paciente", pacienteId, tipo);
    }

    public async Task<CitaAdmisionDto> ObtenerCitaAsync(ClaimsPrincipal usuario, int citaId)
    {
        await using var cn = db.Crear();
        await politica.ExigirCitaAsync(usuario, citaId, cn);
        var c = await RepositorioContexto.CargarAsync(cn, citaId);
        var bono = await RepositorioBonos.ActivoDeCitaAsync(cn, citaId);
        var pagos = (await cn.QueryAsync<PagoDto>(
            "SELECT * FROM dbo.Pagos WHERE CitaId = @Id ORDER BY Id DESC", new { Id = citaId })).ToList();

        await auditoria.RegistrarAsync("VER_CITA", "Cita", citaId);
        return new CitaAdmisionDto(c.CitaId, c.Estado, c.FechaHoraInicio, c.Sede, c.ProfesionalNombre, c.Especialidad,
            $"{c.PrestacionCodigo} {c.PrestacionDescripcion}", c.PacienteRut, c.PacienteNombreBono,
            c.FinanciadorNombre, c.PlanCodigo, c.TramoFonasa, c.EsCarga, c.EsParticular, c.AvisoParticular,
            c.NumeroConvenio, bono, pagos);
    }

    /// <summary>Consulta al financiador cuánto bonifica (sin emitir nada todavía).</summary>
    public async Task<ResultadoValorizacion> ValorizarAsync(ClaimsPrincipal usuario, int citaId)
    {
        await using var cn = db.Crear();
        await politica.ExigirCitaAsync(usuario, citaId, cn);   // A3: recepción solo su sede
        var c = await RepositorioContexto.CargarAsync(cn, citaId);
        await auditoria.RegistrarAsync("VALORIZAR", "Cita", citaId);   // M6: consulta la previsión del paciente
        return await valorizacion.ValorizarAsync(c);
    }

    /// <summary>
    /// C3: pide al servidor un desafío de un solo uso para que el lector de la sede firme la
    /// lectura de huella de ESTE beneficiario para ESTA cita.
    /// </summary>
    public async Task<DesafioHuellaDto> DesafioHuellaAsync(ClaimsPrincipal usuario, int citaId)
    {
        await using var cn = db.Crear();
        await politica.ExigirCitaAsync(usuario, citaId, cn);
        var c = await RepositorioContexto.CargarAsync(cn, citaId);
        if (c.Estado != "RESERVADA")
            throw new ReglaNegocioException($"No se puede emitir bono para una cita en estado {c.Estado}.");
        if (c.EsParticular)
            throw new ReglaNegocioException(c.AvisoParticular ?? "Paciente particular: no requiere bono.");
        await auditoria.RegistrarAsync("HUELLA_DESAFIO", "Cita", citaId);
        return huellas.CrearDesafio(citaId, usuario.UsuarioId(), c.PacienteRut, c.SedeId);
    }

    /// <summary>
    /// Emite el bono electrónico con firma biométrica (huella) del beneficiario.
    /// El bono sólo es válido el día de su emisión, por eso sólo se emite para citas de hoy.
    /// </summary>
    public async Task<BonoDto> EmitirBonoAsync(ClaimsPrincipal usuario, int citaId, EmitirBonoRequest r)
    {
        await using var cn = db.Crear();
        await cn.OpenAsync();
        await politica.ExigirCitaAsync(usuario, citaId, cn);   // A3: recepción solo su sede
        var c = await RepositorioContexto.CargarAsync(cn, citaId);

        if (c.Estado != "RESERVADA")
            throw new ReglaNegocioException(c.Estado == "CONFIRMADA"
                ? "La cita ya fue pagada online con bono web. Sólo falta admitirla."
                : $"No se puede emitir bono para una cita en estado {c.Estado}.");
        if (c.FechaHoraInicio.Date != reloj.Hoy)
            throw new ReglaNegocioException("El bono es válido sólo para la fecha de emisión: se emite el día de la atención.");
        if (c.EsParticular)
            throw new ReglaNegocioException(c.AvisoParticular ?? "Paciente particular: no requiere bono.");

        var existente = await RepositorioBonos.ActivoDeCitaAsync(cn, citaId);
        if (existente is not null)
            throw ReglaNegocioException.Conflicto($"La cita ya tiene el bono folio {existente.Folio} ({existente.Estado}).");

        var derivador = string.IsNullOrWhiteSpace(r.DerivadorRut) ? null : Rut.Exigir(r.DerivadorRut, "RUT del médico derivador");

        // C3: la huella la afirma el lector firmado, nunca el navegador.
        huellas.Exigir(r.ComprobanteHuella, citaId, usuario.UsuarioId(), c.PacienteRut, c.SedeId);

        var emitido = await emisor.EmitirAsync(new SolicitudEmision(
            RepositorioContexto.SolicitudDesde(c), TipoFirma.HUELLA, true, c.RutInstitucion, derivador));

        await using var tx = await cn.BeginTransactionAsync();
        try
        {
            await RepositorioBonos.InsertarAsync(cn, tx, c, emitido, "PRESENCIAL", TipoFirma.HUELLA, "EMITIDO",
                usuario.UsuarioId(), derivador);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            // El financiador ya emitió el bono: hay que anularlo para no dejarlo colgando.
            await emisor.AnularAsync(emitido.Folio, "Error al registrar en clínica");
            throw;
        }

        await auditoria.RegistrarAsync("EMITIR_BONO", "Bono", emitido.Folio, $"cita={citaId} copago={emitido.Valorizacion.TotalCopago}");
        return (await RepositorioBonos.ActivoDeCitaAsync(cn, citaId))!;
    }

    public async Task AnularBonoAsync(ClaimsPrincipal usuario, int citaId)
    {
        await using var cn = db.Crear();
        await politica.ExigirCitaAsync(usuario, citaId, cn);   // A3: recepción solo su sede
        var c = await RepositorioContexto.CargarAsync(cn, citaId);

        var bono = await RepositorioBonos.ActivoDeCitaAsync(cn, citaId)
                   ?? throw ReglaNegocioException.NoEncontrado("El bono");
        if (bono.Estado != "EMITIDO")
            throw new ReglaNegocioException("Sólo se puede anular un bono que no ha sido pagado.");

        await cn.ExecuteAsync("UPDATE dbo.Bonos SET Estado = 'ANULADO' WHERE Id = @Id AND Estado = 'EMITIDO'", new { bono.Id });
        await emisor.AnularAsync(bono.Folio, "Anulado en admisión");
        await auditoria.RegistrarAsync("ANULAR_BONO", "Bono", bono.Folio);
    }

    /// <summary>
    /// Cobra el copago (o el valor particular) en caja y admite al paciente.
    /// Orden real en el mesón: bono emitido → cobro en POS → paciente admitido.
    /// </summary>
    public async Task<CitaAdmisionDto> CobrarAsync(ClaimsPrincipal usuario, int citaId, CobrarRequest r)
    {
        await using var cn = db.Crear();
        await cn.OpenAsync();
        await politica.ExigirCitaAsync(usuario, citaId, cn);   // A3: recepción solo su sede
        var c = await RepositorioContexto.CargarAsync(cn, citaId);

        if (c.Estado != "RESERVADA")
            throw new ReglaNegocioException(c.Estado == "CONFIRMADA"
                ? "La cita ya está pagada. Use \"Admitir\"."
                : $"No se puede cobrar una cita en estado {c.Estado}.");
        if (c.FechaHoraInicio.Date != reloj.Hoy)
            throw new ReglaNegocioException("Sólo se cobran y admiten citas del día.");

        BonoDto? bono = null;
        int monto;
        if (c.EsParticular)
        {
            monto = c.ValorParticular;
        }
        else
        {
            bono = await RepositorioBonos.ActivoDeCitaAsync(cn, citaId)
                   ?? throw new ReglaNegocioException("Primero emita el bono con la huella del paciente.");
            if (bono.Estado != "EMITIDO") throw new ReglaNegocioException("El bono ya fue pagado.");
            if (bono.ValidoHasta.Date < reloj.Hoy)
                throw new ReglaNegocioException("El bono venció (sólo vale el día de emisión). Anúlelo y emita uno nuevo.");
            monto = bono.TotalCopago;
        }

        // Cobro en el POS (o efectivo). Si el copago es $0, no se pasa tarjeta.
        VoucherPos? voucher = null;
        var medio = monto == 0 ? MedioCaja.EFECTIVO : r.Medio;
        if (medio != MedioCaja.EFECTIVO)
        {
            var ultimos = new string((r.UltimosDigitos ?? "").Where(char.IsDigit).ToArray());
            if (ultimos.Length != 4) throw new ReglaNegocioException("Indique los últimos 4 dígitos de la tarjeta.");
            var cuotas = medio == MedioCaja.POS_CREDITO ? Math.Clamp(r.Cuotas ?? 1, 1, 12) : 0;

            voucher = await pos.CobrarAsync(new CobroPos(monto,
                medio == MedioCaja.POS_DEBITO ? MedioPos.POS_DEBITO : MedioPos.POS_CREDITO, ultimos, cuotas));

            if (!voucher.Aprobado)
            {
                await cn.ExecuteAsync("""
                    INSERT INTO dbo.Pagos (CitaId, BonoId, Medio, Monto, Estado, Terminal, UltimosDigitos, CreadoEn, UsuarioCajaId)
                    VALUES (@CitaId, @BonoId, @Medio, @Monto, 'RECHAZADO', @Terminal, @Ultimos, @Ahora, @Usuario);
                    """, new { c.CitaId, BonoId = bono?.Id, Medio = medio.ToString(), Monto = monto, voucher.Terminal,
                               Ultimos = ultimos, Ahora = reloj.Ahora, Usuario = usuario.UsuarioId() });
                throw new ReglaNegocioException(voucher.MotivoRechazo ?? "Pago rechazado.", codigo: "POS_RECHAZO");
            }
        }

        var ahora = reloj.Ahora;
        await using var tx = await cn.BeginTransactionAsync();
        await cn.ExecuteAsync("""
            INSERT INTO dbo.Pagos (CitaId, BonoId, Medio, Monto, Estado, CodigoAutorizacion, NumeroOperacion, NumeroUnico,
                                   Terminal, UltimosDigitos, Cuotas, CreadoEn, FechaPago, UsuarioCajaId)
            VALUES (@CitaId, @BonoId, @Medio, @Monto, 'AUTORIZADO', @Aut, @Op, @Unico, @Terminal, @Ultimos, @Cuotas, @Ahora, @Ahora, @Usuario);
            """, new
            {
                c.CitaId, BonoId = bono?.Id, Medio = medio.ToString(), Monto = monto,
                Aut = voucher?.CodigoAutorizacion, Op = voucher?.NumeroOperacion, Unico = voucher?.NumeroUnico,
                Terminal = voucher?.Terminal, Ultimos = voucher?.UltimosDigitos, Cuotas = voucher?.Cuotas,
                Ahora = ahora, Usuario = usuario.UsuarioId()
            }, tx);

        if (bono is not null)
            await cn.ExecuteAsync("UPDATE dbo.Bonos SET Estado = 'PAGADO' WHERE Id = @Id AND Estado = 'EMITIDO'", new { bono.Id }, tx);

        var filas = await cn.ExecuteAsync(
            "UPDATE dbo.Citas SET Estado = 'ADMITIDA' WHERE Id = @Id AND RowVer = @RowVer",
            new { Id = citaId, c.RowVer }, tx);
        if (filas == 0)
        {
            await tx.RollbackAsync();
            log.LogError("Cobro aprobado {Aut} pero la cita {Cita} cambió durante el pago", voucher?.CodigoAutorizacion, citaId);
            throw ReglaNegocioException.Conflicto(
                $"La cita fue modificada mientras se cobraba. Anule el voucher {voucher?.CodigoAutorizacion} en el POS y reintente.");
        }

        await EventosCita.PublicarAsync(cn, tx, TiposEvento.CitaAdmitida, citaId);
        await tx.CommitAsync();

        await auditoria.RegistrarAsync("COBRAR", "Cita", citaId, $"{medio} ${monto} aut={voucher?.CodigoAutorizacion}");
        return await ObtenerCitaAsync(usuario, citaId);
    }

    /// <summary>Admite una cita que ya se pagó online (Webpay + bono web).</summary>
    public async Task<CitaAdmisionDto> AdmitirPagadaAsync(ClaimsPrincipal usuario, int citaId)
    {
        await using var cn = db.Crear();
        await cn.OpenAsync();
        await politica.ExigirCitaAsync(usuario, citaId, cn);   // A3: recepción solo su sede
        var c = await RepositorioContexto.CargarAsync(cn, citaId);

        if (c.Estado != "CONFIRMADA") throw new ReglaNegocioException("Sólo se admiten aquí citas ya pagadas online.");
        if (c.FechaHoraInicio.Date != reloj.Hoy) throw new ReglaNegocioException("Sólo se admiten citas del día.");

        await using var tx = await cn.BeginTransactionAsync();
        var filas = await cn.ExecuteAsync("UPDATE dbo.Citas SET Estado = 'ADMITIDA' WHERE Id = @Id AND RowVer = @RowVer",
            new { Id = citaId, c.RowVer }, tx);
        if (filas == 0) throw ReglaNegocioException.Conflicto("La cita fue modificada por otra persona.");
        await EventosCita.PublicarAsync(cn, tx, TiposEvento.CitaAdmitida, citaId);
        await tx.CommitAsync();

        await auditoria.RegistrarAsync("ADMITIR", "Cita", citaId, "pagada online");
        return await ObtenerCitaAsync(usuario, citaId);
    }
}
