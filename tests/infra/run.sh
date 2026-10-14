#!/usr/bin/env bash
# SPEC-0005 — runner dos testes de infraestrutura locais (só Docker, nada na nuvem).
# Uso: tests/infra/run.sh [--unit | ID ...]
#   sem argumentos: UT-01 UT-02 UT-03 IT-01 IT-02 IT-03 IT-04 IT-05 IT-06 IT-07
#   --unit:         UT-01 UT-02 UT-03 (rápidos, sem subir o ambiente local)
# CT-01 consulta o GHCR/Actions reais e é chamado à parte: tests/infra/run.sh CT-01
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ids=("$@")
if ((${#ids[@]} == 0)); then
  ids=(UT-01 UT-02 UT-03 IT-01 IT-02 IT-03 IT-04 IT-05 IT-06 IT-07)
elif [[ "${ids[0]}" == "--unit" ]]; then
  ids=(UT-01 UT-02 UT-03)
fi

failed=()
for id in "${ids[@]}"; do
  script="$dir/$id.sh"
  if [[ ! -f "$script" ]]; then
    echo "SPEC-0005:$id FAIL: no such test script ($script)" >&2
    failed+=("$id")
    continue
  fi
  echo "=== SPEC-0005:$id"
  if ! bash "$script"; then
    failed+=("$id")
  fi
done

echo "=== SPEC-0005 infra: $((${#ids[@]} - ${#failed[@]}))/${#ids[@]} passed"
if ((${#failed[@]} > 0)); then
  echo "SPEC-0005 failed: ${failed[*]}" >&2
  exit 1
fi
