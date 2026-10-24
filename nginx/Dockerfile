# Construye los dos frontends React y los sirve con NGINX sin root (hallazgo M4).
# Contexto de build: raíz del repositorio. Imágenes fijadas por digest; Dependabot las actualiza.
FROM node:22-alpine@sha256:fe4f6d74e20370acb04ad889077d14d0f64a9c8ea6a42bc60cddf1e844699914 AS portal
WORKDIR /app
COPY portal/package*.json ./
RUN npm ci
COPY portal/ ./
RUN npm run build

FROM node:22-alpine@sha256:fe4f6d74e20370acb04ad889077d14d0f64a9c8ea6a42bc60cddf1e844699914 AS backoffice
WORKDIR /app
COPY backoffice/package*.json ./
RUN npm ci
COPY backoffice/ ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:stable-alpine@sha256:f5116ed4606f3eb015dd4a8e28cb9b351476dc0ad4bc6e9f93b19cec130cdf03
# Parches de seguridad de Alpine publicados después del digest fijado (nginx, OpenSSL).
# Gate de F1: Trivy sin vulnerabilidades críticas con corrección disponible.
USER root
RUN apk upgrade --no-cache
COPY nginx/nginx.conf /etc/nginx/conf.d/default.conf
COPY nginx/seguridad.conf /etc/nginx/snippets/seguridad.conf
COPY --chmod=755 nginx/05-verificar-certificados.sh /docker-entrypoint.d/05-verificar-certificados.sh
COPY --from=portal /app/dist /usr/share/nginx/portal
COPY --from=backoffice /app/dist /usr/share/nginx/backoffice
# Usuario nginx (uid 101) de la imagen base; puertos > 1024 porque no hay privilegios.
USER 101
EXPOSE 8080 8443
