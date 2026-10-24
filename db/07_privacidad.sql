/* Fase 4 de seguridad: datos y privacidad (hallazgos A9, M6 y M7).
   Idempotente: db-init lo ejecuta en cada arranque, DESPUÉS de 04_usuarios.sql.
   Variable de sqlcmd: TDE_PASS (clave de la llave maestra; la genera configurar-secretos.ps1).

   1. M6  Auditoría inmutable: solo inserción (permisos + trigger) y cadena de hashes SHA-256.
   2. M7  Dynamic Data Masking del RUT y la fecha de nacimiento para cuentas no clínicas.
   3. A9  Limpieza de datos personales que ya habían quedado en la Outbox y en Notificaciones.
   4. M7  TDE: la base y su log quedan cifrados en disco; certificado aparte para respaldos. */
USE ReservaMed;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ============================ 1. Auditoría (M6) ============================ */
IF COL_LENGTH('dbo.Auditoria', 'HashPrevio') IS NULL
    ALTER TABLE dbo.Auditoria ADD HashPrevio BINARY(32) NULL, Hash BINARY(32) NULL;
GO

/* Hash de una fila: SHA-256 del hash anterior y de todos sus campos, en un formato fijo. */
CREATE OR ALTER FUNCTION dbo.fn_HashAuditoria(
    @HashPrevio BINARY(32), @Fecha DATETIME2(0), @UsuarioId INT, @Email NVARCHAR(150), @Rol VARCHAR(12),
    @Accion VARCHAR(40), @Recurso VARCHAR(30), @RecursoId VARCHAR(30), @Ip VARCHAR(45), @Detalle NVARCHAR(500))
RETURNS BINARY(32)
WITH SCHEMABINDING
AS
BEGIN
    RETURN CAST(HASHBYTES('SHA2_256', CONCAT(
        CONVERT(VARCHAR(64), @HashPrevio, 2), N'|', CONVERT(VARCHAR(19), @Fecha, 126), N'|',
        ISNULL(CAST(@UsuarioId AS VARCHAR(12)), ''), N'|', ISNULL(@Email, N''), N'|', ISNULL(@Rol, ''), N'|',
        @Accion, N'|', @Recurso, N'|', ISNULL(@RecursoId, ''), N'|', ISNULL(@Ip, ''), N'|', ISNULL(@Detalle, N''))) AS BINARY(32));
END;
GO

/* Inmutable: ni UPDATE ni DELETE, sea quien sea el que lo intente (también con permisos de escritura). */
CREATE OR ALTER TRIGGER dbo.TR_Auditoria_Inmutable ON dbo.Auditoria
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    THROW 51000, 'La auditoría es de solo inserción: no se puede modificar ni borrar.', 1;
END;
GO

/* Encadenar las filas que existían antes de la fase 4 (una sola vez, en orden de Id). */
IF EXISTS (SELECT 1 FROM dbo.Auditoria WHERE Hash IS NULL)
BEGIN
    DISABLE TRIGGER dbo.TR_Auditoria_Inmutable ON dbo.Auditoria;
    DECLARE @Id BIGINT = 0, @Prev BINARY(32) = NULL;
    SELECT TOP 1 @Prev = Hash FROM dbo.Auditoria
     WHERE Hash IS NOT NULL AND Id < (SELECT MIN(Id) FROM dbo.Auditoria WHERE Hash IS NULL) ORDER BY Id DESC;
    WHILE 1 = 1
    BEGIN
        SELECT TOP 1 @Id = Id FROM dbo.Auditoria WHERE Hash IS NULL AND Id > @Id ORDER BY Id;
        IF @@ROWCOUNT = 0 BREAK;
        UPDATE dbo.Auditoria
           SET HashPrevio = @Prev,
               Hash = dbo.fn_HashAuditoria(@Prev, Fecha, UsuarioId, Email, Rol, Accion, Recurso, RecursoId, Ip, Detalle)
         WHERE Id = @Id;
        SELECT @Prev = Hash FROM dbo.Auditoria WHERE Id = @Id;
    END;
    ENABLE TRIGGER dbo.TR_Auditoria_Inmutable ON dbo.Auditoria;
END;
GO

