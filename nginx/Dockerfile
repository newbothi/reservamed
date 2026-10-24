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

FROM nginxinc/nginx-unprivileged:1.28-alpine@sha256:7377697a821c131a924a7105fafbe7414db4e9fcc77a6f08f776f33f141ec3f8
COPY nginx/nginx.conf /etc/nginx/conf.d/default.conf
COPY nginx/seguridad.conf /etc/nginx/snippets/seguridad.conf
COPY --chmod=755 nginx/05-verificar-certificados.sh /docker-entrypoint.d/05-verificar-certificados.sh
COPY --from=portal /app/dist /usr/share/nginx/portal
COPY --from=backoffice /app/dist /usr/share/nginx/backoffice
# Usuario nginx (uid 101) de la imagen base; puertos > 1024 porque no hay privilegios.
USER 101
EXPOSE 8080 8443
