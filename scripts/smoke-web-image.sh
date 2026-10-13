#!/usr/bin/env bash
# Starts the web image and checks the things only a running container can show.
#
#   scripts/smoke-web-image.sh concordat/web:local
#
# Used by CI against the image it just built and by `Publish images` against the one it just
# pushed, for the reason both of those exist: the unit tests and the browser suite run against the
# dev server, and none of them can tell you that the image starts, that its configuration is read,
# or that its Content-Security-Policy is the one the page needs. Each check below is a way the image
# was or could have been wrong while everything else was green.
#
# No registry is started. The image is meant to come up without one — nginx resolves the upstream
# when a request arrives, not at start-up — and say so with a 502 on /v1 rather than not start.

set -euo pipefail

IMAGE="${1:?usage: smoke-web-image.sh <image>}"
NAME="concordat-web-smoke-$$"

cleanup() {
  docker rm -f "$NAME" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() {
  echo "::error::$1" >&2
  docker logs "$NAME" 2>&1 | tail -20 >&2 || true
  exit 1
}

# --- An environment that is wrong stops the container ---------------------------------------------
# Not "starts with a guess": a bad value would reach config.json and the CSP header, and decide where
# a bearer token is sent.
if docker run --rm -e CONCORDAT_PROFILE=bogus "$IMAGE" >/dev/null 2>&1; then
  fail "the image started with CONCORDAT_PROFILE=bogus"
fi

if docker run --rm -e "CONCORDAT_API_BASE_URL=https://x.example.com;script-src *" "$IMAGE" >/dev/null 2>&1; then
  fail "the image started with a value that would have ended its CSP directive"
fi

# --- It starts with nothing else running -----------------------------------------------------------
docker run -d --name "$NAME" -p 127.0.0.1::8080 \
  -e CONCORDAT_API_UPSTREAM=http://registry.invalid:8080 "$IMAGE" >/dev/null

PORT="$(docker port "$NAME" 8080/tcp | head -1 | sed -E 's/.*:([0-9]+)$/\1/')"
BASE="http://127.0.0.1:${PORT}"

for _ in $(seq 1 30); do
  if curl -fsS "$BASE/healthz" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

curl -fsS "$BASE/healthz" >/dev/null || fail "the image never answered /healthz"

# --- Its configuration is read from the environment, at start-up ----------------------------------
CONFIG="$(curl -fsS "$BASE/config.json")"
echo "config.json: $CONFIG"

echo "$CONFIG" | grep -q '"apiBaseUrl":""' || fail "config.json does not say the API is same-origin"
echo "$CONFIG" | grep -q '"profile":"self-hosted"' || fail "config.json lost its profile"

# --- The page is served, with its policy in a header and not in the markup -------------------------
HEADERS="$(curl -fsS -D - -o /tmp/concordat-smoke-index.html "$BASE/subjects/some.subject")"

echo "$HEADERS" | grep -qi "^content-security-policy: .*script-src 'self';" \
  || fail "the page has no Content-Security-Policy header with script-src 'self'"
echo "$HEADERS" | grep -qi "^content-security-policy: .*frame-ancestors 'none'" \
  || fail "the page's policy does not forbid framing"
echo "$HEADERS" | grep -i "^content-security-policy:" | grep -qi "unsafe-eval" \
  && fail "the PAGE's policy allows unsafe-eval; only the worker's may"

grep -q '<meta http-equiv="Content-Security-Policy"' /tmp/concordat-smoke-index.html \
  && fail "index.html still carries the meta policy, which would intersect with the header"

grep -q '<cd-root' /tmp/concordat-smoke-index.html || fail "a client-side route did not return the app"

# No inline event handlers or scripts: `script-src 'self'` forbids both, and the production build
# will happily emit one. Angular inlines "critical" CSS with `<link media="print" onload="...">`,
# which the policy blocks — leaving the stylesheet stuck on `print` and the whole app unstyled —
# and nothing before this image existed ever served the production build under that policy.
if grep -Eiq '<[a-z][^>]*[[:space:]]on[a-z]+=' /tmp/concordat-smoke-index.html; then
  fail "index.html has an inline event handler, which the page's policy blocks"
fi

if grep -Eiq '<script(>|[[:space:]][^>]*>)[^<]+</script>' /tmp/concordat-smoke-index.html; then
  fail "index.html has an inline script, which the page's policy blocks"
fi

# --- The worker, which compiles schemas, is the one thing allowed to evaluate strings -------------
WORKER="$(docker exec "$NAME" sh -c 'ls /usr/share/nginx/html | grep "^worker-" | head -1')"
[ -n "$WORKER" ] || fail "the build produced no worker"

WORKER_HEADERS="$(curl -fsS -D - -o /dev/null "$BASE/$WORKER")"

echo "$WORKER_HEADERS" | grep -i "^content-security-policy:" | grep -qi "script-src 'self' 'unsafe-eval'" \
  || fail "the worker is not served with the policy that lets it compile schemas"
echo "$WORKER_HEADERS" | grep -i "^content-security-policy:" | grep -qi "connect-src 'none'" \
  || fail "the worker is allowed a network"

# --- A file that is gone is gone, not the app -------------------------------------------------------
STATUS="$(curl -s -o /dev/null -w '%{http_code}' "$BASE/chunk-NOSUCHFILE.js")"
[ "$STATUS" = "404" ] || fail "a missing chunk answered $STATUS instead of 404"

# --- The registry being away is a 502, not a container that will not start -------------------------
STATUS="$(curl -s -o /dev/null -w '%{http_code}' "$BASE/v1/auth/status")"
[ "$STATUS" = "502" ] || fail "/v1 with no registry answered $STATUS instead of 502"

# An API path that ends like a file is still the API. Subjects are dotted, so `orders.json` is a
# subject; nginx once answered it 404 as a static file that did not exist.
for NAME_ in orders.json orders.js orders.css orders.txt; do
  STATUS="$(curl -s -o /dev/null -w '%{http_code}' "$BASE/v1/environments/dev/subjects/$NAME_")"
  [ "$STATUS" = "502" ] || fail "/v1/.../subjects/$NAME_ answered $STATUS instead of being forwarded (502 with no registry)"
done

# --- Editor assets are served as what they are ------------------------------------------------------
FONT="$(docker exec "$NAME" sh -c 'ls /usr/share/nginx/html/monaco | grep "\.ttf$" | head -1')"
curl -fsS -I "$BASE/monaco/$FONT" | grep -qi '^content-type: font/ttf' \
  || fail "the editor's icon font is not served as font/ttf"

echo "the web image starts, is configured from its environment, and serves with the right policies"
