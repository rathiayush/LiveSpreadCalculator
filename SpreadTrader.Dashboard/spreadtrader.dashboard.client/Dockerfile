# Build stage - compiles the Vite/React app to static files.
FROM node:20-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build

# Serve stage - nginx serves the static build and reverse-proxies /api and /hubs to the
# LiveSpreadCalculator Api process so the browser only ever talks to one origin.
FROM nginx:alpine AS final
COPY --from=build /app/dist /usr/share/nginx/html
# Deliberately NOT under /etc/nginx/templates/ - that path is auto-envsubst'd by nginx:alpine's
# own entrypoint scripts using an unrestricted variable list, which would run redundantly (and
# less safely) before our own explicit, restricted substitution in CMD below.
COPY nginx.conf.template /etc/nginx/nginx.conf.template
EXPOSE 80

# "api" resolves via Docker's per-service DNS under local docker-compose (see docker-compose.yml).
# Override to "localhost" when deployed as an Azure App Service sidecar - see nginx.conf.template
# and DEPLOYMENT.md.
ENV API_UPSTREAM_HOST=api

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -qO- http://localhost/ || exit 1

# Renders the template with the real upstream host before nginx starts - nginx:alpine ships
# envsubst (from gettext) for exactly this. Restricting envsubst to this one variable name (single
# quotes so the shell doesn't expand it first) means nginx's own $host/$scheme/$uri/etc. variables
# in the config are left completely untouched.
CMD ["/bin/sh", "-c", "envsubst '${API_UPSTREAM_HOST}' < /etc/nginx/nginx.conf.template > /etc/nginx/conf.d/default.conf && exec nginx -g 'daemon off;'"]
