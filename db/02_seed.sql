/* =====================================================================
   ReservaMed — Datos de demostración
   TODOS los nombres y RUT son inventados (con dígito verificador válido).
   Contraseña de todos los usuarios demo: Demo1234!
   Los códigos de prestación son de ejemplo (referenciales).
   ===================================================================== */
USE ReservaMed;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;  -- requerido al insertar en tablas con índices filtrados
SET NOCOUNT ON;

/* ---------- Sedes ---------- */
INSERT INTO dbo.Sedes (Nombre, Direccion, Comuna, Region, RutInstitucion, RazonSocial) VALUES
 (N'Clínica Andes Salud — Providencia', N'Av. Pedro de Valdivia 1550, piso 4', N'Providencia', N'Metropolitana', '76543210-3', N'Clínica Andes Salud SpA'),
 (N'Centro Médico Andes — Las Condes',  N'Av. Apoquindo 4800, piso 2',        N'Las Condes',  N'Metropolitana', '77100200-5', N'Andes Salud Diagnóstico SpA');

/* ---------- Especialidades ---------- */
INSERT INTO dbo.Especialidades (Nombre) VALUES
 (N'Medicina General'), (N'Gastroenterología'), (N'Dermatología'), (N'Traumatología'), (N'Pediatría');

DECLARE @MG INT  = (SELECT Id FROM dbo.Especialidades WHERE Nombre = N'Medicina General');
DECLARE @GAS INT = (SELECT Id FROM dbo.Especialidades WHERE Nombre = N'Gastroenterología');
DECLARE @DER INT = (SELECT Id FROM dbo.Especialidades WHERE Nombre = N'Dermatología');
DECLARE @TRA INT = (SELECT Id FROM dbo.Especialidades WHERE Nombre = N'Traumatología');
DECLARE @PED INT = (SELECT Id FROM dbo.Especialidades WHERE Nombre = N'Pediatría');

/* ---------- Prestaciones ---------- */
INSERT INTO dbo.Prestaciones (Codigo, Descripcion, EspecialidadId, DuracionMinutos, ValorParticular) VALUES
 ('0101001', N'CONSULTA MEDICINA GENERAL', @MG,  20, 28000),
 ('0101011', N'CONSULTA GASTROENTEROLOGIA', @GAS, 20, 38000),
 ('0101012', N'CONSULTA DERMATOLOGIA',     @DER, 20, 35000),
 ('0101013', N'CONSULTA TRAUMATOLOGIA',    @TRA, 20, 34000),
 ('0101014', N'CONSULTA PEDIATRIA',        @PED, 30, 30000);

/* ---------- Profesionales ---------- */
INSERT INTO dbo.Profesionales (Rut, Nombres, ApellidoPaterno, ApellidoMaterno, EspecialidadId, RegistroSis) VALUES
 ('15234567-4', N'Valentina', N'Soto',     N'Muñoz', @GAS, '412233'),
 ('13876543-1', N'Andrés',    N'Fuentes',  N'Rojas', @MG,  '398871'),
 ('16543987-2', N'Camila',    N'Herrera',  N'Lagos', @DER, '455120'),
 ('12987654-9', N'Rodrigo',   N'Pizarro',  N'Vera',  @TRA, '377654'),
 ('18234765-5', N'Francisca', N'Morales',  N'Díaz',  @PED, '488902');

DECLARE @SedeProv INT = (SELECT Id FROM dbo.Sedes WHERE Comuna = N'Providencia');
DECLARE @SedeLC   INT = (SELECT Id FROM dbo.Sedes WHERE Comuna = N'Las Condes');

/* ---------- Horarios (lunes a viernes, mañanas en una sede y tardes en otra) ---------- */
;WITH Dias AS (SELECT v AS Dia FROM (VALUES (1),(2),(3),(4),(5)) d(v))
INSERT INTO dbo.HorariosProfesional (ProfesionalId, SedeId, PrestacionId, DiaSemana, HoraInicio, HoraFin, DuracionMinutos, VigenteDesde)
SELECT p.Id,
       CASE WHEN h.Turno = 'AM' THEN @SedeProv ELSE @SedeLC END,
       pr.Id, d.Dia, h.Ini, h.Fin, pr.DuracionMinutos, '2026-01-01'
