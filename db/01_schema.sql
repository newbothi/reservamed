/* =====================================================================
   ReservaMed — Esquema de base de datos (SQL Server 2022 / 2025)
   Sistema de reservas de citas médicas + admisión con bono electrónico.
   Las horas se guardan en hora local de Chile (America/Santiago).
   Montos en pesos chilenos (CLP), enteros, sin decimales.
   ===================================================================== */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;  -- obligatorio para índices filtrados (sqlcmd lo trae en OFF)
GO

IF DB_ID('ReservaMed') IS NULL
    CREATE DATABASE ReservaMed;
GO

USE ReservaMed;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------- Catálogos de la clínica ---------- */

CREATE TABLE dbo.Sedes (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    Nombre          NVARCHAR(100) NOT NULL,
    Direccion       NVARCHAR(200) NOT NULL,
    Comuna          NVARCHAR(80)  NOT NULL,
    Region          NVARCHAR(80)  NOT NULL,
    -- Institución prestadora que aparece en el bono (RUT + razón social)
    RutInstitucion  VARCHAR(12)   NOT NULL,
    RazonSocial     NVARCHAR(150) NOT NULL,
    Activa          BIT NOT NULL DEFAULT 1
);

CREATE TABLE dbo.Especialidades (
    Id      INT IDENTITY(1,1) PRIMARY KEY,
    Nombre  NVARCHAR(100) NOT NULL UNIQUE
);

CREATE TABLE dbo.Profesionales (
    Id               INT IDENTITY(1,1) PRIMARY KEY,
    Rut              VARCHAR(12)   NOT NULL UNIQUE,   -- formato 12345678-9
    Nombres          NVARCHAR(80)  NOT NULL,
    ApellidoPaterno  NVARCHAR(60)  NOT NULL,
    ApellidoMaterno  NVARCHAR(60)  NULL,
    EspecialidadId   INT NOT NULL REFERENCES dbo.Especialidades(Id),
    RegistroSis      VARCHAR(20)   NULL,              -- Registro Nacional de Prestadores (Superintendencia de Salud)
    Activo           BIT NOT NULL DEFAULT 1
);

/* Prestaciones con código de arancel (códigos de ejemplo, referenciales) */
CREATE TABLE dbo.Prestaciones (
    Id               INT IDENTITY(1,1) PRIMARY KEY,
    Codigo           VARCHAR(10)   NOT NULL UNIQUE,
    Descripcion      NVARCHAR(150) NOT NULL,
    EspecialidadId   INT NULL REFERENCES dbo.Especialidades(Id),
    DuracionMinutos  INT NOT NULL DEFAULT 20,
    ValorParticular  INT NOT NULL CHECK (ValorParticular > 0)
);

/* Horario semanal recurrente de cada profesional en cada sede.
   DiaSemana: 1 = lunes ... 7 = domingo */
CREATE TABLE dbo.HorariosProfesional (
    Id               INT IDENTITY(1,1) PRIMARY KEY,
    ProfesionalId    INT NOT NULL REFERENCES dbo.Profesionales(Id),
    SedeId           INT NOT NULL REFERENCES dbo.Sedes(Id),
    PrestacionId     INT NOT NULL REFERENCES dbo.Prestaciones(Id),
    DiaSemana        TINYINT NOT NULL CHECK (DiaSemana BETWEEN 1 AND 7),
    HoraInicio       TIME(0) NOT NULL,
    HoraFin          TIME(0) NOT NULL,
    DuracionMinutos  INT NOT NULL CHECK (DuracionMinutos BETWEEN 5 AND 120),
    VigenteDesde     DATE NOT NULL,
    VigenteHasta     DATE NULL,
    CONSTRAINT CK_Horario_Rango CHECK (HoraFin > HoraInicio)
);

