# shellcheck shell=bash
# SPEC-0005 — biblioteca comum dos testes de infraestrutura (tests/infra/).
# Cada script define TEST_ID antes de carregar este arquivo. Toda linha de saída
# começa com "SPEC-0005:<ID>" para rastreabilidade (references/testing.md).
#
# Ferramentas: usa o binário local quando instalado; senão a imagem Docker oficial
# com tag fixa (sobrescrevível por variável de ambiente).

SPEC_ID="SPEC-0005"
: "${TEST_ID:?defina TEST_ID antes de carregar lib.sh}"

INFRA_TESTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$INFRA_TESTS_DIR/../.." && pwd)"

# Imagens oficiais (tags verificadas em 2026-10-13).
TOFU_IMAGE="${TOFU_IMAGE:-ghcr.io/opentofu/opentofu:1.10.7}"
TFLINT_IMAGE="${TFLINT_IMAGE:-ghcr.io/terraform-linters/tflint:v0.64.0}"
CADDY_IMAGE="${CADDY_IMAGE:-caddy:2.11.7-alpine}"
ACTIONLINT_IMAGE="${ACTIONLINT_IMAGE:-rhysd/actionlint:1.7.12}"
UBUNTU_IMAGE="${UBUNTU_IMAGE:-ubuntu:24.04}"

# Contrato consumido SPEC-0004@1 (imagem publicada no GHCR).
API_IMAGE_REPO="${API_IMAGE_REPO:-ghcr.io/thomasravache/vitrine-api}"
CI_WORKFLOW="${CI_WORKFLOW:-CI}"
COMPOSE_BUNDLE_ARTIFACT="${COMPOSE_BUNDLE_ARTIFACT:-compose-bundle}"

log()  { printf '%s:%s %s\n' "$SPEC_ID" "$TEST_ID" "$*"; }
fail() { printf '%s:%s FAIL: %s\n' "$SPEC_ID" "$TEST_ID" "$*" >&2; exit 1; }
pass() { printf '%s:%s PASS: %s\n' "$SPEC_ID" "$TEST_ID" "$*"; }

# Registra uma linha de evidência (stdout e, se definido, em $SPEC0005_EVIDENCE_FILE).
record() {
  local line
  line="$(date -u +%Y-%m-%dT%H:%M:%SZ) $SPEC_ID:$TEST_ID $*"
  printf '%s\n' "$line"
  if [[ -n "${SPEC0005_EVIDENCE_FILE:-}" ]]; then
    printf '%s\n' "$line" >>"$SPEC0005_EVIDENCE_FILE"
  fi
}

