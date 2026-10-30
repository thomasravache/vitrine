#!/usr/bin/env bash
# SPEC-0005 — runner dos testes de infraestrutura locais (só Docker, nada na nuvem).
# Uso: tests/infra/run.sh [--keep] [--unit | ID ...] [--keep]
#   sem argumentos: UT-01 UT-02 UT-03 IT-01 IT-02 IT-03 IT-04 IT-05 IT-06 IT-07
#   --unit:         UT-01 UT-02 UT-03 (rápidos, sem subir o ambiente local)
#   --keep:         não derruba a stack local ao sair (depuração); aceito em qualquer posição
# CT-01 consulta o GHCR/Actions reais e é chamado à parte: tests/infra/run.sh CT-01
# IDs sem prefixo são da SPEC-0005; os de outras specs levam o prefixo `SPEC-NNNN-` no nome do script.
# SPEC-0012 (chamam este runner aninhado; fora da lista padrão, rodar à parte e sem stack no ar):
#   tests/infra/run.sh SPEC-0012-UT-01 SPEC-0012-IT-01
#   (UT-01 exige nenhuma stack `vitrine` no ar; SPEC0012_TAKE_DOWN=1 deixa o teste derrubá-la)
# SPEC-0024 (simulador do ML; fora da lista padrão):
#   tests/infra/run.sh SPEC-0024-CH-01            (estático: Compose/Caddy padrão sem o simulador; não sobe a stack)
#   tests/infra/run.sh SPEC-0024-IT-06            (implanta o ambiente local com o perfil ml-simulator; portas 80/443)
#
# Limpeza (SPEC-0012): o runner devolve a stack local (projeto Compose `vitrine`, o do deploy/deploy.sh)
# como a encontrou. Estado registrado no início, ação ao sair (EXIT, também após SIGINT/SIGTERM):
#   - no ar (algum container rodando)         -> intocada;
#   - inexistente (nenhum container, nem volume -> `docker compose -p vitrine down -v --remove-orphans`
#     com o rótulo com.docker.compose.project)     (só se algo do projeto existir ao final);
#   - existente e parado (containers parados    -> para os containers do projeto e remove só os que o
#     e/ou volumes)                                 run criou; volumes e redes são preservados (sem -v).
#   - Docker indisponível                       -> nada é derrubado.
# `--keep` pula a limpeza. O código de saída do runner é preservado.
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose_project="vitrine"

keep=0
ids=()
for arg in "$@"; do
  if [[ "$arg" == "--keep" ]]; then
    keep=1
  else
    ids+=("$arg")
  fi
done
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

docker_ok() {
  command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1
}

# Containers do projeto (todos, inclusive parados), um ID por linha, ordenados.
project_containers() {
  docker ps -aq --no-trunc --filter "label=com.docker.compose.project=$compose_project" 2>/dev/null | sort
}

project_volumes() {
  docker volume ls -q --filter "label=com.docker.compose.project=$compose_project" 2>/dev/null
}

stack_is_up() {
  [[ -n "$(docker compose -p "$compose_project" ps -q 2>/dev/null)" ]]
}

project_exists() {
  [[ -n "$(project_containers)" || -n "$(project_volumes)" ]]
}

# Estado inicial: up | absent | stopped | unknown (Docker indisponível).
stack_state=unknown
containers_before=""
if docker_ok; then
  if stack_is_up; then
    stack_state=up
  elif project_exists; then
    stack_state=stopped
    containers_before="$(project_containers)"
  else
    stack_state=absent
  fi
fi

cleanup() {
  local rc=$?
  trap - EXIT INT TERM
  if ((keep == 1)); then
    [[ "$stack_state" == up || "$stack_state" == unknown ]] \
      || echo "=== SPEC-0005 infra: --keep: leaving the '$compose_project' project as this run left it"
  elif [[ "$stack_state" == absent ]] && docker_ok && project_exists; then
    echo "=== SPEC-0005 infra: taking down the '$compose_project' stack brought up by this run (use --keep to keep it)"
    docker compose -p "$compose_project" down -v --remove-orphans >/dev/null 2>&1 \
      || echo "SPEC-0005 WARN: could not take down the '$compose_project' stack" >&2
  elif [[ "$stack_state" == stopped ]] && docker_ok; then
    local created
    created="$(comm -13 <(printf '%s\n' "$containers_before") <(project_containers))"
    if stack_is_up || [[ -n "$created" ]]; then
      echo "=== SPEC-0005 infra: stopping the '$compose_project' project (it was stopped before this run; volumes kept)"
      docker compose -p "$compose_project" stop >/dev/null 2>&1 \
        || echo "SPEC-0005 WARN: could not stop the '$compose_project' project" >&2
      if [[ -n "$created" ]]; then
        # shellcheck disable=SC2086 # um ID por palavra
        docker rm -f $created >/dev/null 2>&1 \
          || echo "SPEC-0005 WARN: could not remove containers created by this run" >&2
      fi
    fi
  fi
  exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

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
