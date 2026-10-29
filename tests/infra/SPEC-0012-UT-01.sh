#!/usr/bin/env bash
# SPEC-0012:UT-01 — Dado nenhuma stack `vitrine` no ar, quando `run.sh` executa um IT que sobe a stack e
# termina, então `docker compose -p vitrine ps -q` fica vazio; com `--keep`, os containers continuam.
#
# Local (Docker). O IT executado é a fixture SPEC-0012-FX-up (sobe só o postgres do projeto `vitrine`
# pelos arquivos do Compose de deploy/ e não o derruba). Dois casos:
#   1. `tests/infra/run.sh SPEC-0012-FX-up`         -> sai 0 e nenhum container do projeto fica no ar;
#   2. `tests/infra/run.sh --keep SPEC-0012-FX-up`  -> sai 0 e os containers continuam no ar;
#   3. `tests/infra/run.sh SPEC-0012-FX-up --keep`  -> idem (`--keep` também vale depois dos IDs);
#   4. projeto `vitrine` existente mas PARADO antes (fixture + `docker compose -p vitrine stop`):
#      `tests/infra/run.sh SPEC-0012-FX-up` -> sai 0 e os volumes do projeto continuam existindo
#      (a máquina volta como estava: o runner não apaga dados de uma stack que não criou).
# Precondição: nenhuma stack `vitrine` no ar (SPEC0012_TAKE_DOWN=1 deixa o teste derrubá-la).
# Entre os casos e ao sair, o teste derruba o que sobrou do projeto (`docker compose -p vitrine down -v`,
# como o runner): o volume postgres_data do ambiente local é apagado (só dados sintéticos).
set -euo pipefail
TEST_ID="UT-01"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
SPEC_ID="SPEC-0012"
# shellcheck source=SPEC-0012-lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/SPEC-0012-lib.sh"

require_docker
require_file "$RUN_SH" "tests/infra/run.sh does not exist"
require_no_stack
trap stack_down EXIT

errors=()

# Caso 1 — sem --keep: o runner derruba a stack que subiu.
log "run.sh $FIXTURE_IT (no stack before)"
if run_runner "$FIXTURE_IT"; then
  left="$(stack_ids)"
  if [[ -n "$left" ]]; then
    errors+=("run.sh $FIXTURE_IT left $(wc -l <<<"$left") container(s) of '$COMPOSE_PROJECT' running (docker compose -p $COMPOSE_PROJECT ps -q not empty)")
  else
    log "after run.sh: docker compose -p $COMPOSE_PROJECT ps -q is empty"
  fi
else
  runner_tail
  errors+=("run.sh $FIXTURE_IT exited non-zero")
fi
stack_down

# Caso 2 — com --keep: o runner preserva a stack.
log "run.sh --keep $FIXTURE_IT (no stack before)"
if run_runner --keep "$FIXTURE_IT"; then
  if stack_is_up; then
    log "after run.sh --keep: $(stack_ids | wc -l) container(s) still running"
  else
    errors+=("run.sh --keep $FIXTURE_IT took the '$COMPOSE_PROJECT' stack down (expected it to be kept)")
  fi
else
  runner_tail
  errors+=("run.sh --keep $FIXTURE_IT exited non-zero (is --keep supported?)")
fi

stack_down

# Caso 3 — --keep depois dos IDs também preserva a stack.
log "run.sh $FIXTURE_IT --keep (no stack before)"
if run_runner "$FIXTURE_IT" --keep; then
  if stack_is_up; then
    log "after run.sh $FIXTURE_IT --keep: $(stack_ids | wc -l) container(s) still running"
  else
    errors+=("run.sh $FIXTURE_IT --keep took the '$COMPOSE_PROJECT' stack down (expected --keep after the IDs to keep it)")
  fi
else
  runner_tail
  errors+=("run.sh $FIXTURE_IT --keep exited non-zero (is --keep accepted after the IDs?)")
fi
stack_down

# Caso 4 — projeto existente mas parado: o runner não apaga seus volumes.
log "run.sh $FIXTURE_IT (project '$COMPOSE_PROJECT' exists but is stopped)"
fixture_up || fail "could not bring up the '$COMPOSE_PROJECT' stack for case 4"
docker compose -p "$COMPOSE_PROJECT" stop >/dev/null 2>&1 || fail "could not stop the '$COMPOSE_PROJECT' stack"
stack_is_up && fail "the '$COMPOSE_PROJECT' stack is still up after 'docker compose stop'"
volumes_before="$(stack_volumes)"
[[ -n "$volumes_before" ]] || fail "case 4 precondition: the '$COMPOSE_PROJECT' project has no volumes"
log "before run.sh: stopped project with volume(s) ${volumes_before//$'\n'/ }"
if run_runner "$FIXTURE_IT"; then
  lost="$(comm -23 <(printf '%s\n' "$volumes_before") <(stack_volumes))"
  if [[ -n "$lost" ]]; then
    errors+=("run.sh $FIXTURE_IT removed volume(s) of the stopped '$COMPOSE_PROJECT' project that existed before it: ${lost//$'\n'/ }")
  else
    log "after run.sh: volume(s) of the stopped project still exist"
  fi
else
  runner_tail
  errors+=("run.sh $FIXTURE_IT exited non-zero (stopped project before)")
fi

if ((${#errors[@]} > 0)); then
  for e in "${errors[@]}"; do printf '%s:%s   %s\n' "$SPEC_ID" "$TEST_ID" "$e" >&2; done
  fail "${#errors[@]} violation(s): run.sh must take down only the stack it brought up, unless --keep"
fi
pass "run.sh takes down only the '$COMPOSE_PROJECT' stack it brought up; --keep (before or after the IDs) keeps it"
