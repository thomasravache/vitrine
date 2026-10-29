#!/usr/bin/env bash
# SPEC-0012:IT-01 — Dado a stack `vitrine` já no ar antes do runner, quando `run.sh` termina, então ela
# continua no ar (o runner só derruba o que subiu).
#
# Local (Docker). Se a stack `vitrine` já estiver no ar (ex.: o ambiente local prod-like), usa-a como está;
# senão sobe a fixture (postgres do projeto `vitrine`) e a derruba ao sair. Roda
# `tests/infra/run.sh SPEC-0012-FX-up` (que não recria containers já no ar) e exige: saída 0 e os mesmos
# containers do projeto em execução antes e depois.
set -euo pipefail
TEST_ID="IT-01"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
SPEC_ID="SPEC-0012"
# shellcheck source=SPEC-0012-lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/SPEC-0012-lib.sh"

require_docker
require_file "$RUN_SH" "tests/infra/run.sh does not exist"

# Dado: a stack `vitrine` no ar antes do runner.
if stack_is_up; then
  log "stack '$COMPOSE_PROJECT' already up: using it as is"
else
  log "stack '$COMPOSE_PROJECT' not up: bringing up the fixture (postgres) for this test"
  trap stack_down EXIT
  fixture_up || fail "could not bring up the '$COMPOSE_PROJECT' stack for the precondition"
fi
before="$(stack_ids)"
[[ -n "$before" ]] || fail "precondition: the '$COMPOSE_PROJECT' stack is not up"
log "before run.sh: $(wc -l <<<"$before") container(s) running"

# Quando: o runner executa um IT e termina.
if ! run_runner "$FIXTURE_IT"; then
  runner_tail
  fail "run.sh $FIXTURE_IT exited non-zero"
fi

# Então: a stack continua no ar, com os mesmos containers.
after="$(stack_ids)"
[[ -n "$after" ]] || fail "run.sh took down the '$COMPOSE_PROJECT' stack that was already up before it"
missing="$(comm -23 <(printf '%s\n' "$before") <(printf '%s\n' "$after"))"
[[ -z "$missing" ]] \
  || fail "run.sh stopped $(wc -l <<<"$missing") container(s) of the '$COMPOSE_PROJECT' stack that was up before it"
pass "the '$COMPOSE_PROJECT' stack that was up before run.sh is still up ($(wc -l <<<"$after") container(s))"
