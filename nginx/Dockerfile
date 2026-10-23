# Construye los dos frontends React y los sirve con NGINX.
# Contexto de build: raíz del repositorio.
FROM node:22-alpine AS portal
WORKDIR /app
COPY portal/package*.json ./
RUN npm ci
COPY portal/ ./
RUN npm run build

FROM node:22-alpine AS backoffice
WORKDIR /app
COPY backoffice/package*.json ./
RUN npm ci
COPY backoffice/ ./
RUN npm run build

FROM nginx:1.27-alpine
COPY nginx/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=portal /app/dist /usr/share/nginx/portal
COPY --from=backoffice /app/dist /usr/share/nginx/backoffice
EXPOSE 80
