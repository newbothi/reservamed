/* Fase 5 (brechas ASVS nivel 2). Idempotente: db-init lo ejecuta en cada arranque, después de 07.
   - V3.7.1  Sesiones.ReautenticadoEn: acciones sensibles exigen un TOTP reciente.
   - V8.3.3  Usuarios.PrivacidadAceptadaEn: consentimiento explícito en el registro.
   - V8.3.2  SolicitudesDatos: exportación y solicitud de eliminación (Ley 21.719).
   - V11.1.7 AlertasSeguridad: actividad inusual detectada (bloqueos, accesos denegados, etc.). */
USE ReservaMed;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF COL_LENGTH('dbo.Sesiones', 'ReautenticadoEn') IS NULL
    ALTER TABLE dbo.Sesiones ADD ReautenticadoEn DATETIME2(0) NULL;
IF COL_LENGTH('dbo.Usuarios', 'PrivacidadAceptadaEn') IS NULL
    ALTER TABLE dbo.Usuarios ADD PrivacidadAceptadaEn DATETIME2(0) NULL;
GO

IF OBJECT_ID('dbo.SolicitudesDatos') IS NULL
CREATE TABLE dbo.SolicitudesDatos (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    UsuarioId   INT NOT NULL REFERENCES dbo.Usuarios(Id),
    PacienteId  INT NULL REFERENCES dbo.Pacientes(Id),
    Tipo        VARCHAR(12) NOT NULL CHECK (Tipo IN ('EXPORTAR', 'ELIMINAR')),
    Estado      VARCHAR(12) NOT NULL CHECK (Estado IN ('ATENDIDA', 'PENDIENTE', 'RESUELTA')),
    CreadaEn    DATETIME2(0) NOT NULL,
    ResueltaEn  DATETIME2(0) NULL,
    Nota        NVARCHAR(300) NULL
);
GO

IF OBJECT_ID('dbo.AlertasSeguridad') IS NULL
CREATE TABLE dbo.AlertasSeguridad (
    Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    Regla       VARCHAR(40)   NOT NULL,     -- LOGIN_FALLIDO_IP, ACCESOS_DENEGADOS_USUARIO, ...
    Severidad   VARCHAR(10)   NOT NULL CHECK (Severidad IN ('MEDIA', 'ALTA')),
    Clave       NVARCHAR(150) NOT NULL,     -- IP o usuario agrupado (nunca datos de pacientes)
    Conteo      INT           NOT NULL,
    Detalle     NVARCHAR(300) NOT NULL,
    DesdeEn     DATETIME2(0)  NOT NULL,
    CreadaEn    DATETIME2(0)  NOT NULL,
    RevisadaEn  DATETIME2(0)  NULL,
    RevisadaPor INT NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AlertasSeguridad_Regla')
    CREATE INDEX IX_AlertasSeguridad_Regla ON dbo.AlertasSeguridad(Regla, Clave, CreadaEn DESC);
GO
PRINT 'Cuenta, derechos de datos y alertas de seguridad listos.';
GO
