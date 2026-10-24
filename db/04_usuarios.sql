/* Hallazgo A5: cada servicio entra con su propio usuario y solo los permisos que necesita.
   sa queda solo para db-init. Se ejecuta en cada arranque (idempotente): si cambia una clave
   en .env, se actualiza. Variables de sqlcmd: API_PASS, WORKER_PASS, ML_PASS, ML_SEED_PASS. */
USE master;
GO
IF SUSER_ID('rm_api') IS NULL     CREATE LOGIN rm_api     WITH PASSWORD = N'$(API_PASS)',     CHECK_POLICY = ON, DEFAULT_DATABASE = ReservaMed;
ELSE                              ALTER  LOGIN rm_api     WITH PASSWORD = N'$(API_PASS)';
IF SUSER_ID('rm_worker') IS NULL  CREATE LOGIN rm_worker  WITH PASSWORD = N'$(WORKER_PASS)',  CHECK_POLICY = ON, DEFAULT_DATABASE = ReservaMed;
ELSE                              ALTER  LOGIN rm_worker  WITH PASSWORD = N'$(WORKER_PASS)';
IF SUSER_ID('rm_ml') IS NULL      CREATE LOGIN rm_ml      WITH PASSWORD = N'$(ML_PASS)',      CHECK_POLICY = ON, DEFAULT_DATABASE = ReservaMed;
ELSE                              ALTER  LOGIN rm_ml      WITH PASSWORD = N'$(ML_PASS)';
IF SUSER_ID('rm_ml_seed') IS NULL CREATE LOGIN rm_ml_seed WITH PASSWORD = N'$(ML_SEED_PASS)', CHECK_POLICY = ON, DEFAULT_DATABASE = ReservaMed;
ELSE                              ALTER  LOGIN rm_ml_seed WITH PASSWORD = N'$(ML_SEED_PASS)';
GO

USE ReservaMed;
GO
IF USER_ID('rm_api')     IS NULL CREATE USER rm_api     FOR LOGIN rm_api;
IF USER_ID('rm_worker')  IS NULL CREATE USER rm_worker  FOR LOGIN rm_worker;
IF USER_ID('rm_ml')      IS NULL CREATE USER rm_ml      FOR LOGIN rm_ml;
IF USER_ID('rm_ml_seed') IS NULL CREATE USER rm_ml_seed FOR LOGIN rm_ml_seed;
GO

/* API Core: lee y escribe datos, sin DDL ni permisos de administración. */
ALTER ROLE db_datareader ADD MEMBER rm_api;
ALTER ROLE db_datawriter ADD MEMBER rm_api;
GRANT UPDATE ON OBJECT::dbo.SeqFolioBono TO rm_api;   -- NEXT VALUE FOR (folio del bono)

/* Worker: solo registra notificaciones y consulta el riesgo calculado. */
GRANT INSERT ON dbo.Notificaciones   TO rm_worker;
GRANT SELECT ON dbo.PrediccionesCita TO rm_worker;
/* F4 (A9): los eventos ya no traen datos del paciente; el Worker lee SOLO lo necesario para
   notificar (sin RUT ni fecha de nacimiento) y borra los registros de envío vencidos. */
GRANT SELECT ON dbo.Citas          TO rm_worker;
GRANT SELECT ON dbo.Profesionales  TO rm_worker;
GRANT SELECT ON dbo.Especialidades TO rm_worker;
GRANT SELECT ON dbo.Sedes          TO rm_worker;
GRANT SELECT ON dbo.Pacientes (Id, Nombres, Email, Telefono)         TO rm_worker;
GRANT SELECT ON dbo.Bonos (Id, CitaId, Folio)                         TO rm_worker;
GRANT SELECT ON dbo.Pagos (Id, CitaId, Medio, Monto, Estado, BonoId) TO rm_worker;
GRANT SELECT, DELETE ON dbo.Notificaciones TO rm_worker;

/* ml-service en ejecución: lee lo necesario para entrenar y predecir; escribe solo sus tablas. */
GRANT SELECT ON dbo.Citas               TO rm_ml;
GRANT SELECT ON dbo.Pacientes           TO rm_ml;
GRANT SELECT ON dbo.Previsiones         TO rm_ml;
GRANT SELECT ON dbo.Pagos               TO rm_ml;
GRANT SELECT ON dbo.Financiadores       TO rm_ml;
GRANT SELECT ON dbo.PlanesSalud         TO rm_ml;
GRANT SELECT ON dbo.Profesionales       TO rm_ml;
GRANT SELECT ON dbo.Especialidades      TO rm_ml;
GRANT SELECT ON dbo.Prestaciones        TO rm_ml;
GRANT SELECT ON dbo.Sedes               TO rm_ml;
GRANT SELECT ON dbo.HorariosProfesional TO rm_ml;
GRANT SELECT, INSERT, UPDATE ON dbo.ModelosML        TO rm_ml;
GRANT SELECT, INSERT, UPDATE ON dbo.PrediccionesCita TO rm_ml;

/* ml-bootstrap (una sola vez): además inserta y limpia el historial sintético. */
ALTER ROLE db_datareader ADD MEMBER rm_ml_seed;
GRANT INSERT, DELETE ON dbo.Pacientes   TO rm_ml_seed;
GRANT INSERT, DELETE ON dbo.Previsiones TO rm_ml_seed;
GRANT INSERT, DELETE ON dbo.Citas       TO rm_ml_seed;
GRANT INSERT, DELETE ON dbo.Pagos       TO rm_ml_seed;
GRANT INSERT, UPDATE ON dbo.ModelosML        TO rm_ml_seed;
GRANT INSERT, UPDATE ON dbo.PrediccionesCita TO rm_ml_seed;
GO
PRINT 'Usuarios por servicio configurados.';
GO
