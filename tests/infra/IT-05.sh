#!/usr/bin/env bash
# SPEC-0005:IT-05 — Dado o ambiente local, quando acessado por `http://`, então redireciona para
# `https://`.
#
# Local (Docker). Se o ambiente não estiver no ar, implanta a versão A antes.
# Variáveis opcionais: ver lib.sh (SPEC0005_*).
set -euo pipefail
TEST_ID="IT-05"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_local_env
if ! local_health_ok; then
  prepare_versions
  deploy_ok "$TAG_A"
fi

read -r code location < <(curl_local -o /dev/null -w '%{http_code} %{redirect_url}\n' \
  "http://$LOCAL_HOST/health/live") || fail "http://$LOCAL_HOST is unreachable"
log "http://$LOCAL_HOST/health/live -> $code ${location:-<no Location>}"
case "$code" in
  301 | 302 | 307 | 308) ;;
  *) fail "http:// answered $code, expected a redirect (301/302/307/308)" ;;
esac
[[ "$location" == "https://$LOCAL_HOST/"* ]] || fail "redirect goes to '${location:-<none>}', expected https://$LOCAL_HOST/..."

local_health_ok || fail "redirect target $LOCAL_BASE_URL/health/live is not 2xx"
pass "http://$LOCAL_HOST redirects ($code) to https://$LOCAL_HOST"
