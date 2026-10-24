using System.Security.Cryptography;
using Dapper;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Integraciones.Bonos;

/// <summary>
/// Simulación de I-Med. En producción esto sería un cliente HTTP/SOAP hacia el
/// proveedor, y la información del plan vendría del financiador, no de nuestra base.
/// Aquí, para poder demostrar el flujo, el mock lee el plan desde la tabla Previsiones
/// como si fuera la respuesta de la Isapre o de Fonasa.
/// </summary>
public sealed class ImedMock(ISqlConnectionFactory db, IReloj reloj, ILogger<ImedMock> log) : IEmisorBonos
{
    private sealed class PlanBeneficiario
    {
        public string FinanciadorCodigo { get; set; } = "";
        public string FinanciadorTipo { get; set; } = "";
        public string? PlanCodigo { get; set; }
        public int PorcentajeAmbulatorio { get; set; }
        public int? TopeBonificacionConsulta { get; set; }
        public string? TramoFonasa { get; set; }
        public bool EsCarga { get; set; }
        public string? AfiliadoRut { get; set; }
        public string? AfiliadoNombre { get; set; }
        public string BeneficiarioNombre { get; set; } = "";
    }

    public async Task<Valorizacion> ValorizarAsync(SolicitudValorizacion s, CancellationToken ct = default)
    {
        await Task.Delay(400, ct); // latencia simulada del servicio externo

        await using var cn = db.Crear();
        var plan = await cn.QuerySingleOrDefaultAsync<PlanBeneficiario>("""
            SELECT f.Codigo AS FinanciadorCodigo, f.Tipo AS FinanciadorTipo,
                   pl.Codigo AS PlanCodigo, ISNULL(pl.PorcentajeAmbulatorio, 0) AS PorcentajeAmbulatorio,
                   pl.TopeBonificacionConsulta, pv.TramoFonasa, pv.EsCarga, pv.AfiliadoRut, pv.AfiliadoNombre,
                   UPPER(pa.ApellidoPaterno + ISNULL(' ' + pa.ApellidoMaterno, '') + ' ' + pa.Nombres) AS BeneficiarioNombre
            FROM dbo.Pacientes pa
            JOIN dbo.Previsiones pv  ON pv.PacienteId = pa.Id AND pv.Vigente = 1
            JOIN dbo.Financiadores f ON f.Id = pv.FinanciadorId
            LEFT JOIN dbo.PlanesSalud pl ON pl.Id = pv.PlanSaludId
            WHERE pa.Rut = @Rut
            """, new { Rut = s.BeneficiarioRut });

        if (plan is null || plan.FinanciadorCodigo != s.FinanciadorCodigo)
            throw Rechazo("El beneficiario no aparece vigente en el financiador consultado.");

        if (plan.FinanciadorTipo == "FONASA" && plan.TramoFonasa == "A")
            throw Rechazo("Beneficiario Fonasa tramo A: no puede atenderse en Modalidad Libre Elección (MLE). " +
                          "Debe atenderse en la red pública (MAI) o pagar como particular.");

        var lineas = s.Items.Select(i =>
        {
            var valor = i.ValorUnitario * i.Cantidad;
            var bonif = (int)Math.Round(valor * plan.PorcentajeAmbulatorio / 100m, MidpointRounding.AwayFromZero);
            if (plan.TopeBonificacionConsulta is int tope)
                bonif = Math.Min(bonif, tope * i.Cantidad);
            return new LineaValorizada(i.PrestacionId, i.Codigo, i.Descripcion, i.Cantidad,
                valor, bonif, 0, valor - bonif);
        }).ToList();

        // Si el paciente es titular, afiliado y beneficiario son la misma persona.
        var afiliadoRut = plan.EsCarga && plan.AfiliadoRut is not null ? plan.AfiliadoRut : s.BeneficiarioRut;
        var afiliadoNombre = plan.EsCarga && plan.AfiliadoNombre is not null ? plan.AfiliadoNombre : plan.BeneficiarioNombre;

        return new Valorizacion(plan.PlanCodigo ?? plan.FinanciadorCodigo, afiliadoRut, afiliadoNombre, lineas);
    }

    public async Task<BonoEmitido> EmitirAsync(SolicitudEmision s, CancellationToken ct = default)
    {
        if (!s.FirmaVerificada)
            throw Rechazo(s.TipoFirma == TipoFirma.HUELLA
                ? "La huella no pudo ser verificada. Repita la lectura o use otro dedo."
                : "La autenticación con el financiador no fue exitosa.");

        var valorizacion = await ValorizarAsync(s.Valorizacion, ct);

        await using var cn = db.Crear();
        // En la realidad el folio lo asigna el financiador; aquí usamos una secuencia.
        var folio = await cn.ExecuteScalarAsync<long>("SELECT NEXT VALUE FOR dbo.SeqFolioBono");

        var ahora = reloj.Ahora;
        log.LogInformation("[I-Med mock] Bono {Folio} emitido para {Rut} ({Plan}), copago {Copago}",
            folio, ReservaMed.Contracts.Privacidad.Rut(s.Valorizacion.BeneficiarioRut), valorizacion.PlanCodigo, valorizacion.TotalCopago);

        return new BonoEmitido(folio, NuevoCodigoAuditoria(), ahora, ahora.Date, valorizacion);
    }

    public Task AnularAsync(long folio, string motivo, CancellationToken ct = default)
    {
        log.LogInformation("[I-Med mock] Bono {Folio} anulado: {Motivo}", folio, motivo);
        return Task.CompletedTask;
    }

    private static ReglaNegocioException Rechazo(string mensaje) =>
        new(mensaje, StatusCodes.Status422UnprocessableEntity, "IMED_RECHAZO");

    /// <summary>Código con el formato BONO-XXXX-XXXX-XXXX (sin caracteres ambiguos).</summary>
    private static string NuevoCodigoAuditoria()
    {
        const string alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        string Bloque() => new(Enumerable.Range(0, 4).Select(_ => alfabeto[RandomNumberGenerator.GetInt32(alfabeto.Length)]).ToArray());
        return $"BONO-{Bloque()}-{Bloque()}-{Bloque()}";
    }
}
