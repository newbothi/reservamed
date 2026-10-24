using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ReservaMed.IntegrationTests;

/// <summary>
/// Fase 4, datos y privacidad: auditoría inmutable y encadenada (M6), enmascaramiento y TDE (M7)
/// y eventos sin datos personales (A9).
/// </summary>
[Collection("api")]
public sealed class PrivacidadTests(ApiFactory api)
{
    private static async Task<object?> EscalarAsync(string cadena, string sql)
    {
        await using var cn = new SqlConnection(cadena);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand(sql, cn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<JsonElement> IntegridadAsync()
    {
        var admin = api.ConToken((await api.AdminAsync()).AccessToken);
        return await admin.GetFromJsonAsync<JsonElement>("/api/auditoria/integridad");
    }

    /* ---------------- M6 ---------------- */

    [Theory]
    [InlineData("UPDATE dbo.Auditoria SET Detalle = 'alterado' WHERE Id = (SELECT MAX(Id) FROM dbo.Auditoria)")]
    [InlineData("DELETE FROM dbo.Auditoria WHERE Id = (SELECT MAX(Id) FROM dbo.Auditoria)")]
    [InlineData("INSERT INTO dbo.Auditoria (Fecha, Accion, Recurso) VALUES (SYSDATETIME(), 'FALSO', 'X')")]
    public async Task La_API_no_puede_escribir_la_auditoria_salvo_por_el_procedimiento(string sql)
    {
        await api.AdminAsync();   // garantiza que haya filas
        await Assert.ThrowsAsync<SqlException>(() => EscalarAsync(api.CadenaApi, sql));
    }

    [Theory]
    [InlineData("UPDATE dbo.Auditoria SET Detalle = 'alterado' WHERE Id = (SELECT MAX(Id) FROM dbo.Auditoria)")]
    [InlineData("DELETE FROM dbo.Auditoria WHERE Id = (SELECT MAX(Id) FROM dbo.Auditoria)")]
    public async Task Ni_siquiera_sa_modifica_la_auditoria_sin_desactivar_el_trigger(string sql)
    {
        await api.AdminAsync();
        var ex = await Assert.ThrowsAsync<SqlException>(() => EscalarAsync(api.CadenaSa, sql));
        Assert.Contains("solo inserción", ex.Message);
    }

    [Fact]
    public async Task La_cadena_de_hashes_detecta_una_fila_alterada_por_fuera()
    {
        var antes = await IntegridadAsync();
        Assert.True(antes.GetProperty("integra").GetBoolean(), antes.ToString());

        // Un "atacante" con sa desactiva el trigger y cambia una fila antigua; luego la deja como estaba.
        var id = (long)(await EscalarAsync(api.CadenaSa, "SELECT MIN(Id) FROM dbo.Auditoria"))!;
        var original = await EscalarAsync(api.CadenaSa, $"SELECT Detalle FROM dbo.Auditoria WHERE Id = {id}");
        async Task CambiarAsync(string valor) => await EscalarAsync(api.CadenaSa,
            "DISABLE TRIGGER dbo.TR_Auditoria_Inmutable ON dbo.Auditoria; " +
            $"UPDATE dbo.Auditoria SET Detalle = {valor} WHERE Id = {id}; " +
            "ENABLE TRIGGER dbo.TR_Auditoria_Inmutable ON dbo.Auditoria;");
        try
        {
            await CambiarAsync("N'borrado de evidencia'");
            var despues = await IntegridadAsync();
            Assert.False(despues.GetProperty("integra").GetBoolean());
            Assert.Equal(id, despues.GetProperty("primerError").GetInt64());
        }
        finally
        {
            await CambiarAsync(original is string o ? $"N'{o.Replace("'", "''")}'" : "NULL");
        }
        Assert.True((await IntegridadAsync()).GetProperty("integra").GetBoolean());
    }

    [Fact]
    public async Task Un_acceso_denegado_queda_auditado_con_la_ruta_sin_datos()
    {
        var (email, clave) = await api.PacienteNuevoAsync("denegado");
        var paciente = api.ConToken((await api.LoginPacienteAsync(email, clave)).AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await paciente.GetAsync("/api/auditoria?top=1")).StatusCode);

        var admin = api.ConToken((await api.AdminAsync()).AccessToken);
        var filas = await admin.GetFromJsonAsync<List<JsonElement>>("/api/auditoria?accion=ACCESO_DENEGADO&top=50");
        Assert.Contains(filas!, f => f.GetProperty("detalle").GetString() == "GET api/auditoria (rol)");
    }

    /* ---------------- M7 ---------------- */

    [Theory]
    [InlineData("rm_ml_seed", true, true)]    // carga sintética: no ve RUT ni fecha
    [InlineData("rm_ml", true, false)]        // ml-service: necesita la edad, no el RUT
    [InlineData("rm_api", false, false)]      // la API atiende pacientes: ve todo
    public async Task El_RUT_y_la_fecha_de_nacimiento_se_enmascaran_para_cuentas_no_clinicas(string usuario, bool rutOculto, bool fechaOculta)
    {
        var rut = (string)(await EscalarAsync(api.CadenaSa,
            $"EXECUTE AS USER = '{usuario}'; SELECT TOP 1 Rut FROM dbo.Pacientes ORDER BY Id; REVERT;"))!;
        var fecha = (DateTime)(await EscalarAsync(api.CadenaSa,
            $"EXECUTE AS USER = '{usuario}'; SELECT TOP 1 FechaNacimiento FROM dbo.Pacientes ORDER BY Id; REVERT;"))!;

        Assert.Equal(rutOculto, rut.StartsWith("xx.xxx."));
        Assert.Equal(fechaOculta, fecha == new DateTime(1900, 1, 1));
    }

    [Fact]
    public async Task El_worker_no_puede_leer_el_RUT_ni_la_fecha_de_nacimiento()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => EscalarAsync(api.CadenaSa,
            "EXECUTE AS USER = 'rm_worker'; SELECT TOP 1 Rut FROM dbo.Pacientes; REVERT;"));
        Assert.Equal(230, ex.Number);   // permiso SELECT denegado en la columna
        Assert.NotNull(await EscalarAsync(api.CadenaSa, "EXECUTE AS USER = 'rm_worker'; SELECT TOP 1 Email FROM dbo.Pacientes; REVERT;"));
    }

