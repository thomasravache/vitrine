#!/usr/bin/env bash
# SPEC-0012 — IT de fixture (não faz parte da suíte padrão do run.sh): sobe a stack local `vitrine`
# pelo mesmo caminho dos IT reais (arquivos do Compose de deploy/, projeto `vitrine`), só o postgres,
# e termina SEM derrubá-la — como IT-01..IT-06 fazem com deploy.sh. Usado por SPEC-0012:UT-01 e :IT-01
# via `tests/infra/run.sh [--keep] SPEC-0012-FX-up`.
set -euo pipefail
TEST_ID="FX-up"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
SPEC_ID="SPEC-0012"
# shellcheck source=SPEC-0012-lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/SPEC-0012-lib.sh"

require_docker
require_file "$ENV_EXAMPLE" "deploy/local/.env.example does not exist"
fixture_up || fail "could not bring up the '$COMPOSE_PROJECT' stack (postgres) with docker compose"
stack_is_up || fail "the '$COMPOSE_PROJECT' stack is not up after 'docker compose up'"
pass "stack '$COMPOSE_PROJECT' up ($(stack_ids | wc -l) container(s)); left running on purpose"
