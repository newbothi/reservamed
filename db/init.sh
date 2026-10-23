#!/bin/bash
# Espera a que SQL Server acepte conexiones y ejecuta los scripts una sola vez.
set -e
SQLCMD=/opt/mssql-tools18/bin/sqlcmd
for i in $(seq 1 60); do
  if $SQLCMD -S sqlserver -U sa -P "$SA_PASSWORD" -C -Q "SELECT 1" >/dev/null 2>&1; then break; fi
  echo "Esperando SQL Server ($i)..."; sleep 2
done

EXISTE=$($SQLCMD -S sqlserver -U sa -P "$SA_PASSWORD" -C -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(1) FROM sys.databases WHERE name='ReservaMed'")
if [ "$EXISTE" = "1" ]; then
  echo "La base ReservaMed ya existe; no se ejecutan scripts."
  exit 0
fi

$SQLCMD -S sqlserver -U sa -P "$SA_PASSWORD" -C -I -b -i /scripts/01_schema.sql
$SQLCMD -S sqlserver -U sa -P "$SA_PASSWORD" -C -I -b -i /scripts/02_seed.sql
$SQLCMD -S sqlserver -U sa -P "$SA_PASSWORD" -C -I -b -i /scripts/03_ml.sql
echo "Base ReservaMed creada con datos demo."