FROM dbo.Profesionales p
JOIN dbo.Prestaciones pr ON pr.EspecialidadId = p.EspecialidadId
CROSS JOIN Dias d
CROSS JOIN (VALUES ('AM', CAST('08:30' AS TIME(0)), CAST('13:00' AS TIME(0))),
                   ('PM', CAST('15:00' AS TIME(0)), CAST('18:30' AS TIME(0)))) h(Turno, Ini, Fin)
WHERE NOT (p.Rut = '18234765-5' AND h.Turno = 'PM');  -- pediatría sólo en la mañana

/* Vacaciones del traumatólogo la próxima semana */
INSERT INTO dbo.BloqueosAgenda (ProfesionalId, Desde, Hasta, Motivo)
SELECT Id,
       DATEADD(DAY, 7, CAST(CAST(SYSDATETIME() AS DATE) AS DATETIME2(0))),
       DATEADD(DAY, 12, CAST(CAST(SYSDATETIME() AS DATE) AS DATETIME2(0))),
       N'Vacaciones'
FROM dbo.Profesionales WHERE Rut = '12987654-9';

/* ---------- Financiadores ---------- */
INSERT INTO dbo.Financiadores (Codigo, Nombre, Tipo) VALUES
 ('FONASA',       N'Fonasa',          'FONASA'),
 ('COLMENA',      N'Colmena',         'ISAPRE'),
 ('CRUZBLANCA',   N'Cruz Blanca',     'ISAPRE'),
 ('BANMEDICA',    N'Banmédica',       'ISAPRE'),
 ('CONSALUD',     N'Consalud',        'ISAPRE'),
 ('VIDATRES',     N'Vida Tres',       'ISAPRE'),
 ('NUEVAMASVIDA', N'Nueva Masvida',   'ISAPRE'),
 ('ESENCIAL',     N'Esencial',        'ISAPRE'),
 ('PARTICULAR',   N'Particular',      'PARTICULAR');

/* ---------- Planes (bonificación simplificada) ---------- */
INSERT INTO dbo.PlanesSalud (FinanciadorId, Codigo, Nombre, PorcentajeAmbulatorio, TopeBonificacionConsulta)
SELECT f.Id, x.Codigo, x.Nombre, x.Pct, x.Tope
FROM (VALUES
  ('FONASA',       'MLE',            N'Modalidad Libre Elección',   50, 14000),
  ('COLMENA',      'COL-ORO-2026',   N'Colmena Oro 2026',           50, 16350),
  ('COLMENA',      'COL-PLATA-2026', N'Colmena Plata 2026',         40, 12000),
  ('CRUZBLANCA',   'CB-FULL-70',     N'Cruz Blanca Full 70',        70, 22000),
  ('BANMEDICA',    'BAN-ELITE',      N'Banmédica Elite',            70, 25000),
  ('CONSALUD',     'CON-PLUS-60',    N'Consalud Plus 60',           60, 18000),
  ('VIDATRES',     'VT-PREF',        N'Vida Tres Preferente',       70, 24000),
  ('NUEVAMASVIDA', 'NMV-CLASICO',    N'Nueva Masvida Clásico',      50, 15000),
  ('ESENCIAL',     'ESE-BASE',       N'Esencial Base',              50, 14000)
) x(Fin, Codigo, Nombre, Pct, Tope)
JOIN dbo.Financiadores f ON f.Codigo = x.Fin;

/* ---------- Convenios: cada financiador (menos Particular) con ambas sedes ---------- */
INSERT INTO dbo.Convenios (FinanciadorId, SedeId, NumeroConvenio)
SELECT f.Id, s.Id, CAST(10000 + f.Id * 100 + s.Id AS VARCHAR(20))
FROM dbo.Financiadores f CROSS JOIN dbo.Sedes s
WHERE f.Tipo <> 'PARTICULAR';

/* Aranceles: Fonasa paga sobre un arancel más bajo; las Isapres sobre el valor convenido */
INSERT INTO dbo.ArancelesConvenio (ConvenioId, PrestacionId, Valor)
SELECT c.Id, p.Id,
       CASE WHEN f.Tipo = 'FONASA' THEN CAST(ROUND(p.ValorParticular * 0.75, -1) AS INT)
            ELSE CAST(ROUND(p.ValorParticular * 0.95, -1) AS INT) END