/* Única forma de escribir: encadena la fila nueva con la última, de a una (bloqueo de aplicación). */
CREATE OR ALTER PROCEDURE dbo.Auditoria_Registrar
    @Fecha DATETIME2(0), @UsuarioId INT, @Email NVARCHAR(150), @Rol VARCHAR(12), @Accion VARCHAR(40),
    @Recurso VARCHAR(30), @RecursoId VARCHAR(30), @Ip VARCHAR(45), @Detalle NVARCHAR(500)
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @r INT;
    EXEC @r = sp_getapplock @Resource = 'auditoria-cadena', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @r < 0 THROW 51001, 'No se pudo obtener el turno para escribir la auditoría.', 1;

    DECLARE @Prev BINARY(32) = (SELECT TOP 1 Hash FROM dbo.Auditoria ORDER BY Id DESC);
    INSERT INTO dbo.Auditoria (Fecha, UsuarioId, Email, Rol, Accion, Recurso, RecursoId, Ip, Detalle, HashPrevio, Hash)
    VALUES (@Fecha, @UsuarioId, @Email, @Rol, @Accion, @Recurso, @RecursoId, @Ip, @Detalle, @Prev,
            dbo.fn_HashAuditoria(@Prev, @Fecha, @UsuarioId, @Email, @Rol, @Accion, @Recurso, @RecursoId, @Ip, @Detalle));
    COMMIT;
END;
GO

/* Recalcula la cadena: una fila alterada (su hash ya no calza) o una fila borrada (el enlace se
   corta) aparecen como PrimerError. Devuelve filas revisadas y el primer Id con problema. */
CREATE OR ALTER PROCEDURE dbo.Auditoria_Verificar
AS
BEGIN
    SET NOCOUNT ON;
    WITH c AS (
        SELECT Id, Hash, HashPrevio, LAG(Hash) OVER (ORDER BY Id) AS HashAnterior,
               dbo.fn_HashAuditoria(HashPrevio, Fecha, UsuarioId, Email, Rol, Accion, Recurso, RecursoId, Ip, Detalle) AS Calculado
        FROM dbo.Auditoria)
    SELECT COUNT_BIG(*) AS Filas,
           MIN(CASE WHEN Hash IS NULL OR Hash <> Calculado
                      OR NOT ((HashAnterior IS NULL AND HashPrevio IS NULL) OR HashAnterior = HashPrevio)
                    THEN Id END) AS PrimerError
    FROM c;
END;
GO

/* La API escribe SOLO por el procedimiento (db_datawriter le daría UPDATE/DELETE: DENY gana). */
DENY INSERT, UPDATE, DELETE ON dbo.Auditoria TO rm_api;
GRANT EXECUTE ON dbo.Auditoria_Registrar TO rm_api;
GRANT EXECUTE ON dbo.Auditoria_Verificar TO rm_api;
GO

/* ============================ 2. Enmascaramiento (M7) ============================ */
/* Las cuentas no clínicas (Worker, ml-service, carga sintética) ven "xx.xxx.678-9" y 1900-01-01.
   Solo la API (que atiende a pacientes) tiene UNMASK. El ml-service necesita la edad para el
   modelo: puede ver la fecha de nacimiento, pero no el RUT. */
IF NOT EXISTS (SELECT 1 FROM sys.masked_columns WHERE object_id = OBJECT_ID('dbo.Pacientes') AND name = 'Rut')
    ALTER TABLE dbo.Pacientes ALTER COLUMN Rut ADD MASKED WITH (FUNCTION = 'partial(0,"xx.xxx.",5)');
IF NOT EXISTS (SELECT 1 FROM sys.masked_columns WHERE object_id = OBJECT_ID('dbo.Pacientes') AND name = 'FechaNacimiento')
    ALTER TABLE dbo.Pacientes ALTER COLUMN FechaNacimiento ADD MASKED WITH (FUNCTION = 'default()');
IF NOT EXISTS (SELECT 1 FROM sys.masked_columns WHERE object_id = OBJECT_ID('dbo.Previsiones') AND name = 'AfiliadoRut')
    ALTER TABLE dbo.Previsiones ALTER COLUMN AfiliadoRut ADD MASKED WITH (FUNCTION = 'partial(0,"xx.xxx.",5)');
IF NOT EXISTS (SELECT 1 FROM sys.masked_columns WHERE object_id = OBJECT_ID('dbo.Bonos') AND name = 'BeneficiarioRut')
    ALTER TABLE dbo.Bonos ALTER COLUMN BeneficiarioRut ADD MASKED WITH (FUNCTION = 'partial(0,"xx.xxx.",5)');
