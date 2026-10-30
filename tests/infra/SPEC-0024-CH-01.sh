#!/usr/bin/env bash
# SPEC-0024:CH-01 — Dado o Caddyfile e o Compose atuais, quando os testes de infra da SPEC-0005 rodam após a
# inclusão do diretório de sites extras vazio, então continuam passando (guarda: passa antes da mudança).
#
# Local (Docker; não sobe nem derruba a stack `vitrine`). Na configuração padrão (a de staging/produção: sem
# COMPOSE_PROFILES do simulador e sem CADDY_EXTRA_SITES_DIR):
#   1. os serviços ativos do Compose (deploy/compose.yaml + deploy/compose.override.yaml) são exatamente api e caddy;
#   2. o Caddy, montado com os mesmos binds do Compose (inclusive o diretório de sites extras padrão, se houver),
#      serve só $SITE_ADDRESS e só faz proxy para api:8080;
#   3. o SPEC-0005:UT-02 (`caddy validate` + redirecionamento HTTP->HTTPS) continua passando.
set -euo pipefail
TEST_ID="CH-01"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
SPEC_ID="SPEC-0024"
# shellcheck source=SPEC-0024-lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/SPEC-0024-lib.sh"

require_docker
require_cmd jq
require_file "$ENV_EXAMPLE" "deploy/local/.env.example does not exist"
require_file "$CADDYFILE_PATH" "deploy/Caddyfile does not exist"

env_file="$(spec0024_env_file default)"
trap 'rm -f "$env_file"' EXIT

# 1. Serviços da configuração padrão.
config="$(compose_config_json "$env_file")" || fail "docker compose config failed for the default configuration"
services="$(active_services "$config")"
log "default configuration services: $services"
[[ "$services" == "api caddy" ]] || fail "default configuration must run only 'api caddy', got '$services'"

# 2. Caddy como o Compose o monta.
adapted="$(caddy_adapt_as_composed "$config")" || fail "caddy adapt failed with the Compose binds (default configuration)"
hosts="$(caddy_hosts "$adapted")"
upstreams="$(caddy_upstreams "$adapted")"
log "caddy hosts: ${hosts:-<none>}; upstreams: ${upstreams:-<none>}"
[[ "$hosts" == "vitrine.localhost" ]] || fail "Caddy must serve only vitrine.localhost by default, got '${hosts:-<none>}'"
[[ "$upstreams" == "api:8080" ]] || fail "Caddy must proxy only to api:8080 by default, got '${upstreams:-<none>}'"

# 3. SPEC-0005:UT-02 continua passando.
bash "$INFRA_TESTS_DIR/UT-02.sh" >&2 || fail "SPEC-0005:UT-02 (caddy validate + HTTP->HTTPS) no longer passes"

pass "default Compose runs api+caddy only; Caddy serves only vitrine.localhost -> api:8080; SPEC-0005:UT-02 passes"
