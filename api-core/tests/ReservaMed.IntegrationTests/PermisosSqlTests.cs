using Microsoft.Data.SqlClient;

namespace ReservaMed.IntegrationTests;

/// <summary>Hallazgo A5: la API entra con rm_api, que lee y escribe datos pero no administra la base.</summary>
[Collection("api")]
public sealed class PermisosSqlTests(ApiFactory api)
{
    private async Task<object?> EjecutarAsync(string sql)
    {
        await using var cn = new SqlConnection(api.CadenaApi);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand(sql, cn);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Puede_leer_y_obtener_folios()
    {
        Assert.NotNull(await EjecutarAsync("SELECT COUNT(*) FROM dbo.Citas"));
        Assert.NotNull(await EjecutarAsync("SELECT NEXT VALUE FOR dbo.SeqFolioBono"));
    }

    [Theory]
    [InlineData("CREATE TABLE dbo.Intruso (Id INT)")]
    [InlineData("DROP TABLE dbo.Auditoria")]
    [InlineData("ALTER TABLE dbo.Usuarios ADD Puerta INT")]
    [InlineData("CREATE LOGIN intruso WITH PASSWORD = 'Xy1!xy1!xy1!xy1!'")]
    [InlineData("ALTER ROLE db_owner ADD MEMBER rm_api")]
    public async Task No_puede_cambiar_esquema_ni_permisos(string sql)
    {
        // El número exacto varía por sentencia (262, 3701, 1088, 15151, 15247…): basta con que se rechace.
        await Assert.ThrowsAsync<SqlException>(() => EjecutarAsync(sql));
    }

    [Fact]
    public async Task No_es_sysadmin_ni_db_owner()
    {
        Assert.Equal(0, await EjecutarAsync("SELECT IS_SRVROLEMEMBER('sysadmin')"));
        Assert.Equal(0, await EjecutarAsync("SELECT IS_MEMBER('db_owner')"));
    }
}
