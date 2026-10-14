#!/usr/bin/env bash
# SPEC-0005 — implanta a imagem vitrine-api:<tag> com Docker Compose (servidor ou ambiente local prod-like).
#
# Uso: deploy/deploy.sh <tag> [--env-file <arquivo>]
#   1. grava deploy/.env (permissão 600) a partir de --env-file (ou do deploy/.env atual) + IMAGE_TAG=<tag>;
#   2. `docker compose pull` da imagem só se ela não existir localmente (imagem local é usada como está);
#   3. `docker compose run --rm migrate` (modo --migrate da imagem); falhou: sai 1 SEM trocar a versão;
#   4. `docker compose up -d` com a nova tag e espera GET https://<SITE_ADDRESS>/health/live = 200;
#      falhou: volta para a tag anterior e sai 1;
#   5. registra a tag em deploy/.state/current e a anterior em deploy/.state/previous.
#
# Variáveis lidas do arquivo de ambiente (além das do Compose):
#   SITE_ADDRESS            host do health check (obrigatória)
#   DEPLOY_HEALTH_INSECURE  true = aceita certificado não público (CA interna do Caddy no ambiente local)
#   DEPLOY_HEALTH_TIMEOUT   segundos de espera pelo health (padrão e máximo do contrato: 120)
#   DEPLOY_COMPOSE_BASE     arquivo base do Compose (padrão: deploy/compose.yaml; no servidor, o do compose-bundle)
# Nunca imprime o conteúdo do .env (segredos).
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STATE_DIR="$DEPLOY_DIR/.state"
DOTENV="$DEPLOY_DIR/.env"

say() { printf 'deploy: %s\n' "$*"; }
die() { printf 'deploy: ERRO: %s\n' "$*" >&2; exit "${2:-1}"; }

usage() { die "uso: deploy/deploy.sh <tag> [--env-file <arquivo>]" 2; }

tag=""
env_file=""
while (($# > 0)); do
  case "$1" in
    --env-file)
      (($# >= 2)) || usage
      env_file="$2"
      shift 2
      ;;
    --env-file=*)
      env_file="${1#--env-file=}"
      shift
      ;;
    -h | --help) usage ;;
    -*) usage ;;
    *)
      [[ -z "$tag" ]] || usage
      tag="$1"
      shift
      ;;
  esac
done
[[ -n "$tag" ]] || usage
[[ "$tag" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]] || die "tag inválida: $tag" 2

# --- .env (600) ---------------------------------------------------------------
# Conteúdo base: o --env-file informado; senão o deploy/.env atual (sem IMAGE_TAG).
base_env="$(mktemp)"
trap 'rm -f "$base_env"' EXIT
chmod 600 "$base_env"
if [[ -n "$env_file" ]]; then
  [[ -f "$env_file" ]] || die "arquivo de ambiente não encontrado: $env_file" 2
  grep -v '^[[:space:]]*IMAGE_TAG=' "$env_file" >"$base_env" || true
elif [[ -f "$DOTENV" ]]; then
  grep -v '^[[:space:]]*IMAGE_TAG=' "$DOTENV" >"$base_env" || true
else
  die "sem --env-file e sem deploy/.env existente" 2
fi

# Grava deploy/.env com a tag informada (atômico, sempre 600).
write_dotenv() {
  local t="$1" tmp
  tmp="$(umask 077 && mktemp "$DEPLOY_DIR/.env.XXXXXX")"
  {
    cat "$base_env"
    printf '\nIMAGE_TAG=%s\n' "$t"
  } >"$tmp"
  chmod 600 "$tmp"
  mv -f "$tmp" "$DOTENV"
}

# Lê uma variável do arquivo base sem executá-lo (última ocorrência vence, como no Compose).
env_value() {
  sed -nE "s/^[[:space:]]*$1=(.*)$/\1/p" "$base_env" | tail -n1 | sed -E "s/^[\"'](.*)[\"']$/\1/"
}

