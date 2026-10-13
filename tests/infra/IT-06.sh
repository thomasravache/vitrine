#!/usr/bin/env bash
# SPEC-0005:IT-06 — Dado `infra/cloud-init.yaml` aplicado num container Ubuntu de teste (cloud-init
# em modo local), quando o script de verificação roda, então Docker está instalado,
# `PasswordAuthentication no` e `unattended-upgrades` habilitado.
#
# Local (Docker; baixa pacotes da internet). Sobe $UBUNTU_IMAGE, instala cloud-init e
# openssh-server, entrega o arquivo como user-data do datasource NoCloud e roda os estágios
# init --local, init, modules config e modules final. Falhas de módulos que dependem de systemd
# em execução são mostradas, mas o veredito vem só das três verificações do plano.
# Placeholders de template ${nome} (templatefile do OpenTofu) recebem valores de teste; nomes
# com "key" recebem uma chave pública ed25519 gerada para o teste.
set -euo pipefail
TEST_ID="IT-06"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

CLOUD_INIT="${CLOUD_INIT:-$REPO_ROOT/infra/cloud-init.yaml}"
require_file "$CLOUD_INIT" "infra/cloud-init.yaml does not exist"
require_docker
require_cmd ssh-keygen python3

work="$(mktemp -d)"
name="spec0005-it06-$$"
trap 'docker rm -f "$name" >/dev/null 2>&1 || true; rm -rf "$work"' EXIT

ssh-keygen -q -t ed25519 -N '' -C spec0005-it06 -f "$work/id" >/dev/null
if python3 -I - "$CLOUD_INIT" "$work/user-data" "$(cat "$work/id.pub")" <<'PY'
import re
import sys

src, dst, pub = sys.argv[1:4]
text = open(src, encoding="utf-8").read()
if "%{" in text:
    sys.exit("template directives %{...} are not supported by this test; render them first")
def repl(m):
    name = m.group(1)
    return pub if "key" in name.lower() else f"spec0005-test-{name}"
text = re.sub(r"(?<!\$)\$\{([A-Za-z_][A-Za-z0-9_]*)\}", repl, text)
text = text.replace("$${", "${")
open(dst, "w", encoding="utf-8").write(text)
PY
then :; else fail "could not render infra/cloud-init.yaml for the test"; fi
head -n1 "$work/user-data" | grep -q '^#cloud-config' || fail "infra/cloud-init.yaml must start with '#cloud-config'"
printf 'instance-id: %s\nlocal-hostname: spec0005-it06\n' "$name" >"$work/meta-data"

log "starting $UBUNTU_IMAGE and installing cloud-init (may take minutes)"
docker run -d --name "$name" -v "$work:/seed:ro" "$UBUNTU_IMAGE" sleep infinity >/dev/null
in_box() { docker exec "$name" bash -c "$1"; }

in_box 'export DEBIAN_FRONTEND=noninteractive; apt-get update -qq && apt-get install -y -qq cloud-init openssh-server >/dev/null' \
  || fail "could not install cloud-init in the test container"

log "cloud-init schema"
in_box 'cloud-init schema --config-file /seed/user-data' || fail "infra/cloud-init.yaml fails cloud-init schema validation"

log "applying cloud-init (NoCloud)"
in_box 'mkdir -p /var/lib/cloud/seed/nocloud && cp /seed/user-data /seed/meta-data /var/lib/cloud/seed/nocloud/ \
  && printf "datasource_list: [ NoCloud, None ]\n" >/etc/cloud/cloud.cfg.d/99_spec0005.cfg'
for stage in 'init --local' 'init' 'modules --mode=config' 'modules --mode=final'; do
  if ! in_box "cloud-init $stage" >"$work/stage.log" 2>&1; then
    log "cloud-init $stage reported errors (tail):"
    tail -n 5 "$work/stage.log" | sed "s/^/  /"
  fi
done

# 1. Docker instalado.
docker_version="$(in_box 'command -v docker >/dev/null && docker --version' 2>/dev/null || true)"
[[ -n "$docker_version" ]] || fail "Docker is not installed after cloud-init"
log "docker: $docker_version"

# 2. PasswordAuthentication no (configuração efetiva do sshd).
pwauth="$(in_box 'mkdir -p /run/sshd && ssh-keygen -A >/dev/null 2>&1; sshd -T 2>/dev/null | grep -i "^passwordauthentication "' || true)"
[[ "${pwauth,,}" == "passwordauthentication no" ]] || fail "sshd effective config: '${pwauth:-<unreadable>}', expected 'passwordauthentication no'"
log "sshd: $pwauth"

# 3. unattended-upgrades habilitado.
in_box 'dpkg -s unattended-upgrades >/dev/null 2>&1' || fail "unattended-upgrades is not installed"
periodic="$(in_box 'apt-config dump APT::Periodic::Unattended-Upgrade' 2>/dev/null || true)"
[[ "$periodic" =~ APT::Periodic::Unattended-Upgrade\ \"1\" ]] \
  || fail "APT::Periodic::Unattended-Upgrade is not \"1\" ('${periodic:-<unset>}')"
enabled="$(in_box 'systemctl is-enabled unattended-upgrades 2>/dev/null || true')"
if [[ "$enabled" != "enabled" ]]; then
  in_box 'test -L /etc/systemd/system/multi-user.target.wants/unattended-upgrades.service' \
    || fail "unattended-upgrades service is not enabled ('${enabled:-unknown}')"
fi
log "unattended-upgrades: enabled"

pass "cloud-init: Docker installed, PasswordAuthentication no, unattended-upgrades enabled"
