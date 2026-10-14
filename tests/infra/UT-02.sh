#!/usr/bin/env bash
# SPEC-0005:UT-02 — Dado deploy/Caddyfile, quando `caddy validate` roda, então é válido e
# declara redirecionamento HTTP→HTTPS.
#
# Local (sem nuvem). Usa caddy local se instalado; senão a imagem oficial $CADDY_IMAGE.
# Placeholders de ambiente {$VAR} sem padrão recebem um valor de teste (ut02.example.com)
# quando a variável não está definida — exporte a variável para usar outro valor.
#
# "Declara redirecionamento HTTP→HTTPS" (no JSON adaptado pelo próprio caddy):
#   - existe redirecionamento explícito para https:// (handler static_response com
#     Location https://...), ou
#   - existe servidor HTTPS (:443 ou site com nome de host) e o HTTPS automático do Caddy
#     não está desligado nem com os redirecionamentos desligados (auto_https off /
#     disable_redirects), e nenhum site é servido em http:// sem redirecionar.
set -euo pipefail
TEST_ID="UT-02"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

CADDYFILE="${CADDYFILE:-$REPO_ROOT/deploy/Caddyfile}"

log "caddyfile: ${CADDYFILE#"$REPO_ROOT"/}"
require_file "$CADDYFILE" "deploy/Caddyfile does not exist"
require_cmd jq

# Valores de teste para placeholders {$VAR} sem padrão.
env_args=()
while IFS= read -r var; do
  [[ -n "$var" ]] || continue
  if [[ -z "${!var:-}" ]]; then
    export "$var=ut02.example.com"
    log "placeholder {\$$var} unset; using ut02.example.com"
  fi
  env_args+=(-e "$var")
done < <(grep -oE '\{\$[A-Za-z_][A-Za-z0-9_]*\}' "$CADDYFILE" | sed -E 's/^\{\$//; s/\}$//' | sort -u)

run_caddy() {
  if command -v caddy >/dev/null 2>&1; then
    caddy "$@"
  else
    require_docker
    docker run --rm "${env_args[@]}" -v "$CADDYFILE:/etc/caddy/Caddyfile:ro" "$CADDY_IMAGE" caddy "$@"
  fi
}

if command -v caddy >/dev/null 2>&1; then cfg="$CADDYFILE"; else cfg=/etc/caddy/Caddyfile; fi

log "caddy validate"
run_caddy validate --config "$cfg" --adapter caddyfile || fail "caddy validate reported errors"

log "caddy adapt (to inspect HTTP→HTTPS redirect)"
json="$(run_caddy adapt --config "$cfg" --adapter caddyfile 2>/dev/null)" || fail "caddy adapt failed"

servers="$(jq -c '.apps.http.servers // {}' <<<"$json")"
[[ "$servers" != "{}" ]] || fail "Caddyfile declares no HTTP server"

explicit_redirect="$(jq -r '
  [.. | objects | select(.handler? == "static_response")
      | (.headers.Location // .headers.location // [])[]?
      | select(startswith("https://"))] | length' <<<"$servers")"

https_server="$(jq -r '
  [to_entries[] | .value
   | select(
       ((.listen // []) | any(test(":443$")))
       or ((.routes // []) | any((.match // []) | any((.host // []) | length > 0)))
     )] | length' <<<"$servers")"

auto_https_off="$(jq -r '[.[] | .automatic_https // {} | select(.disable == true or .disable_redirects == true)] | length' <<<"$servers")"

plain_http_sites="$(jq -r '
  [to_entries[] | .value
   | select((.listen // []) | any(test(":80$")))
   | select([.. | objects | select(.handler? == "static_response")
             | (.headers.Location // .headers.location // [])[]? | select(startswith("https://"))] | length == 0)
  ] | length' <<<"$servers")"

if ((explicit_redirect > 0)); then
  pass "caddy validate OK; explicit redirect to https:// declared"
elif ((https_server > 0 && auto_https_off == 0 && plain_http_sites == 0)); then
  pass "caddy validate OK; HTTPS site with Caddy automatic HTTP→HTTPS redirect enabled"
else
  fail "no HTTP→HTTPS redirect: explicit_redirect=$explicit_redirect https_servers=$https_server auto_https_disabled=$auto_https_off plain_http_servers=$plain_http_sites"
fi
