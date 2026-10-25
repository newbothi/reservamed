using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Modules.Auth;

public sealed record UsuarioAdminDto(int Id, string Email, string Rol, string NombreVisible, bool Activo,
    bool EmailVerificado, bool TotpActivo, DateTime? BloqueadoHasta, int SesionesActivas);

/// <summary>Administración de cuentas: desactivar revoca todas las sesiones en el acto (A1).</summary>
[ApiController]
[Route("api/usuarios")]
[Authorize(Roles = Roles.Admin)]
public sealed class UsuariosController(ISqlConnectionFactory db, ServicioSesiones sesiones, IAuditoria auditoria, IEnviadorCorreo correo) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<UsuarioAdminDto>> Listar([FromQuery] string? rol)
    {
        await using var cn = db.Crear();
        return await cn.QueryAsync<UsuarioAdminDto>("""
            SELECT u.Id, u.Email, u.Rol, u.NombreVisible, u.Activo, u.EmailVerificado, u.TotpActivo, u.BloqueadoHasta,
                   (SELECT COUNT(1) FROM dbo.Sesiones s
                    WHERE s.UsuarioId = u.Id AND s.RevocadaEn IS NULL AND s.ExpiraEn > SYSUTCDATETIME()) AS SesionesActivas
            FROM dbo.Usuarios u
            WHERE (@Rol IS NULL AND u.Rol <> 'PACIENTE') OR u.Rol = @Rol
            ORDER BY u.Rol, u.Email
            """, new { Rol = rol });
    }

    [HttpPost("{id:int}/desactivar")]
    [RequiereReautenticacion]
    public async Task<IActionResult> Desactivar(int id)
    {
        if (id == User.UsuarioId()) throw new ReglaNegocioException("No puede desactivar su propia cuenta.");
        await using var cn = db.Crear();
        if (await cn.ExecuteAsync("UPDATE dbo.Usuarios SET Activo = 0 WHERE Id = @Id", new { Id = id }) == 0) return NotFound();
        var n = await sesiones.RevocarTodasAsync(id, "Cuenta desactivada");
        await auditoria.RegistrarAsync("USUARIO_DESACTIVADO", "Usuario", id, $"{n} sesiones revocadas");
        return NoContent();
    }

    [HttpPost("{id:int}/activar")]
    [RequiereReautenticacion]
    public async Task<IActionResult> Activar(int id)
    {
        await using var cn = db.Crear();
        if (await cn.ExecuteAsync("UPDATE dbo.Usuarios SET Activo = 1, IntentosFallidos = 0, BloqueadoHasta = NULL WHERE Id = @Id", new { Id = id }) == 0)
            return NotFound();
        await auditoria.RegistrarAsync("USUARIO_ACTIVADO", "Usuario", id);
        return NoContent();
    }

    /// <summary>Teléfono perdido: borra el autenticador; en el próximo ingreso se enrola uno nuevo.</summary>
    [HttpPost("{id:int}/restablecer-totp")]
    [RequiereReautenticacion]
    public async Task<IActionResult> RestablecerTotp(int id)
    {
        await using var cn = db.Crear();
        if (await cn.ExecuteAsync("UPDATE dbo.Usuarios SET TotpActivo = 0, TotpSecreto = NULL, TotpUltimoPaso = NULL WHERE Id = @Id", new { Id = id }) == 0)
            return NotFound();
        var n = await sesiones.RevocarTodasAsync(id, "TOTP restablecido");
        await auditoria.RegistrarAsync("TOTP_RESTABLECIDO", "Usuario", id, $"{n} sesiones revocadas");
        // V2.2.3: el dueño de la cuenta se entera de que cambió su segundo factor.
        var email = await cn.ExecuteScalarAsync<string>("SELECT Email FROM dbo.Usuarios WHERE Id = @Id", new { Id = id });
        if (email is not null)
            await correo.EnviarAsync(new Correo(email, "Se restableció su autenticador de ReservaMed",
                "Un administrador restableció el autenticador (código de 6 dígitos) de su cuenta y cerró sus sesiones. " +
                "En su próximo ingreso deberá enrolar uno nuevo. Si no lo solicitó, avise de inmediato a la clínica."));
        return NoContent();
    }
}
