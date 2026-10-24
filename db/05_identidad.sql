/* Fase 2 de seguridad: identidad y sesión (hallazgos A1 A2 A7 A8 M1 M2 B4).
   Idempotente: se puede ejecutar sobre una base existente cuantas veces sea necesario
   (db-init lo ejecuta en cada arranque; en local, ejecútelo una vez en SSMS). */
USE ReservaMed;
GO

/* ---------- Usuarios: verificación de correo, TOTP y bloqueo ---------- */
IF COL_LENGTH('dbo.Usuarios', 'EmailVerificado') IS NULL
    -- Las cuentas que ya existían quedan verificadas; las nuevas se insertan con 0.
    ALTER TABLE dbo.Usuarios ADD EmailVerificado BIT NOT NULL CONSTRAINT DF_Usuarios_EmailVerificado DEFAULT 1;
IF COL_LENGTH('dbo.Usuarios', 'TotpSecreto') IS NULL
    ALTER TABLE dbo.Usuarios ADD TotpSecreto VARBINARY(200) NULL;          -- cifrado con AES-GCM (Totp:Clave)
IF COL_LENGTH('dbo.Usuarios', 'TotpActivo') IS NULL
    ALTER TABLE dbo.Usuarios ADD TotpActivo BIT NOT NULL CONSTRAINT DF_Usuarios_TotpActivo DEFAULT 0;
IF COL_LENGTH('dbo.Usuarios', 'TotpUltimoPaso') IS NULL
    ALTER TABLE dbo.Usuarios ADD TotpUltimoPaso BIGINT NULL;               -- evita reusar el mismo código
IF COL_LENGTH('dbo.Usuarios', 'IntentosFallidos') IS NULL
    ALTER TABLE dbo.Usuarios ADD IntentosFallidos INT NOT NULL CONSTRAINT DF_Usuarios_Intentos DEFAULT 0;
IF COL_LENGTH('dbo.Usuarios', 'BloqueosConsecutivos') IS NULL
    ALTER TABLE dbo.Usuarios ADD BloqueosConsecutivos INT NOT NULL CONSTRAINT DF_Usuarios_Bloqueos DEFAULT 0;
IF COL_LENGTH('dbo.Usuarios', 'BloqueadoHasta') IS NULL
    ALTER TABLE dbo.Usuarios ADD BloqueadoHasta DATETIME2(0) NULL;
GO

/* ---------- Sesiones: una por inicio de sesión; revocable ---------- */
IF OBJECT_ID('dbo.Sesiones') IS NULL
CREATE TABLE dbo.Sesiones (
    Id                UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    UsuarioId         INT NOT NULL REFERENCES dbo.Usuarios(Id),
    Cliente           VARCHAR(12)  NOT NULL CHECK (Cliente IN ('PORTAL','BACKOFFICE')),
    CreadaEn          DATETIME2(0) NOT NULL,
    UltimoUso         DATETIME2(0) NOT NULL,
    ExpiraEn          DATETIME2(0) NOT NULL,     -- vida máxima: no se extiende al renovar
    RevocadaEn        DATETIME2(0) NULL,
    MotivoRevocacion  NVARCHAR(100) NULL,
    Ip                VARCHAR(45)  NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Sesiones_Usuario')
    CREATE INDEX IX_Sesiones_Usuario ON dbo.Sesiones(UsuarioId) WHERE RevocadaEn IS NULL;
GO

/* ---------- Tokens de refresco: se guarda solo su SHA-256; un solo uso (rotación) ---------- */
IF OBJECT_ID('dbo.TokensRefresco') IS NULL
CREATE TABLE dbo.TokensRefresco (
    Hash        BINARY(32)       NOT NULL PRIMARY KEY,
    SesionId    UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.Sesiones(Id),
    CreadoEn    DATETIME2(0)     NOT NULL,
    ExpiraEn    DATETIME2(0)     NOT NULL,
    UsadoEn     DATETIME2(0)     NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TokensRefresco_Sesion')
    CREATE INDEX IX_TokensRefresco_Sesion ON dbo.TokensRefresco(SesionId);
GO

/* ---------- Tokens de un solo uso enviados por correo (verificación de cuenta) ---------- */
IF OBJECT_ID('dbo.TokensUsuario') IS NULL
CREATE TABLE dbo.TokensUsuario (
    Hash        BINARY(32)   NOT NULL PRIMARY KEY,
    UsuarioId   INT          NOT NULL REFERENCES dbo.Usuarios(Id),
    Tipo        VARCHAR(20)  NOT NULL CHECK (Tipo IN ('VERIFICAR_CORREO')),
    CreadoEn    DATETIME2(0) NOT NULL,
    ExpiraEn    DATETIME2(0) NOT NULL,
    UsadoEn     DATETIME2(0) NULL
);
GO

/* ---------- Cuenta de QA para verificaciones automáticas (scripts y CI) ----------
   Administrador de demostración, distinto de admin@reservamed.cl para que los scripts puedan
   enrolar su propio TOTP sin tocar el del usuario. Clave Demo1234! (solo datos demo). */
IF NOT EXISTS (SELECT 1 FROM dbo.Usuarios WHERE Email = N'qa@reservamed.cl')
    INSERT INTO dbo.Usuarios (Email, PasswordHash, Rol, NombreVisible, EmailVerificado)
    VALUES (N'qa@reservamed.cl', '$2a$11$LKYLu7WF9Zs7E/NLZXjOauZFoGu/yz5ffOdvh1265UxBSZngaCTOW', 'ADMIN', N'QA automatizado', 1);
GO
PRINT 'Identidad y sesiones listas.';
GO