/* Vacaciones, congresos, licencias: bloquean la agenda */
CREATE TABLE dbo.BloqueosAgenda (
    Id             INT IDENTITY(1,1) PRIMARY KEY,
    ProfesionalId  INT NOT NULL REFERENCES dbo.Profesionales(Id),
    Desde          DATETIME2(0) NOT NULL,
    Hasta          DATETIME2(0) NOT NULL,
    Motivo         NVARCHAR(150) NOT NULL,
    CONSTRAINT CK_Bloqueo_Rango CHECK (Hasta > Desde)
);

/* ---------- Previsión de salud (Fonasa / Isapres) ---------- */

CREATE TABLE dbo.Financiadores (
    Id      INT IDENTITY(1,1) PRIMARY KEY,
    Codigo  VARCHAR(20)   NOT NULL UNIQUE,
    Nombre  NVARCHAR(80)  NOT NULL,
    Tipo    VARCHAR(12)   NOT NULL CHECK (Tipo IN ('FONASA','ISAPRE','PARTICULAR'))
);

/* Plan de salud: define la bonificación ambulatoria (simplificada) */
CREATE TABLE dbo.PlanesSalud (
    Id                       INT IDENTITY(1,1) PRIMARY KEY,
    FinanciadorId            INT NOT NULL REFERENCES dbo.Financiadores(Id),
    Codigo                   VARCHAR(30)   NOT NULL,
    Nombre                   NVARCHAR(100) NOT NULL,
    PorcentajeAmbulatorio    INT NOT NULL CHECK (PorcentajeAmbulatorio BETWEEN 0 AND 100),
    TopeBonificacionConsulta INT NULL,     -- tope en CLP por consulta (NULL = sin tope)
    CONSTRAINT UQ_Plan UNIQUE (FinanciadorId, Codigo)
);

/* Convenio entre financiador y clínica (sede). Es el "Convenio: 13537" del bono. */
CREATE TABLE dbo.Convenios (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    FinanciadorId   INT NOT NULL REFERENCES dbo.Financiadores(Id),
    SedeId          INT NOT NULL REFERENCES dbo.Sedes(Id),
    NumeroConvenio  VARCHAR(20) NOT NULL,
    Vigente         BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_Convenio UNIQUE (FinanciadorId, SedeId)
);

/* Valor convenido de cada prestación por convenio (arancel) */
CREATE TABLE dbo.ArancelesConvenio (
    ConvenioId    INT NOT NULL REFERENCES dbo.Convenios(Id),
    PrestacionId  INT NOT NULL REFERENCES dbo.Prestaciones(Id),
    Valor         INT NOT NULL CHECK (Valor > 0),
    PRIMARY KEY (ConvenioId, PrestacionId)
);

/* ---------- Pacientes y usuarios ---------- */

CREATE TABLE dbo.Pacientes (
    Id               INT IDENTITY(1,1) PRIMARY KEY,
    Rut              VARCHAR(12)   NOT NULL UNIQUE,
    Nombres          NVARCHAR(80)  NOT NULL,
    ApellidoPaterno  NVARCHAR(60)  NOT NULL,
    ApellidoMaterno  NVARCHAR(60)  NULL,
    FechaNacimiento  DATE NOT NULL,
    Sexo             CHAR(1) NOT NULL CHECK (Sexo IN ('M','F','X')),
    Email            NVARCHAR(150) NOT NULL,
    Telefono         VARCHAR(20)   NULL,
    Comuna           NVARCHAR(80)  NULL,
    CreadoEn         DATETIME2(0) NOT NULL DEFAULT SYSDATETIME()
);

/* Previsión vigente del paciente.
   Si el paciente es CARGA, el afiliado (titular del plan) es otra persona:
   en el bono aparecen "Afiliado" y "Beneficiario" por separado. */
