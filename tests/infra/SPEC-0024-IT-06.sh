#!/usr/bin/env bash
# SPEC-0024:IT-06 — Dado o ambiente local prod-like com o perfil `ml-simulator`, quando o deploy local sobe, então
# https://ml-simulador.vitrine.localhost/health responde 200 pelo Caddy. Sem o perfil, o host não responde.
#
# Local (Docker; portas 80/443; padrão SPEC-0005/0012). ATENÇÃO: implanta no projeto Compose `vitrine` (o mesmo do
# deploy/deploy.sh): uma stack local já no ar é trocada pela versão de teste (o runner só derruba o que ele subiu).
#   1. o deploy/local/.env.example liga o perfil ml-simulator e o Compose local declara o serviço (pré-condição);
#   2. constrói a imagem do simulador com o nome que o Compose espera (dotnet publish -t:PublishContainer);
#   3. deploy.sh <tag A> com o .env.example -> GET https://ml-simulador.vitrine.localhost/health = 200 via Caddy;
#   4. deploy.sh <tag B> com o mesmo arquivo sem o perfil e sem CADDY_EXTRA_SITES_DIR -> o host não responde 2xx
#      (a Api continua respondendo em https://vitrine.localhost/health/live).
# Variáveis opcionais: ver lib.sh (SPEC0005_*) e SPEC0024_SIM_HOST (padrão: ml-simulador.vitrine.localhost).
set -euo pipefail
TEST_ID="IT-06"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
SPEC_ID="SPEC-0024"
# shellcheck source=SPEC-0024-lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/SPEC-0024-lib.sh"

require_local_env
require_cmd jq dotnet

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# 1. Pré-condição: o ambiente local liga o simulador.
grep -qE '^[[:space:]]*COMPOSE_PROFILES[[:space:]]*=.*\bml-simulator\b' "$SPEC0005_ENV_FILE" \
  || fail "deploy/local/.env.example does not enable the '$SIM_PROFILE' profile in COMPOSE_PROFILES"
local_env="$(spec0024_env_file local)"
mv "$local_env" "$work/local.env"
config="$(compose_config_json "$work/local.env")" || fail "docker compose config failed for the local environment"
sim_image="$(jq -r --arg s "$SIM_SERVICE" '.services[$s].image // empty' <<<"$config")"
[[ -n "$sim_image" ]] || fail "local Compose has no '$SIM_SERVICE' service with an image (profile '$SIM_PROFILE')"
log "simulator image expected by Compose: $sim_image"

# 2. Imagem do simulador (repositório:tag do Compose).
sim_repo="${sim_image%:*}"
sim_tag="${sim_image##*:}"
[[ "$sim_repo" != "$sim_image" ]] || sim_tag="latest"
require_file "$SIM_CSPROJ" "tools/MercadoLivreSimulator/Vitrine.MercadoLivreSimulator.csproj does not exist"
log "building $sim_repo:$sim_tag (dotnet publish -t:PublishContainer)"
dotnet publish "$SIM_CSPROJ" -t:PublishContainer -p:ContainerRepository="$sim_repo" -p:ContainerImageTag="$sim_tag" \
  -v q -nologo >&2 || fail "could not build the simulator image $sim_repo:$sim_tag"

# 3. Com o perfil: o host do simulador responde pelo Caddy.
prepare_versions
deploy_ok "$TAG_A"
code="$(curl_sim -o /dev/null -w '%{http_code}' "https://$SIM_HOST/health" || true)"
log "with profile: GET https://$SIM_HOST/health -> ${code:-<no response>}"
[[ "$code" == "200" ]] || fail "https://$SIM_HOST/health answered '${code:-<no response>}' with the '$SIM_PROFILE' profile, expected 200"
local_health_ok || fail "$LOCAL_BASE_URL/health/live is not 2xx with the simulator enabled"

# 4. Sem o perfil (e sem o diretório de sites extras): o host não responde.
grep -vE '^[[:space:]]*(COMPOSE_PROFILES|CADDY_EXTRA_SITES_DIR)[[:space:]]*=' "$SPEC0005_ENV_FILE" >"$work/no-sim.env" || true
printf '\nCOMPOSE_PROFILES=db-container\n' >>"$work/no-sim.env"
SPEC0005_ENV_FILE="$work/no-sim.env"
deploy_ok "$TAG_B"
code="$(curl_sim -o /dev/null -w '%{http_code}' "https://$SIM_HOST/health" 2>/dev/null || true)"
log "without profile: GET https://$SIM_HOST/health -> ${code:-<no response>}"
[[ ! "$code" =~ ^2 ]] || fail "https://$SIM_HOST/health answered $code without the '$SIM_PROFILE' profile"
local_health_ok || fail "$LOCAL_BASE_URL/health/live is not 2xx after disabling the simulator"

pass "https://$SIM_HOST/health is 200 via Caddy with the '$SIM_PROFILE' profile and does not answer without it"
