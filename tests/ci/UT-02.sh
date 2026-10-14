#!/usr/bin/env bash
# SPEC-0004:UT-02 — Dado `ci.yml`, quando lido pelo script de verificação, então os nomes dos jobs são
# exatamente os checks obrigatórios do contrato e todo job declara `permissions`.
#
# Local. Contrato (seção 6): workflow "CI"; gatilhos pull_request (main), push (main), workflow_dispatch;
# checks build-lint, unit, integration, architecture, e2e, security, sdd.
# Nome do check = `name:` do job, ou a chave quando não há `name:`; os dois têm de ser o nome do contrato
# (sem matriz no nome, sem expressões), para o check obrigatório da proteção da main bater.
# `permissions` explícito por job e nunca `write-all` (NFR de segurança).
set -euo pipefail
TEST_ID="UT-02"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_ci_yml
require_cmd jq
rel="${CI_YML#"$REPO_ROOT"/}"
json="$(yaml_to_json "$CI_YML")" || fail "cannot parse $rel as YAML"

errors=()

# Workflow e gatilhos do contrato (consumidos pela SPEC-0005 via workflow_run "CI").
name="$(jq -r '.name // ""' <<<"$json")"
[[ "$name" == "$CI_WORKFLOW_NAME" ]] || errors+=("workflow name is '$name', expected '$CI_WORKFLOW_NAME'")
# PyYAML (YAML 1.1) lê a chave `on` como booleano: aceita "on" ou "true".
triggers="$(jq -c '.on // .["true"] // {}' <<<"$json")"
for t in pull_request push workflow_dispatch; do
  jq -e --arg t "$t" 'if type == "object" then has($t) elif type == "array" then index($t) != null else . == $t end' \
    <<<"$triggers" >/dev/null || errors+=("trigger '$t' missing")
done
for t in pull_request push; do
  jq -e --arg t "$t" --arg b "$BASE_BRANCH" \
    'type != "object" or (.[$t] | type != "object") or (.[$t].branches == null) or (.[$t].branches | index($b) != null)' \
    <<<"$triggers" >/dev/null || errors+=("trigger '$t' does not include branch '$BASE_BRANCH'")
done

# Jobs: exatamente os checks obrigatórios.
mapfile -t keys < <(jq -r '.jobs // {} | keys[]' <<<"$json")
mapfile -t names < <(jq -r '.jobs // {} | to_entries[] | (.value.name // .key)' <<<"$json")
expected="$(printf '%s\n' "${REQUIRED_CHECKS[@]}" | sort)"
actual_keys="$(printf '%s\n' "${keys[@]}" | sed '/^$/d' | sort)"
actual_names="$(printf '%s\n' "${names[@]}" | sed '/^$/d' | sort)"
[[ "$actual_keys" == "$expected" ]] \
  || errors+=("job ids are [${actual_keys//$'\n'/ }], expected exactly [${expected//$'\n'/ }]")
[[ "$actual_names" == "$expected" ]] \
  || errors+=("job (check) names are [${actual_names//$'\n'/ }], expected exactly [${expected//$'\n'/ }]")

# permissions explícito em todo job; nunca write-all.
while IFS=$'\t' read -r job has perm; do
  [[ -n "$job" ]] || continue
  if [[ "$has" != "true" ]]; then
    errors+=("job '$job' does not declare 'permissions'")
  elif [[ "$perm" == '"write-all"' ]]; then
    errors+=("job '$job' uses 'permissions: write-all'")
  fi
done < <(jq -r '.jobs // {} | to_entries[] | [.key, (.value | has("permissions") | tostring), (.value.permissions | tojson)] | @tsv' <<<"$json")
top="$(jq -c '.permissions // empty' <<<"$json")"
[[ "$top" != '"write-all"' ]] || errors+=("workflow-level 'permissions: write-all'")

if ((${#errors[@]} > 0)); then
  for e in "${errors[@]}"; do printf '%s:%s   %s\n' "$SPEC_ID" "$TEST_ID" "$e" >&2; done
  fail "${#errors[@]} contract violation(s) in $rel"
fi
pass "workflow '$name': jobs = ${REQUIRED_CHECKS[*]}; every job declares permissions"