CREATE TABLE dbo.Previsiones (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    PacienteId      INT NOT NULL REFERENCES dbo.Pacientes(Id),
    FinanciadorId   INT NOT NULL REFERENCES dbo.Financiadores(Id),
    PlanSaludId     INT NULL REFERENCES dbo.PlanesSalud(Id),
    TramoFonasa     CHAR(1) NULL CHECK (TramoFonasa IN ('A','B','C','D')),
    EsCarga         BIT NOT NULL DEFAULT 0,
    AfiliadoRut     VARCHAR(12)   NULL,     -- titular del plan cuando EsCarga = 1
    AfiliadoNombre  NVARCHAR(150) NULL,
    Vigente         BIT NOT NULL DEFAULT 1,
    ActualizadaEn   DATETIME2(0) NOT NULL DEFAULT SYSDATETIME()
);
CREATE UNIQUE INDEX UX_Prevision_Vigente ON dbo.Previsiones(PacienteId) WHERE Vigente = 1;

CREATE TABLE dbo.Usuarios (
    Id             INT IDENTITY(1,1) PRIMARY KEY,
    Email          NVARCHAR(150) NOT NULL UNIQUE,
    PasswordHash   VARCHAR(100)  NOT NULL,          -- BCrypt
    Rol            VARCHAR(12)   NOT NULL CHECK (Rol IN ('PACIENTE','RECEPCION','MEDICO','ADMIN')),
    NombreVisible  NVARCHAR(150) NOT NULL,
    PacienteId     INT NULL REFERENCES dbo.Pacientes(Id),
    ProfesionalId  INT NULL REFERENCES dbo.Profesionales(Id),
    SedeId         INT NULL REFERENCES dbo.Sedes(Id),  -- sede de trabajo (recepción)
    Activo         BIT NOT NULL DEFAULT 1,
    CreadoEn       DATETIME2(0) NOT NULL DEFAULT SYSDATETIME()
);

/* ---------- Citas ---------- */

CREATE TABLE dbo.Citas (
    Id                   INT IDENTITY(1,1) PRIMARY KEY,
    PacienteId           INT NOT NULL REFERENCES dbo.Pacientes(Id),
    ProfesionalId        INT NOT NULL REFERENCES dbo.Profesionales(Id),
    SedeId               INT NOT NULL REFERENCES dbo.Sedes(Id),
    PrestacionId         INT NOT NULL REFERENCES dbo.Prestaciones(Id),
    FechaHoraInicio      DATETIME2(0) NOT NULL,
    FechaHoraFin         DATETIME2(0) NOT NULL,
    Estado               VARCHAR(15) NOT NULL CHECK (Estado IN
                           ('PENDIENTE_PAGO','RESERVADA','CONFIRMADA','ADMITIDA',
                            'ATENDIDA','CANCELADA','EXPIRADA','NO_ASISTIO')),
    Canal                VARCHAR(10) NOT NULL CHECK (Canal IN ('WEB','PRESENCIAL','TELEFONO')),
    -- 1 mientras la cita ocupa el cupo. Pasa a 0 al cancelar o expirar.
    OcupaCupo            BIT NOT NULL DEFAULT 1,
    ExpiraEn             DATETIME2(0) NULL,    -- reserva temporal mientras se paga online
    RecordatorioEnviado  BIT NOT NULL DEFAULT 0,
    MotivoCancelacion    NVARCHAR(200) NULL,
    CreadoPorUsuarioId   INT NULL REFERENCES dbo.Usuarios(Id),
    CreadoEn             DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),
    RowVer               ROWVERSION NOT NULL,  -- bloqueo optimista
    CONSTRAINT CK_Cita_Rango CHECK (FechaHoraFin > FechaHoraInicio)
);

/* La regla clave: un profesional no puede tener dos citas activas a la misma hora.
   Si dos personas reservan el mismo cupo al mismo tiempo, la segunda inserción falla (error 2601). */
CREATE UNIQUE INDEX UX_Citas_CupoActivo
    ON dbo.Citas(ProfesionalId, FechaHoraInicio)
    WHERE OcupaCupo = 1;

CREATE INDEX IX_Citas_Paciente ON dbo.Citas(PacienteId, FechaHoraInicio DESC);
CREATE INDEX IX_Citas_Sede_Fecha ON dbo.Citas(SedeId, FechaHoraInicio);
CREATE INDEX IX_Citas_Expiracion ON dbo.Citas(ExpiraEn) WHERE Estado = 'PENDIENTE_PAGO';

