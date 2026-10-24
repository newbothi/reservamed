#!/bin/bash
# Espera a SQL Server, crea la base una sola vez y (en cada arranque) configura los
# usuarios por servicio (A5). Es el único proceso que usa sa.
set -euo pipefail
SQLCMD=/opt/mssql-tools18/bin/sqlcmd
sa() { $SQLCMD -S sqlserver -U sa -P "$SA_PASSWORD" -C "$@"; }

for i in $(seq 1 60); do
  if sa -Q "SELECT 1" >/dev/null 2>&1; then break; fi
  echo "Esperando SQL Server ($i)..."; sleep 2
done

EXISTE=$(sa -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(1) FROM sys.databases WHERE name='ReservaMed'")
if [ "$EXISTE" = "1" ]; then
  echo "La base ReservaMed ya existe; no se ejecutan los scripts de esquema."
else
  sa -I -b -i /scripts/01_schema.sql
  sa -I -b -i /scripts/02_seed.sql
  sa -I -b -i /scripts/03_ml.sql
  echo "Base ReservaMed creada con datos demo."
fi

for v in SQL_API_PASS SQL_WORKER_PASS SQL_ML_PASS SQL_ML_SEED_PASS SQL_TDE_PASS; do
  if [ -z "${!v:-}" ]; then echo "Falta $v (ejecute scripts/configurar-secretos.ps1)." >&2; exit 1; fi
done
# F2: identidad y sesiones (idempotente, aplica también sobre bases existentes)
sa -I -b -i /scripts/05_identidad.sql
# F3: cuentas de QA por rol (verificaciones automáticas; idempotente)
sa -I -b -i /scripts/06_qa_roles.sql
sa -I -b -i /scripts/04_usuarios.sql \
  -v API_PASS="$SQL_API_PASS" WORKER_PASS="$SQL_WORKER_PASS" ML_PASS="$SQL_ML_PASS" ML_SEED_PASS="$SQL_ML_SEED_PASS"
# F4: auditoría encadenada, enmascaramiento, limpieza de datos personales y TDE (idempotente;
# va después de 04 porque da permisos a los usuarios por servicio).
sa -I -b -i /scripts/07_privacidad.sql -v TDE_PASS="$SQL_TDE_PASS"