site="$(env_value SITE_ADDRESS)"
site="${site#https://}"
site="${site%%/*}"
[[ -n "$site" ]] || die "SITE_ADDRESS não definido no arquivo de ambiente" 2
health_insecure="$(env_value DEPLOY_HEALTH_INSECURE)"
health_timeout="$(env_value DEPLOY_HEALTH_TIMEOUT)"
health_timeout="${health_timeout:-120}"
[[ "$health_timeout" =~ ^[0-9]+$ ]] && ((health_timeout <= 120)) || health_timeout=120
compose_base="$(env_value DEPLOY_COMPOSE_BASE)"
compose_base="${compose_base:-$DEPLOY_DIR/compose.yaml}"
[[ "$compose_base" == /* ]] || compose_base="$DEPLOY_DIR/$compose_base"
[[ -f "$compose_base" ]] || die "arquivo base do Compose não encontrado: $compose_base" 2

compose() {
  docker compose --project-directory "$DEPLOY_DIR" --env-file "$DOTENV" \
    -f "$compose_base" -f "$DEPLOY_DIR/compose.override.yaml" "$@"
}

# --- Estado ---------------------------------------------------------------------
mkdir -p "$STATE_DIR"
current=""
[[ -f "$STATE_DIR/current" ]] && current="$(tr -d '[:space:]' <"$STATE_DIR/current")"

health_ok() {
  local args=(-fsS -o /dev/null --max-time 5 --resolve "$site:443:127.0.0.1")
  [[ "$health_insecure" == "true" ]] && args+=(-k)
  curl "${args[@]}" "https://$site/health/live" 2>/dev/null
}

# Espera o health (≤ health_timeout s). Desiste antes se o container da API entrar em loop de reinício.
wait_health() {
  local deadline=$((SECONDS + health_timeout)) id restarts
  while ((SECONDS < deadline)); do
    health_ok && return 0
    id="$(compose ps -q api 2>/dev/null | head -n1)"
    if [[ -n "$id" ]]; then
      restarts="$(docker inspect -f '{{.RestartCount}}' "$id" 2>/dev/null || echo 0)"
      if ((restarts >= 3)); then
        say "a API reiniciou $restarts vezes; health check abandonado"
        return 1
      fi
    fi
    sleep 1
  done
  return 1
}

say "implantando ${tag} em https://${site} (atual: ${current:-nenhuma})"
write_dotenv "$tag"

# Volta o .env para a versão em execução (ou remove a tag) quando a troca não acontece.
restore_dotenv() {
  if [[ -n "$current" ]]; then write_dotenv "$current"; fi
}

# --- Imagem ---------------------------------------------------------------------
# Só baixa do registry as imagens que não existem localmente (uma tag nunca muda de imagem).
say "docker compose pull (somente imagens ausentes)"
compose pull --quiet --policy missing || { restore_dotenv; die "falha ao baixar as imagens da tag ${tag}"; }

# --- Migrações (antes da troca) ---------------------------------------------------
say "migrações: docker compose run --rm migrate"
if ! compose run --rm --quiet-pull migrate; then
  restore_dotenv
  die "migração falhou; versão ${current:-nenhuma} mantida"
fi

# --- Troca de versão + health ---------------------------------------------------
t0=$SECONDS
compose up -d --remove-orphans
if wait_health; then
  printf '%s\n' "$tag" >"$STATE_DIR/current.tmp" && mv -f "$STATE_DIR/current.tmp" "$STATE_DIR/current"
  if [[ -n "$current" && "$current" != "$tag" ]]; then
    printf '%s\n' "$current" >"$STATE_DIR/previous.tmp" && mv -f "$STATE_DIR/previous.tmp" "$STATE_DIR/previous"
  fi
  say "OK: ${tag} no ar (health 200 em $((SECONDS - t0)) s)"
  exit 0
fi

say "health check de ${tag} falhou (limite: ${health_timeout} s)"
if [[ -n "$current" && "$current" != "$tag" ]]; then
  say "voltando para ${current}"
  write_dotenv "$current"
  compose up -d --remove-orphans
  if wait_health; then
    die "deploy de ${tag} revertido; ${current} no ar"
  fi
  die "deploy de ${tag} revertido, mas ${current} também não respondeu ao health check"
fi
die "deploy de ${tag} falhou e não há versão anterior para voltar"