FROM dbo.Convenios c
JOIN dbo.Financiadores f ON f.Id = c.FinanciadorId
CROSS JOIN dbo.Prestaciones p;

/* Ajuste puntual: consulta de gastroenterología con Colmena */
UPDATE a SET Valor = 36110
FROM dbo.ArancelesConvenio a
JOIN dbo.Convenios c ON c.Id = a.ConvenioId
JOIN dbo.Financiadores f ON f.Id = c.FinanciadorId
JOIN dbo.Prestaciones p ON p.Id = a.PrestacionId
WHERE f.Codigo = 'COLMENA' AND p.Codigo = '0101011';

/* ---------- Pacientes ---------- */
INSERT INTO dbo.Pacientes (Rut, Nombres, ApellidoPaterno, ApellidoMaterno, FechaNacimiento, Sexo, Email, Telefono, Comuna) VALUES
 ('19876321-7', N'Ignacio',  N'Reyes',     N'Castro', '1997-04-12', 'M', N'ignacio@demo.cl',  '+56911112222', N'Ñuñoa'),
 ('20456789-1', N'Martina',  N'Silva',     N'Torres', '2000-09-30', 'F', N'martina@demo.cl',  '+56933334444', N'Maipú'),
 ('21567890-3', N'Benjamín', N'Contreras', N'Pino',   '2003-01-18', 'M', N'benjamin@demo.cl', '+56955556666', N'La Florida'),
 ('17345678-6', N'Sofía',    N'Gutiérrez', N'Bravo',  '1990-11-05', 'F', N'sofia@demo.cl',    '+56977778888', N'Providencia');

/* Previsiones: Ignacio es CARGA de su madre en Colmena; Martina Fonasa C; Benjamín Fonasa A; Sofía particular */
INSERT INTO dbo.Previsiones (PacienteId, FinanciadorId, PlanSaludId, TramoFonasa, EsCarga, AfiliadoRut, AfiliadoNombre)
SELECT pa.Id, f.Id, pl.Id, x.Tramo, x.EsCarga, x.AfRut, x.AfNom
FROM (VALUES
  ('19876321-7', 'COLMENA',    'COL-ORO-2026', NULL, 1, '14765432-4', N'CASTRO NUÑEZ PATRICIA ANDREA'),
  ('20456789-1', 'FONASA',     'MLE',          'C',  0, NULL, NULL),
  ('21567890-3', 'FONASA',     'MLE',          'A',  0, NULL, NULL),
  ('17345678-6', 'PARTICULAR', NULL,           NULL, 0, NULL, NULL)
) x(Rut, Fin, PlanCod, Tramo, EsCarga, AfRut, AfNom)
JOIN dbo.Pacientes pa ON pa.Rut = x.Rut
JOIN dbo.Financiadores f ON f.Codigo = x.Fin
LEFT JOIN dbo.PlanesSalud pl ON pl.FinanciadorId = f.Id AND pl.Codigo = x.PlanCod;

/* ---------- Usuarios (contraseña: Demo1234!) ---------- */
DECLARE @Hash VARCHAR(100) = '$2a$11$LKYLu7WF9Zs7E/NLZXjOauZFoGu/yz5ffOdvh1265UxBSZngaCTOW';

INSERT INTO dbo.Usuarios (Email, PasswordHash, Rol, NombreVisible, SedeId) VALUES
 (N'admin@reservamed.cl',     @Hash, 'ADMIN',     N'Administrador',       NULL),
 (N'recepcion@reservamed.cl', @Hash, 'RECEPCION', N'Recepción Providencia', @SedeProv),
 (N'recepcion.lc@reservamed.cl', @Hash, 'RECEPCION', N'Recepción Las Condes', @SedeLC);

INSERT INTO dbo.Usuarios (Email, PasswordHash, Rol, NombreVisible, ProfesionalId)
SELECT N'dra.soto@reservamed.cl', @Hash, 'MEDICO', N'Dra. Valentina Soto', Id
FROM dbo.Profesionales WHERE Rut = '15234567-4';

INSERT INTO dbo.Usuarios (Email, PasswordHash, Rol, NombreVisible, PacienteId)
SELECT p.Email, @Hash, 'PACIENTE', p.Nombres + N' ' + p.ApellidoPaterno, p.Id
FROM dbo.Pacientes p;
GO

PRINT 'Datos demo cargados.';
GO