IF NOT EXISTS (SELECT 1 FROM sys.masked_columns WHERE object_id = OBJECT_ID('dbo.Bonos') AND name = 'AfiliadoRut')
    ALTER TABLE dbo.Bonos ALTER COLUMN AfiliadoRut ADD MASKED WITH (FUNCTION = 'partial(0,"xx.xxx.",5)');
GO
GRANT UNMASK TO rm_api;
GRANT UNMASK ON dbo.Pacientes(FechaNacimiento) TO rm_ml;
GO

/* ============================ 3. Datos personales ya guardados (A9) ============================ */
/* Eventos antiguos en la Outbox (llevaban nombre, correo y teléfono): quedan solo con el id y el estado. */
UPDATE dbo.Outbox
   SET Payload = CONCAT(N'{"citaId":', JSON_VALUE(Payload, '$.citaId'), N',"estado":"', JSON_VALUE(Payload, '$.estado'), N'","version":2}')
 WHERE ISJSON(Payload) = 1 AND (JSON_VALUE(Payload, '$.pacienteEmail') IS NOT NULL OR JSON_VALUE(Payload, '$.pacienteNombre') IS NOT NULL);

/* Registro de envíos antiguo: destino enmascarado y sin el nombre del paciente en el cuerpo. */
UPDATE n
   SET Destino = CASE WHEN n.Canal = 'EMAIL' AND CHARINDEX('@', n.Destino) > 1
                      THEN LEFT(n.Destino, 1) + N'***' + SUBSTRING(n.Destino, CHARINDEX('@', n.Destino), 150)
                      ELSE N'***' + RIGHT(REPLACE(REPLACE(REPLACE(n.Destino, N' ', N''), N'+', N''), N'-', N''), 4) END,
       Cuerpo = CASE WHEN pa.Id IS NULL THEN n.Cuerpo
                     ELSE REPLACE(REPLACE(n.Cuerpo, pa.Nombres + N' ' + pa.ApellidoPaterno, N'{paciente}'), pa.Nombres, N'{paciente}') END
  FROM dbo.Notificaciones n
  LEFT JOIN dbo.Citas c     ON c.Id = n.CitaId
  LEFT JOIN dbo.Pacientes pa ON pa.Id = c.PacienteId
 WHERE n.Destino NOT LIKE N'%***%';
GO

/* ============================ 4. TDE y certificado de respaldos (M7) ============================ */
USE master;
GO
IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##')
    CREATE MASTER KEY ENCRYPTION BY PASSWORD = N'$(TDE_PASS)';
GO
/* Los certificados se respaldan junto a los datos (volumen sqldata): sin ellos no se podría
   restaurar la base ni un respaldo en otro servidor. Su clave privada queda cifrada con TDE_PASS. */
IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'ReservaMedTDE')
BEGIN
    CREATE CERTIFICATE ReservaMedTDE WITH SUBJECT = 'ReservaMed - cifrado de datos en reposo (TDE)', EXPIRY_DATE = '20361231';
    BACKUP CERTIFICATE ReservaMedTDE TO FILE = '/var/opt/mssql/data/ReservaMedTDE.cer'
        WITH PRIVATE KEY (FILE = '/var/opt/mssql/data/ReservaMedTDE.pvk', ENCRYPTION BY PASSWORD = N'$(TDE_PASS)');
END;
IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'ReservaMedRespaldos')
BEGIN
    CREATE CERTIFICATE ReservaMedRespaldos WITH SUBJECT = 'ReservaMed - cifrado de respaldos', EXPIRY_DATE = '20361231';
    BACKUP CERTIFICATE ReservaMedRespaldos TO FILE = '/var/opt/mssql/data/ReservaMedRespaldos.cer'
        WITH PRIVATE KEY (FILE = '/var/opt/mssql/data/ReservaMedRespaldos.pvk', ENCRYPTION BY PASSWORD = N'$(TDE_PASS)');
END;
GO
USE ReservaMed;
GO
IF NOT EXISTS (SELECT 1 FROM sys.dm_database_encryption_keys WHERE database_id = DB_ID())
    CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = AES_256 ENCRYPTION BY SERVER CERTIFICATE ReservaMedTDE;
GO
IF (SELECT is_encrypted FROM sys.databases WHERE name = 'ReservaMed') = 0
    ALTER DATABASE ReservaMed SET ENCRYPTION ON;
GO
PRINT 'Privacidad: auditoría encadenada, enmascaramiento y TDE configurados.';
GO
