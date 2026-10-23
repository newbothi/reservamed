using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Modules.Citas;

[ApiController]
[Route("api/citas")]
[Authorize]
public sealed class CitasController(ServicioCitas citas) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = "PACIENTE,RECEPCION,ADMIN")]
    public async Task<ActionResult<ReservaCreada>> Reservar(ReservarRequest r)
    {
        var creada = await citas.ReservarAsync(User, r);
        return CreatedAtAction(nameof(Obtener), new { id = creada.CitaId }, creada);
    }

    [HttpGet("mias")]
    [Authorize(Roles = Roles.Paciente)]
    public Task<IEnumerable<CitaResumenDto>> Mias() =>
        citas.DelPacienteAsync(User.PacienteId() ?? throw ReglaNegocioException.Prohibido());

    [HttpGet("{id:int}")]
    public Task<CitaResumenDto> Obtener(int id) => citas.ObtenerAsync(User, id);

    [HttpPost("{id:int}/cancelar")]
    [Authorize(Roles = "PACIENTE,RECEPCION,ADMIN")]
    public async Task<IActionResult> Cancelar(int id, CancelarRequest r)
    {
        await citas.CancelarAsync(User, id, r.Motivo);
        return NoContent();
    }

    [HttpPost("{id:int}/atendida")]
    [Authorize(Roles = "MEDICO,ADMIN")]
    public async Task<IActionResult> Atendida(int id)
    {
        await citas.CerrarAtencionAsync(User, id, asistio: true);
        return NoContent();
    }

    [HttpPost("{id:int}/no-asistio")]
    [Authorize(Roles = Roles.StaffYMedico)]
    public async Task<IActionResult> NoAsistio(int id)
    {
        await citas.CerrarAtencionAsync(User, id, asistio: false);
        return NoContent();
    }
}
