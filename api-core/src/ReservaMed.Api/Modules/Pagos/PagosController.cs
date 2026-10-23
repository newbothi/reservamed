using System.Security.Claims;
using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReservaMed.Api.Common;
using ReservaMed.Api.Integraciones.Bonos;
using ReservaMed.Api.Integraciones.Pagos;
using ReservaMed.Api.Modules.Admision;
using ReservaMed.Api.Modules.Citas;
using ReservaMed.Contracts;

namespace ReservaMed.Api.Modules.Pagos;

public sealed record IniciarPagoRequest(int CitaId);
public sealed record PagoIniciado(string Token, string Url, int Monto, ResultadoValorizacion Valorizacion);
public sealed record ConfirmarPagoRequest(string Token);
public sealed record PagoConfirmado(bool Aprobado, string Mensaje, int CitaId, string EstadoCita, int Monto,
    string? CodigoAutorizacion, string? UltimosDigitos, long? FolioBono);

/// <summary>
/// Pago online desde el portal: se valoriza con el financiador (bono web), se cobra el copago
/// en Webpay y, si se aprueba, se emite el bono y la cita queda CONFIRMADA.
/// </summary>
public sealed class ServicioPagosWeb(
    ISqlConnectionFactory db,
    IReloj reloj,
    IEmisorBonos emisor,
    IPasarelaWebpay webpay,
    ServicioValorizacion valorizacion,
    IOptions<OpcionesWebpay> opcionesWebpay,
    IAuditoria auditoria,
    ILogger<ServicioPagosWeb> log)
{
    public async Task<PagoIniciado> IniciarAsync(ClaimsPrincipal usuario, int citaId)
    {
        await using var cn = db.Crear();
        await cn.OpenAsync();
        var c = await RepositorioContexto.CargarAsync(cn, citaId);
        ExigirPropia(usuario, c);

        if (c.Estado != "PENDIENTE_PAGO")
            throw new ReglaNegocioException($"La cita está {c.Estado}: no requiere pago online.");
        if (c.ExpiraEn is DateTime exp && exp <= reloj.Ahora)
            throw new ReglaNegocioException("El tiempo para pagar esta reserva expiró. Reserve nuevamente.");

        var v = await valorizacion.ValorizarAsync(c);
        var monto = v.TotalCopago;
        if (monto <= 0)
            throw new ReglaNegocioException("El copago es $0: presente su previsión en la clínica para emitir el bono.");

        // buy_order: máximo 26 caracteres en Webpay
        var buyOrder = $"RM{citaId}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
        var tx = await webpay.CrearAsync(buyOrder, $"u{usuario.UsuarioId()}", monto, opcionesWebpay.Value.ReturnUrl);

        await cn.ExecuteAsync("""
            INSERT INTO dbo.Pagos (CitaId, Medio, Monto, Estado, BuyOrder, TokenWebpay, CreadoEn)
            VALUES (@CitaId, 'WEBPAY', @Monto, 'PENDIENTE', @BuyOrder, @Token, @Ahora);
            """, new { CitaId = citaId, Monto = monto, BuyOrder = buyOrder, tx.Token, Ahora = reloj.Ahora });

        return new PagoIniciado(tx.Token, tx.Url, monto, v);
    }

    public async Task<PagoConfirmado> ConfirmarAsync(ClaimsPrincipal usuario, string token)
    {
        await using var cn = db.Crear();
        await cn.OpenAsync();

        var pago = await cn.QuerySingleOrDefaultAsync<(int Id, int CitaId, int Monto, string Estado)>(
            "SELECT Id, CitaId, Monto, Estado FROM dbo.Pagos WHERE TokenWebpay = @Token", new { Token = token });
        if (pago.Id == 0) throw ReglaNegocioException.NoEncontrado("El pago");

        var c = await RepositorioContexto.CargarAsync(cn, pago.CitaId);
        ExigirPropia(usuario, c);

        if (pago.Estado != "PENDIENTE")
            return await ResumenAsync(cn, c.CitaId, pago.Estado == "AUTORIZADO", "Este pago ya fue procesado.");

        var r = await webpay.ConfirmarAsync(token);
        var ahora = reloj.Ahora;

        if (!r.Autorizada)
        {
            await cn.ExecuteAsync("UPDATE dbo.Pagos SET Estado = 'RECHAZADO', UltimosDigitos = @U WHERE Id = @Id",
                new { U = r.UltimosDigitos, pago.Id });
            return await ResumenAsync(cn, c.CitaId, false,
                "El pago fue rechazado. Puede intentarlo de nuevo mientras su reserva siga vigente.");
        }

        // Si la reserva expiró mientras el usuario estaba en Webpay, se devuelve el dinero.
        if (c.Estado != "PENDIENTE_PAGO")
        {
            await webpay.ReversarAsync(token, r.Monto);
            await cn.ExecuteAsync("UPDATE dbo.Pagos SET Estado = 'ANULADO' WHERE Id = @Id", new { pago.Id });
            return await ResumenAsync(cn, c.CitaId, false,
                "Su reserva expiró antes de completar el pago. El cargo fue reversado.");
        }

        // Bono web: el beneficiario se autentica con el financiador en lugar de poner la huella.
        BonoEmitido? bono = null;
        if (!c.EsParticular)
        {
            try
            {
                bono = await emisor.EmitirAsync(new SolicitudEmision(
                    RepositorioContexto.SolicitudDesde(c), TipoFirma.CLAVE_WEB, true, c.RutInstitucion, null));
            }
            catch (ReglaNegocioException ex)
            {
                await webpay.ReversarAsync(token, r.Monto);
                await cn.ExecuteAsync("UPDATE dbo.Pagos SET Estado = 'ANULADO' WHERE Id = @Id", new { pago.Id });
                return await ResumenAsync(cn, c.CitaId, false, $"No se pudo emitir el bono: {ex.Message} El cargo fue reversado.");
            }
        }

        await using var tx = await cn.BeginTransactionAsync();
        int? bonoId = bono is null ? null
            : await RepositorioBonos.InsertarAsync(cn, tx, c, bono, "WEB", TipoFirma.CLAVE_WEB, "PAGADO", usuario.UsuarioId(), null);

        await cn.ExecuteAsync("""
            UPDATE dbo.Pagos
            SET Estado = 'AUTORIZADO', BonoId = @BonoId, CodigoAutorizacion = @Aut, UltimosDigitos = @U,
                Cuotas = @Cuotas, FechaPago = @Ahora
            WHERE Id = @Id
            """, new { BonoId = bonoId, Aut = r.CodigoAutorizacion, U = r.UltimosDigitos, r.Cuotas, Ahora = ahora, pago.Id }, tx);

        var filas = await cn.ExecuteAsync("""
            UPDATE dbo.Citas SET Estado = 'CONFIRMADA', ExpiraEn = NULL
            WHERE Id = @Id AND RowVer = @RowVer AND Estado = 'PENDIENTE_PAGO'
            """, new { Id = c.CitaId, c.RowVer }, tx);

        if (filas == 0)
        {
            await tx.RollbackAsync();
            await webpay.ReversarAsync(token, r.Monto);
            if (bono is not null) await emisor.AnularAsync(bono.Folio, "Cita modificada durante el pago");
            await cn.ExecuteAsync("UPDATE dbo.Pagos SET Estado = 'ANULADO' WHERE Id = @Id", new { pago.Id });
            log.LogWarning("Pago {BuyOrder} reversado: la cita {Cita} cambió durante el pago", r.BuyOrder, c.CitaId);
            return await ResumenAsync(cn, c.CitaId, false, "La reserva cambió durante el pago. El cargo fue reversado.");
        }

        await EventosCita.PublicarAsync(cn, tx, TiposEvento.CitaConfirmada, c.CitaId, bono?.Folio, r.Monto);
        await tx.CommitAsync();

        await auditoria.RegistrarAsync("PAGO_WEBPAY", "Cita", c.CitaId, $"${r.Monto} aut={r.CodigoAutorizacion}");
        return new PagoConfirmado(true, "¡Pago aprobado! Su hora quedó confirmada.", c.CitaId, "CONFIRMADA",
            r.Monto, r.CodigoAutorizacion, r.UltimosDigitos, bono?.Folio);
    }

    private static async Task<PagoConfirmado> ResumenAsync(System.Data.IDbConnection cn, int citaId, bool aprobado, string mensaje)
    {
        var (estado, monto, aut, ultimos, folio) = await cn.QuerySingleAsync<(string, int?, string?, string?, long?)>("""
            SELECT c.Estado, p.Monto, p.CodigoAutorizacion, p.UltimosDigitos, b.Folio
            FROM dbo.Citas c
            OUTER APPLY (SELECT TOP 1 * FROM dbo.Pagos WHERE CitaId = c.Id AND Medio = 'WEBPAY' ORDER BY Id DESC) p
            LEFT JOIN dbo.Bonos b ON b.CitaId = c.Id AND b.Estado <> 'ANULADO'
            WHERE c.Id = @Id
            """, new { Id = citaId });
        return new PagoConfirmado(aprobado, mensaje, citaId, estado, monto ?? 0, aut, ultimos, folio);
    }

    private static void ExigirPropia(ClaimsPrincipal usuario, ContextoCita c)
    {
        if (usuario.PacienteId() != c.PacienteId) throw ReglaNegocioException.NoEncontrado("La cita");
    }
}

[ApiController]
[Route("api/pagos/webpay")]
[Authorize(Roles = Roles.Paciente)]
public sealed class PagosController(ServicioPagosWeb pagos) : ControllerBase
{
    [HttpPost("iniciar")]
    public Task<PagoIniciado> Iniciar(IniciarPagoRequest r) => pagos.IniciarAsync(User, r.CitaId);

    [HttpPost("confirmar")]
    public Task<PagoConfirmado> Confirmar(ConfirmarPagoRequest r) => pagos.ConfirmarAsync(User, r.Token);
}