# Falha cedo, com mensagem clara, quando o alvo do teste não foi informado.
require_env() {
  local v missing=()
  for v in "$@"; do
    [[ -n "${!v:-}" ]] || missing+=("$v")
  done
  if ((${#missing[@]} > 0)); then
    fail "target not provisioned / env var(s) missing: ${missing[*]}"
  fi
}

require_cmd() {
  local c
  for c in "$@"; do
    command -v "$c" >/dev/null 2>&1 || fail "required command not found: $c"
  done
}

require_file() {
  [[ -f "$1" ]] || fail "${2:-file not found}: ${1#"$REPO_ROOT"/}"
}

# --- Ferramentas -------------------------------------------------------------

require_docker() {
  require_cmd docker
  docker info >/dev/null 2>&1 || fail "docker daemon is not reachable"
}

require_gh() {
  require_cmd gh jq
  gh auth status >/dev/null 2>&1 || fail "gh is not authenticated (run 'gh auth login' or set GH_TOKEN)"
}

# --- Ambiente local prod-like (contrato SPEC-0005@2, seção 6) -----------------
# Imagem <repo>:<tag>: sem argumentos sobe a aplicação; `--migrate` migra e sai.
# deploy/compose.override.yaml: serviço one-shot `migrate` (`${IMAGE_REPO:-vitrine-api}:${IMAGE_TAG}`).
# deploy/deploy.sh <tag> [--env-file <arquivo>] ; deploy/rollback.sh ;
# deploy/local/.env.example ; https://vitrine.localhost (Caddy, CA interna) ;
# deploy/.state/current e deploy/.state/previous.
#
# Variáveis (todas opcionais):
#   SPEC0005_LOCAL_HOST   host do ambiente local (padrão: vitrine.localhost)
#   SPEC0005_ENV_FILE     arquivo passado em --env-file (padrão: cópia de deploy/local/.env.example)
#   SPEC0005_IMAGE_REPO   repositório das imagens locais (padrão: vitrine-api, o padrão do Compose;
#                         se definido, é gravado como IMAGE_REPO na cópia do --env-file)
#   SPEC0005_BASE_IMAGE   imagem real da API usada como base; sem ela: ghcr.io/thomasravache/vitrine-api:main
#                         se existir, senão construída localmente com
#                         `dotnet publish src/Api/Vitrine.Api.csproj -t:PublishContainer`
#   SPEC0005_BASE_SOURCE  local = pula o GHCR e sempre constrói a base a partir do código
#   SPEC0005_TAG_A/_B     duas tags existentes para usar no lugar das versões locais derivadas
#   SPEC0005_DOTENV       caminho do .env gravado pelo deploy.sh (padrão: descoberto pelos
#                         rótulos do Compose no container da API)

LOCAL_HOST="${SPEC0005_LOCAL_HOST:-vitrine.localhost}"
LOCAL_BASE_URL="https://$LOCAL_HOST"
LOCAL_IMAGE_REPO="${SPEC0005_IMAGE_REPO:-vitrine-api}"
DEPLOY_SH="$REPO_ROOT/deploy/deploy.sh"
ROLLBACK_SH="$REPO_ROOT/deploy/rollback.sh"
ENV_EXAMPLE="$REPO_ROOT/deploy/local/.env.example"
STATE_DIR="$REPO_ROOT/deploy/.state"
FIXTURES_DIR="$INFRA_TESTS_DIR/fixtures"
API_CSPROJ="$REPO_ROOT/src/Api/Vitrine.Api.csproj"
BUSYBOX_IMAGE="${BUSYBOX_IMAGE:-busybox:1.37.0-musl}"
PSQL_IMAGE="${PSQL_IMAGE:-postgres:18.6-alpine}"

# curl contra o ambiente local: CA interna (-k) e *.localhost fixado em 127.0.0.1.
curl_local() {
  curl -sS -k --max-time 10 --resolve "$LOCAL_HOST:443:127.0.0.1" --resolve "$LOCAL_HOST:80:127.0.0.1" "$@"
}

local_health_ok() {
  curl_local -f -o /dev/null "$LOCAL_BASE_URL/health/live" 2>/dev/null
}

require_local_env() {
  [[ -f "$DEPLOY_SH" ]] || fail "deploy/deploy.sh does not exist"
  [[ -x "$DEPLOY_SH" ]] || fail "deploy/deploy.sh is not executable"
  [[ -f "$ENV_EXAMPLE" ]] || fail "deploy/local/.env.example does not exist"
  require_docker
  require_cmd curl
  if [[ -z "${SPEC0005_ENV_FILE:-}" ]]; then
    SPEC0005_ENV_FILE="$(mktemp)"
    if [[ "${SPEC_ID:-}" == "SPEC-0024" ]]; then
      cp "$ENV_EXAMPLE" "$SPEC0005_ENV_FILE"
    else
      # SPEC-0024: o .env.example liga o simulador do ML (perfil ml-simulator + sites extras do Caddy), cuja imagem é só
      # local. Os testes da SPEC-0005/0012 não dependem dele: a cópia tira o perfil e CADDY_EXTRA_SITES_DIR (como o IT-06).
      grep -vE '^[[:space:]]*(COMPOSE_PROFILES|CADDY_EXTRA_SITES_DIR)[[:space:]]*=' "$ENV_EXAMPLE" >"$SPEC0005_ENV_FILE" || true
      sed -nE 's/^[[:space:]]*COMPOSE_PROFILES[[:space:]]*=[[:space:]]*//p' "$ENV_EXAMPLE" | tail -n1 \
        | tr ',' '\n' | sed -E 's/^[[:space:]]+|[[:space:]]+$//g' | grep -vx 'ml-simulator' | grep -v '^$' | paste -sd, - \
        | sed 's/^/COMPOSE_PROFILES=/' >>"$SPEC0005_ENV_FILE" || true
    fi
    if [[ -n "${SPEC0005_IMAGE_REPO:-}" ]]; then
      printf '\nIMAGE_REPO=%s\n' "$SPEC0005_IMAGE_REPO" >>"$SPEC0005_ENV_FILE"
    fi
  fi
  [[ -f "$SPEC0005_ENV_FILE" ]] || fail "SPEC0005_ENV_FILE does not exist: $SPEC0005_ENV_FILE"
}

# Resolve a imagem base real: SPEC0005_BASE_IMAGE > GHCR :main > build local (dotnet publish).
# Exporta SPEC0005_BASE_IMAGE para que scripts filhos reaproveitem.
require_base_image() {
  require_docker
  if [[ -n "${SPEC0005_BASE_IMAGE:-}" ]]; then
    docker image inspect "$SPEC0005_BASE_IMAGE" >/dev/null 2>&1 || docker pull -q "$SPEC0005_BASE_IMAGE" >/dev/null 2>&1 \
      || fail "SPEC0005_BASE_IMAGE=$SPEC0005_BASE_IMAGE is not available"
  elif [[ "${SPEC0005_BASE_SOURCE:-}" != "local" ]] \
    && docker pull -q "${API_IMAGE_REPO}:main" >/dev/null 2>&1; then
    SPEC0005_BASE_IMAGE="${API_IMAGE_REPO}:main"
    log "base image: $SPEC0005_BASE_IMAGE (GHCR)"
  else
    require_cmd dotnet git
    local tag
    tag="spec0005-base-$(git -C "$REPO_ROOT" rev-parse --short HEAD)"
    if [[ -n "$(git -C "$REPO_ROOT" status --porcelain -- src)" ]] \
      || ! docker image inspect "$LOCAL_IMAGE_REPO:$tag" >/dev/null 2>&1; then
      log "building base image $LOCAL_IMAGE_REPO:$tag (dotnet publish -t:PublishContainer)"
      dotnet publish "$API_CSPROJ" -t:PublishContainer -p:ContainerRepository="$LOCAL_IMAGE_REPO" \
        -p:ContainerImageTag="$tag" -v q -nologo >&2 \
        || fail "could not build the base image with dotnet publish -t:PublishContainer"
    fi
    SPEC0005_BASE_IMAGE="$LOCAL_IMAGE_REPO:$tag"
  fi
  export SPEC0005_BASE_IMAGE
}

# Constrói localmente <repo>:<tag> derivada da base (versão distinta, mesmo conteúdo).
# Tag existente é reaproveitada: uma tag nunca muda de imagem (como um SHA publicado).
build_version() {
  local tag="$1"
  docker image inspect "${LOCAL_IMAGE_REPO}:$tag" >/dev/null 2>&1 && return 0
  require_base_image
  printf 'FROM %s\nLABEL org.opencontainers.image.version="%s" spec="SPEC-0005"\n' "$SPEC0005_BASE_IMAGE" "$tag" \
    | docker build -q -t "${LOCAL_IMAGE_REPO}:$tag" - >/dev/null \
    || fail "could not build local test version ${LOCAL_IMAGE_REPO}:$tag"
}

# Duas versões para os testes: SPEC0005_TAG_A/_B ou versões locais derivadas da base,
# com o ID da base na tag (base nova => tags novas).
prepare_versions() {
  local base_id=""
  if [[ -z "${SPEC0005_TAG_A:-}" || -z "${SPEC0005_TAG_B:-}" ]]; then
    require_base_image
    base_id="$(docker image inspect -f '{{.Id}}' "$SPEC0005_BASE_IMAGE")"
    base_id="${base_id#sha256:}"
    base_id="${base_id:0:12}"
  fi
  TAG_A="${SPEC0005_TAG_A:-local-a-$base_id}"
  TAG_B="${SPEC0005_TAG_B:-local-b-$base_id}"
  [[ -n "${SPEC0005_TAG_A:-}" ]] || build_version "$TAG_A"
  [[ -n "${SPEC0005_TAG_B:-}" ]] || build_version "$TAG_B"
}

# Dublê tests/infra/fixtures/<nome>/Dockerfile FROM a base, com wrapper estático (busybox)
# que delega ao entrypoint original da base; imagem <repo>:<nome>.
build_proof() {
  local proof="$1" entrypoint
  [[ -f "$FIXTURES_DIR/$proof/Dockerfile" ]] || fail "fixture tests/infra/fixtures/$proof/Dockerfile does not exist"
  require_base_image
  entrypoint="$(docker image inspect -f '{{range .Config.Entrypoint}}{{.}} {{end}}' "$SPEC0005_BASE_IMAGE")"
  entrypoint="${entrypoint% }"
  [[ -n "$entrypoint" ]] || fail "base image $SPEC0005_BASE_IMAGE has no entrypoint to wrap"
  docker build -q -t "${LOCAL_IMAGE_REPO}:$proof" \
    --build-arg "BASE_IMAGE=$SPEC0005_BASE_IMAGE" --build-arg "BUSYBOX_IMAGE=$BUSYBOX_IMAGE" \
    --build-arg "APP_ENTRYPOINT=$entrypoint" "$FIXTURES_DIR/$proof" >/dev/null \
    || fail "could not build proof image ${LOCAL_IMAGE_REPO}:$proof"
  log "proof image ${LOCAL_IMAGE_REPO}:$proof built FROM $SPEC0005_BASE_IMAGE"
}

# Roda deploy/deploy.sh <tag> --env-file <arquivo> na raiz do repositório; devolve o código de saída.
run_deploy() {
  (cd "$REPO_ROOT" && "$DEPLOY_SH" "$1" --env-file "$SPEC0005_ENV_FILE")
}

run_rollback() {
  (cd "$REPO_ROOT" && "$ROLLBACK_SH")
}

# Implanta e exige sucesso (passo "Dado").
deploy_ok() {
  log "deploy.sh $1"
  run_deploy "$1" || fail "deploy/deploy.sh $1 failed (exit $?)"
}

# Containers da aplicação em execução (exclui one-off, como o serviço migrate): "<id> <tag>".
api_containers() {
  docker ps --filter status=running --format '{{.ID}} {{.Image}} {{.Label "com.docker.compose.oneoff"}}' \
    | awk -v p="${LOCAL_IMAGE_REPO}:" 'index($2, p) == 1 && $3 != "True" { print $1, substr($2, length(p) + 1) }'
}

# Tag da imagem da aplicação em execução no Docker local (exatamente uma esperada).
running_api_tag() {
  local tags
  tags="$(api_containers | awk '{ print $2 }' | sort -u)"
  [[ -n "$tags" ]] || fail "no running application container of ${LOCAL_IMAGE_REPO}:<tag>"
  [[ "$(printf '%s\n' "$tags" | wc -l)" -eq 1 ]] \
    || fail "more than one ${LOCAL_IMAGE_REPO} tag running: ${tags//$'\n'/ }"
  printf '%s\n' "$tags"
}

api_container_id() {
  api_containers | awk 'NR == 1 { print $1 }'
}

state_tag() {
  local f="$STATE_DIR/$1"
  [[ -f "$f" ]] || fail "deploy/.state/$1 does not exist"
  tr -d '[:space:]' <"$f"
}

assert_live_version() {
  local expected="$1" running current
  running="$(running_api_tag)"
  [[ "$running" == "$expected" ]] || fail "running ${LOCAL_IMAGE_REPO}:$running, expected :$expected"
  local_health_ok || fail "GET $LOCAL_BASE_URL/health/live via Caddy is not 2xx"
  current="$(state_tag current)"
  [[ "$current" == "$expected" ]] || fail "deploy/.state/current is '$current', expected '$expected'"
}

# Eventos de container desde <epoch>: "<evento> <imagem> <oneoff> <serviço> <exitCode>".
container_events_since() {
  docker events --since "$1" --until "$(($(date +%s) + 1))" --filter type=container \
    --format '{{.Action}} {{.Actor.Attributes.image}} {{index .Actor.Attributes "com.docker.compose.oneoff"}} {{index .Actor.Attributes "com.docker.compose.service"}} {{index .Actor.Attributes "exitCode"}}'
}

# Sonda de disponibilidade em segundo plano: grava "<epoch> up|down" a cada 0,5 s em $1.
start_probe() {
  (
    while :; do
      if local_health_ok; then s=up; else s=down; fi
      printf '%s %s\n' "$(date +%s.%N)" "$s"
      sleep 0.5
    done
  ) >>"$1" &
  PROBE_PID=$!
}

stop_probe() {
  kill "${PROBE_PID:-0}" 2>/dev/null || true
  wait "${PROBE_PID:-0}" 2>/dev/null || true
}

# Arquivos .env gravados pelo deploy.sh para o Compose (exceto o --env-file de entrada).
# Fonte: SPEC0005_DOTENV, ou rótulos do Compose no container da API
# (com.docker.compose.project.environment_file e <working_dir>/.env).
deployed_dotenv_files() {
  if [[ -n "${SPEC0005_DOTENV:-}" ]]; then
    printf '%s\n' "$SPEC0005_DOTENV"
    return 0
  fi
  local id envf wd f
  id="$(api_container_id)"
  [[ -n "$id" ]] || fail "no running ${LOCAL_IMAGE_REPO} container to locate the deployed .env"
  envf="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.environment_file"}}' "$id")"
  wd="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.working_dir"}}' "$id")"
  {
    tr ',' '\n' <<<"$envf"
    [[ -z "$wd" ]] || printf '%s/.env\n' "$wd"
  } | sed '/^$/d' | sort -u | while IFS= read -r f; do
    if [[ "$f" != "${SPEC0005_ENV_FILE:-}" && -f "$f" ]]; then printf '%s\n' "$f"; fi
  done
  return 0
}