/* ---------- Bonos (emitidos por el financiador vía I-Med) ---------- */

CREATE SEQUENCE dbo.SeqFolioBono AS BIGINT START WITH 151000001 INCREMENT BY 1;

CREATE TABLE dbo.Bonos (
    Id                     INT IDENTITY(1,1) PRIMARY KEY,
    Folio                  BIGINT NOT NULL UNIQUE,
    CitaId                 INT NOT NULL REFERENCES dbo.Citas(Id),
    FinanciadorId          INT NOT NULL REFERENCES dbo.Financiadores(Id),
    ConvenioId             INT NOT NULL REFERENCES dbo.Convenios(Id),
    NumeroConvenio         VARCHAR(20)   NOT NULL,
    PlanCodigo             VARCHAR(30)   NULL,
    -- Beneficiario (quien se atiende) y afiliado (titular del plan)
    BeneficiarioPacienteId INT NOT NULL REFERENCES dbo.Pacientes(Id),
    BeneficiarioRut        VARCHAR(12)   NOT NULL,
    BeneficiarioNombre     NVARCHAR(150) NOT NULL,
    AfiliadoRut            VARCHAR(12)   NOT NULL,
    AfiliadoNombre         NVARCHAR(150) NOT NULL,
    -- Prestador
    RutInstitucion         VARCHAR(12)   NOT NULL,
    NombreInstitucion      NVARCHAR(150) NOT NULL,
    ProfesionalId          INT NOT NULL REFERENCES dbo.Profesionales(Id),
    RutProfesional         VARCHAR(12)   NOT NULL,
    NombreProfesional      NVARCHAR(150) NOT NULL,
    DerivadorRut           VARCHAR(12)   NULL,
    -- Emisión
    Canal                  VARCHAR(10)   NOT NULL CHECK (Canal IN ('PRESENCIAL','WEB')),
    TipoFirma              VARCHAR(10)   NOT NULL CHECK (TipoFirma IN ('HUELLA','CLAVE_WEB')),
    CodigoAuditoria        VARCHAR(25)   NOT NULL UNIQUE,   -- BONO-XXXX-XXXX-XXXX
    RutEmisor              VARCHAR(12)   NOT NULL,          -- quien emite (clínica)
    FechaEmision           DATETIME2(0)  NOT NULL,
    ValidoHasta            DATE NOT NULL,                   -- válido sólo para la fecha de emisión
    Estado                 VARCHAR(10) NOT NULL CHECK (Estado IN ('EMITIDO','PAGADO','ANULADO')),
    -- Totales
    TotalValor             INT NOT NULL,
    TotalBonificacion      INT NOT NULL,
    TotalOtraBonificacion  INT NOT NULL DEFAULT 0,
    TotalCopago            INT NOT NULL,
    EmitidoPorUsuarioId    INT NULL REFERENCES dbo.Usuarios(Id),
    RowVer                 ROWVERSION NOT NULL,
    CONSTRAINT CK_Bono_Totales CHECK (TotalValor = TotalBonificacion + TotalOtraBonificacion + TotalCopago)
);
CREATE UNIQUE INDEX UX_Bono_CitaActivo ON dbo.Bonos(CitaId) WHERE Estado <> 'ANULADO';

CREATE TABLE dbo.BonoDetalles (
    Id                INT IDENTITY(1,1) PRIMARY KEY,
    BonoId            INT NOT NULL REFERENCES dbo.Bonos(Id),
    PrestacionId      INT NOT NULL REFERENCES dbo.Prestaciones(Id),
    Codigo            VARCHAR(10)   NOT NULL,
    Descripcion       NVARCHAR(150) NOT NULL,
    Cantidad          INT NOT NULL CHECK (Cantidad > 0),
    Valor             INT NOT NULL,
    Bonificacion      INT NOT NULL,
    OtraBonificacion  INT NOT NULL DEFAULT 0,
    Copago            INT NOT NULL,
    CONSTRAINT CK_Detalle_Montos CHECK (Valor = Bonificacion + OtraBonificacion + Copago)
);

