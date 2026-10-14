#!/usr/bin/env bash
# SPEC-0005:UT-03 — Dado `deploy.yml`, quando analisado (actionlint + verificação), então todo job
# de deploy tem a condição `vars.DEPLOY_ENABLED == 'true'`.
#
# Local (sem nuvem). Mapa 6.1: sem DEPLOY_ENABLED os jobs são pulados e nada é implantado —
# por isso TODO job do workflow precisa da condição como termo obrigatório (conjunção de nível
# superior), sem `||` de nível superior que a contorne.
# Usa actionlint local se instalado; senão a imagem oficial $ACTIONLINT_IMAGE.
set -euo pipefail
TEST_ID="UT-03"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

DEPLOY_YML="${DEPLOY_YML:-$REPO_ROOT/.github/workflows/deploy.yml}"
require_file "$DEPLOY_YML" ".github/workflows/deploy.yml does not exist"
require_cmd python3

log "actionlint"
if command -v actionlint >/dev/null 2>&1; then
  actionlint -no-color "$DEPLOY_YML" || fail "actionlint reported errors"
else
  require_docker
  rel="${DEPLOY_YML#"$REPO_ROOT"/}"
  docker run --rm -v "$REPO_ROOT:/repo:ro" -w /repo "$ACTIONLINT_IMAGE" -no-color "$rel" \
    || fail "actionlint reported errors"
fi

log "every job gated by vars.DEPLOY_ENABLED == 'true'"
problems="$(python3 -I - "$DEPLOY_YML" <<'PY'
import re
import sys
import yaml

doc = yaml.safe_load(open(sys.argv[1], encoding="utf-8")) or {}
jobs = doc.get("jobs") or {}
problems = []
if not jobs:
    problems.append("deploy.yml has no jobs")
gate = re.compile(r"^vars\.DEPLOY_ENABLED\s*==\s*'true'$")


def top_level_split(expr, op):
    """Split expr on op outside parentheses and quotes."""
    parts, depth, quote, start, i = [], 0, None, 0, 0
    while i < len(expr):
        c = expr[i]
        if quote:
            if c == quote:
                quote = None
        elif c == "'":
            quote = c
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
        elif depth == 0 and expr.startswith(op, i):
            parts.append(expr[start:i])
            start = i + len(op)
            i += len(op)
            continue
        i += 1
    parts.append(expr[start:])
    return [p.strip() for p in parts]


def strip_parens(e):
    e = e.strip()
    while e.startswith("(") and e.endswith(")"):
        inner = e[1:-1]
        depth = 0
        balanced = True
        for c in inner:
            depth += c == "("
            depth -= c == ")"
            if depth < 0:
                balanced = False
                break
        if not balanced:
            break
        e = inner.strip()
    return e


for name, job in jobs.items():
    cond = str((job or {}).get("if", "")).strip()
    cond = re.sub(r"^\$\{\{\s*(.*?)\s*\}\}$", r"\1", cond, flags=re.S)
    expr = strip_parens(cond)
    if len(top_level_split(expr, "||")) > 1:
        problems.append(f"job '{name}' condition can bypass the gate with a top-level '||' (if: {cond})")
        continue
    conjuncts = [strip_parens(c) for c in top_level_split(expr, "&&")]
    if not any(gate.match(c) for c in conjuncts):
        problems.append(f"job '{name}' lacks if: vars.DEPLOY_ENABLED == 'true' as a required condition (if: {cond or '<none>'})")
print("\n".join(problems))
PY
)" || fail "could not parse $DEPLOY_YML"
[[ -z "$problems" ]] || fail "${problems//$'\n'/; }"

pass "actionlint clean; every job requires vars.DEPLOY_ENABLED == 'true'"
