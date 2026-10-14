# shellcheck shell=bash
# SPEC-0004 — biblioteca comum dos testes do pipeline de CI (tests/ci/).
# Cada script define TEST_ID antes de carregar este arquivo. Toda linha de saída
# começa com "SPEC-0004:<ID>" para rastreabilidade (references/testing.md).
#
# UT-*: leem .github/workflows/ci.yml localmente (actionlint/yq: binário local ou imagem Docker fixa).
# IT-*/CT-*: verificam execuções reais no GitHub (gh CLI) de PRs/branches de prova `ci-proof/*`;
#            o alvo (PR, execução ou SHA) vem sempre de variável de ambiente.
#
# Variáveis comuns (opcionais):
#   GH_REPO                 owner/repo (padrão: deduzido do remote origin)
#   SPEC0004_WAIT=1         espera os checks do PR terminarem (`gh pr checks --watch`) antes de verificar
#   SPEC0004_EVIDENCE_FILE  arquivo onde as linhas de evidência (links das execuções) são acrescentadas
#   SPEC0004_ALLOW_ANY_BRANCH=1  aceita PR cuja branch não seja `ci-proof/*` (padrão: recusa)

SPEC_ID="SPEC-0004"
: "${TEST_ID:?defina TEST_ID antes de carregar lib.sh}"

CI_TESTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$CI_TESTS_DIR/../.." && pwd)"

# Contrato (seção 6 da spec).
CI_YML="${CI_YML:-$REPO_ROOT/.github/workflows/ci.yml}"
CI_WORKFLOW_NAME="${CI_WORKFLOW_NAME:-CI}"
CI_WORKFLOW_FILE="${CI_WORKFLOW_FILE:-ci.yml}"
# shellcheck disable=SC2034 # usado pelos scripts que carregam esta biblioteca
REQUIRED_CHECKS=(build-lint unit integration architecture e2e security sdd)
BASE_BRANCH="${BASE_BRANCH:-main}"
API_IMAGE_REGISTRY="ghcr.io"
API_IMAGE_NAME="${API_IMAGE_NAME:-thomasravache/vitrine-api}"
API_IMAGE_REPO="$API_IMAGE_REGISTRY/$API_IMAGE_NAME"
COMPOSE_BUNDLE_ARTIFACT="${COMPOSE_BUNDLE_ARTIFACT:-compose-bundle}"
COMPOSE_BUNDLE_FILES=(docker-compose.yaml .env.template)
PROOF_BRANCH_PREFIX="ci-proof/"

# Imagens oficiais com tag fixa (sobrescrevíveis).
ACTIONLINT_IMAGE="${ACTIONLINT_IMAGE:-rhysd/actionlint:1.7.12}"
YQ_IMAGE="${YQ_IMAGE:-mikefarah/yq:4.44.3}"

log()  { printf '%s:%s %s\n' "$SPEC_ID" "$TEST_ID" "$*"; }
fail() { printf '%s:%s FAIL: %s\n' "$SPEC_ID" "$TEST_ID" "$*" >&2; exit 1; }
pass() { printf '%s:%s PASS: %s\n' "$SPEC_ID" "$TEST_ID" "$*"; }

# Linha de evidência (stdout e, se definido, $SPEC0004_EVIDENCE_FILE).
record() {
  local line
  line="$(date -u +%Y-%m-%dT%H:%M:%SZ) $SPEC_ID:$TEST_ID $*"
  printf '%s\n' "$line"
  if [[ -n "${SPEC0004_EVIDENCE_FILE:-}" ]]; then
    printf '%s\n' "$line" >>"$SPEC0004_EVIDENCE_FILE"
  fi
}

