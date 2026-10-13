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

# Contrato consumido SPEC-0004@1.
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
# deploy/deploy.sh <tag> [--env-file <arquivo>] ; deploy/rollback.sh ;
# deploy/local/.env.example ; https://vitrine.localhost (Caddy, CA interna) ;
# deploy/.state/current e deploy/.state/previous.
#
# Variáveis (todas opcionais):
#   SPEC0005_LOCAL_HOST   host do ambiente local (padrão: vitrine.localhost)
#   SPEC0005_ENV_FILE     arquivo passado em --env-file (padrão: cópia de deploy/local/.env.example)
#   SPEC0005_BASE_IMAGE   imagem real da API usada como base das versões de teste
#                         (padrão: ghcr.io/thomasravache/vitrine-api:main, publicada pela SPEC-0004)
#   SPEC0005_TAG_A/_B     duas tags existentes para usar no lugar das versões locais derivadas
#   SPEC0005_DOTENV       caminho do .env gravado pelo deploy.sh (padrão: descoberto pelos
#                         rótulos do Compose no container da API)

LOCAL_HOST="${SPEC0005_LOCAL_HOST:-vitrine.localhost}"
LOCAL_BASE_URL="https://$LOCAL_HOST"
DEPLOY_SH="$REPO_ROOT/deploy/deploy.sh"
ROLLBACK_SH="$REPO_ROOT/deploy/rollback.sh"
ENV_EXAMPLE="$REPO_ROOT/deploy/local/.env.example"
STATE_DIR="$REPO_ROOT/deploy/.state"
BASE_IMAGE="${SPEC0005_BASE_IMAGE:-${API_IMAGE_REPO}:main}"

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
    cp "$ENV_EXAMPLE" "$SPEC0005_ENV_FILE"
  fi
  [[ -f "$SPEC0005_ENV_FILE" ]] || fail "SPEC0005_ENV_FILE does not exist: $SPEC0005_ENV_FILE"
}

# Garante a imagem base (local ou baixada do GHCR).
require_base_image() {
  docker image inspect "$BASE_IMAGE" >/dev/null 2>&1 || docker pull -q "$BASE_IMAGE" >/dev/null 2>&1 \
    || fail "base image $BASE_IMAGE not available (published by SPEC-0004 CI; set SPEC0005_BASE_IMAGE to an existing ${API_IMAGE_REPO} image)"
}

# Constrói localmente ${API_IMAGE_REPO}:<tag> derivada da base (versão distinta, mesmo conteúdo).
build_version() {
  local tag="$1"
  require_base_image
  printf 'FROM %s\nLABEL org.opencontainers.image.version="%s" spec="SPEC-0005"\n' "$BASE_IMAGE" "$tag" \
    | docker build -q -t "${API_IMAGE_REPO}:$tag" - >/dev/null \
    || fail "could not build local test version ${API_IMAGE_REPO}:$tag"
}

# Duas versões para os testes: SPEC0005_TAG_A/_B ou versões locais derivadas da base.
prepare_versions() {
  TAG_A="${SPEC0005_TAG_A:-local-spec0005-a}"
  TAG_B="${SPEC0005_TAG_B:-local-spec0005-b}"
  [[ -n "${SPEC0005_TAG_A:-}" ]] || build_version "$TAG_A"
  [[ -n "${SPEC0005_TAG_B:-}" ]] || build_version "$TAG_B"
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

# Tag da imagem da API em execução no Docker local (exatamente uma esperada).
running_api_tag() {
  local tags
  tags="$(docker ps --filter status=running --format '{{.Image}}' \
    | awk -v p="${API_IMAGE_REPO}:" 'index($0, p) == 1 { print substr($0, length(p) + 1) }' | sort -u)"
  [[ -n "$tags" ]] || fail "no running container of ${API_IMAGE_REPO}:<tag>"
  [[ "$(printf '%s\n' "$tags" | wc -l)" -eq 1 ]] \
    || fail "more than one ${API_IMAGE_REPO} tag running: ${tags//$'\n'/ }"
  printf '%s\n' "$tags"
}

api_container_id() {
  docker ps --filter status=running --format '{{.ID}} {{.Image}}' \
    | awk -v p="${API_IMAGE_REPO}:" 'index($2, p) == 1 { print $1; exit }'
}

state_tag() {
  local f="$STATE_DIR/$1"
  [[ -f "$f" ]] || fail "deploy/.state/$1 does not exist"
  tr -d '[:space:]' <"$f"
}

assert_live_version() {
  local expected="$1" running current
  running="$(running_api_tag)"
  [[ "$running" == "$expected" ]] || fail "running ${API_IMAGE_REPO}:$running, expected :$expected"
  local_health_ok || fail "GET $LOCAL_BASE_URL/health/live via Caddy is not 2xx"
  current="$(state_tag current)"
  [[ "$current" == "$expected" ]] || fail "deploy/.state/current is '$current', expected '$expected'"
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
  [[ -n "$id" ]] || fail "no running ${API_IMAGE_REPO} container to locate the deployed .env"
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