    [Fact]
    public async Task La_base_queda_cifrada_con_TDE()
    {
        int estado = 0;
        for (var i = 0; i < 30 && estado != 3; i++)
        {
            estado = Convert.ToInt32(await EscalarAsync(api.CadenaSa,
                "SELECT ISNULL(MAX(encryption_state), 0) FROM sys.dm_database_encryption_keys WHERE database_id = DB_ID('ReservaMed')"));
            if (estado != 3) await Task.Delay(1000);
        }
        Assert.Equal(3, estado);   // 3 = cifrada
        Assert.Equal("AES", await EscalarAsync(api.CadenaSa,
            "SELECT key_algorithm FROM sys.dm_database_encryption_keys WHERE database_id = DB_ID('ReservaMed')"));
    }

    /* ---------------- A9 ---------------- */

    [Fact]
    public async Task Los_eventos_de_la_Outbox_llevan_solo_identificadores()
    {
        var (email, clave) = await api.PacienteNuevoAsync("evento");
        var c = api.ConToken((await api.LoginPacienteAsync(email, clave)).AccessToken);
        var desde = DateTime.Today.AddDays(3).ToString("yyyy-MM-dd");
        var hasta = DateTime.Today.AddDays(17).ToString("yyyy-MM-dd");
        var cupo = (await api.CreateClient().GetFromJsonAsync<List<JsonElement>>(
            $"/api/agenda/disponibilidad?especialidadId=1&desde={desde}&hasta={hasta}"))!.OrderBy(_ => Random.Shared.Next()).First();
        var r = await c.PostAsJsonAsync("/api/citas", new
        {
            profesionalId = cupo.GetProperty("profesionalId").GetInt32(), sedeId = cupo.GetProperty("sedeId").GetInt32(),
            inicio = cupo.GetProperty("inicio").GetDateTime(), modalidadPago = "EN_CLINICA"
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var citaId = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("citaId").GetInt32();

        var payload = (string)(await EscalarAsync(api.CadenaSa,
            $"SELECT TOP 1 Payload FROM dbo.Outbox WHERE JSON_VALUE(Payload, '$.citaId') = '{citaId}' ORDER BY Id DESC"))!;
        var campos = JsonDocument.Parse(payload).RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "citaId", "estado", "version" }, campos);
        Assert.DoesNotContain(email, payload);
    }

    [Fact]
    public async Task El_RUT_se_busca_en_el_cuerpo_no_en_la_URL()
    {
        var admin = api.ConToken((await api.AdminAsync()).AccessToken);
        var rut = (string)(await EscalarAsync(api.CadenaApi, "SELECT TOP 1 Rut FROM dbo.Pacientes ORDER BY Id"))!;

        var ok = await admin.PostAsJsonAsync("/api/admision/pacientes/buscar", new { rut });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var viejo = await admin.GetAsync($"/api/admision/pacientes/{rut}");
        Assert.True(viejo.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }
}