require_env() {
  local v missing=()
  for v in "$@"; do
    [[ -n "${!v:-}" ]] || missing+=("$v")
  done
  if ((${#missing[@]} > 0)); then
    fail "env var(s) missing: ${missing[*]} (see the header of tests/ci/$TEST_ID.sh)"
  fi
}

require_cmd() {
  local c
  for c in "$@"; do
    command -v "$c" >/dev/null 2>&1 || fail "required command not found: $c"
  done
}

require_ci_yml() {
  [[ -f "$CI_YML" ]] || fail "${CI_YML#"$REPO_ROOT"/} does not exist — CI workflow not implemented yet"
}

require_docker() {
  require_cmd docker
  docker info >/dev/null 2>&1 || fail "docker daemon is not reachable"
}

require_number() {
  [[ "${!1}" =~ ^[0-9]+$ ]] || fail "$1 must be a number, got '${!1}'"
}

# --- YAML ---------------------------------------------------------------------

# Converte um arquivo YAML em JSON: yq local (mikefarah), python3+PyYAML ou a imagem $YQ_IMAGE.
yaml_to_json() {
  local file="$1" dir
  if command -v yq >/dev/null 2>&1 && yq --version 2>/dev/null | grep -q mikefarah; then
    yq -o=json '.' "$file"
  elif command -v python3 >/dev/null 2>&1 && python3 -c 'import yaml' >/dev/null 2>&1; then
    python3 -c 'import json, sys, yaml; json.dump(yaml.safe_load(open(sys.argv[1], encoding="utf-8")), sys.stdout, default=str)' "$file"
  else
    require_docker
    dir="$(cd "$(dirname "$file")" && pwd)"
    docker run --rm -u "$(id -u):$(id -g)" -v "$dir:/work:ro" -w /work "$YQ_IMAGE" -o=json '.' "$(basename "$file")"
  fi
}

# --- GitHub -------------------------------------------------------------------

require_gh() {
  require_cmd gh jq
  gh auth status >/dev/null 2>&1 || fail "gh is not authenticated (run 'gh auth login' or set GH_TOKEN)"
  if [[ -z "${GH_REPO:-}" ]]; then
    local origin
    origin="$(git -C "$REPO_ROOT" remote get-url origin 2>/dev/null || true)"
    GH_REPO="$(sed -nE 's#^.*github\.com[:/]([^/]+/[^/.]+)(\.git)?$#\1#p' <<<"$origin")"
  fi
  [[ -n "${GH_REPO:-}" ]] || fail "cannot infer GH_REPO from the origin remote; set GH_REPO=owner/repo"
  export GH_REPO
}

# Metadados do PR de prova; exige branch ci-proof/* (salvo SPEC0004_ALLOW_ANY_BRANCH=1).
# Define PR_JSON, PR_URL, PR_HEAD_SHA, PR_HEAD_REF.
load_pr() {
  local pr="$1"
  PR_JSON="$(gh pr view "$pr" -R "$GH_REPO" \
    --json number,url,state,title,headRefName,headRefOid,baseRefName,mergeStateStatus,mergeable,files 2>&1)" \
    || fail "cannot read PR #$pr in $GH_REPO: $PR_JSON"
  PR_URL="$(jq -r .url <<<"$PR_JSON")"
  PR_HEAD_SHA="$(jq -r .headRefOid <<<"$PR_JSON")"
  PR_HEAD_REF="$(jq -r .headRefName <<<"$PR_JSON")"
  log "PR $PR_URL head=$PR_HEAD_REF@${PR_HEAD_SHA:0:12} base=$(jq -r .baseRefName <<<"$PR_JSON")"
  [[ "$(jq -r .baseRefName <<<"$PR_JSON")" == "$BASE_BRANCH" ]] || fail "PR #$pr does not target $BASE_BRANCH"
  if [[ "${SPEC0004_ALLOW_ANY_BRANCH:-}" != "1" && "$PR_HEAD_REF" != "$PROOF_BRANCH_PREFIX"* ]]; then
    fail "PR #$pr head branch '$PR_HEAD_REF' is not a proof branch ($PROOF_BRANCH_PREFIX*)"
  fi
  if [[ "${SPEC0004_WAIT:-}" == "1" ]]; then
    log "waiting for checks of PR #$pr (gh pr checks --watch)"
    gh pr checks "$pr" -R "$GH_REPO" --watch --interval 30 >/dev/null 2>&1 || true
  fi
}

# Checks do PR: "[{name, bucket, state, link, workflow, startedAt, completedAt}]" (mais recente por nome).
pr_checks_json() {
  local out
  out="$(gh pr checks "$1" -R "$GH_REPO" --json name,bucket,state,link,workflow,startedAt,completedAt 2>/dev/null)" || true
  [[ -n "$out" ]] || fail "PR #$1 has no checks reported (CI did not run?)"
  jq 'group_by(.name) | map(sort_by(.startedAt // "") | last)' <<<"$out"
}

# Um check (JSON) pelo nome exato, vindo do workflow do CI; falha se não existir.
check_by_name() {
  local checks="$1" name="$2" c
  c="$(jq -c --arg n "$name" --arg w "$CI_WORKFLOW_NAME" \
    '[.[] | select(.name == $n and ((.workflow // "") == $w or (.workflow // "") == ""))] | last // empty' <<<"$checks")"
  [[ -n "$c" ]] || fail "check '$name' (workflow '$CI_WORKFLOW_NAME') not found on the PR"
  printf '%s\n' "$c"
}

# Exige que o check esteja concluído (sem pending), para não julgar uma execução em andamento.
require_check_completed() {
  local c="$1" bucket
  bucket="$(jq -r .bucket <<<"$c")"
  [[ "$bucket" != "pending" ]] || fail "check '$(jq -r .name <<<"$c")' still pending — wait or set SPEC0004_WAIT=1"
}

# IDs a partir do link do check: https://github.com/<o>/<r>/actions/runs/<run>/job/<job>.
check_run_id() { sed -nE 's#.*/actions/runs/([0-9]+).*#\1#p' <<<"$(jq -r .link <<<"$1")"; }
check_job_id() { sed -nE 's#.*/job/([0-9]+).*#\1#p' <<<"$(jq -r .link <<<"$1")"; }

# Passos com falha de um job: um nome por linha.
job_failed_steps() {
  gh api "repos/$GH_REPO/actions/jobs/$1" --jq '.steps[]? | select(.conclusion == "failure") | .name'
}

job_log() {
  gh run view "$2" -R "$GH_REPO" --job "$1" --log 2>/dev/null \
    || gh api "repos/$GH_REPO/actions/jobs/$1/logs" 2>/dev/null \
    || fail "cannot download the log of job $1 (run $2)"
}

# Anotações do check run (job ID = check run ID no Actions): "<level>\t<title>\t<message>".
check_annotations() {
  gh api --paginate "repos/$GH_REPO/check-runs/$1/annotations" \
    --jq '.[] | [.annotation_level, (.title // ""), (.message // "")] | @tsv'
}

# Proteção da main: contexts obrigatórios, um por linha.
required_contexts() {
  gh api "repos/$GH_REPO/branches/$BASE_BRANCH/protection/required_status_checks" \
    --jq '([.contexts[]?] + [.checks[]?.context]) | unique | .[]' 2>/dev/null || true
}

# Execução do CI (JSON do endpoint actions/runs/<id>).
load_run() {
  RUN_JSON="$(gh api "repos/$GH_REPO/actions/runs/$1" 2>&1)" || fail "cannot read Actions run $1: $RUN_JSON"
}

# Execução de CI de push na main, concluída com sucesso, para um SHA (vazio se não houver).
successful_main_run_for_sha() {
  gh api "repos/$GH_REPO/actions/workflows/$CI_WORKFLOW_FILE/runs?head_sha=$1&event=push&branch=$BASE_BRANCH&status=success&per_page=5" \
    --jq '.workflow_runs | sort_by(.run_started_at) | last // empty'
}

# Baixa o artefato compose-bundle da execução para $2 e exige os arquivos do contrato.
assert_compose_bundle() {
  local run_id="$1" dest="$2" count f found
  count="$(gh api "repos/$GH_REPO/actions/runs/$run_id/artifacts" \
    --jq "[.artifacts[] | select(.name == \"$COMPOSE_BUNDLE_ARTIFACT\" and .expired == false)] | length")" \
    || fail "cannot list artifacts of run $run_id"
  ((count > 0)) || fail "artifact '$COMPOSE_BUNDLE_ARTIFACT' not found (or expired) in run $run_id"
  gh run download "$run_id" -R "$GH_REPO" -n "$COMPOSE_BUNDLE_ARTIFACT" -D "$dest" >/dev/null 2>&1 \
    || fail "cannot download artifact '$COMPOSE_BUNDLE_ARTIFACT' of run $run_id"
  for f in "${COMPOSE_BUNDLE_FILES[@]}"; do
    found="$(find "$dest" -type f -name "$f" -print -quit)"
    [[ -n "$found" ]] || fail "artifact '$COMPOSE_BUNDLE_ARTIFACT' of run $run_id has no $f"
  done
  log "artifact '$COMPOSE_BUNDLE_ARTIFACT' (run $run_id) contains: ${COMPOSE_BUNDLE_FILES[*]}"
}

# Digest do manifesto ghcr.io/<name>:<tag> (vazio se não existir).
# Registro anônimo (pacote público) -> docker manifest/imagetools (login local) -> API de pacotes do gh.
image_digest() {
  local tag="$1" token digest=""
  if command -v curl >/dev/null 2>&1; then
    token="$(curl -fsS "https://$API_IMAGE_REGISTRY/token?scope=repository:$API_IMAGE_NAME:pull" 2>/dev/null | jq -r '.token // empty' 2>/dev/null || true)"
    if [[ -n "$token" ]]; then
      digest="$(curl -fsSI -H "Authorization: Bearer $token" \
        -H 'Accept: application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json' \
        "https://$API_IMAGE_REGISTRY/v2/$API_IMAGE_NAME/manifests/$tag" 2>/dev/null \
        | tr -d '\r' | awk -F': ' 'tolower($1) == "docker-content-digest" { print $2 }' || true)"
    fi
  fi
  if [[ -z "$digest" ]] && command -v docker >/dev/null 2>&1; then
    digest="$(docker buildx imagetools inspect "$API_IMAGE_REPO:$tag" --format '{{json .Manifest}}' 2>/dev/null | jq -r '.digest // empty' 2>/dev/null || true)"
  fi
  if [[ -z "$digest" ]]; then
    local owner="${API_IMAGE_NAME%%/*}" pkg="${API_IMAGE_NAME#*/}"
    digest="$(gh api --paginate "users/$owner/packages/container/$pkg/versions" \
      --jq ".[] | select(.metadata.container.tags | index(\"$tag\")) | .name" 2>/dev/null | head -n1 || true)"
  fi
  printf '%s\n' "$digest"
}

# Exige o check `security` vermelho, com passo falho cujo nome casa <step_re> e log do job que casa
# <log_re> (regex estendida, sem distinção de maiúsculas). Define SECURITY_CHECK, SECURITY_FAILED_STEPS e
# SECURITY_LOG (texto do log do job).
assert_security_failed_by() {
  local pr="$1" step_re="$2" log_re="$3" checks bucket job_id run_id hits
  checks="$(pr_checks_json "$pr")"
  SECURITY_CHECK="$(check_by_name "$checks" security)"
  require_check_completed "$SECURITY_CHECK"
  bucket="$(jq -r .bucket <<<"$SECURITY_CHECK")"
  log "check security: $bucket ($(jq -r .link <<<"$SECURITY_CHECK"))"
  [[ "$bucket" == "fail" ]] || fail "check 'security' is '$bucket', expected 'fail'"
  job_id="$(check_job_id "$SECURITY_CHECK")"
  run_id="$(check_run_id "$SECURITY_CHECK")"
  [[ -n "$job_id" && -n "$run_id" ]] || fail "cannot extract run/job id from the 'security' check link"
  mapfile -t SECURITY_FAILED_STEPS < <(job_failed_steps "$job_id")
  log "security failed step(s): ${SECURITY_FAILED_STEPS[*]:-none}"
  printf '%s\n' "${SECURITY_FAILED_STEPS[@]}" | grep -qiE "$step_re" \
    || fail "no failed step of 'security' matches /$step_re/ (failed: ${SECURITY_FAILED_STEPS[*]:-none})"
  SECURITY_LOG="$(job_log "$job_id" "$run_id")"
  hits="$(grep -iE "$log_re" <<<"$SECURITY_LOG" | head -n 5 || true)"
  [[ -n "$hits" ]] || fail "log of job 'security' has no finding matching /$log_re/"
  printf '%s\n' "$hits" | sed "s/^/$SPEC_ID:$TEST_ID   log: /"
}
