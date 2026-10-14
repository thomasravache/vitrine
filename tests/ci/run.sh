#!/usr/bin/env bash
# SPEC-0004 — runner dos testes do pipeline de CI.
# Uso: tests/ci/run.sh [--remote | --all | ID ...]
#   sem argumentos: UT-01 UT-02 (locais: leem .github/workflows/ci.yml)
#   --remote:       IT-01 IT-02 IT-03 IT-04 IT-05 CT-01 CT-02 (GitHub real; exigem as variáveis de cada script)
#   --all:          todos
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
unit=(UT-01 UT-02)
remote=(IT-01 IT-02 IT-03 IT-04 IT-05 CT-01 CT-02)
ids=("$@")
if ((${#ids[@]} == 0)); then
  ids=("${unit[@]}")
elif [[ "${ids[0]}" == "--remote" ]]; then
  ids=("${remote[@]}")
elif [[ "${ids[0]}" == "--all" ]]; then
  ids=("${unit[@]}" "${remote[@]}")
fi

failed=()
for id in "${ids[@]}"; do
  script="$dir/$id.sh"
  if [[ ! -f "$script" ]]; then
    echo "SPEC-0004:$id FAIL: no such test script ($script)" >&2
    failed+=("$id")
    continue
  fi
  echo "=== SPEC-0004:$id"
  if ! bash "$script"; then
    failed+=("$id")
  fi
done

echo "=== SPEC-0004 ci: $((${#ids[@]} - ${#failed[@]}))/${#ids[@]} passed"
if ((${#failed[@]} > 0)); then
  echo "SPEC-0004 failed: ${failed[*]}" >&2
  exit 1
fi