/* ---------- Pagos (POS presencial en caja o Webpay online) ---------- */

CREATE TABLE dbo.Pagos (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    CitaId              INT NOT NULL REFERENCES dbo.Citas(Id),
    BonoId              INT NULL REFERENCES dbo.Bonos(Id),   -- NULL en atención particular
    Medio               VARCHAR(12) NOT NULL CHECK (Medio IN ('POS_DEBITO','POS_CREDITO','WEBPAY','EFECTIVO')),
    Monto               INT NOT NULL CHECK (Monto >= 0),
    Estado              VARCHAR(12) NOT NULL CHECK (Estado IN ('PENDIENTE','AUTORIZADO','RECHAZADO','ANULADO')),
    -- Datos del voucher Transbank
    CodigoAutorizacion  VARCHAR(10)  NULL,
    NumeroOperacion     VARCHAR(20)  NULL,
    NumeroUnico         VARCHAR(30)  NULL,
    Terminal            VARCHAR(20)  NULL,
    UltimosDigitos      CHAR(4)      NULL,     -- nunca se guarda el número completo de la tarjeta
    Cuotas              INT NULL,
    -- Webpay
    BuyOrder            VARCHAR(26)  NULL,
    TokenWebpay         VARCHAR(64)  NULL,
    CreadoEn            DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),
    FechaPago           DATETIME2(0) NULL,
    UsuarioCajaId       INT NULL REFERENCES dbo.Usuarios(Id)
);
CREATE UNIQUE INDEX UX_Pago_Token ON dbo.Pagos(TokenWebpay) WHERE TokenWebpay IS NOT NULL;

/* ---------- Mensajería, notificaciones y auditoría ---------- */

/* Patrón Outbox: el evento se guarda en la misma transacción que el cambio de estado
   y luego un proceso en segundo plano lo publica en RabbitMQ. */
CREATE TABLE dbo.Outbox (
    Id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    Tipo         VARCHAR(50)  NOT NULL,     -- routing key, ej: cita.confirmada
    Payload      NVARCHAR(MAX) NOT NULL,
    CreadoEn     DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),
    ProcesadoEn  DATETIME2(0) NULL,
    Intentos     INT NOT NULL DEFAULT 0,
    UltimoError  NVARCHAR(500) NULL
);
CREATE INDEX IX_Outbox_Pendientes ON dbo.Outbox(Id) WHERE ProcesadoEn IS NULL;

CREATE TABLE dbo.Notificaciones (
    Id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    CitaId    INT NULL,
    Evento    VARCHAR(50)   NOT NULL,
    Canal     VARCHAR(10)   NOT NULL CHECK (Canal IN ('EMAIL','SMS','WHATSAPP')),
    Destino   NVARCHAR(150) NOT NULL,
    Asunto    NVARCHAR(200) NOT NULL,
    Cuerpo    NVARCHAR(MAX) NOT NULL,
    EnviadaEn DATETIME2(0)  NOT NULL DEFAULT SYSDATETIME()
);

/* Registro de quién vio o modificó qué dato (Ley 20.584 y Ley 19.628) */
CREATE TABLE dbo.Auditoria (
    Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    Fecha       DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),
    UsuarioId   INT NULL,
    Email       NVARCHAR(150) NULL,
    Rol         VARCHAR(12) NULL,
    Accion      VARCHAR(40) NOT NULL,      -- VER_PACIENTE, EMITIR_BONO, COBRAR, CANCELAR_CITA...
    Recurso     VARCHAR(30) NOT NULL,      -- Paciente, Cita, Bono, Pago
    RecursoId   VARCHAR(30) NULL,
    Ip          VARCHAR(45) NULL,
    Detalle     NVARCHAR(500) NULL
);
CREATE INDEX IX_Auditoria_Fecha ON dbo.Auditoria(Fecha DESC);
GO
