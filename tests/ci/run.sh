#!/usr/bin/env bash
# SPEC-0004 — runner dos testes do pipeline de CI (reaproveitado por specs posteriores, ex.: SPEC-0011).
# Uso: tests/ci/run.sh [--remote | --local-it | --all | ID ...]
#   sem argumentos: UT-01 UT-02 SPEC-0011-UT-01 (locais: leem .github/workflows/ci.yml)
#   --remote:       IT-01 IT-02 IT-03 IT-04 IT-05 CT-01 CT-02 (GitHub real; exigem as variáveis de cada script)
#   --local-it:     SPEC-0011-IT-01 (local e lento: copia o repositório, build da API + orval; exige dotnet e node)
#   --all:          todos
# IDs sem prefixo são da SPEC-0004; os de outras specs levam o prefixo `SPEC-NNNN-` no nome do script.
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
unit=(UT-01 UT-02 SPEC-0011-UT-01)
local_it=(SPEC-0011-IT-01)
remote=(IT-01 IT-02 IT-03 IT-04 IT-05 CT-01 CT-02)
ids=("$@")
if ((${#ids[@]} == 0)); then
  ids=("${unit[@]}")
elif [[ "${ids[0]}" == "--remote" ]]; then
  ids=("${remote[@]}")
elif [[ "${ids[0]}" == "--local-it" ]]; then
  ids=("${local_it[@]}")
elif [[ "${ids[0]}" == "--all" ]]; then
  ids=("${unit[@]}" "${local_it[@]}" "${remote[@]}")
fi

# Tag de rastreabilidade: "SPEC-0011-UT-01" -> "SPEC-0011:UT-01"; sem prefixo -> "SPEC-0004:<ID>".
tag() {
  if [[ "$1" =~ ^(SPEC-[0-9]{4,})-(.+)$ ]]; then
    printf '%s:%s' "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}"
  else
    printf 'SPEC-0004:%s' "$1"
  fi
}

failed=()
for id in "${ids[@]}"; do
  script="$dir/$id.sh"
  if [[ ! -f "$script" ]]; then
    echo "$(tag "$id") FAIL: no such test script ($script)" >&2
    failed+=("$id")
    continue
  fi
  echo "=== $(tag "$id")"
  if ! bash "$script"; then
    failed+=("$id")
  fi
done

echo "=== tests/ci: $((${#ids[@]} - ${#failed[@]}))/${#ids[@]} passed"
if ((${#failed[@]} > 0)); then
  echo "tests/ci failed: ${failed[*]}" >&2
  exit 1
fi
