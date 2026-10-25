/* =====================================================================
   F5b — revisión del modelo de inasistencia (idempotente).
   - ModelosML guarda la calibración (Brier y por decil) y por qué se rechazó un modelo.
   Un modelo que no supera la calidad mínima queda registrado como inactivo.
   ===================================================================== */
USE ReservaMed;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF COL_LENGTH('dbo.ModelosML', 'Brier') IS NULL
    ALTER TABLE dbo.ModelosML ADD Brier DECIMAL(6,4) NULL, BrierBase DECIMAL(6,4) NULL,
                                  Calibracion NVARCHAR(2000) NULL, Observaciones NVARCHAR(400) NULL;
GO
PRINT 'Revisión del modelo de ML lista.';
GO
