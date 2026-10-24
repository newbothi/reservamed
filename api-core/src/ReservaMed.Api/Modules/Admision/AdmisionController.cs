using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReservaMed.Api.Common;
using ReservaMed.Api.Modules.Citas;

namespace ReservaMed.Api.Modules.Admision;

/// <summary>Mesón de admisión: buscar paciente, valorizar, emitir bono, cobrar y admitir.</summary>
[ApiController]
[Route("api/admision")]
[Authorize(Roles = Roles.Staff)]
public sealed class AdmisionController(ServicioAdmision admision) : ControllerBase
{
    /// <summary>A9: el RUT va en el cuerpo, no en la URL (las URL quedan en los logs de NGINX y del navegador).</summary>
    [HttpPost("pacientes/buscar")]
    public Task<PacienteAdmisionDto> BuscarPaciente(BuscarPacienteRequest r) => admision.BuscarPacienteAsync(r.Rut);

    [HttpPost("pacientes")]
    public async Task<IActionResult> CrearPaciente(CrearPacienteRequest r) =>
        Ok(new { id = await admision.CrearPacienteAsync(r) });

    [HttpPut("pacientes/{id:int}/prevision")]
    public async Task<IActionResult> ActualizarPrevision(int id, PrevisionRequest r)
    {
        await admision.ActualizarPrevisionAsync(id, r);
        return NoContent();
    }

    [HttpGet("citas/{id:int}")]
    public Task<CitaAdmisionDto> Cita(int id) => admision.ObtenerCitaAsync(User, id);

    [HttpPost("citas/{id:int}/valorizar")]
    public Task<ResultadoValorizacion> Valorizar(int id) => admision.ValorizarAsync(User, id);

    /// <summary>C3: desafío de un solo uso para que el lector de huella firme la lectura.</summary>
    [HttpPost("citas/{id:int}/huella/desafio")]
    public Task<DesafioHuellaDto> DesafioHuella(int id) => admision.DesafioHuellaAsync(User, id);

    [HttpPost("citas/{id:int}/bono")]
    public Task<BonoDto> EmitirBono(int id, EmitirBonoRequest r) => admision.EmitirBonoAsync(User, id, r);

    [HttpPost("citas/{id:int}/bono/anular")]
    public async Task<IActionResult> AnularBono(int id)
    {
        await admision.AnularBonoAsync(User, id);
        return NoContent();
    }

    [HttpPost("citas/{id:int}/cobrar")]
    public Task<CitaAdmisionDto> Cobrar(int id, CobrarRequest r) => admision.CobrarAsync(User, id, r);

    [HttpPost("citas/{id:int}/admitir")]
    public Task<CitaAdmisionDto> Admitir(int id) => admision.AdmitirPagadaAsync(User, id);
}
