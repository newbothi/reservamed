using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReservaMed.Api.Common;

namespace ReservaMed.Api.Integraciones.Fhir;

/// <summary>
/// Exposición mínima de recursos HL7 FHIR R4 (Patient, Practitioner, Appointment)
/// para integrar con un HIS o con otros sistemas clínicos.
/// El identificador RUN sigue la forma usada en los ejemplos de la guía HL7 Chile (CL Core);
/// es referencial y no está validado contra el perfil oficial.
/// </summary>
[ApiController]
[Route("fhir")]
[Authorize(Roles = Roles.StaffYMedico)]
[Produces("application/fhir+json")]
public sealed class FhirController(ISqlConnectionFactory db, IAuditoria auditoria) : ControllerBase
{
    private const string SistemaRun = "http://regcivil.cl/Validacion/RUN";

    private sealed class FilaPaciente
    {
        public int Id { get; set; }
        public string Rut { get; set; } = "";
        public string Nombres { get; set; } = "";
        public string ApellidoPaterno { get; set; } = "";
        public string? ApellidoMaterno { get; set; }
        public DateTime FechaNacimiento { get; set; }
        public string Sexo { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Telefono { get; set; }
    }

    private sealed class FilaCita
    {
        public int Id { get; set; }
        public string Estado { get; set; } = "";
        public DateTime Inicio { get; set; }
        public DateTime Fin { get; set; }
        public int PacienteId { get; set; }
        public string Paciente { get; set; } = "";
        public int ProfesionalId { get; set; }
        public string Profesional { get; set; } = "";
        public string Especialidad { get; set; } = "";
        public string Codigo { get; set; } = "";
        public string Prestacion { get; set; } = "";
        public string Sede { get; set; } = "";
    }

    [HttpGet("Patient/{id:int}")]
    public async Task<IActionResult> Patient(int id)
    {
        await using var cn = db.Crear();
        var p = await cn.QuerySingleOrDefaultAsync<FilaPaciente>(
            "SELECT Id, Rut, Nombres, ApellidoPaterno, ApellidoMaterno, FechaNacimiento, Sexo, Email, Telefono FROM dbo.Pacientes WHERE Id = @Id",
            new { Id = id });
        if (p is null) return NotFound(OperationOutcome("Patient no encontrado"));

        await auditoria.RegistrarAsync("FHIR_PATIENT", "Paciente", id);
        return Fhir(new
        {
            resourceType = "Patient",
            id = p.Id.ToString(),
            identifier = new[] { new { use = "official", system = SistemaRun, value = p.Rut } },
            name = new[]
            {
                new
                {
                    use = "official",
                    family = p.ApellidoPaterno,
                    given = p.Nombres.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    // CL Core usa una extensión para el segundo apellido
                    _family = p.ApellidoMaterno is null ? null : new
                    {
                        extension = new[] { new { url = "https://hl7chile.cl/fhir/ig/clcore/StructureDefinition/SegundoApellido", valueString = p.ApellidoMaterno } }
                    }
                }
            },
            gender = p.Sexo switch { "M" => "male", "F" => "female", _ => "other" },
            birthDate = p.FechaNacimiento.ToString("yyyy-MM-dd"),
            telecom = new object[]
            {
                new { system = "email", value = p.Email },
                new { system = "phone", value = p.Telefono }
            }
        });
    }

    [HttpGet("Practitioner/{id:int}")]
    public async Task<IActionResult> Practitioner(int id)
    {
        await using var cn = db.Crear();
        var m = await cn.QuerySingleOrDefaultAsync<(int Id, string Rut, string Nombres, string ApellidoPaterno, string Especialidad)>("""
            SELECT p.Id, p.Rut, p.Nombres, p.ApellidoPaterno, e.Nombre
            FROM dbo.Profesionales p JOIN dbo.Especialidades e ON e.Id = p.EspecialidadId WHERE p.Id = @Id
            """, new { Id = id });
        if (m.Id == 0) return NotFound(OperationOutcome("Practitioner no encontrado"));

        return Fhir(new
        {
            resourceType = "Practitioner",
            id = m.Id.ToString(),
            identifier = new[] { new { use = "official", system = SistemaRun, value = m.Rut } },
            name = new[] { new { family = m.ApellidoPaterno, given = m.Nombres.Split(' ') } },
            qualification = new[] { new { code = new { text = m.Especialidad } } }
        });
    }

    [HttpGet("Appointment/{id:int}")]
    public async Task<IActionResult> Appointment(int id)
    {
        await using var cn = db.Crear();
        var a = await cn.QuerySingleOrDefaultAsync<FilaCita>("""
            SELECT c.Id, c.Estado, c.FechaHoraInicio AS Inicio, c.FechaHoraFin AS Fin,
                   pa.Id AS PacienteId, pa.Nombres + ' ' + pa.ApellidoPaterno AS Paciente,
                   pr.Id AS ProfesionalId, pr.Nombres + ' ' + pr.ApellidoPaterno AS Profesional,
                   e.Nombre AS Especialidad, ps.Codigo, ps.Descripcion AS Prestacion, s.Nombre AS Sede
            FROM dbo.Citas c
            JOIN dbo.Pacientes pa ON pa.Id = c.PacienteId
            JOIN dbo.Profesionales pr ON pr.Id = c.ProfesionalId
            JOIN dbo.Especialidades e ON e.Id = pr.EspecialidadId
            JOIN dbo.Prestaciones ps ON ps.Id = c.PrestacionId
            JOIN dbo.Sedes s ON s.Id = c.SedeId
            WHERE c.Id = @Id
            """, new { Id = id });
        if (a is null) return NotFound(OperationOutcome("Appointment no encontrado"));

        await auditoria.RegistrarAsync("FHIR_APPOINTMENT", "Cita", id);
        var zona = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        string Iso(DateTime local) => new DateTimeOffset(local, zona.GetUtcOffset(local)).ToString("yyyy-MM-ddTHH:mm:sszzz");

        return Fhir(new
        {
            resourceType = "Appointment",
            id = a.Id.ToString(),
            status = EstadoFhir(a.Estado),
            serviceType = new[] { new { text = a.Especialidad } },
            serviceCategory = new[] { new { text = "Consulta ambulatoria" } },
            reasonCode = new[] { new { coding = new[] { new { system = "urn:reservamed:prestaciones", code = a.Codigo, display = a.Prestacion } } } },
            start = Iso(a.Inicio),
            end = Iso(a.Fin),
            participant = new object[]
            {
                new { actor = new { reference = $"Patient/{a.PacienteId}", display = a.Paciente }, status = "accepted" },
                new { actor = new { reference = $"Practitioner/{a.ProfesionalId}", display = a.Profesional }, status = "accepted" },
                new { actor = new { display = a.Sede }, status = "accepted" }
            }
        });
    }

    private static string EstadoFhir(string estado) => estado switch
    {
        "PENDIENTE_PAGO" => "pending",
        "RESERVADA" or "CONFIRMADA" => "booked",
        "ADMITIDA" => "arrived",
        "ATENDIDA" => "fulfilled",
        "NO_ASISTIO" => "noshow",
        _ => "cancelled"
    };

    private ContentResult Fhir(object recurso) => new()
    {
        Content = System.Text.Json.JsonSerializer.Serialize(recurso, new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        }),
        ContentType = "application/fhir+json; charset=utf-8",
        StatusCode = 200
    };

    private static object OperationOutcome(string mensaje) => new
    {
        resourceType = "OperationOutcome",
        issue = new[] { new { severity = "error", code = "not-found", diagnostics = mensaje } }
    };
}
