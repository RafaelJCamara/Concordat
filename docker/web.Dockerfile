# The Concordat web app as a container (M4).
#
# Build from the repository root:
#   docker build -f docker/web.Dockerfile -t concordat/web .
#
# ONE IMAGE, EVERY DEPLOYMENT. Nothing about where the registry lives is compiled in: the image
# serves the same bundle everywhere and reads its configuration when it starts, from environment
# variables (see docker/web/18-concordat-config.envsh). That is what `ConcordatConfig` in
# web/src/app/core/config/app-config.ts promises, and a build per deployment is how somebody ends
# up pointed at somebody else's registry.
#
#   CONCORDAT_API_UPSTREAM          where nginx forwards /v1 and /health to.   [http://registry:8080]
#   CONCORDAT_API_BASE_URL          where the BROWSER sends API requests.      [empty: same origin]
#   CONCORDAT_PROFILE               self-hosted | cloud.                       [self-hosted]
#   CONCORDAT_TENANT                the tenant to disambiguate to.             [none]
#   CONCORDAT_DEFAULT_ENVIRONMENT   the environment selected on a cold start.  [dev]
#
# The default is the arrangement that needs nothing else configured and cannot be got wrong:
# the browser talks only to this container, and this container forwards /v1 to the registry. One
# origin means no CORS, no cross-origin credential, and a Content-Security-Policy that never has
# to name anybody else.

# ---- build ----------------------------------------------------------------
# Pinned by digest, not just tag, for the reason docker/api.Dockerfile gives. The Node version is
# the one CI builds with and the floor the Angular CLI enforces (web/package.json "engines").
FROM node:22.22.3-alpine@sha256:e58326d0d441090181ac150dc2078d3e2cf6a0d42e809aebba3ef5880935ffdd AS build
# node:22.22.3-alpine

WORKDIR /src/web

# The lockfile first, so the dependency layer is rebuilt when dependencies change and not
# whenever a component does. `npm ci` and not `install`: the build must use exactly what the
# lockfile names, or it is not the build that was tested.
COPY web/package.json web/package-lock.json ./
RUN npm ci

COPY web/ ./
RUN npx ng build

# The Content-Security-Policy moves from a <meta> tag in index.html to a response header.
#
# index.html carries one so that a purely static host still gets a backstop, and says in a comment
# that "if this page ever gets a server in front of it, move this to a response header". It has one
# now. Two policies are intersected by the browser, so leaving both would mean the meta tag's
# `connect-src 'self'` quietly vetoes a configured cross-origin API that the header allowed; and a
# header can say what a meta tag cannot (`frame-ancestors`). The header, computed from the same
# configuration as config.json, is therefore the only policy in this image.
#
# Removed and then checked, because a pattern that stops matching after somebody reformats the tag
# would otherwise leave both in place and nothing would say so. The check looks for the tag and not
# for the words: index.html also explains itself in a comment that uses them.
RUN sed -i -E 's#<meta http-equiv="Content-Security-Policy"[^>]*>##' dist/concordat-web/browser/index.html \
 && ! grep -q '<meta http-equiv="Content-Security-Policy"' dist/concordat-web/browser/index.html

# ---- runtime --------------------------------------------------------------
# The unprivileged nginx image: it listens on 8080 and runs as an ordinary user, so the container
# needs no capability to bind a port and a compromise of nginx is not a compromise of root.
FROM nginxinc/nginx-unprivileged@sha256:0c79d56aee561a1d81c63f00eee5fb5fe29279560cdc55e91425133104c7fbe6 AS runtime
# nginxinc/nginx-unprivileged:1.29-alpine

# Rendered by the base image's envsubst from the environment at start-up, into conf.d.
COPY docker/web/nginx.conf.template /etc/nginx/templates/default.conf.template

# Sourced by the base image's entrypoint, between its own resolver script (15) and its template
# rendering (20): it validates the environment, writes config.json, and exports what the
# template needs. `--chmod` because a checkout on Windows does not carry the executable bit, and
# the entrypoint ignores a script that lacks it.
COPY --chmod=0755 docker/web/18-concordat-config.envsh /docker-entrypoint.d/18-concordat-config.envsh

COPY --from=build /src/web/dist/concordat-web/browser /usr/share/nginx/html

ENV CONCORDAT_API_UPSTREAM=http://registry:8080

EXPOSE 8080

# wget, because the alpine image has it and has no curl. /healthz is answered by nginx itself and
# says "this container is serving", which is all that can be said without the registry: whether
# the registry is up is the registry's health check, and conflating the two would make the UI
# report itself broken whenever the API restarts.
HEALTHCHECK --interval=10s --timeout=3s --retries=6 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1
