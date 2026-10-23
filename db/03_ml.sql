/* =====================================================================
   ReservaMed — Módulo de machine learning
   - ModelosML: cada entrenamiento queda registrado con sus métricas.
   - PrediccionesCita: riesgo de inasistencia por cita.
     Va en una tabla aparte (y no como columna de Citas) para que el
     ml-service no modifique el ROWVERSION de la cita y no provoque
     conflictos de concurrencia en admisión.
   ===================================================================== */
USE ReservaMed;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.ModelosML') IS NULL
CREATE TABLE dbo.ModelosML (
    Id             INT IDENTITY(1,1) PRIMARY KEY,
    Tipo           VARCHAR(30)   NOT NULL,              -- INASISTENCIA
    Version        VARCHAR(50)   NOT NULL UNIQUE,
    Algoritmo      VARCHAR(80)   NOT NULL,
    EntrenadoEn    DATETIME2(0)  NOT NULL,
    Muestras       INT NOT NULL,
    TasaBase       DECIMAL(6,4)  NOT NULL,              -- % de inasistencia en los datos
    Auc            DECIMAL(6,4)  NOT NULL,
    PrecisionAlto  DECIMAL(6,4)  NOT NULL,              -- precisión del nivel ALTO
    RecallAlto     DECIMAL(6,4)  NOT NULL,              -- cobertura del nivel ALTO
    UmbralMedio    DECIMAL(6,4)  NOT NULL,
    UmbralAlto     DECIMAL(6,4)  NOT NULL,
    Coeficientes   NVARCHAR(MAX) NOT NULL,              -- JSON con variable, coeficiente y odds ratio
    DatosSinteticos BIT NOT NULL,
    Activo         BIT NOT NULL DEFAULT 0
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ModelosML_Activo')
    CREATE UNIQUE INDEX UX_ModelosML_Activo ON dbo.ModelosML(Tipo) WHERE Activo = 1;
GO

IF OBJECT_ID('dbo.PrediccionesCita') IS NULL
CREATE TABLE dbo.PrediccionesCita (
    CitaId         INT PRIMARY KEY REFERENCES dbo.Citas(Id),
    Probabilidad   DECIMAL(6,4) NOT NULL,
    Nivel          VARCHAR(5)   NOT NULL CHECK (Nivel IN ('BAJO','MEDIO','ALTO')),
    Factores       NVARCHAR(1000) NULL,                 -- JSON: variables que más subieron el riesgo
    ModeloVersion  VARCHAR(50)  NOT NULL,
    CalculadoEn    DATETIME2(0) NOT NULL
);
GO

PRINT 'Tablas de ML listas.';
GO
