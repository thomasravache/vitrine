#!/usr/bin/env bash
# SPEC-0005:IT-01 — Dado o ambiente local prod-like, quando `deploy.sh <tag>` roda, então a versão
# responde 200 em `/health/live` via Caddy, o `.env` tem permissão 600 e o tempo de troca é
# registrado. NFR (seção 5): indisponibilidade na troca ≤ 30 s, medida aqui.
#
# Local (Docker). Implanta a versão A, depois a B sondando https://vitrine.localhost/health/live
# a cada 0,5 s; a maior janela contínua sem 2xx é o tempo de troca.
# Variáveis opcionais: ver lib.sh (SPEC0005_*) e SPEC0005_MAX_DOWNTIME (padrão: 30).
set -euo pipefail
TEST_ID="IT-01"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

MAX_DOWNTIME="${SPEC0005_MAX_DOWNTIME:-30}"

require_local_env
# O arquivo de entrada fica 644: o .env gravado pelo deploy.sh precisa ter 600 por conta própria.
chmod 644 "$SPEC0005_ENV_FILE"
prepare_versions

# Dado: ambiente local com a versão A.
deploy_ok "$TAG_A"
assert_live_version "$TAG_A"

# Quando: deploy.sh B, com sonda de disponibilidade.
probe_log="$(mktemp)"
trap 'stop_probe; rm -f "$probe_log"' EXIT
start_probe "$probe_log"
deploy_ok "$TAG_B"
stop_probe

# Então: versão B no ar via Caddy.
assert_live_version "$TAG_B"
log "${LOCAL_IMAGE_REPO}:$TAG_B answers 200 on $LOCAL_BASE_URL/health/live"

# .env gravado com permissão 600 e fora do controle de versão.
mapfile -t dotenvs < <(deployed_dotenv_files)
((${#dotenvs[@]} > 0)) || fail "could not find the .env written by deploy.sh (set SPEC0005_DOTENV)"
for f in "${dotenvs[@]}"; do
  mode="$(stat -c '%a' "$f")"
  [[ "$mode" == "600" ]] || fail ".env $f has mode $mode, expected 600"
  if [[ "$f" == "$REPO_ROOT"/* ]]; then
    ! git -C "$REPO_ROOT" ls-files --error-unmatch "$f" >/dev/null 2>&1 || fail ".env $f is tracked by git"
    git -C "$REPO_ROOT" check-ignore -q "$f" || fail ".env $f is not git-ignored"
  fi
  log ".env $f mode 600"
done

# Tempo de troca (maior janela contínua sem resposta 2xx).
downtime="$(awk '
  $2 == "down" { if (start == "") start = $1; last = $1; next }
  $2 == "up"   { if (start != "") { d = $1 - start; if (d > max) max = d; start = "" } }
  END { if (start != "") { d = last - start; if (d > max) max = d } printf "%.1f", max + 0 }' "$probe_log")"
samples="$(wc -l <"$probe_log")"
((samples > 0)) || fail "availability probe recorded no samples"
record "swap_downtime_seconds=$downtime samples=$samples from=$TAG_A to=$TAG_B"
awk -v d="$downtime" -v m="$MAX_DOWNTIME" 'BEGIN { exit !(d <= m) }' \
  || fail "swap downtime ${downtime}s exceeds ${MAX_DOWNTIME}s"

pass "deploy.sh $TAG_B: health 200 via Caddy, .env 600, swap downtime ${downtime}s"
