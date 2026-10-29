#!/usr/bin/env bash
# SPEC-0005 — runner dos testes de infraestrutura locais (só Docker, nada na nuvem).
# Uso: tests/infra/run.sh [--keep] [--unit | ID ...]
#   sem argumentos: UT-01 UT-02 UT-03 IT-01 IT-02 IT-03 IT-04 IT-05 IT-06 IT-07
#   --unit:         UT-01 UT-02 UT-03 (rápidos, sem subir o ambiente local)
#   --keep:         não derruba a stack local ao sair (depuração); aceito antes dos IDs
# CT-01 consulta o GHCR/Actions reais e é chamado à parte: tests/infra/run.sh CT-01
# IDs sem prefixo são da SPEC-0005; os de outras specs levam o prefixo `SPEC-NNNN-` no nome do script.
# SPEC-0012 (chamam este runner aninhado; fora da lista padrão, rodar à parte e sem stack no ar):
#   tests/infra/run.sh SPEC-0012-UT-01 SPEC-0012-IT-01
#   (UT-01 exige nenhuma stack `vitrine` no ar; SPEC0012_TAKE_DOWN=1 deixa o teste derrubá-la)
#
# Limpeza (SPEC-0012): ao sair, se a stack local (projeto Compose `vitrine`, o do deploy/deploy.sh)
# NÃO estava no ar quando o runner começou e está no ar ao final, o runner a derruba com
# `docker compose -p vitrine down -v --remove-orphans`. Uma stack que já estava no ar é preservada.
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose_project="vitrine"

keep=0
while (($# > 0)) && [[ "$1" == "--keep" ]]; do
  keep=1
  shift
done

ids=("$@")
if ((${#ids[@]} == 0)); then
  ids=(UT-01 UT-02 UT-03 IT-01 IT-02 IT-03 IT-04 IT-05 IT-06 IT-07)
elif [[ "${ids[0]}" == "--unit" ]]; then
  ids=(UT-01 UT-02 UT-03)
fi

# Tag de rastreabilidade: "SPEC-0012-UT-01" -> "SPEC-0012:UT-01"; sem prefixo -> "SPEC-0005:<ID>".
tag() {
  if [[ "$1" =~ ^(SPEC-[0-9]{4,})-(.+)$ ]]; then
    printf '%s:%s' "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}"
  else
    printf 'SPEC-0005:%s' "$1"
  fi
}

# Docker indisponível: nada a registrar nem a derrubar.
docker_ok() {
  command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1
}

stack_is_up() {
  [[ -n "$(docker compose -p "$compose_project" ps -q 2>/dev/null)" ]]
}

stack_was_up=1
if docker_ok && ! stack_is_up; then
  stack_was_up=0
fi

cleanup() {
  local rc=$?
  if ((keep == 0 && stack_was_up == 0)) && docker_ok && stack_is_up; then
    echo "=== SPEC-0005 infra: taking down the '$compose_project' stack brought up by this run (use --keep to keep it)"
    docker compose -p "$compose_project" down -v --remove-orphans >/dev/null 2>&1 \
      || echo "SPEC-0005 WARN: could not take down the '$compose_project' stack" >&2
  elif ((keep == 1)) && ((stack_was_up == 0)) && docker_ok && stack_is_up; then
    echo "=== SPEC-0005 infra: --keep: leaving the '$compose_project' stack running"
  fi
  exit "$rc"
}
trap cleanup EXIT

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

echo "=== SPEC-0005 infra: $((${#ids[@]} - ${#failed[@]}))/${#ids[@]} passed"
if ((${#failed[@]} > 0)); then
  echo "SPEC-0005 failed: ${failed[*]}" >&2
  exit 1
fi
