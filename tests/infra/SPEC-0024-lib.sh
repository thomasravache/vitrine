# shellcheck shell=bash
# SPEC-0024 — utilitários comuns de CH-01/IT-06 (simulador do Mercado Livre no ambiente local prod-like).
# Carregado depois de lib.sh (que fixa SPEC_ID=SPEC-0005: o script redefine SPEC_ID depois do source).
#
# Contrato (SPEC-0024, seção 6):
#   - serviço `ml-simulator` no deploy/compose.override.yaml, só no perfil `ml-simulator`, HTTP interno 8080, GET /health;
#   - Caddy serve https://ml-simulador.vitrine.localhost -> ml-simulator:8080 por um diretório de sites extras
#     (CADDY_EXTRA_SITES_DIR), vazio por padrão e apontado para deploy/local/caddy-sites só pelo deploy/local/.env.example;
#   - deploy/Caddyfile não cita o simulador.
#
# "Configuração padrão" = a de staging/produção: sem COMPOSE_PROFILES de simulador e sem CADDY_EXTRA_SITES_DIR.

SIM_SERVICE="ml-simulator"
SIM_PROFILE="ml-simulator"
SIM_HOST="${SPEC0024_SIM_HOST:-ml-simulador.vitrine.localhost}"
SIM_CSPROJ="$REPO_ROOT/tools/MercadoLivreSimulator/Vitrine.MercadoLivreSimulator.csproj"
COMPOSE_BASE_FILE="$REPO_ROOT/deploy/compose.yaml"
COMPOSE_OVERRIDE_FILE="$REPO_ROOT/deploy/compose.override.yaml"
CADDYFILE_PATH="$REPO_ROOT/deploy/Caddyfile"

# Arquivo de ambiente para o Compose a partir do deploy/local/.env.example (+ IMAGE_TAG exigido na interpolação).
#   default : sem COMPOSE_PROFILES e sem CADDY_EXTRA_SITES_DIR (como staging/produção)
#   local   : o .env.example como está (ambiente local prod-like)
spec0024_env_file() {
  local mode="$1" f
  f="$(mktemp)"
  case "$mode" in
    default) grep -vE '^[[:space:]]*(COMPOSE_PROFILES|CADDY_EXTRA_SITES_DIR)[[:space:]]*=' "$ENV_EXAMPLE" >"$f" || true ;;
    local) cp "$ENV_EXAMPLE" "$f" ;;
    *) fail "spec0024_env_file: unknown mode '$mode'" ;;
  esac
  printf '\nIMAGE_TAG=%s\n' "${SPEC0024_CONFIG_TAG:-spec0024-config}" >>"$f"
  printf '%s\n' "$f"
}

# `docker compose config --format json` dos arquivos do deploy.sh (base + override) com o arquivo de ambiente $1.
# COMPOSE_PROFILES e CADDY_EXTRA_SITES_DIR do processo não vazam para o resultado (vale só o arquivo).
compose_config_json() {
  local env_file="$1"
  env -u COMPOSE_PROFILES -u CADDY_EXTRA_SITES_DIR \
    docker compose --project-directory "$REPO_ROOT/deploy" --env-file "$env_file" \
    -f "$COMPOSE_BASE_FILE" -f "$COMPOSE_OVERRIDE_FILE" config --format json
}

# Serviços ativos (perfis do arquivo de ambiente), ordenados, separados por espaço.
active_services() {
  jq -r '.services | keys | sort | join(" ")' <<<"$1"
}

# Hosts e upstreams do Caddy montado como no Compose: roda `caddy adapt` na imagem do serviço caddy com os mesmos
# binds (somente leitura) e devolve o JSON adaptado. $1 = JSON do compose config.
caddy_adapt_as_composed() {
  local config="$1" image site mounts=()
  image="$(jq -r '.services.caddy.image' <<<"$config")"
  site="$(jq -r '.services.caddy.environment.SITE_ADDRESS // empty' <<<"$config")"
  [[ -n "$image" && "$image" != null ]] || fail "compose config has no caddy service image"
  while IFS=$'\t' read -r src dst; do
    [[ -n "$src" ]] || continue
    [[ -e "$src" ]] || fail "caddy bind source does not exist: ${src#"$REPO_ROOT"/} (-> $dst)"
    mounts+=(--mount "type=bind,src=$src,dst=$dst,readonly")
  done < <(jq -r '.services.caddy.volumes[]? | select(.type == "bind") | [.source, .target] | @tsv' <<<"$config")
  docker run --rm "${mounts[@]}" -e "SITE_ADDRESS=${site:-vitrine.localhost}" "$image" \
    caddy adapt --config /etc/caddy/Caddyfile --adapter caddyfile 2>/dev/null
}

caddy_hosts() {
  jq -r '[.apps.http.servers // {} | .[] | .routes[]? | .match[]? | .host[]?] | unique | join(" ")' <<<"$1"
}

caddy_upstreams() {
  jq -r '[.. | objects | select(.handler? == "reverse_proxy") | .upstreams[]?.dial] | unique | join(" ")' <<<"$1"
}

# curl contra o host do simulador no Caddy local (CA interna, *.localhost fixado em 127.0.0.1).
curl_sim() {
  curl -sS -k --max-time 10 --resolve "$SIM_HOST:443:127.0.0.1" --resolve "$SIM_HOST:80:127.0.0.1" "$@"
}
