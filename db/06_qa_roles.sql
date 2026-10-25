/* Fase 3: cuentas de QA por rol para las verificaciones automáticas (scripts y CI).
   Son distintas de las cuentas demo para que los scripts enrolen su propio TOTP sin tocar el
   autenticador que el usuario configuró en las suyas. Clave Demo1234! (solo datos demo).
   Idempotente: db-init lo ejecuta en cada arranque. */
USE ReservaMed;
GO
DECLARE @Hash VARCHAR(100) = '$2a$11$LKYLu7WF9Zs7E/NLZXjOauZFoGu/yz5ffOdvh1265UxBSZngaCTOW';  -- nosemgrep: detected-bcrypt-hash (hash de la clave demo publicada Demo1234!)
DECLARE @SedeProv INT = (SELECT Id FROM dbo.Sedes WHERE Comuna = N'Providencia');
DECLARE @SedeLC   INT = (SELECT Id FROM dbo.Sedes WHERE Comuna = N'Las Condes');
DECLARE @Soto     INT = (SELECT Id FROM dbo.Profesionales WHERE Rut = '15234567-4');

IF NOT EXISTS (SELECT 1 FROM dbo.Usuarios WHERE Email = N'qa.recepcion@reservamed.cl')
    INSERT INTO dbo.Usuarios (Email, PasswordHash, Rol, NombreVisible, SedeId, EmailVerificado)
    VALUES (N'qa.recepcion@reservamed.cl', @Hash, 'RECEPCION', N'QA Recepción Providencia', @SedeProv, 1);

IF NOT EXISTS (SELECT 1 FROM dbo.Usuarios WHERE Email = N'qa.recepcion.lc@reservamed.cl')
    INSERT INTO dbo.Usuarios (Email, PasswordHash, Rol, NombreVisible, SedeId, EmailVerificado)
    VALUES (N'qa.recepcion.lc@reservamed.cl', @Hash, 'RECEPCION', N'QA Recepción Las Condes', @SedeLC, 1);

-- Médico de QA: misma agenda que la Dra. Soto (ve solo los pacientes con cita en esa agenda).
IF NOT EXISTS (SELECT 1 FROM dbo.Usuarios WHERE Email = N'qa.medico@reservamed.cl')
    INSERT INTO dbo.Usuarios (Email, PasswordHash, Rol, NombreVisible, ProfesionalId, EmailVerificado)
    VALUES (N'qa.medico@reservamed.cl', @Hash, 'MEDICO', N'QA Médico (agenda Dra. Soto)', @Soto, 1);
GO
PRINT 'Cuentas de QA por rol listas.';
GO
