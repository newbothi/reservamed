FROM python:3.12-slim-bookworm@sha256:6a0e06f5ba0b373d051aabd3fffb9b873f27f9a2a3a14bcbe37dee6f0e3bbc70

# Driver ODBC 18 de Microsoft para conectarse a SQL Server desde Linux
RUN apt-get update && apt-get install -y --no-install-recommends curl gnupg2 ca-certificates unixodbc \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
 && curl -fsSL https://packages.microsoft.com/config/debian/12/prod.list -o /etc/apt/sources.list.d/mssql-release.list \
 && apt-get update && ACCEPT_EULA=Y apt-get install -y --no-install-recommends msodbcsql18 \
 && apt-get purge -y curl gnupg2 && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY app ./app
COPY scripts ./scripts

# M4: usuario sin privilegios; la carpeta de modelos (volumen) le pertenece.
RUN useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin ml \
 && mkdir -p /data/modelos && chown ml:ml /data/modelos
USER 10001

ENV TZ=America/Santiago PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 ML_MODELOS=/data/modelos
EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
