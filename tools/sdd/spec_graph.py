#!/usr/bin/env python3
"""Deterministic tooling for the sdd-management skill (stdlib only). Run with --help."""
from __future__ import annotations

import argparse
import datetime as dt
import difflib
import heapq
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unicodedata
from collections import Counter, defaultdict, deque
from pathlib import Path

SDD_TOOL_VERSION = "2026.10.13"
SKILL_DIR = Path(__file__).resolve().parent.parent
TEMPLATES_DIR = SKILL_DIR / "references"
ASSETS_DIR = SKILL_DIR / "assets"
VENDORED_TOOL = "tools/sdd/spec_graph.py"

SPEC_ID_RE = re.compile(r"^SPEC-\d{4,}$")
ADR_ID_RE = re.compile(r"^ADR-\d{4,}$")
SPEC_REF_RE = re.compile(r"\bSPEC-\d{4,}\b")
CONTRACT_REF_RE = re.compile(r"^(SPEC-\d{4,})(?:@v?(\d+))?$")
TEST_ID_RE = re.compile(r"\b(?:CH|UT|IT|CT|E2E)-\d{2,}\b")
TEST_DEF_RE = re.compile(r"\*\*((?:CH|UT|IT|CT|E2E)-\d{2,})\*\*")
TEST_TAG_RE = re.compile(r"\b(SPEC-\d{4,}):((?:CH|UT|IT|CT|E2E)-\d{2,})\b")
PLACEHOLDER_RE = re.compile(r"\{\{[^{}]*\}\}")
COMMENT_RE = re.compile(r"<!--.*?-->", re.DOTALL)
CONVENTIONAL_RE = re.compile(r"^(\w+)(?:\([^)]*\))?!?:")
WILDCARD_RE = re.compile(r"[*?]")
CHECKBOX_RE = re.compile(r"^\s*[-*] \[( |x|X)\]", re.MULTILINE)
UNCHECKED_RE = re.compile(r"^\s*[-*] \[ \]", re.MULTILINE)

STATUSES = ("proposed", "approved", "in-progress", "implemented", "deprecated")
OPEN = {"proposed", "approved", "in-progress"}
APPROVED_PLUS = {"approved", "in-progress", "implemented"}
DONE = {"implemented", "deprecated"}
STATUS_RANK = {"in-progress": 0, "approved": 1, "proposed": 2}
TIERS = ("lite", "full", "epic")
TYPES = ("feature", "fix", "refactor", "migration", "foundation")
TYPES_BY_TIER = {
    "lite": {"feature", "fix", "refactor"},
    "full": set(TYPES),
    "epic": {"feature", "migration", "foundation"},
}
SIZES = ("S", "M", "L")
ADR_STATUSES = ("proposed", "accepted", "rejected", "deprecated", "superseded")
ADR_ORIGINS = ("decision", "user", "as-is")
GATE_ROWS = ("G0", "G1", "G2", "G3", "G4", "G5", "H2", "G6", "G7")
DELIVERY_SUBSECTIONS = ("O que foi entregue", "Como foi feito", "Verificação", "Definição de Pronto", "Deploy", "Pendências")
IMPEDIMENT_TYPES = ("spec", "decisão", "trabalho", "externo", "falha")
IMPEDIMENT_COLUMNS = ("ID", "Aberto em", "Fase/Gate", "Tipo", "Descrição", "Tentativas", "Responsável", "Resolução", "Fechado em")
IMP_ID_RE = re.compile(r"^IMP-(\d{2,})$")
ADR_REF_RE = re.compile(r"\bADR-\d{4,}\b")
CELL_SPLIT_RE = re.compile(r"(?<!\\)\|")
EXTERNAL_RE = re.compile(r"^([a-z][a-z0-9-]*):(\S+)$")
COMMIT_TYPES = ("build", "chore", "ci", "docs", "feat", "fix", "perf", "refactor", "revert", "style", "test")
CONVENTIONAL_BY_TYPE = {"feature": "feat", "fix": "fix", "refactor": "refactor", "migration": "refactor", "foundation": "build"}
CHANGELOG_BY_TYPE = {"feature": "Added", "fix": "Fixed", "refactor": "Changed", "migration": "Changed", "foundation": "Added"}
PR_TITLE_DEFAULT = "{conv}: {title} [{id}]"
PR_TITLE_FIELDS = {"conv", "type", "title", "id", "scope"}
MERGE_GATES = ("G0", "G1", "G2", "G3", "G4")
# Merge messages that change-request platforms generate (GitHub, GitLab, Azure DevOps, Bitbucket); override with pr_merge_pattern.
PR_MERGE_PATTERN_DEFAULT = r"Merge pull request #?\d+|See merge request|Merged PR \d+|Merge pull request .* from|\(#\d+\)$|\(!\d+\)$"
SQUASH_PATTERN = r"\(#\d+\)$|\(!\d+\)$"
CONTROL_PATHS = ["tools/sdd/", "docs/specs/sdd-config.yml", "docs/adr/", ".githooks/"]

# Pillars of an end-to-end delivery. Neutral: each is a question the project answers with an ADR (or a recorded waiver).
PILLAR_CATALOG_VERSION = "2026.09"
PROFILES = ("minimo", "padrao", "rigoroso")
PILLARS = {
    "arquitetura": ("Arquitetura e fronteiras", "Como o sistema é dividido e quais dependências são proibidas?", "minimo"),
    "testes": ("Estratégia de testes", "Quais níveis de teste, onde rodam e o que é obrigatório?", "minimo"),
    "qualidade-codigo": ("Qualidade de código", "Formatação, lint, tipos e análise estática rodam automaticamente?", "minimo"),
    "entrega": ("Entrega contínua e ambientes", "Como o código chega a produção, e como volta atrás?", "minimo"),
    "fluxo-mudanca": ("Fluxo de mudança", "Como mudanças entram na branch principal e quem revisa?", "minimo"),
    "segredos-dados": ("Segredos e dados sensíveis", "Onde ficam credenciais e como dados pessoais são protegidos?", "minimo"),
    "seguranca-acesso": ("Identidade e acesso", "Como usuários e serviços se autenticam e o que cada um pode fazer?", "padrao"),
    "dependencias": ("Dependências e ciclo de vida da stack", "Como vulnerabilidades, atualizações e fim de suporte são tratados?", "padrao"),
    "observabilidade": ("Observabilidade", "Como se sabe que o sistema está saudável ou quebrado?", "padrao"),
    "resiliencia": ("Resiliência e recuperação", "Backups, tempo de recuperação e tolerância a falhas?", "padrao"),
    "dados-migracoes": ("Dados e migrações", "Como o schema evolui sem quebrar versões em uso?", "padrao"),
    "custo": ("Custo", "Quanto custa operar, com que premissas, e como é acompanhado?", "padrao"),
    "desempenho-escala": ("Desempenho e escala", "Quais metas de carga e latência, e como são verificadas?", "rigoroso"),
    "acessibilidade-conformidade": ("Acessibilidade e conformidade", "Quais normas (acessibilidade, legais, do setor) se aplicam?", "rigoroso"),
}
NA_RECURRENT_THRESHOLD = 3
JOURNEY_TAG_RE = re.compile(r"\[jornada:\s*([\w-]+)\]", re.IGNORECASE)
PHASES = ("DISCOVER", "SPEC (G0)", "APPROVAL (H1)", "PLAN (waves)", "EXECUTE (G1–G4)",
          "INTEGRATE (G5 + H2)", "DEPLOY (G6)", "CLOSE (G7)")
H1_QUESTION = ("Os contratos e os cenários de teste estão adequados e seguem o padrão atual? "
               "Digite 'Aprovado' para prosseguir ou solicite ajustes.")
H2_QUESTION = ("Onda N integrada: build, testes (unitários, integração, contrato, E2E), arquitetura e CI passando (G5). "
               "Deseja revisar antes de prosseguir? Digite 'Aprovado' para seguir ou solicite ajustes.")
GATE_STATUSES = ("N/A", "FAIL", "PENDING", "PASS")
LEGACY_DIRS = ("proposed", "approved", "implemented", "deprecated")
RED_TYPES = {"test"}
GREEN_TYPES = {"feat", "fix", "refactor", "perf"}

DEFAULT_TEST_PATHS = [
    "**/test/**", "**/tests/**", "**/__tests__/**", "**/e2e/**", "**/playwright/**", "**/cypress/**",
    "**/*Tests/**", "**/*Test/**", "**/*.test.*", "**/*.spec.*", "**/*.e2e.*",
    "**/*Tests.*", "**/*Test.*", "**/test_*.py", "**/*_test.*",
]
DEFAULT_SCOPE_EXEMPT = ["docs/specs/**", "docs/adr/**"]
DEFAULT_ROOT_DOCS = ["README.md", "AGENTS.md", "CLAUDE.md", "GEMINI.md"]
DEFAULT_MANIFESTS = [
    "**/package.json", "**/*.csproj", "**/*.fsproj", "**/Directory.Packages.props", "**/packages.config",
    "**/requirements*.txt", "**/pyproject.toml", "**/Pipfile", "**/go.mod", "**/pom.xml",
    "**/build.gradle", "**/build.gradle.kts", "**/Gemfile", "**/Cargo.toml", "**/composer.json",
    "**/pubspec.yaml", "**/Package.swift", "**/Podfile",
]

# (heading keyword, must have content before approval)
REQUIRED_SECTIONS = {
    "full": [
        ("Visão Geral", True), ("Motivação", True), ("Dependências", True),
        ("Decisão Arquitetural", True), ("Requisitos Não-Funcionais", True),
        ("Contrato", True), ("Plano de Testes", True), ("Plano de Rollout", True),
        ("Questões em Aberto", True), ("Aprovação", False), ("Checklist", False),
        ("Registro de Gates", False), ("Registro de Impedimentos", False),
        ("Relatório de Entrega", False), ("Emendas", False),
    ],
    "lite": [
        ("Problema", True), ("Causa Raiz", True), ("Mudança Proposta", True),
        ("Plano de Testes", True), ("Questões em Aberto", True), ("Aprovação", False),
        ("Checklist", False), ("Registro de Gates", False), ("Registro de Impedimentos", False),
        ("Relatório de Entrega", False), ("Emendas", False),
    ],
    "epic": [
        ("Visão", True), ("Escopo", True), ("Arquitetura Alvo", True), ("Decomposição", True),
        ("Estratégia de Entrega", True), ("Riscos", True), ("Critérios de Aceite", True),
        ("Questões em Aberto", True), ("Aprovação", False), ("Registro de Impedimentos", False),
        ("Relatório de Entrega", False), ("Emendas", False),
    ],
}


# ---------------------------------------------------------------- text utils

def norm(text: str) -> str:
    text = unicodedata.normalize("NFKD", text)
    return "".join(c for c in text if not unicodedata.combining(c)).lower()


def slugify(text: str, limit: int = 60) -> str:
    text = unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode()
    text = re.sub(r"[^a-zA-Z0-9]+", "-", text).strip("-").lower()
    return text[:limit].rstrip("-") or "spec"


def today() -> str:
    return dt.date.today().isoformat()


def valid_date(value: str) -> bool:
    try:
        dt.date.fromisoformat(value)
        return True
    except (TypeError, ValueError):
        return False


def parse_scalar(value: str):
    value = value.strip()
    if not value:
        return ""
    if value[0] == '"':
        m = re.match(r'^"((?:[^"\\]|\\.)*)"', value)
        return json.loads('"' + m.group(1) + '"') if m else value[1:]
    if value[0] == "'":
        m = re.match(r"^'((?:[^']|'')*)'", value)
        return m.group(1).replace("''", "'") if m else value[1:]
    value = re.split(r"\s+#", value, maxsplit=1)[0].strip()
    if value.startswith("[") and value.endswith("]"):
        inner = value[1:-1].strip()
        return [parse_scalar(item) for item in inner.split(",") if item.strip()]
    return value


def parse_yaml_subset(text: str) -> dict:
    """YAML subset: `key: value`, inline `[a, b]`, `- item` block lists and indented nested mappings."""
    rows = [(len(raw) - len(raw.lstrip(" ")), raw.strip()) for raw in text.splitlines()
            if raw.strip() and not raw.lstrip().startswith("#")]
    if not rows:
        return {}
    value, _ = _parse_block(rows, 0, rows[0][0])
    return value if isinstance(value, dict) else {}


def _parse_block(rows, i: int, indent: int):
    if rows[i][1].startswith("- ") or rows[i][1] == "-":
        items = []
        while i < len(rows) and rows[i][0] == indent and rows[i][1].startswith("-"):
            items.append(parse_scalar(rows[i][1][1:]))
            i += 1
        return items, i
    data = {}
    while i < len(rows) and rows[i][0] >= indent:
        if rows[i][0] > indent:
            i += 1
            continue
        pair = re.match(r"^([A-Za-z_][\w-]*)\s*:(?:\s+(.*))?$", rows[i][1])
        i += 1
        if not pair:
            continue
        key, rest = pair.group(1), (pair.group(2) or "").strip()
        if rest and not rest.startswith("#"):
            data[key] = parse_scalar(rest)
        elif i < len(rows) and (rows[i][0] > indent or (rows[i][0] == indent and rows[i][1].startswith("-"))):
            data[key], i = _parse_block(rows, i, rows[i][0])
        else:
            data[key] = ""
    return data, i


def fmt_value(value) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, list):
        return "[" + ", ".join(fmt_value(v) for v in value) + "]"
    value = str(value)
    if value == "":
        return ""
    if (re.search(r":\s|\s#|^[\[\]{}&*!|>'\"%@`,-]|^\s|\s$", value)
            or value.lower() in ("true", "false", "null", "yes", "no", "~")):
        return json.dumps(value, ensure_ascii=False)
    return value


def split_frontmatter(text: str):
    lines = text.split("\n")
    if not lines or lines[0].strip() != "---":
        return None, text
    for i in range(1, len(lines)):
        if lines[i].strip() == "---":
            return "\n".join(lines[1:i]), "\n".join(lines[i + 1:])
    return None, text


def set_meta(text: str, key: str, value) -> str:
    lines = text.split("\n")
    if not lines or lines[0].strip() != "---":
        raise ValueError("arquivo sem frontmatter")
    end = next((i for i in range(1, len(lines)) if lines[i].strip() == "---"), None)
    if end is None:
        raise ValueError("frontmatter sem '---' de fechamento")
    rendered = f"{key}: {fmt_value(value)}".rstrip()
    for i in range(1, end):
        if re.match(rf"^{re.escape(key)}\s*:", lines[i]):
            j = i + 1
            while j < end and re.match(r"^\s*-\s+", lines[j]):
                j += 1
            lines[i:j] = [rendered]
            return "\n".join(lines)
    lines.insert(end, rendered)
    return "\n".join(lines)


def parse_sections(body: str):
    sections, heading, buf, fence = [], None, [], False
    for line in body.split("\n"):
        if line.lstrip().startswith("```"):
            fence = not fence
        if not fence and line.startswith("## "):
            if heading is not None:
                sections.append((heading, "\n".join(buf)))
            heading, buf = line[3:].strip(), []
        elif heading is not None:
            buf.append(line)
    if heading is not None:
        sections.append((heading, "\n".join(buf)))
    return sections


def parse_tables(content: str):
    raw_tables, cur = [], []
    for line in content.split("\n"):
        s = line.strip()
        if s.startswith("|") and s.endswith("|") and len(s) > 1:
            cur.append([c.strip() for c in CELL_SPLIT_RE.split(s[1:-1])])
        elif cur:
            raw_tables.append(cur)
            cur = []
    if cur:
        raw_tables.append(cur)
    tables = []
    for t in raw_tables:
        if len(t) >= 2 and all(re.fullmatch(r":?-{2,}:?", c.replace(" ", "")) for c in t[1] if c):
            tables.append((t[0], t[2:]))
    return tables


def test_sort_key(tid: str):
    prefix, num = tid.split("-")
    return (["CH", "UT", "IT", "CT", "E2E"].index(prefix), int(num))


def parse_subsections(content: str):
    subs, heading, buf = [], None, []
    for line in content.split("\n"):
        if line.startswith("### "):
            if heading is not None:
                subs.append((heading, "\n".join(buf)))
            heading, buf = line[4:].strip(), []
        elif heading is not None:
            buf.append(line)
    if heading is not None:
        subs.append((heading, "\n".join(buf)))
    return subs


def find_sub(subs, keyword: str):
    key = norm(keyword)
    return next((content for heading, content in subs if key in norm(heading)), None)


def md_cell(text: str) -> str:
    return str(text).replace("|", "\\|").replace("\n", " ")


# ---------------------------------------------------------------- globs

def glob_regex(pattern: str):
    pattern = pattern.strip()
    pattern = pattern.removeprefix("./")
    if not WILDCARD_RE.search(pattern):
        return re.compile("^" + re.escape(pattern.rstrip("/")) + "(?:/.*)?$")
    out, i = [], 0
    while i < len(pattern):
        if pattern.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif pattern.startswith("**", i):
            out.append(".*")
            i += 2
        elif pattern[i] == "*":
            out.append("[^/]*")
            i += 1
        elif pattern[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(pattern[i]))
            i += 1
    return re.compile("^" + "".join(out) + "$")


def matches_any(path: str, patterns) -> bool:
    return any(glob_regex(p).match(path) for p in patterns)


def literal_prefix(pattern: str) -> str:
    m = WILDCARD_RE.search(pattern)
    return pattern[:m.start()] if m else pattern


def patterns_overlap(a: str, b: str) -> bool:
    """Conservative: may report overlap that doesn't exist, never misses a real one."""
    wa, wb = bool(WILDCARD_RE.search(a)), bool(WILDCARD_RE.search(b))
    if not wa and not wb:
        a2, b2 = a.rstrip("/"), b.rstrip("/")
        return a2 == b2 or a2.startswith(b2 + "/") or b2.startswith(a2 + "/")
    if wa != wb:
        wild, lit = (a, b) if wa else (b, a)
        lit = lit.rstrip("/")
        prefix = literal_prefix(wild)
        return (bool(glob_regex(wild).match(lit))
                or prefix.startswith(lit + "/")
                or ("**" in wild and lit.startswith(prefix)))
    pa, pb = literal_prefix(a), literal_prefix(b)
    return pa.startswith(pb) or pb.startswith(pa)


# ---------------------------------------------------------------- model

class Doc:
    def __init__(self, path: Path, text: str):
        self.path = path
        self.text = text
        fm, self.body = split_frontmatter(text)
        self.has_frontmatter = fm is not None
        self.meta = parse_yaml_subset(fm) if fm is not None else {}
        self._sections = None

    def scalar(self, key: str) -> str:
        v = self.meta.get(key, "")
        return v.strip() if isinstance(v, str) else ""

    def list(self, key: str):
        v = self.meta.get(key)
        if isinstance(v, list):
            return [x.strip() for x in v if isinstance(x, str) and x.strip()]
        if isinstance(v, str) and v.strip():
            return [v.strip()]
        return []

    @property
    def id(self) -> str:
        return self.scalar("id")

    @property
    def title(self) -> str:
        return self.scalar("title")

    @property
    def status(self) -> str:
        return self.scalar("status").lower()

    @property
    def tier(self) -> str:
        return self.scalar("tier").lower()

    @property
    def type(self) -> str:
        return self.scalar("type").lower()

    @property
    def legacy(self) -> bool:
        return self.scalar("legacy").lower() in ("true", "yes", "1")

    def section(self, keyword: str):
        if self._sections is None:
            self._sections = parse_sections(self.body)
        key = norm(keyword)
        for heading, content in self._sections:
            if key in norm(heading):
                return content
        return None


def contract_refs(doc: Doc):
    refs = []
    for raw in doc.list("consumes_contract"):
        m = CONTRACT_REF_RE.match(raw)
        if m:
            refs.append((m.group(1), int(m.group(2)) if m.group(2) else None, raw))
        else:
            refs.append((None, None, raw))
    return refs


def contract_version(doc: Doc) -> int:
    try:
        return int(doc.scalar("contract_version") or 1)
    except ValueError:
        return 1


def plan_test_ids(doc: Doc):
    plan = COMMENT_RE.sub("", doc.section("Plano de Testes") or "")
    return sorted(set(TEST_ID_RE.findall(plan)), key=test_sort_key)


PRIVATE_BANNER = "MODO PRIVADO"
# Comandos que gravam registros: em modo privado, cada um termina com um commit no git do spec_root.
RECORD_COMMANDS = {"new", "new-adr", "set", "gate", "impede", "resolve", "index", "upgrade", "living-docs",
                   "whats-new"}


def normalize_remote(url: str) -> str:
    """Chave do registro do modo privado: host/caminho do remoto, sem protocolo, usuário, porta e .git."""
    url = url.strip()
    m = (re.match(r"^[\w+.-]+://(?:[^@/]+@)?([^/:]+)(?::\d+)?/(.+)$", url)
         or re.match(r"^(?:[^@/]+@)?([^/:]+):(?!//)(.+)$", url))
    if not m:
        return url.rstrip("/")
    path = m.group(2).strip("/")
    path = path.removesuffix(".git")
    return f"{m.group(1).lower()}/{path}"


def registry_path() -> Path:
    if os.environ.get("SDD_CONFIG_HOME"):
        return Path(os.environ["SDD_CONFIG_HOME"]) / "projects.json"
    base = Path(os.environ["XDG_CONFIG_HOME"]) if os.environ.get("XDG_CONFIG_HOME") else Path.home() / ".config"
    return base / "sdd" / "projects.json"


def load_registry() -> dict:
    path = registry_path()
    if not path.exists():
        return {"projects": {}}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (ValueError, OSError) as e:
        die(f"registro inválido: {path}: {e}")
    if not isinstance(data, dict) or not isinstance(data.get("projects", {}), dict):
        die(f"registro inválido: {path}: esperado um objeto com \"projects\"")
    data.setdefault("projects", {})
    return data


def save_registry(data: dict):
    path = registry_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def repo_keys(root: Path):
    """Chaves possíveis do repositório no registro: remoto normalizado e caminho da worktree principal."""
    keys = []
    code, out, _ = run_git(root, "remote", "get-url", "origin")
    if code == 0 and out.strip():
        keys.append(normalize_remote(out))
    code, out, _ = run_git(root, "rev-parse", "--git-common-dir")
    main = (root / out.strip()).resolve().parent if code == 0 and out.strip() else root.resolve()
    keys.append(str(main))
    return keys


def resolve_mode(root: Path):
    """Entrada do registro do usuário para o repositório em root, ou None (modo completo)."""
    if not registry_path().exists():
        return None
    projects = load_registry()["projects"]
    for key in repo_keys(root):
        entry = projects.get(key)
        if isinstance(entry, dict) and entry.get("mode") == "private" and entry.get("spec_root"):
            return {"key": key, "mode": "private", "spec_root": Path(entry["spec_root"]).expanduser(),
                    "sync": entry.get("sync") or "none"}
    return None


class Project:
    def __init__(self, root: Path):
        self.root = root
        mode = resolve_mode(root)
        self.private = mode is not None
        self.mode_key = mode["key"] if mode else None
        self.spec_root = mode["spec_root"] if mode else None
        self.sync = mode["sync"] if mode else "none"
        base = self.spec_root if mode else root / "docs"
        self.specs_dir = base / "specs"
        self.adr_dir = base / "adr"
        self.config_path = (self.spec_root if mode else self.specs_dir) / "sdd-config.yml"
        self.config = self._load_config()
        self.specs = {}
        self.adrs = {}
        self.load_issues = []
        self._load()

    def _load_config(self) -> dict:
        path = self.config_path
        return parse_yaml_subset(path.read_text(encoding="utf-8")) if path.exists() else {}

    def cfg(self, key: str, default: str = "") -> str:
        v = self.config.get(key)
        return v.strip() if isinstance(v, str) and v.strip() else default

    def cfg_list(self, key: str, default):
        v = self.config.get(key)
        if isinstance(v, list) and v:
            return [x for x in v if x]
        if isinstance(v, str) and v.strip():
            return [v.strip()]
        return list(default)

    def cfg_map(self, key: str) -> dict:
        v = self.config.get(key)
        return v if isinstance(v, dict) else {}

    def tracker(self) -> dict:
        t = self.cfg_map("tracker")
        provider = (t.get("provider") or "none").strip().lower() if isinstance(t.get("provider"), str) else "none"
        labels = t.get("labels") if isinstance(t.get("labels"), list) else ["sdd"]
        return {
            "provider": provider or "none",
            "project": t.get("project", "") if isinstance(t.get("project"), str) else "",
            "mode": t.get("mode", "push") if isinstance(t.get("mode"), str) else "push",
            "statuses": t.get("statuses") if isinstance(t.get("statuses"), dict) else {},
            "issue_types": t.get("issue_types") if isinstance(t.get("issue_types"), dict) else {},
            "blocked_label": t.get("blocked_label") or "sdd-bloqueado",
            "labels": labels,
        }

    def external_ref(self, doc: Doc):
        provider = self.tracker()["provider"]
        if provider == "none":
            return ""
        return next((e for e in doc.list("external") if e.startswith(provider + ":")), "")

    def scope_exempt(self):
        """Paths never out of scope: configured exemptions plus root docs, changelog and living docs updated at G7."""
        docs = self.cfg_list("root_docs", DEFAULT_ROOT_DOCS) + [self.cfg("changelog", "CHANGELOG.md")]
        if not self.private:
            docs += [v.strip() for v in self.cfg_map("living_docs").values() if isinstance(v, str)]
        return self.cfg_list("scope_exempt", DEFAULT_SCOPE_EXEMPT) + [d for d in docs if d]

    def max_parallel(self) -> int:
        try:
            return max(0, int(self.cfg("max_parallel", "0")))
        except ValueError:
            return 0

    def _load_dir(self, folder: Path, prefix: str, store: dict):
        if not folder.is_dir():
            return
        for path in sorted(folder.glob("*.md")):
            if path.name in ("INDEX.md", "README.md"):
                continue
            doc = Doc(path, path.read_text(encoding="utf-8"))
            if not doc.has_frontmatter:
                self.load_issues.append(("ERROR", path.name, "arquivo sem frontmatter — use `new`/`new-adr` ou `migrate`"))
                continue
            key = doc.id or path.stem
            if not key.startswith(prefix + "-"):
                self.load_issues.append(("WARN", path.name, f"id '{key}' não segue o padrão {prefix}-NNNN"))
            if key in store:
                self.load_issues.append(("ERROR", key, f"ID duplicado em {path.name} e {store[key].path.name}"))
                continue
            store[key] = doc

    def _load(self):
        self._load_dir(self.specs_dir, "SPEC", self.specs)
        self._load_dir(self.adr_dir, "ADR", self.adrs)
        legacy = [d for d in LEGACY_DIRS if (self.specs_dir / d).is_dir() and any((self.specs_dir / d).glob("*.md"))]
        if legacy:
            self.load_issues.append(("WARN", "docs/specs", f"layout legado encontrado ({', '.join(d + '/' for d in legacy)}) — rode `migrate`"))

    def children(self, epic_id: str):
        return sorted((d for d in self.specs.values() if d.scalar("parent") == epic_id), key=lambda d: d.id)

    def consumers(self, spec_id: str):
        out = []
        for d in sorted(self.specs.values(), key=lambda d: d.id):
            for pid, ver, _ in contract_refs(d):
                if pid == spec_id:
                    out.append((d, ver))
        return out

    def next_number(self, prefix: str) -> int:
        pattern = re.compile(rf"^{prefix}-(\d+)")
        folder, store = (self.specs_dir, self.specs) if prefix == "SPEC" else (self.adr_dir, self.adrs)
        nums = [0]
        if folder.is_dir():
            nums += [int(m.group(1)) for p in folder.rglob("*.md") for m in [pattern.match(p.name)] if m]
        nums += [int(m.group(1)) for k in store for m in [pattern.match(k)] if m]
        return max(nums) + 1

    def rel(self, path: Path) -> str:
        try:
            return path.relative_to(self.root).as_posix()
        except ValueError:
            return str(path)


def find_root(start: Path) -> Path:
    start = start.resolve()
    for p in [start, *start.parents]:
        if (p / "docs" / "specs").is_dir() or (p / ".git").exists():
            return p
    return start


def commit_spec_root(spec_root: Path, sync: str, message: str):
    """Commit de tudo o que mudou no spec_root; push quando sync = push e há remoto. Falhas viram aviso."""
    run_git(spec_root, "add", "-A")
    if run_git(spec_root, "diff", "--cached", "--quiet")[0] == 0:
        return
    code, _, err = run_git(spec_root, "commit", "-q", "-m", message)
    if code != 0:
        print(f"aviso: commit no spec_root falhou ({spec_root}): {err.strip()}", file=sys.stderr)
        return
    remote = run_git(spec_root, "remote")[1].split()
    if sync == "push" and remote:
        code, _, err = run_git(spec_root, "push", "-q", remote[0], "HEAD")
        if code != 0:
            print(f"aviso: push do spec_root falhou ({remote[0]}): {err.strip()}", file=sys.stderr)


def commit_records(project: Project, message: str):
    """Modo privado: commit dos registros no git do spec_root (e push com sync: push)."""
    if project.private:
        commit_spec_root(project.spec_root, project.sync, message)


def spec_root_pending(spec_root: Path, sync: str):
    """Avisos sobre o spec_root: mudanças sem commit e commits sem push."""
    out = []
    changed = run_git(spec_root, "status", "--porcelain")[1].splitlines()
    if changed:
        out.append(f"spec_root com {len(changed)} mudança(s) sem commit em {spec_root}")
    if sync == "push":
        code, ahead, _ = run_git(spec_root, "rev-list", "--count", "@{u}..HEAD")
        if code == 0 and ahead.strip() not in ("", "0"):
            out.append(f"spec_root com {ahead.strip()} commit(s) sem push")
    return out


def cmd_private(project: Project, action: str, spec_root, sync):
    root = project.root
    registry = load_registry()
    projects = registry["projects"]
    if action == "status":
        if not project.private:
            print(f"modo completo: nenhuma entrada para este repositório em {registry_path()}")
            return 0
        print(f"modo privado: {project.mode_key}\n  spec_root: {project.spec_root}\n  sync: {project.sync}")
        for warning in spec_root_pending(project.spec_root, project.sync):
            print(f"⚠ {warning}")
        return 0
    if action == "detect":
        if not project.private:
            die("private detect: este repositório não está em modo privado (rode `private init`)")
        conv = team_conventions(project)
        write_team_block(project.config_path, conv)
        commit_records(project, "sdd: private detect")
        print(describe_team(conv))
        return 0
    if action == "off":
        key = next((k for k in repo_keys(root) if k in projects), None)
        if key is None:
            print("nada a desligar: este repositório não está no registro")
            return 0
        entry = projects.pop(key)
        save_registry(registry)
        print(f"modo privado desligado para {key}; o spec_root continua em {entry.get('spec_root')}")
        return 0
    if run_git(root, "rev-parse", "--is-inside-work-tree")[0] != 0:
        die("private init roda dentro de um repositório git")
    if run_git(root, "ls-files", "docs/specs")[1].strip():
        die("este repositório usa o SDD versionado (modo completo) em docs/specs; o modo privado é para "
            "repositórios onde o SDD não está no repositório")
    keys = repo_keys(root)
    key = keys[0]
    current = next((projects[k] for k in keys if isinstance(projects.get(k), dict)), {})
    if spec_root:
        target = Path(spec_root).expanduser()
    elif current.get("spec_root"):
        target = Path(current["spec_root"]).expanduser()
    else:
        data_home = Path(os.environ["XDG_DATA_HOME"]) if os.environ.get("XDG_DATA_HOME") else Path.home() / ".local/share"
        default_root = Path(registry["default_root"]).expanduser() if registry.get("default_root") else data_home / "sdd"
        target = default_root.joinpath(*key.split("/")[-2:])
    target = target.resolve()
    repo = Path(keys[-1])
    if target == repo or repo in target.parents:
        die(f"spec_root {target} fica dentro do repositório {repo}: as specs ficariam no clone do time; "
            "use uma pasta fora dele (padrão: <default_root>/<org>/<repo>)")
    sync = sync or current.get("sync") or "none"
    for k in keys:
        projects.pop(k, None)
    projects[key] = {"mode": "private", "spec_root": str(target), "sync": sync}
    save_registry(registry)
    (target / "specs").mkdir(parents=True, exist_ok=True)
    (target / "adr").mkdir(exist_ok=True)
    config = target / "sdd-config.yml"
    if not config.exists():
        config.write_text((TEMPLATES_DIR / "sdd-config.template.yml").read_text(encoding="utf-8"), encoding="utf-8")
    if not (target / ".git").exists():
        run_git(target, "init", "-q")
    conv = team_conventions(Project(root))
    write_team_block(config, conv)
    commit_spec_root(target, sync, "sdd: private init")
    print(f"modo privado ativado para {key}\n  spec_root: {target}\n  sync: {sync}")
    print(describe_team(conv))
    print(install_leak_hook(root))
    return 0


LEAK_SECTION_TITLES = ("Mapa de Comportamentos", "Registro de Gates", "Relatório de Entrega",
                       "Registro de Impedimentos", "Questões em Aberto", "Definição de Pronto")
TEAM_HOOK_MARKERS = ((".husky", "husky"), ("lefthook.yml", "lefthook"), (".lefthook.yml", "lefthook"),
                     (".pre-commit-config.yaml", "pre-commit"))


def spec_root_forms(spec_root):
    """Formas conhecidas do mesmo spec_root, sem repetição: o caminho resolvido, a forma sob o HOME literal e
    sob o resolvido (absoluta e ~/) e, no macOS, o caminho sem o prefixo /private quando aponta para a mesma pasta."""
    root = Path(spec_root)
    forms = [str(root)]
    homes = []
    for home in (Path.home(), Path.home().resolve()):
        if home not in homes:
            homes.append(home)
    for base in homes:
        try:
            rel = root.relative_to(base)
        except ValueError:
            continue
        for home in homes:
            forms.append(str(home / rel))
        forms.append("~/" + rel.as_posix())
    if root.as_posix().startswith("/private/"):
        bare = Path(root.as_posix()[len("/private"):])
        if bare.resolve() == root.resolve():
            forms.append(str(bare))
    return list(dict.fromkeys(forms))


def build_leak_patterns(spec_root, extra, adr_ids=()):
    """Padrões do leak-check: [(rótulo, regex compilada)] — padrões padrão, caminho do spec_root e extras do usuário.

    ADR-NNNN só é achado quando o ADR é do spec_root (adr_ids): ADRs do próprio time não são vazamento.
    """
    patterns = [("tag de teste", re.compile(r"\bSPEC-\d{4,}:[A-Z0-9]+-\d+\b")),
                ("ID de spec", re.compile(r"\bSPEC-\d{4,}\b")),
                ("sdd-config", re.compile(r"\bsdd-config\b"))]
    if adr_ids:
        patterns.append(("ID de ADR", re.compile(r"\b(?:" + "|".join(re.escape(a) for a in sorted(adr_ids)) + r")\b")))
    patterns += [("seção do SDD", re.compile(re.escape(title))) for title in LEAK_SECTION_TITLES]
    if spec_root:
        patterns += [("caminho do spec_root", re.compile(re.escape(form))) for form in spec_root_forms(spec_root)]
    for pattern in extra or []:
        try:
            patterns.append(("leak_patterns", re.compile(pattern)))
        except re.error as e:
            raise ValueError(f"leak_patterns: regex inválida '{pattern}': {e}") from e
    return patterns


def leak_findings(text: str, origin: str, patterns, allow, first_line: int = 1):
    """Achados de vazamento em text: [(origem, linha, rótulo, trecho)]. leak_allow é testado contra o trecho."""
    try:
        allowed = [re.compile(a) for a in allow or []]
    except re.error as e:
        raise ValueError(f"leak_allow: regex inválida '{e.pattern}': {e}") from e
    found = []
    for number, line in enumerate(text.splitlines(), first_line):
        snippet = line.strip()[:80]
        labels = []
        for label, rx in patterns:
            if rx.search(line) and label not in labels:
                labels.append(label)
        if labels and not any(a.search(snippet) for a in allowed):
            found.append((origin, number, ", ".join(labels), snippet))
    return found


def added_lines(diff: str):
    """Linhas adicionadas num diff -U0: [(arquivo, linha, texto)]."""
    out, path, number = [], None, 0
    for line in diff.splitlines():
        if line.startswith("+++ "):
            path = line[6:] if line.startswith("+++ b/") else None
        elif line.startswith("@@"):
            m = re.match(r"@@ -\S+ \+(\d+)", line)
            number = int(m.group(1)) if m else 0
        elif line.startswith("+") and path:
            out.append((path, number, line[1:]))
            number += 1
    return out


def leak_rules(project: Project):
    try:
        patterns = build_leak_patterns(project.spec_root, project.cfg_list("leak_patterns", []), project.adrs)
        allow = [re.compile(a) for a in project.cfg_list("leak_allow", [])]
    except ValueError as e:
        die(str(e))
    except re.error as e:
        die(f"leak_allow: regex inválida '{e.pattern}': {e}")
    return patterns, allow


def leak_scan(project: Project, root: Path, staged=False, rng=None, message=None, branch=None):
    """Coleta as fontes (diff staged, commits do intervalo, mensagem, branch) e devolve os achados."""
    patterns, allow = leak_rules(project)
    found = []
    if staged:
        for path, number, text in added_lines(run_git(root, "diff", "--cached", "-U0", "--no-color")[1]):
            found += leak_findings(text, path, patterns, allow, number)
    if rng:
        start, _, end = rng.partition("..")
        if start.strip("0") == "":
            code, out, err = run_git(root, "rev-list", end or "HEAD", "--not", "--remotes")
        else:
            code, out, err = run_git(root, "rev-list", rng)
        if code != 0:
            die(f"intervalo inválido '{rng}': {err.strip()}")
        for sha in out.split():
            found += leak_findings(run_git(root, "log", "-1", "--format=%B", sha)[1], f"commit {sha[:7]}", patterns, allow)
            for path, number, text in added_lines(run_git(root, "show", "-U0", "--format=", "--no-color", sha)[1]):
                found += leak_findings(text, f"{path} ({sha[:7]})", patterns, allow, number)
    if message:
        found += leak_findings(Path(message).read_text(encoding="utf-8"), "mensagem", patterns, allow)
    if branch:
        found += leak_findings(branch, "branch", patterns, allow)
    return found


def cmd_leak_check(project: Project, staged: bool, rng, message, branch):
    if not project.private:
        print("modo completo: nada a checar (o leak-check vale para repositórios em modo privado)")
        return 0
    root = project.root
    if not (staged or rng or message or branch):
        staged = True
        branch = run_git(root, "rev-parse", "--abbrev-ref", "HEAD")[1].strip()
        code, base, _ = run_git(root, "merge-base", project.cfg("base_branch", "main"), "HEAD")
        rng = f"{base.strip()}..HEAD" if code == 0 else None
    found = leak_scan(project, root, staged, rng, message, branch)
    for origin, number, label, snippet in found:
        where = "branch" if origin == "branch" else f"{origin}:{number}"
        print(f"[LEAK] FAIL {where} — {label} — \"{snippet}\"")
    print(f"Resultado: {'FAIL' if found else 'PASS'} — {len(found)} achado(s)")
    return 1 if found else 0


def team_hook_manager(root: Path):
    code, out, _ = run_git(root, "config", "--get", "core.hooksPath")
    if code == 0 and out.strip():
        return "core.hooksPath"
    return next((label for name, label in TEAM_HOOK_MARKERS if (root / name).exists()), None)


def install_leak_hook(root: Path) -> str:
    """Instala o pre-push do leak-check em .git/hooks quando é seguro; devolve a mensagem para o usuário."""
    manager = team_hook_manager(root)
    if manager:
        return (f"⚠ hooks do time detectados ({manager}): o hook local não foi instalado; o leak-check roda dentro de "
                "worktree/gate/pr — rode `leak-check` antes de cada push")
    hooks = Path(run_git(root, "rev-parse", "--git-path", "hooks")[1].strip())
    hook = (hooks if hooks.is_absolute() else root / hooks) / "pre-push"
    if hook.exists():
        return f"⚠ {hook} já existe: o hook do leak-check não foi instalado — rode `leak-check` antes de cada push"
    text = (ASSETS_DIR / "hooks" / "pre-push-leak").read_text(encoding="utf-8")
    hook.parent.mkdir(parents=True, exist_ok=True)
    hook.write_text(text.replace("__PYTHON__", sys.executable).replace("__SDD_TOOL__", str(Path(__file__).resolve())),
                    encoding="utf-8")
    hook.chmod(0o755)
    return f"hook pre-push do leak-check instalado em {hook} (local, não versionado)"


def die(msg: str, code: int = 2):
    print(f"erro: {msg}", file=sys.stderr)
    sys.exit(code)


# ---------------------------------------------------------------- validation (G0)

def validate(project: Project, only=None):
    issues = []

    def add(level, subject, msg):
        issues.append((level, subject, msg))

    for level, subject, msg in project.load_issues:
        if only is None or subject in only:
            add(level, subject, msg)
    for sid in sorted(project.specs):
        if only is None or sid in only:
            validate_spec(project, project.specs[sid], add)
    for cycle in find_cycles(project):
        if only is None or any(n in only for n in cycle):
            add("ERROR", cycle[0], "ciclo em depends_on: " + " → ".join(cycle))
    for aid in sorted(project.adrs):
        if only is None or aid in only:
            validate_adr(project, project.adrs[aid], add)
    if only is None:
        validate_config(project, add)
        validate_change_flow(project, add)
        validate_pillars(project, add)
        validate_journeys(project, add)
        validate_living_docs(project, add)
        validate_worktrees(project, add)
        validate_test_tooling(project, add)
        seen = defaultdict(list)
        for sid, doc in sorted(project.specs.items()):
            evidence = ((gate_table(doc) or {}).get("G2") or {}).get("evidence", "")
            if evidence and not doc.legacy:
                seen[evidence].append(sid)
        for evidence, sids in seen.items():
            if len(sids) > 1:
                add("WARN", sids[0], f"evidência de G2 idêntica em {', '.join(sids)} ('{evidence[:50]}') — cada spec precisa da própria execução")
    return issues


def pillar_settings(project: Project):
    cfg = project.cfg_map("pillars")
    profile = cfg.get("profile") if isinstance(cfg.get("profile"), str) else ""
    extra = cfg.get("extra") if isinstance(cfg.get("extra"), dict) else {}
    return profile.strip(), pillar_waivers(project), extra


def required_pillars(project: Project):
    profile, _, extra = pillar_settings(project)
    if profile not in PROFILES:
        return []
    level = PROFILES.index(profile)
    ids = [pid for pid, (_, _, lvl) in PILLARS.items() if PROFILES.index(lvl) <= level]
    return ids + [pid for pid in extra if pid not in ids]


def pillar_coverage(project: Project):
    _, waived, extra = pillar_settings(project)
    rows = []
    for pid in required_pillars(project):
        title = PILLARS[pid][0] if pid in PILLARS else str(extra.get(pid, pid))
        adrs = [a for a in sorted(project.adrs.values(), key=lambda d: d.id) if pid in a.list("pillars")]
        accepted = [a.id for a in adrs if a.status == "accepted"]
        proposed = [a.id for a in adrs if a.status == "proposed"]
        if accepted:
            state, detail = "coberto", ", ".join(accepted)
        elif proposed:
            state, detail = "proposto", ", ".join(proposed) + " (aguarda aprovação)"
        elif pid in waived:
            state, detail = "dispensado", str(waived[pid])
        else:
            state, detail = "sem decisão", f"`new-adr --baseline {pid}`" if pid in PILLARS else "crie um ADR com pillars: [" + pid + "]"
        rows.append((pid, title, state, detail))
    return rows


def recurrent_na(project: Project):
    """Gates repeatedly N/A for a pillar that isn't waived: the gap is permanent, not temporary."""
    _, waived, _ = pillar_settings(project)
    out = []
    for gate, pid in GATE_PILLAR.items():
        sids = [sid for sid, d in sorted(project.specs.items())
                if not d.legacy and ((gate_table(d) or {}).get(gate) or {}).get("status") == "N/A"]
        if len(sids) >= NA_RECURRENT_THRESHOLD and pid not in waived:
            out.append((gate, pid, sids))
    return out


def validate_pillars(project: Project, add):
    profile, waived, extra = pillar_settings(project)
    known = set(PILLARS) | set(extra)
    for pid in waived:
        if pid not in known:
            add("ERROR", "sdd-config", f"pillars.waived: pilar desconhecido '{pid}' ({', '.join(sorted(known))})")
        elif len(str(waived[pid]).strip()) < 15:
            add("ERROR", "sdd-config", f"pillars.waived.{pid}: motivo com pelo menos 15 caracteres")
    for adr in project.adrs.values():
        unknown = [p for p in adr.list("pillars") if p not in known]
        if unknown:
            add("ERROR", adr.id, f"pillars desconhecido(s): {', '.join(unknown)} — veja `pillars` (ou declare em pillars.extra)")
    foundations = [d for d in project.specs.values() if d.tier == "epic" and d.type == "foundation"]
    if profile and profile not in PROFILES:
        add("ERROR", "sdd-config", f"pillars.profile '{profile}' inválido ({' | '.join(PROFILES)})")
        return
    if not profile:
        if foundations:
            add("WARN", "sdd-config", "projeto com épico de fundação sem pillars.profile (minimo | padrao | rigoroso) — rode `pillars`")
        return
    missing = [pid for pid, _, state, _ in pillar_coverage(project) if state == "sem decisão"]
    if missing:
        add("WARN", "pilares", f"sem decisão no perfil {profile}: {', '.join(missing)} — `pillars` mostra como resolver")
        for epic in foundations:
            if epic.status == "proposed":
                add("ERROR", epic.id, f"épico de fundação não passa do G0 com pilares sem decisão: {', '.join(missing)} "
                                      "(crie o ADR de base ou registre a dispensa com motivo)")
    covered = {pid for pid, _, state, _ in pillar_coverage(project) if state == "coberto"}
    for gate, pid, sids in recurrent_na(project):
        if pid in covered:
            add("WARN", "pilares", f"{gate} N/A em {len(sids)} specs — o pilar '{pid}' tem ADR mas nada o verifica: "
                                   "crie a spec do teste/pipeline que o garante")
        else:
            add("WARN", "pilares", f"{gate} N/A em {len(sids)} specs — o pilar '{pid}' está em aberto de forma permanente: "
                                   f"crie a spec que resolve ou registre pillars.waived.{pid} com motivo")


def cmd_pillars(project: Project):
    profile, _waived, _ = pillar_settings(project)
    if profile not in PROFILES:
        print("pillars.profile não definido no sdd-config.yml (minimo | padrao | rigoroso). Catálogo:")
        for pid, (title, question, lvl) in PILLARS.items():
            print(f"  {pid:<28} [{lvl}] {title} — {question}")
        return 0
    print(f"Pilares — perfil {profile} (catálogo {PILLAR_CATALOG_VERSION})\n")
    icons = {"coberto": "✅", "proposto": "📝", "dispensado": "➖", "sem decisão": "⚠"}
    for pid, title, state, detail in pillar_coverage(project):
        print(f"{icons[state]} {title} [{pid}] — {state}: {detail}")
        if state == "sem decisão" and pid in PILLARS:
            print(f"     pergunta a responder: {PILLARS[pid][1]}")
    covered = {pid for pid, _, state, _ in pillar_coverage(project) if state == "coberto"}
    for gate, pid, sids in recurrent_na(project):
        listed = f"{', '.join(sids[:5])}{' …' if len(sids) > 5 else ''}"
        if pid in covered:
            print(f"\n⚠ {gate} N/A em {len(sids)} specs ({listed}): o pilar '{pid}' tem ADR, mas nada o verifica — "
                  "crie a spec do teste/pipeline que o garante (enforced_by)")
        else:
            print(f"\n⚠ {gate} N/A em {len(sids)} specs ({listed}): pilar '{pid}' sem solução — crie a spec que resolve "
                  "ou registre a dispensa")
    return 0


def journey_coverage(project: Project):
    journeys = project.cfg_map("critical_journeys")
    proven, planned = defaultdict(list), defaultdict(list)
    unknown = []
    for sid, doc in sorted(project.specs.items()):
        plan = COMMENT_RE.sub("", doc.section("Plano de Testes") or "")
        for line in plan.split("\n"):
            if "E2E-" not in line:
                continue
            for jid in JOURNEY_TAG_RE.findall(line):
                if jid not in journeys:
                    unknown.append((sid, jid))
                elif doc.status == "implemented":
                    proven[jid].append(sid)
                elif doc.status in OPEN:
                    planned[jid].append(sid)
    rows = []
    for jid, title in journeys.items():
        state = "provada" if proven[jid] else "planejada" if planned[jid] else "descoberta"
        rows.append((jid, str(title), state, proven[jid] or planned[jid]))
    return rows, unknown


def validate_journeys(project: Project, add):
    rows, unknown = journey_coverage(project)
    for sid, jid in unknown:
        add("ERROR", sid, f"E2E marca [jornada: {jid}], que não está em critical_journeys do sdd-config")
    uncovered = [jid for jid, _, state, _ in rows if state == "descoberta"]
    if uncovered:
        add("WARN", "jornadas", f"jornadas críticas sem E2E em nenhuma spec: {', '.join(uncovered)} — "
                                "marque o E2E que as prova com [jornada: <id>] ou crie a spec")


def cmd_journeys(project: Project):
    rows, unknown = journey_coverage(project)
    if not rows:
        print("Nenhuma jornada em critical_journeys no sdd-config.yml.")
        return 0
    icons = {"provada": "✅", "planejada": "📝", "descoberta": "⚠"}
    for jid, title, state, sids in rows:
        print(f"{icons[state]} {title} [{jid}] — {state}" + (f": {', '.join(sids)}" if sids else ""))
    for sid, jid in unknown:
        print(f"✖ {sid} marca jornada desconhecida '{jid}'")
    return 0


LIVING_DOCS = {"journeys": ("guia de jornadas", "template-journeys.md"),
               "system_map": ("mapa do sistema", "template-system-map.md")}
JOURNEY_BLOCK_RE = re.compile(r"<!-- jornada:inicio -->\n(.*?)<!-- jornada:fim -->\n", re.DOTALL)


def living_docs_base(project: Project) -> Path:
    """Base dos documentos vivos: spec_root no modo privado (SPEC-0003@1), raiz do repositório no completo."""
    return project.spec_root if project.private else project.root


def living_docs_paths(project: Project):
    """{chave: caminho absoluto} dos documentos vivos ligados; caminho fora da base → ValueError."""
    base = living_docs_base(project)
    out = {}
    for key, value in project.cfg_map("living_docs").items():
        if key not in LIVING_DOCS or not isinstance(value, str) or not value.strip():
            continue
        rel = Path(value.strip())
        norm_rel = os.path.normpath(rel.as_posix())
        path = Path(os.path.normpath(base / rel))
        outside = rel.is_absolute() or norm_rel.split("/")[0] == ".."
        if not outside:
            try:
                path.resolve().relative_to(base.resolve())
            except ValueError:
                outside = True
        if outside:
            raise ValueError(f"living_docs.{key}: '{value.strip()}' sai da base ({base}) — use um caminho relativo dentro dela")
        if norm_rel == "." or path.is_dir():
            raise ValueError(f"living_docs.{key}: '{value.strip()}' é uma pasta — informe o arquivo Markdown do documento")
        out[key] = path
    return out


def living_docs_findings(journeys, guide_text: str, adrs, map_text: str):
    """Cobertura dos documentos vivos: jornadas críticas no guia e ADRs accepted no mapa."""
    cited = set(JOURNEY_TAG_RE.findall(guide_text))
    out = [("journey_missing", jid) for jid in journeys if jid not in cited]
    out += [("journey_unknown", jid) for jid in sorted(cited) if jid not in journeys]
    out += [("adr_uncited", aid) for aid, status in sorted(adrs.items())
            if status == "accepted" and not re.search(rf"\b{re.escape(aid)}\b", map_text)]
    return out


def living_docs_inputs(project: Project, paths):
    """Entradas de living_docs_findings só para os documentos que existem."""
    guide, mapa = paths.get("journeys"), paths.get("system_map")
    has_guide, has_map = bool(guide and guide.is_file()), bool(mapa and mapa.is_file())
    journeys = project.cfg_map("critical_journeys") if has_guide else {}
    adrs = {aid: doc.status for aid, doc in project.adrs.items()} if has_map else {}
    return (journeys, guide.read_text(encoding="utf-8") if has_guide else "",
            adrs, mapa.read_text(encoding="utf-8") if has_map else "")


def validate_living_docs(project: Project, add):
    try:
        paths = living_docs_paths(project)
    except ValueError as e:
        add("ERROR", "sdd-config", str(e))
        return
    base = living_docs_base(project)
    for key, path in paths.items():
        if not path.is_file():
            add("WARN", f"living_docs.{key}", f"{path.relative_to(base).as_posix()} não existe — rode living-docs --init")
    messages = {
        "journey_missing": ("journeys", "guia de jornadas sem a jornada crítica {0} — adicione a seção com [jornada: {0}]"),
        "journey_unknown": ("journeys", "guia de jornadas cita [jornada: {0}], que não está em critical_journeys"),
        "adr_uncited": ("system_map", "mapa do sistema não cita {0} (accepted) — diga onde a decisão aparece no sistema"),
    }
    for kind, value in living_docs_findings(*living_docs_inputs(project, paths)):
        key, msg = messages[kind]
        add("WARN", f"living_docs.{key}", msg.format(value))


def journey_proof(project: Project, jid: str, sids) -> str:
    """'SPEC-NNNN (E2E-xx)' da primeira spec implementada que prova a jornada."""
    for sid in sids:
        plan = COMMENT_RE.sub("", project.specs[sid].section("Plano de Testes") or "")
        for line in plan.split("\n"):
            if jid in JOURNEY_TAG_RE.findall(line):
                e2e = next((t for t in TEST_ID_RE.findall(line) if t.startswith("E2E-")), "")
                if e2e:
                    return f"{sid} ({e2e})"
    return "— (sem E2E)"


def fill_block(block: str, values) -> str:
    """Substitui {{chave}} numa passada só: o texto inserido nunca é expandido de novo."""
    return re.sub(r"\{\{(\w+)\}\}", lambda m: values.get(m.group(1), m.group(0)), block)


def render_journeys(project: Project, template: str) -> str:
    m = JOURNEY_BLOCK_RE.search(template)
    block = m.group(1) if m else ""
    rows, _ = journey_coverage(project)
    if rows:
        sections = [fill_block(block, {"title": title, "id": jid,
                                        "proof": journey_proof(project, jid, sids) if state == "provada" else "— (sem E2E)"})
                    for jid, title, state, sids in rows]
    else:
        sections = ["<!-- Nenhuma jornada em critical_journeys: declare-as no sdd-config.yml e troque o exemplo abaixo "
                    "por uma seção por jornada, com [jornada: <id>] no título. -->\n"
                    + fill_block(block, {"title": "Nome da jornada", "id": "<id>", "proof": "— (sem E2E)"})]
    return template[:m.start()] + "".join(sections) + template[m.end():] if m else template


def render_system_map(project: Project, template: str) -> str:
    accepted = [f"- {aid} — {doc.title}" for aid, doc in sorted(project.adrs.items()) if doc.status == "accepted"]
    return template.replace("{{decisions}}", "\n".join(accepted) or "- Nenhum ADR aceito ainda.")


def cmd_living_docs(project: Project, init: bool):
    try:
        paths = living_docs_paths(project)
    except ValueError as e:
        die(str(e))
    if not paths:
        print("Nenhum documento vivo em living_docs no sdd-config.yml.")
        return 0
    base = living_docs_base(project)
    if init:
        renders = {"journeys": render_journeys, "system_map": render_system_map}
        for key, path in paths.items():
            rel = path.relative_to(base).as_posix()
            if path.exists():
                print(f"mantido: {rel}")
                continue
            template = (TEMPLATES_DIR / LIVING_DOCS[key][1]).read_text(encoding="utf-8")
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(renders[key](project, template), encoding="utf-8")
            print(f"criado: {rel}")
        return 0
    journeys = project.cfg_map("critical_journeys")
    adrs = {aid: doc.status for aid, doc in project.adrs.items()}
    guide, mapa = (paths[k].read_text(encoding="utf-8") if k in paths and paths[k].is_file() else ""
                   for k in ("journeys", "system_map"))
    findings = set(living_docs_findings(journeys, guide, adrs, mapa))
    for key, path in paths.items():
        print(f"{LIVING_DOCS[key][0]}: {path.relative_to(base).as_posix()} — {'existe' if path.is_file() else 'ausente'}")
        if key == "journeys":
            for jid, title in journeys.items():
                print(f"  {'⚠' if ('journey_missing', jid) in findings else '✅'} {jid} — {title}")
            for kind, jid in sorted(findings):
                if kind == "journey_unknown":
                    print(f"  ✖ {jid} — fora de critical_journeys")
        else:
            for aid, status in sorted(adrs.items()):
                if status == "accepted":
                    mark = "⚠" if ("adr_uncited", aid) in findings else "✅"
                    print(f"  {mark} {aid} — {project.adrs[aid].title}")
    return 0


WORKTREE_DIR_DEFAULT = "../{repo}.worktrees"


def worktree_base(project: Project) -> Path:
    pattern = project.cfg("worktree_dir", WORKTREE_DIR_DEFAULT).replace("{repo}", project.root.name)
    base = Path(pattern)
    return (base if base.is_absolute() else project.root / base).resolve()


def spec_slug(doc: Doc) -> str:
    stem = doc.path.stem
    return stem[len(doc.id) + 1:] if stem.startswith(doc.id + "-") else slugify(doc.title)


def worktree_for(project: Project, doc: Doc):
    slug = spec_slug(doc)
    if project.private:
        return worktree_base(project) / slug, f"{CONVENTIONAL_BY_TYPE.get(doc.type, 'feat')}/{slug}"
    return worktree_base(project) / f"{doc.id}-{slug}", f"sdd/{doc.id}-{slug}"


def list_worktrees(project: Project):
    code, out, _ = run_git(project.root, "worktree", "list", "--porcelain")
    if code != 0:
        return []
    entries, cur = [], {}
    for line in out.splitlines() + [""]:
        if not line.strip():
            if cur:
                entries.append(cur)
            cur = {}
        elif line.startswith("worktree "):
            cur["path"] = line[9:]
        elif line.startswith("branch "):
            cur["branch"] = line[7:].replace("refs/heads/", "")
        elif line.startswith("prunable"):
            cur["prunable"] = True
    return entries[1:]  # the first entry is the main checkout


def stale_worktrees(project: Project):
    stale = []
    for entry in list_worktrees(project):
        m = re.search(r"\bsdd/(SPEC-\d{4,})", entry.get("branch", ""))
        doc = project.specs.get(m.group(1)) if m else None
        if entry.get("prunable") or not Path(entry.get("path", "")).exists():
            stale.append((entry.get("path", "?"), "pasta não existe mais — rode `git worktree prune`"))
        elif doc is not None and doc.status in DONE:
            stale.append((entry["path"], f"{doc.id} está {doc.status} — `git worktree remove {entry['path']}`"))
    return stale


def misplaced_worktrees(project: Project):
    """Worktrees outside worktree_dir (e.g. old sibling folders, or a folder inside the repo)."""
    base = worktree_base(project)
    root = project.root.resolve()
    allow = project.cfg_list("worktree_allow", [])
    out = []
    for entry in list_worktrees(project):
        path = Path(entry.get("path", "")).resolve()
        if not path.exists() or path == base or base in path.parents:
            continue
        inside = root in path.parents
        rel = path.relative_to(root).as_posix() if inside else str(path)
        if allow and matches_any(rel, allow):
            continue
        branch = entry.get("branch", "")
        m = re.search(r"\bsdd/(SPEC-\d{4,}[\w-]*)", branch)
        target = base / (m.group(1) if m else path.name)
        why = ""
        if inside and run_git(project.root, "check-ignore", "-q", rel + "/")[0] != 0:
            why = " — está DENTRO do repositório e fora do .gitignore (ferramentas podem ver o código em dobro)"
        out.append((str(path), (f"fora do worktree_dir{why}; quando ninguém estiver trabalhando nela: "
                                f"`git worktree move {path} {target}`")))
    return out


def validate_worktrees(project: Project, add):
    if run_git(project.root, "rev-parse", "--is-inside-work-tree")[0] != 0:
        return
    base = worktree_base(project)
    try:
        inside = base.relative_to(project.root.resolve())
    except ValueError:
        inside = None
    if inside is not None and run_git(project.root, "check-ignore", "-q", str(inside) + "/")[0] != 0:
        add("WARN", "worktrees", f"worktree_dir '{inside}' fica dentro do repositório e não está no .gitignore — "
                                 "adicione-o (e exclua a pasta de builds de container, IDE e descoberta de testes) ou use "
                                 f"o padrão {WORKTREE_DIR_DEFAULT}")
    for path, why in stale_worktrees(project):
        add("WARN", "worktrees", f"worktree esquecida {path}: {why}")
    for path, why in misplaced_worktrees(project):
        add("WARN", "worktrees", f"worktree {path} {why}")


def cmd_worktree(project: Project, spec_id: str, create: bool, base):
    doc = project.specs.get(spec_id)
    if doc is None or doc.tier == "epic":
        die(f"{spec_id} não encontrada ou é épico")
    path, branch = worktree_for(project, doc)
    if not create:
        print(f"{path}\n{branch}")
        return 0
    if project.private:
        n, first_path, first_branch = 2, path, branch
        while path.exists():
            path, branch = first_path.with_name(f"{first_path.name}-{n}"), f"{first_branch}-{n}"
            n += 1
    if path.exists():
        die(f"{path} já existe")
    start = base or (f"sdd/{doc.scalar('parent')}" if doc.scalar("parent") else project.cfg("base_branch", "main"))
    exists = run_git(project.root, "rev-parse", "--verify", "--quiet", f"refs/heads/{branch}")[0] == 0
    args = ["worktree", "add", str(path), branch] if exists else ["worktree", "add", str(path), "-b", branch, start]
    path.parent.mkdir(parents=True, exist_ok=True)
    code, _out, err = run_git(project.root, *args)
    if code != 0:
        die(f"git worktree add falhou: {err.strip()}")
    print(f"worktree criada: {path}\nbranch: {branch}" + ("" if exists else f" (a partir de {start})"))
    return 0


REQUIRED_TEST_LEVELS = (("unitario", "Unitário"), ("integracao", "Integração"), ("e2e", "E2E"))


def validate_test_tooling(project: Project, add):
    """The `testes` pillar decides a tool (or a reasoned N/A) per test level; may be spread over several ADRs."""
    adrs = [a for a in sorted(project.adrs.values(), key=lambda d: d.id)
            if "testes" in a.list("pillars") and a.status in ("proposed", "accepted")]
    if not adrs or any(PLACEHOLDER_RE.search(COMMENT_RE.sub("", a.text)) for a in adrs):
        return
    levels = {}
    for adr in adrs:
        for header, rows in parse_tables(COMMENT_RE.sub("", adr.body)):
            if not header or norm(header[0]) not in ("nivel", "nível"):
                continue
            for row in rows:
                if len(row) > 1 and row[0]:
                    levels.setdefault(norm(row[0]), (row[1].strip(), adr.id))
    level = "WARN" if any(a.status == "accepted" for a in adrs) else "ERROR"
    subject = adrs[0].id
    for key, label in REQUIRED_TEST_LEVELS:
        tool, where = levels.get(key, ("", subject))
        if not tool:
            add(level, where, f"pilar testes sem decisão para o nível {label}: tabela 'Ferramenta por nível' "
                              "(ferramenta, por quê, onde roda) ou 'N/A — motivo'")
        elif re.match(r"^N/A", tool, re.IGNORECASE) and len(re.sub(r"^N/A\s*[—–:-]?\s*", "", tool, flags=re.IGNORECASE)) < 10:
            add(level, where, f"pilar testes: N/A no nível {label} sem motivo")


def validate_change_flow(project: Project, add):
    flow = project.cfg("change_flow", "pr")
    if flow not in ("pr", "direct"):
        add("ERROR", "sdd-config", f"change_flow '{flow}' inválido (pr | direct)")
        return
    if flow == "direct":
        if not project.cfg("change_flow_reason"):
            add("ERROR", "sdd-config", "change_flow: direct exige change_flow_reason (ex: repositório pessoal sem plataforma)")
        return
    if run_git(project.root, "rev-parse", "--is-inside-work-tree")[0] != 0:
        return
    base = project.cfg("base_branch", "main")
    first = run_git(project.root, "log", "--format=%H", "--diff-filter=A", "--reverse", base, "--", "docs/specs")[1].split()
    if not first:
        return
    log = run_git(project.root, "log", "--first-parent", "--format=%h%x1f%P%x1f%s", f"{first[0]}..{base}", "-n", "300")[1]
    pattern = re.compile(project.cfg("pr_merge_pattern", PR_MERGE_PATTERN_DEFAULT))
    direct, records, local_merges, squashed = [], [], [], []
    for line in log.splitlines():
        sha, parents, subject = (line.split("\x1f") + ["", ""])[:3]
        is_merge = len(parents.split()) > 1
        if is_merge and not pattern.search(subject):
            local_merges.append(f"{sha} {subject[:50]}")
        elif not is_merge and re.search(SQUASH_PATTERN, subject):
            squashed.append(sha)
        elif not is_merge:
            files = run_git(project.root, "show", "--name-only", "--format=", sha)[1].split()
            if files and all(f.startswith(("docs/specs/", "docs/adr/")) for f in files):
                records.append(sha)
            else:
                direct.append(f"{sha} {subject[:50]}")
    if direct:
        add("WARN", "fluxo", f"{len(direct)} commit(s) de código direto(s) na {base} sem solicitação de mudança (change_flow: pr): "
                             + "; ".join(direct[:3]) + " — proteja a branch e passe tudo por PR/MR")
    if records:
        add("WARN", "fluxo", f"{len(records)} commit(s) de registros SDD direto(s) na {base} — registre gates na branch de "
                             "integração (vão no PR da onda) e o fechamento (G6/G7) num PR de fechamento")
    if local_merges:
        add("WARN", "fluxo", f"{len(local_merges)} merge(s) na {base} sem PR/MR (feitos localmente?): "
                             + "; ".join(local_merges[:3]) + " — ajuste pr_merge_pattern se a plataforma usa outra mensagem")
    if squashed:
        add("WARN", "fluxo", f"{len(squashed)} merge(s) por squash: os commits citados nas evidências dos gates somem do histórico "
                             "— prefira merge commit (ADR de fluxo de mudança) ou registre o commit final no G5")


def validate_config(project: Project, add):
    t = project.tracker()
    if t["provider"] != "none":
        if not t["project"]:
            add("ERROR", "sdd-config", f"tracker.provider = {t['provider']} exige tracker.project")
        missing = [s for s in STATUSES if not t["statuses"].get(s)]
        if missing:
            add("WARN", "sdd-config", f"tracker.statuses sem mapeamento para: {', '.join(missing)}")
        kinds = ["epic"] + list(TYPES)
        missing = [k for k in kinds if not t["issue_types"].get(k)]
        if missing:
            add("WARN", "sdd-config", f"tracker.issue_types sem mapeamento para: {', '.join(missing)}")
    h1 = project.cfg("h1_policy", "chat")
    if h1 not in ("chat", "pr-approval"):
        add("ERROR", "sdd-config", f"h1_policy '{h1}' inválida (chat | pr-approval)")
    elif h1 == "pr-approval":
        for sid, doc in sorted(project.specs.items()):
            if doc.status in APPROVED_PLUS and not doc.legacy and not doc.scalar("approved_via"):
                add("WARN", sid, "h1_policy pr-approval: registre approved_via (ex: \"PR #12\") com o revisor em approved_by")
    policy = project.cfg("h2_policy", "per-wave")
    policy_approved = bool(project.cfg("h2_policy_approved_by") and valid_date(project.cfg("h2_policy_approved_at")))
    if policy not in ("per-wave", "auto-on-green"):
        add("ERROR", "sdd-config", f"h2_policy '{policy}' inválida (per-wave | auto-on-green)")
    elif policy == "auto-on-green" and not policy_approved:
        add("ERROR", "sdd-config", "h2_policy auto-on-green exige h2_policy_approved_by e h2_policy_approved_at (decisão humana registrada)")
    h2_texts = defaultdict(list)
    for sid, doc in sorted(project.specs.items()):
        evidence = ((gate_table(doc) or {}).get("H2") or {}).get("evidence", "")
        if evidence and not evidence.startswith("política auto-on-green") and not doc.legacy:
            h2_texts[re.sub(r"\s+", " ", evidence.lower())].append(sid)
    policy_declared = policy == "auto-on-green" and policy_approved
    for evidence, sids in h2_texts.items():
        if len(sids) >= 3 and not policy_declared:
            add("WARN", "sdd-config", f"H2 com o mesmo texto em {len(sids)} specs ('{evidence[:50]}') — se é uma autorização "
                                      "permanente, declare h2_policy: auto-on-green no sdd-config.yml em vez de repeti-la")
    fields = set(re.findall(r"\{(\w+)\}", project.cfg("pr_title", PR_TITLE_DEFAULT)))
    if fields - PR_TITLE_FIELDS:
        add("ERROR", "sdd-config", f"pr_title com campos desconhecidos: {', '.join(sorted(fields - PR_TITLE_FIELDS))} "
                                   f"(use {', '.join(sorted(PR_TITLE_FIELDS))})")
    vendored = project.root / VENDORED_TOOL
    if vendored.exists():
        m = re.search(r'SDD_TOOL_VERSION = "([^"]+)"', vendored.read_text(encoding="utf-8"))
        if not m or m.group(1) != SDD_TOOL_VERSION:
            add("WARN", "sdd-config", f"{VENDORED_TOOL} ({m.group(1) if m else '?'}) difere da skill ({SDD_TOOL_VERSION}) "
                                      "— rode `vendor --force` para atualizar")


def validate_spec(project: Project, doc: Doc, add):
    sid = doc.id
    if not SPEC_ID_RE.match(sid):
        add("ERROR", sid or doc.path.name, "id inválido (esperado SPEC-NNNN)")
        return
    if doc.path.stem != sid and not doc.path.name.startswith(sid + "-"):
        add("ERROR", sid, f"nome do arquivo deve começar com '{sid}-' (atual: {doc.path.name})")
    if not doc.title:
        add("ERROR", sid, "title vazio")
    if doc.tier not in TIERS:
        add("ERROR", sid, f"tier inválido '{doc.tier}' (lite | full | epic)")
        return
    if doc.status not in STATUSES:
        add("ERROR", sid, f"status inválido '{doc.status}' ({' | '.join(STATUSES)})")
        return
    if not valid_date(doc.scalar("created")):
        add("ERROR", sid, "created deve ser uma data YYYY-MM-DD")
    placeholders = sorted(set(PLACEHOLDER_RE.findall(COMMENT_RE.sub("", doc.text))))
    if placeholders:
        shown = ", ".join(p[:40] for p in placeholders[:4]) + (" …" if len(placeholders) > 4 else "")
        add("ERROR", sid, f"{len(placeholders)} marcador(es) {{{{...}}}} não preenchido(s): {shown}")
    validate_refs(project, doc, add)
    if doc.legacy:
        if doc.status in OPEN:
            add("WARN", sid, "spec legada aberta — reescreva no template atual (depends_on, touches, seções) e remova `legacy`")
        return
    allowed_types = TYPES_BY_TIER[doc.tier]
    if not doc.type:
        add("ERROR", sid, f"type obrigatório ({' | '.join(t for t in TYPES if t in allowed_types)})")
    elif doc.type not in TYPES:
        add("ERROR", sid, f"type inválido '{doc.type}' ({' | '.join(TYPES)})")
    elif doc.type not in allowed_types:
        add("ERROR", sid, f"type {doc.type} não é permitido no tier {doc.tier} "
                          f"({' | '.join(t for t in TYPES if t in allowed_types)})")
    if doc.status in APPROVED_PLUS and not (doc.scalar("approved_by") and valid_date(doc.scalar("approved_at"))):
        add("ERROR", sid, "status exige aprovação humana registrada (approved_by + approved_at) — H1")
    for keyword, must_fill in REQUIRED_SECTIONS[doc.tier]:
        content = doc.section(keyword)
        if content is None:
            add("ERROR", sid, f"seção obrigatória ausente: '{keyword}'")
        elif must_fill and not COMMENT_RE.sub("", content).strip():
            add("ERROR", sid, f"seção vazia: '{keyword}'")
    validate_questions(doc, add)
    validate_impediments(project, doc, add)
    if doc.tier == "epic":
        validate_epic(project, doc, add)
    else:
        validate_executable(project, doc, add)


def validate_refs(project: Project, doc: Doc, add):
    sid, specs = doc.id, project.specs
    parent = doc.scalar("parent")
    if parent:
        if doc.tier == "epic":
            add("ERROR", sid, "épico não pode ter parent (use depends_on entre épicos)")
        elif parent not in specs:
            add("ERROR", sid, f"parent {parent} não existe")
        elif specs[parent].tier != "epic":
            add("ERROR", sid, f"parent {parent} não é um épico")
    for dep in doc.list("depends_on"):
        if dep == sid:
            add("ERROR", sid, "depends_on referencia a própria spec")
        elif dep not in specs:
            add("ERROR", sid, f"depends_on: {dep} não existe")
        elif specs[dep].status == "deprecated" and doc.status in OPEN:
            add("ERROR", sid, f"depende de {dep}, que está deprecated — reavalie a dependência")
    version = contract_version(doc)
    if doc.tier != "epic" and version > 1 and not doc.legacy:
        amendments = sum(len(rows) for _, rows in parse_tables(COMMENT_RE.sub("", doc.section("Emendas") or "")))
        if amendments < version - 1:
            add("ERROR", sid, f"contract_version v{version} exige {version - 1} emenda(s) registrada(s) na seção Emendas "
                              f"(encontrada(s): {amendments})")
    for pid, ver, raw in contract_refs(doc):
        if pid is None:
            add("ERROR", sid, f"consumes_contract inválido '{raw}' (formato SPEC-NNNN@versão)")
            continue
        if pid == sid:
            add("ERROR", sid, "consumes_contract referencia a própria spec")
            continue
        provider = specs.get(pid)
        if provider is None:
            add("ERROR", sid, f"consumes_contract: {pid} não existe")
            continue
        if provider.tier == "epic":
            add("ERROR", sid, f"{pid} é um épico e não tem contrato — aponte para a spec filha que define o contrato")
        current = contract_version(provider)
        if ver is None:
            add("ERROR", sid, f"fixe a versão do contrato consumido: {pid}@{current}")
        elif ver > current:
            add("ERROR", sid, f"{pid}@{ver} não existe (versão atual do contrato: v{current})")
        elif ver < current and doc.status in OPEN:
            add("WARN", sid, f"contrato de {pid} mudou (v{ver} → v{current}): revise esta spec e atualize o pin")
        if provider.status == "deprecated" and doc.status in OPEN:
            add("ERROR", sid, f"consome contrato de {pid}, que está deprecated")
    for ext in doc.list("external"):
        if not EXTERNAL_RE.match(ext):
            add("ERROR", sid, f"external inválido '{ext}' (formato provedor:referência, ex: jira:PAY-123, github:org/repo#12)")
    provider = project.tracker()["provider"]
    if provider != "none" and doc.status in OPEN and not project.external_ref(doc):
        add("WARN", sid, f"sem item no board ({provider}) — rode `sync` e execute as operações do adaptador")
    for adr in doc.list("adrs"):
        if adr not in project.adrs:
            add("ERROR", sid, f"adrs: {adr} não existe em docs/adr/")
        elif project.adrs[adr].status in ("superseded", "rejected") and doc.status in OPEN:
            add("WARN", sid, f"{adr} está {project.adrs[adr].status} — confirme se a decisão ainda vale")


def validate_executable(project: Project, doc: Doc, add):
    sid, status, tier = doc.id, doc.status, doc.tier
    size = doc.scalar("size").upper()
    if size not in SIZES:
        add("ERROR", sid, "size deve ser S ou M (L não é permitido)")
    elif size == "L":
        add("ERROR", sid, "size L é grande demais para um agente — quebre em um épico com specs S/M")
    elif tier == "lite" and size != "S":
        add("WARN", sid, "spec lite deveria ser S — se for maior, promova para full")
    touches = doc.list("touches")
    if not touches:
        add("ERROR" if status in APPROVED_PLUS else "WARN", sid,
            "touches vazio — declare os globs que a spec pode alterar (paralelismo e G4 dependem disso)")
    for pattern in touches:
        if pattern.startswith("**"):
            add("WARN", sid, f"touches '{pattern}' começa com ** e conflita com qualquer spec — seja específico")

    plan = COMMENT_RE.sub("", doc.section("Plano de Testes") or "")
    defined = set(TEST_ID_RE.findall(plan))
    dups = sorted(t for t, n in Counter(TEST_DEF_RE.findall(plan)).items() if n > 1)
    if dups:
        add("WARN", sid, f"IDs de teste definidos mais de uma vez: {', '.join(dups)}")
    if not defined:
        add("ERROR", sid, "plano de testes sem IDs (UT-01, IT-01, CT-01…) — No Test = No Handoff")
    kinds = {t.split("-")[0] for t in defined}
    user_facing = doc.scalar("user_facing").lower()
    if tier == "full":
        if "UT" not in kinds:
            add("ERROR", sid, "tier full exige ao menos um teste unitário (UT-xx)")
        if "IT" not in kinds:
            add("ERROR", sid, "tier full exige ao menos um teste de integração (IT-xx)")
        if (contract_refs(doc) or project.consumers(sid)) and "CT" not in kinds:
            add("ERROR", sid, "spec consome ou fornece contrato para outra spec: exige teste de contrato (CT-xx)")
        if user_facing not in ("true", "false"):
            add("ERROR", sid, "user_facing deve ser true ou false (true = altera jornada do usuário em UI ou API pública)")
        elif user_facing == "true" and "E2E" not in kinds:
            add("ERROR", sid, "user_facing: true exige ao menos um teste E2E (E2E-xx) da jornada afetada")
        elif user_facing == "false" and "E2E" not in kinds:
            e2e = find_sub(parse_subsections(plan), "E2E") or ""
            m = re.search(r"N/A\s*[—–:-]?\s*(.*)", e2e)
            reason = re.sub(r"user_facing\s*:\s*false", "", m.group(1) if m else "", flags=re.IGNORECASE).strip(" .;—–-")
            if len(reason) < 15:
                add("ERROR" if status == "proposed" else "WARN", sid,
                    "user_facing: false sem justificativa: na seção Testes E2E escreva \"N/A — <por que nenhuma jornada "
                    "de usuário (UI ou API pública) é afetada>\"")
        if doc.type == "migration" and not doc.list("adrs"):
            add("ERROR", sid, "type migration exige o ADR da nova arquitetura/padrão em adrs")
        validate_contract_map(doc, defined, add)
    else:
        if project.consumers(sid):
            add("WARN", sid, "outra spec consome o contrato desta spec lite — contrato público pede tier full")
        if user_facing == "true" and "E2E" not in kinds:
            add("WARN", sid, "defeito em jornada de usuário: o teste de regressão deveria ser E2E (E2E-xx)")

    checklist = doc.section("Checklist")
    if checklist is not None:
        boxes = CHECKBOX_RE.findall(COMMENT_RE.sub("", checklist))
        if status in ("in-progress", "implemented") and not boxes:
            add("ERROR", sid, "checklist de implementação vazio — deve ser preenchido na fase PLAN")
        elif status == "approved" and not boxes:
            add("WARN", sid, "checklist de implementação ainda não preenchido (fase PLAN)")
        unchecked = sum(1 for b in boxes if b == " ")
        if status == "implemented" and unchecked:
            add("ERROR", sid, f"{unchecked} item(ns) do checklist não marcado(s)")

    gates = gate_table(doc)
    if gates is not None:
        missing = [g for g in GATE_ROWS if g not in gates]
        if missing:
            add("ERROR", sid, f"registro de gates sem as linhas: {', '.join(missing)}")
        for g, info in gates.items():
            if info["status"] not in GATE_STATUSES:
                add("ERROR", sid, f"gate {g}: status '{info['status']}' inválido (PENDING | PASS | FAIL | N/A)")
            elif info["status"] in ("PASS", "N/A") and not info["evidence"]:
                add("ERROR", sid, f"gate {g} marcado {info['status']} sem evidência")
        manual = [g for g in ("G1", "G2", "G3", "G5", "G6") if gates.get(g, {}).get("evidence", "").startswith(MANUAL_PREFIX)]
        if manual:
            add("WARN", sid, f"evidência manual (não gerada pela ferramenta) em {', '.join(manual)} — prefira `gate {sid} <G> --run`")
        if status in APPROVED_PLUS and gates.get("G0", {}).get("status") != "PASS":
            add("WARN", sid, "G0 não registrado como PASS no registro de gates")
        if status == "implemented":
            pending = [g for g in GATE_ROWS if gates.get(g, {}).get("status") not in ("PASS", "N/A")]
            if pending:
                add("ERROR", sid, f"implemented exige todos os gates PASS/N/A — pendentes: {', '.join(pending)}")

    if status == "implemented":
        validate_delivery(doc, defined, add)


def validate_delivery(doc: Doc, defined, add):
    """Relatório de Entrega: what was done, how, and proof that every planned test passed (Definition of Done)."""
    sid = doc.id
    content = doc.section("Relatório de Entrega")
    if content is None:
        return
    subs = parse_subsections(COMMENT_RE.sub("", content))
    required = list(DELIVERY_SUBSECTIONS) + (["Prova de Correção"] if doc.type == "fix" else [])
    for name in required:
        body = find_sub(subs, name)
        if body is None:
            add("ERROR", sid, f"Relatório de Entrega sem a subseção '{name}'")
        elif not body.strip():
            add("ERROR", sid, f"Relatório de Entrega: '{name}' vazio")
    rows = {}
    for header, trs in parse_tables(find_sub(subs, "Verificação") or ""):
        cols = [norm(c) for c in header]
        if "resultado" not in cols:
            continue
        ri = cols.index("resultado")
        ei = cols.index("evidencia") if "evidencia" in cols else None
        for r in trs:
            for tid in TEST_ID_RE.findall(r[0] if r else ""):
                result = r[ri] if ri < len(r) else ""
                evidence = r[ei] if ei is not None and ei < len(r) else ""
                rows[tid] = (result.upper(), evidence.strip())
    missing = [t for t in sorted(defined, key=test_sort_key) if t not in rows]
    if missing:
        add("ERROR", sid, f"Verificação não comprova os testes do plano: {', '.join(missing)}")
    failing = sorted((t for t, (res, _) in rows.items() if "PASS" not in res), key=test_sort_key)
    if failing:
        add("ERROR", sid, f"Verificação com testes sem PASS: {', '.join(failing)}")
    no_evidence = sorted((t for t, (res, ev) in rows.items() if "PASS" in res and not ev), key=test_sort_key)
    if no_evidence:
        add("ERROR", sid, f"Verificação sem evidência (CI, commit ou relatório): {', '.join(no_evidence)}")
    dod = find_sub(subs, "Definição de Pronto") or ""
    if not CHECKBOX_RE.search(dod):
        add("ERROR", sid, "Definição de Pronto sem itens")
    elif UNCHECKED_RE.search(dod):
        add("ERROR", sid, "Definição de Pronto com itens não marcados — não está pronto")


def validate_contract_map(doc: Doc, defined, add):
    sid = doc.id
    content = COMMENT_RE.sub("", doc.section("Contrato") or "")
    tables = [(h, rows) for h, rows in parse_tables(content) if any(norm(c) == "testes" for c in h)]
    if not tables:
        add("ERROR", sid, "contrato sem 'Mapa de Comportamentos' (tabela com coluna 'Testes')")
        return
    for header, rows in tables:
        col = next(i for i, c in enumerate(header) if norm(c) == "testes")
        if not rows:
            add("ERROR", sid, "Mapa de Comportamentos sem linhas")
        for row in rows:
            label = row[0] if row and row[0] else "?"
            refs = TEST_ID_RE.findall(row[col] if col < len(row) else "")
            if not refs:
                add("ERROR", sid, f"comportamento '{label}' sem teste associado")
                continue
            missing = [r for r in refs if r not in defined]
            if missing:
                add("ERROR", sid, f"comportamento '{label}' referencia teste ausente do plano: {', '.join(missing)}")


def gate_table(doc: Doc):
    content = doc.section("Registro de Gates")
    if content is None:
        return None
    gates = {}
    for _, rows in parse_tables(COMMENT_RE.sub("", content)):
        for row in rows:
            words = row[0].split() if row else []
            key = words[0].upper() if words else ""
            if key in GATE_ROWS:
                cell = (row[1] if len(row) > 1 else "").strip().upper()
                status = next((s for s in GATE_STATUSES if s in cell), cell)
                gates[key] = {"status": status, "evidence": (row[2] if len(row) > 2 else "").strip()}
    return gates


def normalize_imp_type(value: str):
    key = norm(value or "").strip()
    return next((t for t in IMPEDIMENT_TYPES if norm(t) == key), None)


def impediments(doc: Doc):
    content = doc.section("Registro de Impedimentos")
    if content is None:
        return []
    items = []
    for header, rows in parse_tables(COMMENT_RE.sub("", content)):
        cols = [norm(c) for c in header]
        if "id" not in cols:
            continue
        idx = {name: cols.index(norm(name)) for name in IMPEDIMENT_COLUMNS if norm(name) in cols}
        for row in rows:
            item = {name: (row[i].strip().replace("\\|", "|") if i < len(row) else "") for name, i in idx.items()}
            for name in IMPEDIMENT_COLUMNS:
                item.setdefault(name, "")
            if item["ID"]:
                items.append(item)
    return items


def open_impediments(doc: Doc):
    return [i for i in impediments(doc) if not i["Fechado em"]]


def age_days(date: str) -> str:
    try:
        return f"{(dt.date.today() - dt.date.fromisoformat(date)).days}d"
    except ValueError:
        return "?"


def impediment_label(item) -> str:
    kind = normalize_imp_type(item["Tipo"]) or item["Tipo"]
    return f"{item['ID']} ({kind} → {item['Responsável'] or '?'}, {age_days(item['Aberto em'])})"


def validate_questions(doc: Doc, add):
    content = doc.section("Questões em Aberto")
    if content is None:
        return
    lines = COMMENT_RE.sub("", content).split("\n")
    pending = [l for l in lines if re.match(r"^\s*[-*] \[ \]", l)]
    if pending:
        add("ERROR", doc.id, f"{len(pending)} questão(ões) em aberto sem resposta — responda e registre antes do H1")
    for line in lines:
        if re.match(r"^\s*[-*] \[[xX]\]", line) and not re.search(r"\s[—–-]\s", line.split("]", 1)[1]):
            add("WARN", doc.id, f"questão marcada sem resposta registrada ('pergunta — resposta'): '{line.strip()[6:60]}'")


def validate_impediments(project: Project, doc: Doc, add):
    sid = doc.id
    emendas = COMMENT_RE.sub("", doc.section("Emendas") or "")
    amendment_rows = sum(len(rows) for _, rows in parse_tables(emendas))
    seen = set()
    for item in impediments(doc):
        iid = item["ID"]
        if not IMP_ID_RE.match(iid):
            add("ERROR", sid, f"impedimento com ID inválido '{iid}' (esperado IMP-NN)")
        if iid in seen:
            add("ERROR", sid, f"impedimento {iid} duplicado")
        seen.add(iid)
        kind = normalize_imp_type(item["Tipo"])
        if kind is None:
            add("ERROR", sid, f"{iid}: tipo '{item['Tipo']}' inválido ({' | '.join(IMPEDIMENT_TYPES)})")
        if not valid_date(item["Aberto em"]):
            add("ERROR", sid, f"{iid}: 'Aberto em' deve ser uma data YYYY-MM-DD")
        if not item["Descrição"]:
            add("ERROR", sid, f"{iid}: sem descrição do que falta")
        if not item["Responsável"]:
            add("ERROR", sid, f"{iid}: sem responsável — quem precisa agir?")
        closed, resolution = item["Fechado em"], item["Resolução"]
        if not closed:
            if doc.status == "implemented":
                add("ERROR", sid, f"{iid} aberto — spec não pode estar implemented com impedimento aberto")
            elif doc.status == "deprecated":
                add("WARN", sid, f"{iid} aberto em spec deprecated — feche registrando o cancelamento como resolução")
            continue
        if not valid_date(closed):
            add("ERROR", sid, f"{iid}: 'Fechado em' deve ser uma data YYYY-MM-DD")
        if not resolution:
            add("ERROR", sid, f"{iid}: fechado sem resolução")
        elif kind == "spec" and ("emenda" not in norm(resolution) or not amendment_rows):
            add("ERROR", sid, f"{iid}: impedimento de spec se resolve por emenda — cite-a (ex: 'Emenda v2') e registre-a na seção Emendas")
        elif kind == "trabalho":
            refs = SPEC_REF_RE.findall(resolution)
            if not refs:
                add("ERROR", sid, f"{iid}: impedimento de trabalho se resolve com uma spec — cite o SPEC-NNNN criado")
            for ref in refs:
                if ref not in project.specs:
                    add("ERROR", sid, f"{iid}: resolução cita {ref}, que não existe")
        elif kind == "decisão":
            for ref in ADR_REF_RE.findall(resolution):
                if ref not in project.adrs:
                    add("ERROR", sid, f"{iid}: resolução cita {ref}, que não existe em docs/adr/")


def validate_epic(project: Project, doc: Doc, add):
    sid = doc.id
    kids = project.children(sid)
    if doc.list("touches") or doc.list("consumes_contract"):
        add("WARN", sid, "épico não executa código: mova touches/consumes_contract para as specs filhas")
    if doc.status in APPROVED_PLUS and not kids:
        add("ERROR", sid, f"épico aprovado sem specs filhas (nenhuma spec com parent: {sid})")
    decomposition = COMMENT_RE.sub("", doc.section("Decomposição") or "")
    # Only the first column lists children; other columns (e.g. "Depende de") may cite specs of other epics.
    listed = {ref for _, rows in parse_tables(decomposition) for row in rows if row
              for ref in SPEC_REF_RE.findall(row[0])}
    for kid in kids:
        if kid.id not in decomposition:
            add("WARN", sid, f"filha {kid.id} não aparece na seção Decomposição")
    for ref in sorted(set(SPEC_REF_RE.findall(decomposition))):
        if ref not in project.specs:
            add("WARN", sid, f"Decomposição cita {ref}, que ainda não existe")
        elif ref in listed and project.specs[ref].scalar("parent") != sid:
            add("WARN", sid, f"Decomposição lista {ref} como filha, mas ela não tem parent: {sid}")
    criteria = COMMENT_RE.sub("", doc.section("Critérios de Aceite") or "")
    level = "ERROR" if doc.status == "implemented" else "WARN"
    for line in (l for l in criteria.split("\n") if CHECKBOX_RE.match(l)):
        label = re.sub(r"^\s*[-*] \[.\]\s*", "", line)[:60]
        refs = TEST_TAG_RE.findall(line)
        if not refs:
            add(level, sid, f"critério de aceite sem teste que o prove (SPEC-NNNN:E2E-xx): '{label}'")
        for spec_ref, tid in refs:
            child = project.specs.get(spec_ref)
            if child is None:
                add(level, sid, f"critério cita {spec_ref}:{tid}, mas {spec_ref} não existe")
            elif tid not in plan_test_ids(child):
                add(level, sid, f"critério cita {spec_ref}:{tid}, que não está no plano de testes de {spec_ref}")
    open_kids = [k.id for k in kids if k.status not in DONE]
    if doc.status == "implemented":
        if open_kids:
            add("ERROR", sid, f"épico implemented com filhas abertas: {', '.join(open_kids)}")
        if UNCHECKED_RE.search(criteria):
            add("ERROR", sid, "critérios de aceite do épico não marcados")
        if not COMMENT_RE.sub("", doc.section("Relatório de Entrega") or "").strip():
            add("ERROR", sid, "Relatório de Entrega do épico vazio")
    elif kids and not open_kids and doc.status != "deprecated":
        add("WARN", sid, "todas as filhas concluídas — valide os critérios de aceite e feche o épico")
    if doc.status == "proposed":
        early = [k.id for k in kids if k.status in APPROVED_PLUS]
        if early:
            add("WARN", sid, f"filhas aprovadas antes do épico: {', '.join(early)}")


def validate_adr(project: Project, adr: Doc, add):
    aid = adr.id
    if not ADR_ID_RE.match(aid):
        add("ERROR", aid or adr.path.name, "id inválido (esperado ADR-NNNN)")
        return
    if adr.path.stem != aid and not adr.path.name.startswith(aid + "-"):
        add("ERROR", aid, f"nome do arquivo deve começar com '{aid}-'")
    if adr.status not in ADR_STATUSES:
        add("ERROR", aid, f"status inválido '{adr.status}' ({' | '.join(ADR_STATUSES)})")
    origin = adr.scalar("origin").lower()
    if origin and origin not in ADR_ORIGINS:
        add("ERROR", aid, f"origin inválido '{origin}' ({' | '.join(ADR_ORIGINS)})")
    if not PLACEHOLDER_RE.search(COMMENT_RE.sub("", adr.text)) and adr.status in ("proposed", "accepted"):
        validate_adr_content(adr, origin, add)
    if PLACEHOLDER_RE.search(COMMENT_RE.sub("", adr.text)):
        add("ERROR", aid, "marcadores {{...}} não preenchidos")
    if adr.status == "accepted" and not adr.scalar("enforced_by"):
        add("WARN", aid, "ADR aceito sem enforced_by — informe o teste de arquitetura que o garante (G3) ou 'N/A — motivo'")
    if adr.status == "superseded":
        by = adr.scalar("superseded_by")
        if not by:
            add("ERROR", aid, "superseded exige superseded_by")
        elif by not in project.adrs:
            add("ERROR", aid, f"superseded_by: {by} não existe")
    sup = adr.scalar("supersedes")
    if sup and sup not in project.adrs:
        add("ERROR", aid, f"supersedes: {sup} não existe")


def validate_adr_content(adr: Doc, origin: str, add):
    """Decision quality: options and sources for recommended decisions; risks recorded for user-chosen ones."""
    aid = adr.id
    options = COMMENT_RE.sub("", adr.section("Opções") or "")
    bullets = [l for l in options.split("\n") if re.match(r"^\s*[-*]\s+\S", l) and not re.match(r"^\s*[-*]\s+N/A", l, re.IGNORECASE)]
    sources = COMMENT_RE.sub("", (adr.section("Mais Informações") or "") + (adr.section("Fontes") or ""))
    consequences = COMMENT_RE.sub("", adr.section("Consequências") or adr.section("Resultado da Decisão") or "")
    if origin in ("", "decision"):
        if len(bullets) < 2:
            add("WARN", aid, "decisão recomendada com menos de 2 opções consideradas — compare alternativas reais "
                             "(ou use origin: user se a escolha veio do usuário, ou as-is se já existia)")
        if not re.search(r"https?://", sources) and not re.search(r"n[aã]o verificad", norm(sources)):
            add("WARN", aid, "sem fontes (links oficiais com data) nem declaração explícita de 'não verificado' em Mais Informações")
    elif origin == "user":
        if not re.search(r"ruim|risco|trade-?off|limita", norm(consequences)):
            add("WARN", aid, "escolha do usuário sem riscos registrados — respeite a escolha, mas registre riscos e custos em Consequências")


def find_cycles(project: Project):
    graph = {sid: [d for d in doc.list("depends_on") if d in project.specs] for sid, doc in project.specs.items()}
    color, stack, cycles = {}, [], []

    def visit(node):
        color[node] = 1
        stack.append(node)
        for nxt in graph[node]:
            if color.get(nxt) == 1:
                cycles.append(stack[stack.index(nxt):] + [nxt])
            elif nxt not in color:
                visit(nxt)
        stack.pop()
        color[node] = 2

    for node in sorted(graph):
        if node not in color:
            visit(node)
    return cycles


# ---------------------------------------------------------------- planning

def touches_overlap(a: Doc, b: Doc) -> bool:
    ta, tb = a.list("touches"), b.list("touches")
    if not ta or not tb:
        return True
    return any(patterns_overlap(x, y) for x in ta for y in tb)


def readiness(project: Project, doc: Doc):
    stops = "; ".join(impediment_label(i) for i in open_impediments(doc))
    if doc.status == "in-progress":
        if stops:
            return "stalled", f"🚧 parada por impedimento: {stops}"
        return "running", "🔄 em andamento"
    if doc.status == "proposed":
        return "blocked", "⏳ aguardando aprovação (H1)" + (f"; 🚧 impedimento: {stops}" if stops else "")
    blockers = [f"impedimento {stops}"] if stops else []
    for dep in doc.list("depends_on"):
        d = project.specs.get(dep)
        if d is None or d.status != "implemented":
            blockers.append(f"aguarda implementação de {dep}")
    for pid, ver, _ in contract_refs(doc):
        provider = project.specs.get(pid) if pid else None
        if provider is None:
            continue
        if provider.status not in APPROVED_PLUS:
            blockers.append(f"aguarda aprovação do contrato de {pid}")
        elif ver is None or ver < contract_version(provider):
            blockers.append(f"contrato de {pid} mudou (v{ver} → v{contract_version(provider)}): revisar spec")
    if not doc.list("touches"):
        blockers.append("touches vazio")
    if blockers:
        return "blocked", "⛔ " + "; ".join(blockers)
    return "ready", "✅ pronta"


def compute_waves(project: Project):
    specs = project.specs
    nodes = {sid for sid, d in specs.items() if d.status in OPEN and d.tier != "epic" and SPEC_ID_RE.match(sid)}
    deps = {sid: [d for d in specs[sid].list("depends_on") if d in nodes] for sid in nodes}
    indegree = {sid: len(deps[sid]) for sid in nodes}
    dependents = defaultdict(list)
    for sid, ds in deps.items():
        for d in ds:
            dependents[d].append(sid)
    heap = [(STATUS_RANK[specs[s].status], s) for s in nodes if indegree[s] == 0]
    heapq.heapify(heap)
    order = []
    while heap:
        _, sid = heapq.heappop(heap)
        order.append(sid)
        for t in dependents[sid]:
            indegree[t] -= 1
            if indegree[t] == 0:
                heapq.heappush(heap, (STATUS_RANK[specs[t].status], t))
    if len(order) < len(nodes):
        return None
    max_parallel = project.max_parallel()
    wave, members, notes = {}, defaultdict(list), defaultdict(list)
    for sid in order:
        w = 1 + max((wave[d] for d in deps[sid]), default=0)
        while True:
            clash = [o for o in members[w] if touches_overlap(specs[sid], specs[o])]
            if clash:
                reason = ("touches vazio, não roda em paralelo" if not specs[sid].list("touches")
                          else f"arquivos em comum com {', '.join(clash)}")
                notes[sid].append(f"saiu da onda {w}: {reason}")
                w += 1
                continue
            if max_parallel and len(members[w]) >= max_parallel:
                notes[sid].append(f"saiu da onda {w}: max_parallel={max_parallel}")
                w += 1
                continue
            break
        wave[sid] = w
        members[w].append(sid)
    return wave, dict(members), notes


def next_batch(project: Project, plan):
    wave = plan[0]
    specs = project.specs
    running = sorted(s for s in wave if specs[s].status == "in-progress")
    ready = sorted((s for s in wave if readiness(project, specs[s])[0] == "ready"), key=lambda s: (wave[s], s))
    max_parallel = project.max_parallel()
    batch, held = [], []
    for sid in ready:
        clash = [o for o in running + batch if touches_overlap(specs[sid], specs[o])]
        if clash:
            held.append((sid, f"arquivos em comum com {', '.join(clash)}"))
        elif max_parallel and len(running) + len(batch) >= max_parallel:
            held.append((sid, f"limite max_parallel={max_parallel}"))
        else:
            batch.append(sid)
    return running, batch, held


def require_plan(project: Project):
    plan = compute_waves(project)
    if plan is None:
        cycles = find_cycles(project)
        die("ciclo em depends_on impede o planejamento: " + "; ".join(" → ".join(c) for c in cycles), 1)
    return plan


# ---------------------------------------------------------------- commands

def cmd_validate(project: Project, ids, staged: bool = False, exclude_drafts: bool = False):
    only = None
    if staged:
        files = run_git(project.root, "diff", "--cached", "--name-only")[1].splitlines()
        ids = sorted({m.group(0) for f in files for m in [re.search(r"(SPEC|ADR)-\d{4,}", Path(f).name)]
                      if m and f.startswith(("docs/specs/", "docs/adr/"))})
        ids = [i for i in ids if not (i in project.specs and project.specs[i].status == "proposed")]
        if not ids:
            print("validate --staged: nenhuma spec/ADR não-rascunho no stage")
            return 0
    if exclude_drafts and not ids:
        drafts = {sid for sid, d in project.specs.items() if d.status == "proposed"}
        issues = [i for i in validate(project) if i[1] not in drafts]
        return report_issues(issues, f"projeto inteiro, exceto {len(drafts)} rascunho(s)")
    if ids:
        unknown = [i for i in ids if i not in project.specs and i not in project.adrs]
        if unknown:
            die(f"não encontrado(s): {', '.join(unknown)}")
        only = set(ids)
        for i in ids:
            if i in project.specs and project.specs[i].tier == "epic":
                only |= {k.id for k in project.children(i)}
    return report_issues(validate(project, only), ", ".join(sorted(only)) if only else "projeto inteiro")


def ci_annotate(level: str, message: str):
    """When running in GitHub Actions, surface findings as annotations on the PR (plain text elsewhere)."""
    if os.environ.get("GITHUB_ACTIONS") != "true" or level not in ("ERROR", "FAIL", "WARN"):
        return
    kind = "warning" if level == "WARN" else "error"
    text = message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    print(f"::{kind} title=SDD::{text}")


def report_issues(issues, scope: str) -> int:
    errors = [i for i in issues if i[0] == "ERROR"]
    warns = [i for i in issues if i[0] == "WARN"]
    for level, subject, msg in sorted(issues, key=lambda x: (x[1], x[0] != "ERROR")):
        print(f"{level:<5} {subject}: {msg}")
        ci_annotate(level, f"{subject}: {msg}")
    print(f"\nG0 [{scope}]: {'PASS' if not errors else 'FAIL'} — {len(errors)} erro(s), {len(warns)} aviso(s)")
    return 1 if errors else 0


def cmd_waves(project: Project):
    plan = require_plan(project)
    wave, members, notes = plan
    if not wave:
        print("Nenhuma spec aberta para planejar.")
        return 0
    specs = project.specs
    for w in sorted(members):
        print(f"\nOnda {w}")
        for sid in members[w]:
            d = specs[sid]
            print(f"  {sid}  {d.status:<11} {d.tier}/{d.scalar('size') or '?'}  {d.title}")
            print(f"             {readiness(project, d)[1]}")
            for note in notes.get(sid, []):
                print(f"             ↳ {note}")
    print()
    print_next(project, plan)
    return 0


def print_next(project: Project, plan):
    running, batch, held = next_batch(project, plan)
    specs = project.specs
    active = [s for s in running if readiness(project, specs[s])[0] == "running"]
    if active:
        print("Em andamento: " + ", ".join(active))
    for sid in running:
        state, label = readiness(project, specs[sid])
        if state == "stalled":
            print(f"Parada {sid}: {label}")
    for epic in sorted((d for d in specs.values() if d.tier == "epic" and d.status in OPEN), key=lambda d: d.id):
        for item in open_impediments(epic):
            print(f"Impedimento no épico {epic.id}: {impediment_label(item)} — {item['Descrição']}")
    if batch:
        print("Próximo lote (despachar em paralelo): " + ", ".join(batch))
        for sid in batch:
            d = specs[sid]
            print(f"  {sid}  {d.title}  → {', '.join(d.list('touches'))}")
    else:
        print("Nenhuma spec pronta para despachar agora.")
    for sid, why in held:
        print(f"  aguardando {sid}: {why}")
    blocked = [s for s in plan[0] if readiness(project, specs[s])[0] == "blocked"]
    for sid in sorted(blocked):
        print(f"  bloqueada {sid}: {readiness(project, specs[sid])[1]}")


def cmd_next(project: Project):
    print_next(project, require_plan(project))
    return 0


def mermaid_label(doc: Doc) -> str:
    title = doc.title.replace('"', "'")
    if len(title) > 40:
        title = title[:39] + "…"
    return f'{doc.id}<br/>{title}'


def build_index(project: Project) -> str:
    specs = project.specs
    plan = compute_waves(project)
    issues = validate(project)
    errors = sum(1 for i in issues if i[0] == "ERROR")
    warns = sum(1 for i in issues if i[0] == "WARN")
    out = [
        "# Índice de Specs",
        "",
        f"> Gerado por `spec_graph.py index` em {today()} — não edite manualmente.",
        "",
        "## Saúde",
        "",
        f"- Validação (G0): **{errors} erro(s), {warns} aviso(s)**" + (" — rode `spec_graph.py validate`" if issues else ""),
    ]
    counts = Counter(d.status for d in specs.values() if d.tier != "epic")
    out.append("- Specs: " + (", ".join(f"{s} {counts[s]}" for s in STATUSES if counts[s]) or "nenhuma"))
    all_imps = [(d, i) for d in sorted(specs.values(), key=lambda d: d.id) for i in impediments(d)]
    open_imps = [(d, i) for d, i in all_imps if not i["Fechado em"]]
    out.append(f"- Impedimentos: **{len(open_imps)} aberto(s)**, {len(all_imps) - len(open_imps)} resolvido(s)")
    out.append("")
    if open_imps:
        out += ["## Impedimentos Abertos", "",
                "| Spec | ID | Tipo | Fase/Gate | Responsável | Aberto em | Idade | Descrição |",
                "|---|---|---|---|---|---|---|---|"]
        for d, i in open_imps:
            out.append(f"| {d.id} | {i['ID']} | {normalize_imp_type(i['Tipo']) or md_cell(i['Tipo'])} | {md_cell(i['Fase/Gate'])} "
                       f"| {md_cell(i['Responsável'])} | {i['Aberto em']} | {age_days(i['Aberto em'])} | {md_cell(i['Descrição'])} |")
        out.append("")

    coverage = pillar_coverage(project)
    if coverage:
        out += ["## Cobertura de Pilares", "", f"Perfil: **{pillar_settings(project)[0]}**", "",
                "| Pilar | Situação | Detalhe |", "|---|---|---|"]
        out += [f"| {md_cell(title)} | {state} | {md_cell(detail)} |" for _, title, state, detail in coverage]
        out.append("")
    journeys, _ = journey_coverage(project)
    if journeys:
        out += ["## Jornadas Críticas", "", "| Jornada | Situação | Specs |", "|---|---|---|"]
        out += [f"| {md_cell(title)} | {state} | {', '.join(sids) or '—'} |" for _, title, state, sids in journeys]
        out.append("")
    out += ["## Plano de Execução", ""]
    if plan is None:
        out.append("⛔ Ciclo em `depends_on` — planejamento impossível até ser resolvido.")
    elif not plan[0]:
        out.append("Nenhuma spec aberta.")
    else:
        _wave, members, notes = plan
        running, batch, _held = next_batch(project, plan)
        stalled = [s for s in running if readiness(project, specs[s])[0] == "stalled"]
        out.append(f"**Em andamento:** {', '.join(s for s in running if s not in stalled) or '—'}  ")
        out.append(f"**Paradas por impedimento:** {', '.join(stalled) or '—'}  ")
        out.append(f"**Próximo lote:** {', '.join(batch) or '—'}")
        out += ["", "| Onda | Spec | Título | Tier/Tam. | Status | Prontidão | Observação |", "|---|---|---|---|---|---|---|"]
        for w in sorted(members):
            for sid in members[w]:
                d = specs[sid]
                note = "; ".join(notes.get(sid, [])) or ""
                out.append(f"| {w} | {sid} | {md_cell(d.title)} | {d.tier}/{d.scalar('size') or '?'} | {d.status} "
                           f"| {md_cell(readiness(project, d)[1])} | {md_cell(note)} |")
    out.append("")

    epics = sorted((d for d in specs.values() if d.tier == "epic"), key=lambda d: d.id)
    if epics:
        out += ["## Épicos", "", "| Épico | Título | Status | Progresso |", "|---|---|---|---|"]
        for e in epics:
            kids = project.children(e.id)
            done = sum(1 for k in kids if k.status == "implemented")
            active = [k for k in kids if k.status != "deprecated"]
            out.append(f"| {e.id} | {md_cell(e.title)} | {e.status} | {done}/{len(active)} implementadas |")
        out.append("")

    open_ids = {sid for sid, d in specs.items() if d.status in OPEN and d.tier != "epic"}
    referenced = set()
    for sid in open_ids:
        referenced |= set(specs[sid].list("depends_on"))
        referenced |= {pid for pid, _, _ in contract_refs(specs[sid]) if pid}
    graph_ids = sorted(i for i in open_ids | referenced if i in specs and specs[i].tier != "epic")
    if graph_ids:
        out += ["## Grafo de Dependências", "",
                "Seta contínua: depende da implementação. Seta tracejada: consome contrato.", "", "```mermaid", "flowchart LR"]
        grouped = defaultdict(list)
        for sid in graph_ids:
            grouped[specs[sid].scalar("parent")].append(sid)
        node = lambda sid: "S" + sid.split("-")[1]
        klass = lambda sid: specs[sid].status.replace("-", "")
        for parent in sorted(grouped):
            indent = "  "
            if parent and parent in specs:
                out.append(f'  subgraph E{parent.split("-")[1]}["{parent} · {specs[parent].title.replace(chr(34), chr(39))}"]')
                indent = "    "
            for sid in grouped[parent]:
                out.append(f'{indent}{node(sid)}["{mermaid_label(specs[sid])}"]:::{klass(sid)}')
            if parent and parent in specs:
                out.append("  end")
        for sid in graph_ids:
            d = specs[sid]
            for dep in d.list("depends_on"):
                if dep in graph_ids:
                    out.append(f"  {node(dep)} --> {node(sid)}")
            for pid, ver, _ in contract_refs(d):
                if pid in graph_ids:
                    out.append(f"  {node(pid)} -. contrato v{ver or '?'} .-> {node(sid)}")
        out += [
            "  classDef proposed fill:#fef3c7,stroke:#d97706,color:#111",
            "  classDef approved fill:#dbeafe,stroke:#2563eb,color:#111",
            "  classDef inprogress fill:#ede9fe,stroke:#7c3aed,color:#111",
            "  classDef implemented fill:#dcfce7,stroke:#16a34a,color:#111",
            "  classDef deprecated fill:#f3f4f6,stroke:#9ca3af,color:#6b7280",
            "```", "",
        ]

    out += ["## Todas as Specs", "",
            "| ID | Título | Tier | Tipo | Status | Criada | Épico | Depende de | Consome contrato |",
            "|---|---|---|---|---|---|---|---|---|"]
    for sid in sorted(specs):
        d = specs[sid]
        out.append(f"| [{sid}]({d.path.name}) | {md_cell(d.title)} | {d.tier} | {d.type or '—'} | {d.status} | {d.scalar('created')} "
                   f"| {d.scalar('parent') or '—'} | {', '.join(d.list('depends_on')) or '—'} "
                   f"| {', '.join(d.list('consumes_contract')) or '—'} |")
    out.append("")

    if project.adrs:
        out += ["## ADRs", "", "| ID | Título | Status | Garantido por (G3) |", "|---|---|---|---|"]
        for aid in sorted(project.adrs):
            a = project.adrs[aid]
            out.append(f"| [{aid}](../adr/{a.path.name}) | {md_cell(a.title)} | {a.status} | {md_cell(a.scalar('enforced_by') or '—')} |")
        out.append("")
    return "\n".join(out)


def cmd_index(project: Project, stdout: bool):
    text = build_index(project)
    if stdout:
        print(text)
        return 0
    project.specs_dir.mkdir(parents=True, exist_ok=True)
    dest = project.specs_dir / "INDEX.md"
    dest.write_text(text, encoding="utf-8")
    print(f"escrito: {project.rel(dest)}")
    return 0


def run_git(root: Path, *args, input=None):
    result = subprocess.run(["git", *args], check=False, cwd=root, capture_output=True, text=True, input=input)
    return result.returncode, result.stdout, result.stderr


TEST_TARGET_RE = re.compile(r"(?:→|->)\s*`?(\S+?)::(\S+?)`?\s*$")
LEDGER_SECTION = "Commits"


def role_by_files(files, test_paths) -> str:
    """Modo privado: papel do commit pelos arquivos — red (só test_paths) ou green (algum arquivo fora)."""
    return "red" if files and all(matches_any(f, test_paths) for f in files) else "green"


def parse_test_target(line: str):
    """Linha do plano com `→ caminho::nome` (ou `->`): (caminho, nome), ou None sem mapeamento."""
    m = TEST_TARGET_RE.search(line.strip())
    return (m.group(1), m.group(2)) if m else None


def plan_test_targets(doc: Doc) -> dict:
    """ID do teste → (caminho, nome), para as linhas do plano que apontam `→ caminho::nome`."""
    targets = {}
    for line in COMMENT_RE.sub("", doc.section("Plano de Testes") or "").splitlines():
        ids = TEST_ID_RE.findall(line)
        target = parse_test_target(line)
        if ids and target:
            targets.setdefault(ids[0], target)
    return targets


def patch_id(root: Path, diff: str) -> str:
    out = run_git(root, "patch-id", "--stable", input=diff)[1].split()
    return out[0] if out else ""


def spec_commits(project: Project, spec_id: str, root: Path, base: str):
    """Commits da spec em merge-base..HEAD: por mensagem (modo completo) ou pela branch inteira (modo privado)."""
    code, merge_base, err = run_git(root, "merge-base", base, "HEAD")
    if code != 0:
        die(f"não foi possível achar merge-base com '{base}': {err.strip()}")
    merge_base = merge_base.strip()
    _, log, _ = run_git(root, "log", "--reverse", "--no-merges", "--format=%H%x1f%s%x1f%b%x1e", f"{merge_base}..HEAD")
    test_paths = project.cfg_list("test_paths", DEFAULT_TEST_PATHS)
    commits = []
    for chunk in log.split("\x1e"):
        if not chunk.strip():
            continue
        parts = chunk.strip("\n").split("\x1f")
        sha, subject = parts[0].strip(), parts[1] if len(parts) > 1 else ""
        body = parts[2] if len(parts) > 2 else ""
        files = run_git(root, "diff-tree", "--no-commit-id", "--name-only", "-r", "--root", sha)[1].split()
        if "scaffold" in subject.lower() and (project.private or subject.lower().startswith("chore")):
            kind = "scaffold"
        elif project.private:
            kind = role_by_files(files, test_paths)
        else:
            m = CONVENTIONAL_RE.match(subject)
            ctype = m.group(1).lower() if m else ""
            kind = "red" if ctype in RED_TYPES else "green" if ctype in GREEN_TYPES else "other"
        commits.append({"sha": sha, "subject": subject, "body": body, "kind": kind, "files": files})
    if project.private:
        return merge_base, commits, commits
    return merge_base, commits, [c for c in commits if spec_id in commit_spec_refs(c["subject"], c["body"])]


REFS_TRAILER_RE = re.compile(r"^Refs:\s*(.+)$", re.MULTILINE | re.IGNORECASE)


def commit_spec_refs(subject: str, body: str) -> set:
    """Specs a que o commit pertence. Com rodapé `Refs:`, só ele conta (o corpo pode citar outras specs como
    contexto, ex.: "consome SPEC-0005@2"); sem rodapé, vale qualquer menção no assunto ou no corpo."""
    from_trailer = set(SPEC_REF_RE.findall("\n".join(REFS_TRAILER_RE.findall(body))))
    if from_trailer:  # um `Refs: #12` sem spec não esconde o commit: cai na regra das menções
        return from_trailer
    return set(SPEC_REF_RE.findall(subject + "\n" + body))


def write_commit_ledger(project: Project, doc: Doc, root: Path):
    """Modo privado: grava na spec (spec_root) os commits da branch com papel, SHA, patch-id e arquivos."""
    merge_base, _, mine = spec_commits(project, doc.id, root, project.cfg("base_branch", "main"))
    if not mine:
        return
    rows = []
    for c in mine:
        pid = patch_id(root, run_git(root, "show", "--format=", c["sha"])[1])
        rows.append(f"| {c['kind']} | {c['sha']} | {pid} | {md_cell(', '.join(c['files']))} |")
    head = run_git(root, "rev-parse", "HEAD")[1].strip()
    rows.append(f"| branch | {head} | {patch_id(root, run_git(root, 'diff', merge_base, 'HEAD')[1])} | — |")
    block = [f"## {LEDGER_SECTION}",
             "<!-- Modo privado: gravado pelo gate --run. Liga a spec aos commits da branch sem marcas no repositório. -->",
             "| Papel | SHA | patch-id | Arquivos |", "|---|---|---|---|", *rows, ""]
    lines = doc.path.read_text(encoding="utf-8").split("\n")
    bounds = section_bounds(lines, LEDGER_SECTION)
    if bounds:
        lines[bounds[0]:bounds[1]] = block
    else:
        lines = lines + ([""] if lines and lines[-1].strip() else []) + block
    doc.path.write_text("\n".join(lines).rstrip("\n") + "\n", encoding="utf-8")


def ledger_rows(doc_path: Path):
    lines = doc_path.read_text(encoding="utf-8").split("\n")
    bounds = section_bounds(lines, LEDGER_SECTION)
    rows = []
    for line in lines[bounds[0] + 1:bounds[1]] if bounds else []:
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) >= 3 and cells[0] in ("red", "green", "scaffold", "branch"):
            rows.append({"role": cells[0], "sha": cells[1], "pid": cells[2]})
    return rows


def find_spec_merge(project: Project, doc: Doc, root: Path):
    """Modo privado: onde a spec entrou na base — merge commit, squash (patch-id da branch) ou rebase (patch-ids)."""
    rows = ledger_rows(doc.path)
    if not rows:
        return False, "registro de commits vazio — rode o gate G1 ou G2 na branch da spec antes"
    base = project.cfg("base_branch", "main")
    branch = next((r for r in rows if r["role"] == "branch"), None)
    commits = [r for r in rows if r["role"] != "branch"]
    head = branch["sha"] if branch else commits[-1]["sha"]
    if run_git(root, "merge-base", "--is-ancestor", head, base)[0] == 0:
        for merge in run_git(root, "rev-list", "--first-parent", "--merges", base)[1].split():
            if (run_git(root, "merge-base", "--is-ancestor", head, merge)[0] == 0
                    and run_git(root, "merge-base", "--is-ancestor", head, f"{merge}^1")[0] != 0):
                return True, f"merge {merge[:7]}"
        return True, f"merge (fast-forward) {head[:7]}"
    pids = {}
    for sha in run_git(root, "rev-list", "--first-parent", "--no-merges", "-n", "200", base)[1].split():
        pids.setdefault(patch_id(root, run_git(root, "show", "--format=", sha)[1]), sha)
    if branch and branch["pid"] in pids:
        return True, f"squash {pids[branch['pid']][:7]}"
    found = [pids.get(r["pid"]) for r in commits]
    if found and all(found):
        return True, f"rebase {found[0][:7]}..{found[-1][:7]}"
    return False, f"merge da spec não encontrado em {base}"


def verify_spec(project: Project, spec_id: str, base, git_root=None):
    doc = project.specs.get(spec_id)
    if doc is None:
        die(f"{spec_id} não encontrada")
    if doc.tier == "epic":
        die("verify roda por spec executável (lite/full), não por épico")
    root = Path(git_root) if git_root else project.root
    if run_git(root, "rev-parse", "--is-inside-work-tree")[0] != 0:
        die("não é um repositório git")
    base = base or project.cfg("base_branch", "main")
    merge_base, commits, mine = spec_commits(project, spec_id, root, base)
    test_paths = project.cfg_list("test_paths", DEFAULT_TEST_PATHS)
    exempt = project.scope_exempt()
    untraced = [] if project.private else [c for c in commits if not SPEC_REF_RE.search(c["subject"] + "\n" + c["body"])]
    short = lambda c: f"{c['sha'][:7]} {c['subject'][:72]}"
    is_test = lambda f: matches_any(f, test_paths)
    is_exempt = lambda f: matches_any(f, exempt)
    results = []

    reds = [i for i, c in enumerate(mine) if c["kind"] == "red"]
    greens = [i for i, c in enumerate(mine) if c["kind"] == "green"]
    if not mine:
        results.append(("FAIL", "G1", f"nenhum commit da spec em {base}..HEAD" if project.private
                        else f"nenhum commit referencia {spec_id} em {base}..HEAD"))
    elif not reds:
        results.append(("FAIL", "G1", "nenhum commit só de testes — a fase Red não foi registrada" if project.private
                        else "nenhum commit test(...) — a fase Red não foi registrada"))
    else:
        early = [mine[i] for i in greens if i < reds[0]]
        if early:
            results.append(("FAIL", "G1", "implementação antes dos testes: " + "; ".join(short(c) for c in early)))
        else:
            results.append(("PASS", "G1", f"Red antes do Green — primeiro red: {short(mine[reds[0]])}"))
        bad = sorted({f for i in reds for f in mine[i]["files"] if not is_test(f) and not is_exempt(f)})
        if bad:
            results.append(("FAIL", "G1", "commits test(...) alteram arquivos fora de test_paths: " + ", ".join(bad)
                            + " — scaffolding de contrato vai em commit chore(...): scaffold"))
        else:
            results.append(("PASS", "G1", "commits red só alteram testes"))

    plan_ids = plan_test_ids(doc)
    if not plan_ids:
        results.append(("FAIL", "G1", "plano de testes sem IDs"))
    elif project.private:
        targets, missing = plan_test_targets(doc), []
        for tid in plan_ids:
            target = targets.get(tid)
            if target is None:
                missing.append(f"{tid} (sem → caminho::nome no plano)")
                continue
            code, content, _ = run_git(root, "show", f"HEAD:{target[0]}")
            if code != 0 or target[1] not in content:
                missing.append(f"{tid} ({target[0]}::{target[1]})")
        if missing:
            results.append(("FAIL", "G1", "teste do plano sem caminho::nome no HEAD: " + ", ".join(missing)))
        else:
            results.append(("PASS", "G1", f"rastreabilidade: {len(plan_ids)}/{len(plan_ids)} testes do plano no código ({', '.join(plan_ids)})"))
    else:
        missing, outside = [], []
        for tid in plan_ids:
            tag = f"{spec_id}:{tid}"
            out = run_git(root, "grep", "-l", "-F", "-e", tag, "--", ".", ":(exclude)docs/specs")[1]
            found = [line for line in out.splitlines() if line.strip()]
            if not found:
                missing.append(tag)
            elif not any(is_test(f) for f in found):
                outside.append(tag)
        if missing:
            results.append(("FAIL", "G1", "testes do plano sem tag no código versionado: " + ", ".join(missing)))
        else:
            results.append(("PASS", "G1", f"rastreabilidade: {len(plan_ids)}/{len(plan_ids)} testes do plano no código ({', '.join(plan_ids)})"))
        if outside:
            results.append(("WARN", "G1", "tags encontradas só fora de test_paths: " + ", ".join(outside)))

    touches = doc.list("touches")
    changed = sorted({f for c in mine for f in c["files"]})
    out_of_scope = [f for f in changed if not is_exempt(f) and not matches_any(f, touches)]
    if not touches:
        results.append(("FAIL", "G4", "touches vazio — escopo não verificável"))
    elif out_of_scope:
        results.append(("FAIL", "G4", "arquivos alterados fora de touches: " + ", ".join(out_of_scope)))
    else:
        results.append(("PASS", "G4", f"escopo: {len(changed)} arquivo(s) dentro de touches"))

    if reds:
        edited = sorted({f"{f} ({c['sha'][:7]})" for i, c in enumerate(mine)
                         if i > reds[0] and c["kind"] != "red" for f in c["files"] if is_test(f)})
        if edited:
            results.append(("WARN", "G4", "testes alterados fora de commits red (reviewer deve justificar): " + ", ".join(edited)))
    manifests = project.cfg_list("dependency_manifests", DEFAULT_MANIFESTS)
    changed_manifests = [f for f in changed if matches_any(f, manifests)]
    if changed_manifests:
        if doc.type in ("migration", "foundation") or doc.list("adrs"):
            results.append(("INFO", "G4", "manifestos de dependência alterados — reviewer confirma que estão cobertos pelos ADRs: "
                            + ", ".join(changed_manifests)))
        else:
            results.append(("WARN", "G4", "manifestos de dependência alterados sem ADR na spec — lib/framework nova em projeto "
                            "existente exige ADR aprovado ou type migration; bump de versão precisa de justificativa: "
                            + ", ".join(changed_manifests)))
    scaffolds = [c for c in mine if c["kind"] == "scaffold"]
    if scaffolds:
        results.append(("INFO", "G4", "reviewer confirma que scaffold não tem lógica: " + "; ".join(short(c) for c in scaffolds)))
    if untraced:
        results.append(("WARN", "G4", f"{len(untraced)} commit(s) sem referência a spec: " + "; ".join(short(c) for c in untraced[:5])))
    if run_git(root, "status", "--porcelain")[1].strip():
        results.append(("WARN", "--", "há alterações não commitadas — não entram na verificação"))

    return f"verify {spec_id} — base {base} @ {merge_base[:7]}, {len(mine)} commit(s) da spec", results


def print_results(header: str, results) -> int:
    print(header + "\n")
    for level, gate, msg in results:
        print(f"[{gate}] {level:<4}  {msg}")
        ci_annotate(level, f"[{gate}] {msg}")
    fails = sum(1 for r in results if r[0] == "FAIL")
    warns = sum(1 for r in results if r[0] == "WARN")
    print(f"\nResultado: {'FAIL' if fails else 'PASS'} — {fails} falha(s), {warns} aviso(s)")
    return 1 if fails else 0


def cmd_verify(project: Project, spec_id: str, base):
    return print_results(*verify_spec(project, spec_id, base))


def cmd_impacted(project: Project, spec_id: str):
    doc = project.specs.get(spec_id)
    if doc is None:
        die(f"{spec_id} não encontrada")
    specs = project.specs
    current = contract_version(doc)
    print(f"{spec_id} — {doc.title} (contrato v{current})\n")
    consumers = project.consumers(spec_id)
    print("Consomem o contrato:")
    for d, ver in consumers:
        state = "em dia" if ver == current else f"DESATUALIZADO (pin @{ver})"
        print(f"  {d.id}  [{d.status}]  {d.title} — {state}")
    if not consumers:
        print("  —")
    direct = [d for d in sorted(specs.values(), key=lambda d: d.id) if spec_id in d.list("depends_on")]
    print("Dependem da implementação:")
    for d in direct:
        print(f"  {d.id}  [{d.status}]  {d.title}")
    if not direct:
        print("  —")
    reverse = defaultdict(set)
    for d in specs.values():
        for dep in d.list("depends_on"):
            reverse[dep].add(d.id)
    seen = {spec_id} | {d.id for d, _ in consumers} | {d.id for d in direct}
    queue = deque(sorted(seen - {spec_id}))
    transitive = []
    while queue:
        cur = queue.popleft()
        for nxt in sorted(reverse[cur]):
            if nxt not in seen:
                seen.add(nxt)
                transitive.append(nxt)
                queue.append(nxt)
    print("Impacto transitivo:")
    for sid in transitive:
        print(f"  {sid}  [{specs[sid].status}]  {specs[sid].title}")
    if not transitive:
        print("  —")
    return 0


def cmd_report(project: Project, spec_id: str, changelog: bool = False):
    doc = project.specs.get(spec_id)
    if doc is None:
        die(f"{spec_id} não encontrada")
    if changelog:
        docs = project.children(spec_id) if doc.tier == "epic" else [doc]
        groups = defaultdict(list)
        for d in docs:
            if d.status != "deprecated":
                groups[CHANGELOG_BY_TYPE.get(d.type, "Changed")].append(f"- {d.title} ({d.id})")
        for category in ("Added", "Changed", "Deprecated", "Removed", "Fixed", "Security"):
            if groups[category]:
                print(f"### {category}\n" + "\n".join(groups[category]) + "\n")
        return 0
    meta = [f"**Tipo:** {doc.type or '?'}", f"**Tier:** {doc.tier}", f"**Status:** {doc.status}"]
    if doc.scalar("parent"):
        meta.append(f"**Épico:** {doc.scalar('parent')}")
    out = [f"## {doc.id} — {doc.title}", "", " · ".join(meta), ""]
    delivery = COMMENT_RE.sub("", doc.section("Relatório de Entrega") or "").strip()
    out += [delivery or "_Relatório de Entrega ainda não preenchido._", ""]
    if doc.tier == "epic":
        out += ["### Specs do épico", "", "| Spec | Título | Tipo | Status |", "|---|---|---|---|"]
        for kid in project.children(spec_id):
            out.append(f"| {kid.id} | {md_cell(kid.title)} | {kid.type} | {kid.status} |")
        out.append("")
    else:
        gates = COMMENT_RE.sub("", doc.section("Registro de Gates") or "").strip()
        if gates:
            out += ["### Registro de Gates", "", gates, ""]
    if impediments(doc):
        out += ["### Impedimentos", "", COMMENT_RE.sub("", doc.section("Registro de Impedimentos") or "").strip(), ""]
    out.append(f"Spec completa: `{project.rel(doc.path)}`")
    print("\n".join(out))
    return 0


# ---------------------------------------------------------------- gate (evidence generated by the tool)

GATE_COMMANDS = {
    "G2": ("build", "test", "lint", "coverage"),
    "G3": ("arch_test",),
    "G5": ("build", "test", "test_integration", "test_e2e", "arch_test", "security_scan"),
    "G6": ("smoke_test",),
}
RUNNABLE_GATES = {"G0", "G1", "G2", "G3", "G4", "G5", "G6", "G7"}
HUMAN_GATES = {"H2"}
GATE_PILLAR = {"G3": "arquitetura", "G6": "entrega"}
MANUAL_PREFIX = "MANUAL:"


def run_shell(command: str, cwd: Path):
    try:
        result = subprocess.run(command, shell=True, check=False, cwd=cwd, capture_output=True, text=True, timeout=1800)
    except subprocess.TimeoutExpired:
        return 124, "timeout (30 min)"
    lines = [l.strip() for l in (result.stdout + "\n" + result.stderr).splitlines() if l.strip()]
    summary = next((l for l in reversed(lines) if not l.startswith("---")), "")
    return result.returncode, summary[:90]


def head_ref(cwd: Path):
    code, sha, _ = run_git(cwd, "rev-parse", "--short", "HEAD")
    dirty = bool(run_git(cwd, "status", "--porcelain", "--untracked-files=no")[1].strip())
    return (sha.strip() if code == 0 else "?"), dirty


def set_gate_row(doc: Doc, gate: str, status: str, evidence: str):
    lines = doc.text.split("\n")
    bounds = section_bounds(lines, "Registro de Gates")
    if bounds is None:
        die(f"{doc.id} não tem 'Registro de Gates' — rode `upgrade {doc.id} --apply`")
    for i in range(bounds[0] + 1, bounds[1]):
        cells = [c.strip() for c in CELL_SPLIT_RE.split(lines[i].strip()[1:-1])] if lines[i].strip().startswith("|") else []
        if cells and cells[0].split() and cells[0].split()[0].upper() == gate:
            lines[i] = f"| {cells[0]} | {status} | {md_cell(evidence)} | {today()} |"
            doc.path.write_text("\n".join(lines), encoding="utf-8")
            return
    die(f"{doc.id}: linha do gate {gate} não encontrada no Registro de Gates — rode `upgrade {doc.id} --apply`")


def pillar_waivers(project: Project) -> dict:
    waived = project.cfg_map("pillars").get("waived")
    return waived if isinstance(waived, dict) else {}


def cmd_gate(project: Project, spec_id, gate, run, cwd, na, fail, by, evidence, manual, allow_dirty, expect_green=False,
             at=None):
    doc = project.specs.get(spec_id)
    if doc is None or doc.tier == "epic":
        die(f"{spec_id} não encontrada ou é épico (gates são por spec executável)")
    gate = gate.upper()
    if gate not in GATE_ROWS:
        die(f"gate inválido {gate} ({' | '.join(GATE_ROWS)})")
    gates = gate_table(doc) or {}
    order = list(GATE_ROWS)
    workdir = Path(cwd).resolve() if cwd else project.root

    if fail:
        set_gate_row(doc, gate, "FAIL", fail)
        print(f"{spec_id} {gate} = FAIL — {fail}")
        return 0
    pending = [g for g in order[:order.index(gate)] if not gate_ok(gates, g)]
    if pending:
        die(f"{gate} só depois de {', '.join(pending)} (PASS ou N/A)")

    if na:
        if len(na.strip()) < 15:
            die("N/A exige motivo com pelo menos 15 caracteres")
        pillar = GATE_PILLAR.get(gate)
        if pillar:
            refs = [r for r in SPEC_REF_RE.findall(na) if r in project.specs and r != spec_id]
            if not refs and pillar not in pillar_waivers(project):
                die(f"N/A em {gate} precisa apontar a spec que vai resolver (SPEC-NNNN existente) "
                    f"ou a dispensa do pilar '{pillar}' em sdd-config.yml (pillars.waived)")
        set_gate_row(doc, gate, "N/A", na)
        print(f"{spec_id} {gate} = N/A — {na}")
        return 0

    if gate in HUMAN_GATES:
        policy = project.cfg("h2_policy", "per-wave")
        if gate == "H2" and policy == "auto-on-green" and not by:
            if not gate_ok(gates, "G5") or gates.get("G5", {}).get("status") != "PASS":
                die("h2_policy auto-on-green exige G5 = PASS")
            approver = project.cfg("h2_policy_approved_by", "?")
            text = f"política auto-on-green (aprovada por {approver} em {project.cfg('h2_policy_approved_at', '?')}); G5 PASS"
        elif by:
            text = f"aprovado por {by}" + (f" — {evidence}" if evidence else "")
        else:
            die(f"{gate} é humano: informe --by <quem aprovou> (ou configure h2_policy: auto-on-green)")
        set_gate_row(doc, gate, "PASS", text)
        print(f"{spec_id} {gate} = PASS — {text}")
        return 0

    if not run:
        if not manual or not evidence:
            die(f"{gate} registra PASS com --run (evidência gerada) ou, excepcionalmente, --manual --evidence \"...\"")
        set_gate_row(doc, gate, "PASS", f"{MANUAL_PREFIX} {evidence}")
        print(f"{spec_id} {gate} = PASS (manual) — {evidence}")
        return 0

    branch_dir = workdir
    if gate == "G1" and not at and project.private:
        reds = [c for c in spec_commits(project, spec_id, workdir, project.cfg("base_branch", "main"))[2]
                if c["kind"] == "red"]
        at = reds[-1]["sha"] if reds else None
    temp_dir = None
    if gate == "G1" and at:
        if run_git(workdir, "rev-parse", "--verify", "--quiet", f"{at}^{{commit}}")[0] != 0:
            die(f"--at {at}: commit não encontrado")
        temp_dir = Path(tempfile.mkdtemp(prefix="sdd-g1-"))
        code, _, err = run_git(workdir, "worktree", "add", "--detach", str(temp_dir / "wt"), at)
        if code != 0:
            shutil.rmtree(temp_dir, ignore_errors=True)
            die(f"git worktree add falhou: {err.strip()}")
        workdir = temp_dir / "wt"
    try:
        return gate_run(project, doc, spec_id, gate, workdir, branch_dir, by, allow_dirty, expect_green, bool(at),
                        evidence)
    finally:
        if temp_dir:
            if run_git(branch_dir, "worktree", "remove", "--force", str(workdir))[0] != 0:
                print(f"aviso: remova a worktree temporária com `git worktree remove --force {workdir}`", file=sys.stderr)
            shutil.rmtree(temp_dir, ignore_errors=True)


def gate_run(project: Project, doc: Doc, spec_id, gate, workdir: Path, branch_dir: Path, by, allow_dirty, expect_green,
             at_red: bool, note=None):
    sha, dirty = head_ref(workdir)
    if dirty and not allow_dirty and gate != "G0":
        die(f"há alterações não commitadas em {workdir}: a evidência precisa apontar um commit (commite ou use --allow-dirty)")
    parts, ok = [], True
    if gate == "G0":
        errors = [m for lvl, s, m in validate(project, {spec_id}) if lvl == "ERROR" and s == spec_id]
        ok = not errors
        parts.append(f"validate: {len(errors)} erro(s)")
    elif gate in ("G1", "G4"):
        _, results = verify_spec(project, spec_id, None, git_root=workdir)
        wanted = {"G1"} if gate == "G1" else {"G1", "G4"}
        fails = [m for lvl, g, m in results if lvl == "FAIL" and g in wanted]
        ok = not fails
        parts.append(f"verify {'+'.join(sorted(wanted))}: {'PASS' if ok else 'FAIL — ' + fails[0][:70]}")
        if gate == "G1" and ok and project.cfg("test"):
            code, summary = run_shell(project.cfg("test"), workdir)
            red = code != 0
            ok = red or expect_green
            parts.append(f"`{project.cfg('test')}` exit {code} ({'red' if red else 'verde'}: {summary})")
            if not red and not expect_green and not at_red:
                reds = [c for c in spec_commits(project, spec_id, workdir, project.cfg("base_branch", "main"))[2]
                        if c["kind"] == "red"]
                if reds:
                    parts.append(f"HEAD já verde: rode com --at {reds[-1]['sha'][:7]} (último commit red)")
        if gate == "G4":
            if not by:
                die("G4 exige o veredito do Reviewer: --by \"<revisor>: APPROVED ...\"")
            if "APPROVED" not in by.upper():
                ok = False
            parts.append(f"revisão: {by}")
    elif gate == "G7":
        issues = []
        plan = set(plan_test_ids(doc))
        validate_delivery(doc, plan, lambda lvl, s, m: issues.append(m) if lvl == "ERROR" else None)
        ok = not issues
        parts.append("Relatório de Entrega e Definição de Pronto: " + ("ok" if ok else issues[0][:80]))
        if project.private:
            merged, text = find_spec_merge(project, doc, workdir)
            ok = ok and merged
            parts.append(text)
    else:
        keys = GATE_COMMANDS[gate] + (("ci",) if gate == "G5" and project.private else ())
        configured = [key for key in keys if project.cfg(key)]
        if not configured:
            die(f"nenhum comando configurado para {gate} ({', '.join(GATE_COMMANDS[gate])}) — configure no sdd-config "
                f"ou registre N/A com --na apontando a spec que vai criá-lo")
        for key in configured:
            code, summary = run_shell(project.cfg(key), workdir)
            parts.append(f"{key} exit {code} ({summary})")
            ok = ok and code == 0
    if project.private and gate in ("G4", "G5"):
        code, merge_base, _ = run_git(workdir, "merge-base", project.cfg("base_branch", "main"), "HEAD")
        branch = run_git(workdir, "rev-parse", "--abbrev-ref", "HEAD")[1].strip()
        leaks = leak_scan(project, workdir, rng=f"{merge_base.strip()}..HEAD" if code == 0 else None, branch=branch)
        if leaks:
            ok = False
            origin, number, label, _ = leaks[0]
            parts.append(f"vazamento: {len(leaks)} achado(s) (1º: {origin}:{number} — {label})")
        else:
            parts.append("leak-check: limpo")
    if note:
        parts.append(f"nota: {note}")
    evidence_text = "; ".join(parts) + f" — {sha}" + (" (árvore suja)" if dirty else "")
    set_gate_row(doc, gate, "PASS" if ok else "FAIL", evidence_text)
    if project.private and gate in ("G1", "G2", "G3", "G4", "G5"):
        write_commit_ledger(project, doc, branch_dir)
    print(f"{spec_id} {gate} = {'PASS' if ok else 'FAIL'} — {evidence_text}")
    return 0 if ok else 1


def section_bounds(lines, keyword: str):
    key, start, fence = norm(keyword), None, False
    for i, line in enumerate(lines):
        if line.lstrip().startswith("```"):
            fence = not fence
        if fence or not line.startswith("## "):
            continue
        if start is not None:
            return start, i
        if key in norm(line[3:]):
            start = i
    return (start, len(lines)) if start is not None else None


def impediment_table_lines(doc: Doc):
    lines = doc.text.split("\n")
    bounds = section_bounds(lines, "Registro de Impedimentos")
    if bounds is None:
        die(f"{doc.id} não tem a seção 'Registro de Impedimentos' — atualize a spec para o template atual")
    table = [i for i in range(bounds[0] + 1, bounds[1]) if lines[i].strip().startswith("|")]
    if len(table) < 2:
        die(f"{doc.id}: tabela do Registro de Impedimentos não encontrada")
    header = [norm(c.strip()) for c in CELL_SPLIT_RE.split(lines[table[0]].strip()[1:-1])]
    return lines, table, header


def cmd_impede(project: Project, spec_id: str, kind: str, reason: str, owner: str, phase, tried):
    doc = project.specs.get(spec_id)
    if doc is None:
        die(f"{spec_id} não encontrada")
    normalized = normalize_imp_type(kind)
    if normalized is None:
        die(f"tipo '{kind}' inválido ({' | '.join(IMPEDIMENT_TYPES)})")
    lines, table, header = impediment_table_lines(doc)
    numbers = [int(m.group(1)) for item in impediments(doc) for m in [IMP_ID_RE.match(item["ID"])] if m]
    iid = f"IMP-{max(numbers, default=0) + 1:02d}"
    values = {"ID": iid, "Aberto em": today(), "Fase/Gate": phase or "—", "Tipo": normalized,
              "Descrição": reason, "Tentativas": tried or "—", "Responsável": owner, "Resolução": "", "Fechado em": ""}
    cells = [md_cell(values.get(next((n for n in IMPEDIMENT_COLUMNS if norm(n) == col), ""), "")) for col in header]
    lines.insert(table[-1] + 1, "| " + " | ".join(cells) + " |")
    doc.path.write_text("\n".join(lines), encoding="utf-8")
    print(f"{spec_id}/{iid} aberto: {normalized} → {owner} — {reason}")
    return 0


def cmd_resolve(project: Project, spec_id: str, iid: str, resolution: str):
    doc = project.specs.get(spec_id)
    if doc is None:
        die(f"{spec_id} não encontrada")
    item = next((i for i in impediments(doc) if i["ID"] == iid), None)
    if item is None:
        die(f"{spec_id}: impedimento {iid} não encontrado")
    if item["Fechado em"]:
        die(f"{spec_id}/{iid} já foi fechado em {item['Fechado em']}")
    kind = normalize_imp_type(item["Tipo"])
    amendment_rows = sum(len(rows) for _, rows in parse_tables(COMMENT_RE.sub("", doc.section("Emendas") or "")))
    if kind == "spec" and ("emenda" not in norm(resolution) or not amendment_rows):
        die("impedimento de spec se resolve por emenda: registre-a na seção Emendas e cite-a (ex: 'Emenda v2 — ...')")
    if kind == "trabalho":
        refs = SPEC_REF_RE.findall(resolution)
        if not refs or any(r not in project.specs for r in refs):
            die("impedimento de trabalho se resolve com uma spec existente: cite o SPEC-NNNN criado (use `new` antes)")
    lines, table, header = impediment_table_lines(doc)
    for idx in table[2:]:
        cells = [c.strip() for c in CELL_SPLIT_RE.split(lines[idx].strip()[1:-1])]
        if cells and cells[0] == iid:
            cells += [""] * (len(header) - len(cells))
            cells[header.index(norm("Resolução"))] = md_cell(resolution)
            cells[header.index(norm("Fechado em"))] = today()
            lines[idx] = "| " + " | ".join(cells) + " |"
            break
    doc.path.write_text("\n".join(lines), encoding="utf-8")
    print(f"{spec_id}/{iid} resolvido em {today()}: {resolution}")
    return 0


# ---------------------------------------------------------------- status (process state machine)

def gate_ok(gates: dict, gate: str) -> bool:
    return gates.get(gate, {}).get("status") in ("PASS", "N/A")


def has_checklist(doc: Doc) -> bool:
    return bool(CHECKBOX_RE.search(COMMENT_RE.sub("", doc.section("Checklist") or "")))


def spec_state(project: Project, doc: Doc):
    """Phases done (8 booleans) and the next mandatory step, derived only from the files."""
    only = {doc.id} | ({k.id for k in project.children(doc.id)} if doc.tier == "epic" else set())
    errors = [m for lvl, s, m in validate(project, only) if lvl == "ERROR" and s == doc.id]
    stops = open_impediments(doc)
    approved = doc.status in APPROVED_PLUS

    if doc.tier == "epic":
        kids = [k for k in project.children(doc.id) if k.status != "deprecated"]
        kid_states = [spec_state(project, k)[0] for k in kids]
        rolled = [bool(kid_states) and all(s[i] for s in kid_states) for i in range(8)]
        done = [True, not errors or approved, approved, approved and rolled[3], rolled[4], rolled[5], rolled[6],
                doc.status == "implemented" and not errors]
    else:
        gates = gate_table(doc) or {}
        done = [True, not errors and gate_ok(gates, "G0") or approved, approved, approved and has_checklist(doc),
                all(gate_ok(gates, g) for g in ("G1", "G2", "G3", "G4")),
                gate_ok(gates, "G5") and gate_ok(gates, "H2"), gate_ok(gates, "G6"),
                doc.status == "implemented" and gate_ok(gates, "G7") and not errors]

    def with_errors(text):
        return text + "".join(f"\n    - {e}" for e in errors[:6]) + ("\n    - …" if len(errors) > 6 else "")

    if doc.status == "deprecated":
        step = "spec cancelada (deprecated) — nada a fazer"
    elif stops:
        step = ("PARADA — resolva antes de qualquer outro passo: "
                + "; ".join(f"{impediment_label(i)}: {i['Descrição']}" for i in stops)
                + f". Depois: `resolve {doc.id} IMP-NN --resolution \"...\"`")
    elif doc.status == "implemented":
        step = with_errors("inconsistente: corrija e rode validate") if errors else "concluída"
    elif doc.status == "proposed":
        if errors:
            step = with_errors(f"SPEC (G0): corrija {len(errors)} erro(s) e rode `validate {doc.id}`")
        elif doc.tier != "epic" and not done[1]:
            step = "SPEC (G0): validate está limpo — registre G0 = PASS com evidência no Registro de Gates"
        else:
            step = f"APPROVAL (H1): apresente o resumo e pergunte exatamente: \"{H1_QUESTION}\""
    elif errors:
        step = with_errors("corrija as inconsistências antes de seguir")
    elif doc.tier == "epic":
        open_kids = [k.id for k in project.children(doc.id) if k.status not in DONE]
        if not open_kids:
            step = "CLOSE: marque os critérios de aceite (cada um provado por teste), escreva o Relatório de Entrega e `set status=implemented`"
        else:
            step = f"acompanhe as filhas ({', '.join(open_kids)}) com `status` e `next`"
    elif not has_checklist(doc):
        step = "PLAN: preencha o Checklist de Implementação (fases começando por testes, + dod_extra) e rode `waves`/`next`/`index`"
    elif doc.status == "approved":
        state, label = readiness(project, doc)
        step = (f"EXECUTE: `set {doc.id} status=in-progress` e despache o Test-writer (references/multi-agent.md)"
                if state == "ready" else f"aguardando: {label}")
    else:
        gates = gate_table(doc) or {}
        steps = {
            "G1": f"G1: Test-writer escreve os testes do plano; depois `verify {doc.id}` + resumo das falhas → registre G1",
            "G2": "G2: Implementer faz os testes passarem seguindo o padrão de referência → registre G2 (build + suíte completa)",
            "G3": "G3: rode arch_test → registre G3",
            "G4": f"G4: Reviewer independente (`verify {doc.id}` + veredito) → registre G4",
            "G5": "G5: aguarde a onda; Integrator faz merge, roda todas as suítes e o CI (`pr-check`) → registre G5",
            "H2": f"H2: pergunte exatamente: \"{H2_QUESTION}\"",
            "G6": "G6: Release publica em staging pelo pipeline (smoke + E2E); produção só após confirmação explícita do usuário",
            "G7": "CLOSE (G7): Relatório de Entrega com Verificação por teste, docs raiz, CHANGELOG, `set status=implemented`",
        }
        pending = next((g for g in ("G1", "G2", "G3", "G4", "G5", "H2", "G6", "G7") if not gate_ok(gates, g)), None)
        step = steps[pending] if pending else f"todos os gates ok — `set {doc.id} status=implemented` e `validate`"
    t = project.tracker()
    if t["provider"] != "none" and doc.status != "deprecated" and not project.external_ref(doc):
        step += f"\n  Board ({t['provider']}): item não vinculado — rode `sync` e execute as operações."
    return done, step


def progress_panel(doc: Doc, done) -> str:
    lines = [f"> **SDD Progress — {doc.id} ({doc.tier} · {doc.type or '?'})**"]
    lines += [f"> [{'x' if ok else ' '}] {i}. {name}" for i, (ok, name) in enumerate(zip(done, PHASES), start=1)]
    return "\n".join(lines)


def cmd_status(project: Project, ids):
    code = status_body(project, ids)
    news = status_news_line(project)  # também quando não há spec aberta (status_body retorna cedo)
    if news:
        print(news)
    return code


def status_body(project: Project, ids):
    if project.private:
        for warning in spec_root_pending(project.spec_root, project.sync):
            print(f"⚠ {warning}")
    if ids:
        for sid in ids:
            doc = project.specs.get(sid)
            if doc is None:
                die(f"{sid} não encontrada")
            done, step = spec_state(project, doc)
            print(f"{doc.id} — {doc.title} ({doc.tier} · {doc.type or '?'}) — status: {doc.status}\n")
            print(progress_panel(doc, done))
            print(f"\nPróximo passo: {step}\n")
        return 0
    for path, why in stale_worktrees(project):
        print(f"⚠ worktree esquecida {path}: {why}")
    for path, why in misplaced_worktrees(project):
        print(f"⚠ worktree {path} {why}")
    gaps = [pid for pid, _, state, _ in pillar_coverage(project) if state == "sem decisão"]
    if gaps:
        print(f"⚠ Pilares sem decisão: {', '.join(gaps)} — rode `pillars`\n")
    open_docs = [d for d in sorted(project.specs.values(), key=lambda d: d.id) if d.status in OPEN]
    if not open_docs:
        print("Nenhuma spec aberta. Novo trabalho: DISCOVER → `new`.")
        return 0
    for doc in open_docs:
        done, step = spec_state(project, doc)
        phase = next((PHASES[i] for i, ok in enumerate(done) if not ok), "—")
        print(f"{doc.id}  {doc.status:<11} {phase:<20} {doc.title}")
        print(f"             → {step.splitlines()[0]}")
    return 0


# ---------------------------------------------------------------- novidades da skill (whats-new)

VERSION_RE = re.compile(r"^\d+(?:\.\d+)+$")
CHANGELOG_SECTION_RE = re.compile(r"^## \[([^\]]+)\]")
# Banner de seção ou chave comentada (só quando o valor parece YAML; "# pr: toda mudança…" é prosa e fica).
TEMPLATE_NOISE_RE = re.compile(r"^#\s*(?:---|[a-z_][a-z0-9_]*:\s*(?:$|[\"'\[{]|-?\d|true\b|false\b))")


def version_key(version: str) -> tuple:
    """"2026.10.10" -> (2026, 10, 10): versões comparadas numericamente, não como texto."""
    return tuple(int(part) for part in version.strip().split("."))


def changelog_since(text: str, since, until: str) -> list:
    """Seções de versão do CHANGELOG com since < versão <= until, na ordem do arquivo ([Unreleased] nunca entra)."""
    if not since or not VERSION_RE.match(since) or not VERSION_RE.match(until):
        return []
    low, high = version_key(since), version_key(until)
    sections, current = [], None
    for line in text.splitlines():
        if line.startswith("## "):
            if current is not None:
                sections.append("\n".join(current).rstrip("\n"))
            m = CHANGELOG_SECTION_RE.match(line)
            version = m.group(1).strip() if m else ""
            keep = bool(VERSION_RE.match(version)) and low < version_key(version) <= high
            current = [line] if keep else None
        elif current is not None:
            current.append(line)
    if current is not None:
        sections.append("\n".join(current).rstrip("\n"))
    return sections


def missing_config_keys(template_text: str, config: dict) -> list:
    """(chave, comentário) das chaves de topo do template ausentes no config, na ordem do template."""
    out, comments = [], []
    for line in template_text.splitlines():
        if line.startswith("#"):
            if not TEMPLATE_NOISE_RE.match(line):  # banner de seção e chave comentada não descrevem a chave
                comments.append(line.lstrip("#").strip())
            continue
        m = re.match(r"^([A-Za-z_][\w-]*)\s*:", line)
        if m and m.group(1) not in config:
            out.append((m.group(1), " ".join(c for c in comments if c)))
        comments = []
    return out


def project_tool_version(project: Project):
    """Versão do projeto: tools/sdd (se existir), senão sdd_version do sdd-config; None = desconhecida."""
    vendored = project.root / VENDORED_TOOL
    if vendored.exists():
        m = re.search(r'SDD_TOOL_VERSION = "([^"]+)"', vendored.read_text(encoding="utf-8"))
        version = m.group(1) if m else ""
        return (version if VERSION_RE.match(version) else None), "tools/sdd"
    version = project.config.get("sdd_version")
    version = version.strip() if isinstance(version, str) else ""
    return (version if VERSION_RE.match(version) else None), "sdd_version"


def skill_news(project: Project):
    """(versão do projeto, origem, seções novas do CHANGELOG ou None sem CHANGELOG, chaves novas do template)."""
    version, source = project_tool_version(project)
    changelog = SKILL_DIR / "CHANGELOG.md"
    sections = changelog_since(changelog.read_text(encoding="utf-8"), version, SDD_TOOL_VERSION) \
        if changelog.exists() else None
    template = TEMPLATES_DIR / "sdd-config.template.yml"
    keys = [(k, c) for k, c in missing_config_keys(template.read_text(encoding="utf-8"), project.config)
            if k != "sdd_version"] if template.exists() else []
    return version, source, sections, keys


def is_older(version) -> bool:
    return bool(version) and version_key(version) < version_key(SDD_TOOL_VERSION)


def status_news_line(project: Project) -> str:
    """Linha do `status` quando a skill tem novidade para o projeto; "" quando em dia."""
    if not project.config_path.exists():
        return ""
    version, _, sections, keys = skill_news(project)
    older = is_older(version)
    if not older and not keys:
        return ""
    parts = []
    if older and sections:
        parts.append(f"{len(sections)} versão(ões)")
    elif older:
        parts.append(f"projeto na {version}")  # sem CHANGELOG (ou sem seções) não há como contar versões
    if keys:
        parts.append(f"{len(keys)} configuração(ões) nova(s)")
    return f"Novidades da skill: {' e '.join(parts)} — rode whats-new"


def write_config_key(path: Path, key: str, value: str):
    """Cria ou troca a linha `key: "value"` de topo no sdd-config; o resto do arquivo fica igual, byte a byte."""
    with path.open(encoding="utf-8", newline="") as fh:
        text = fh.read()
    rendered = f'{key}: "{value}"'
    m = re.search(rf"^{re.escape(key)}\s*:[^\r\n]*", text, re.MULTILINE)
    if m:
        text = text[:m.start()] + rendered + text[m.end():]
    else:
        eol = "\r\n" if "\r\n" in text else "\n"
        if text and not text.endswith(("\n", "\r")):
            text += eol
        text += rendered + eol
    with path.open("w", encoding="utf-8", newline="") as fh:
        fh.write(text)


def cmd_whats_new(project: Project, ack: bool):
    if ack:
        if not project.config_path.exists():
            print(f"aviso: {project.rel(project.config_path)} não existe — nada gravado; crie o sdd-config a partir de "
                  "references/sdd-config.template.yml")
            return 0
        write_config_key(project.config_path, "sdd_version", SDD_TOOL_VERSION)
        print(f"sdd_version = {SDD_TOOL_VERSION}")
        return 0
    version, source, sections, keys = skill_news(project)
    print(f"Projeto: {version or 'desconhecida'} · skill: {SDD_TOOL_VERSION}\n")
    older = is_older(version)
    if sections is None:
        print(f"CHANGELOG da skill não encontrado ({SKILL_DIR / 'CHANGELOG.md'})\n")
    newer = bool(version) and version_key(version) > version_key(SDD_TOOL_VERSION)
    if version is None and source == "tools/sdd":
        print(f"{VENDORED_TOOL} sem versão legível — rode vendor --force\n")
    elif version is None:
        print("Versão do projeto desconhecida — rode whats-new --ack depois de revisar\n")
    elif older:
        print(f"Novidades desde {version}:\n")
        if sections:
            print("\n\n".join(sections) + "\n")
        elif sections is not None:
            print("(nenhuma entrada no CHANGELOG da skill para essas versões)\n")
    elif newer:
        print(f"A skill instalada ({SDD_TOOL_VERSION}) é mais antiga que o projeto ({version}) — atualize a cópia da skill.\n")
    if keys:
        print("Configurações novas (ausentes no seu sdd-config):")
        for key, comment in keys:
            print(f"- {key}: {comment}" if comment else f"- {key}:")
        print()
    steps = []
    if source == "tools/sdd" and (older or version is None):
        steps.append(f"- `spec_graph.py vendor --force` — atualiza {VENDORED_TOOL} para a {SDD_TOOL_VERSION}")
    if older:
        steps.append("- `spec_graph.py upgrade` (simulação) e `upgrade --apply` — leva as specs aos templates atuais")
    if keys:
        steps.append(f"- copie a chave do template references/sdd-config.template.yml para {project.rel(project.config_path)} "
                     "(ou deixe de fora para manter o comportamento atual)")
    if source == "sdd_version" and (older or version is None):
        steps.append(f"- `spec_graph.py whats-new --ack` — registra sdd_version = {SDD_TOOL_VERSION} depois de revisar")
    if newer:
        steps.append(f"- atualize a cópia instalada da skill ({SKILL_DIR}) para a {version} ou mais nova")
    if steps:
        print("Para se atualizar:\n" + "\n".join(steps))
    else:
        print(f"Projeto em dia com a skill ({SDD_TOOL_VERSION}).")
    return 0


# ---------------------------------------------------------------- export / board sync (agnostic)

def spec_record(project: Project, doc: Doc, plan) -> dict:
    wave = plan[0] if plan else {}
    state, label = (readiness(project, doc) if doc.tier != "epic" and doc.status in OPEN else (None, None))
    gates = gate_table(doc) or {}
    return {
        "id": doc.id, "title": doc.title, "tier": doc.tier, "type": doc.type, "status": doc.status,
        "created": doc.scalar("created"), "parent": doc.scalar("parent") or None,
        "depends_on": doc.list("depends_on"), "consumes_contract": doc.list("consumes_contract"),
        "contract_version": contract_version(doc), "touches": doc.list("touches"), "adrs": doc.list("adrs"),
        "external": doc.list("external"), "approved_by": doc.scalar("approved_by") or None,
        "approved_at": doc.scalar("approved_at") or None, "path": project.rel(doc.path),
        "wave": wave.get(doc.id), "readiness": {"state": state, "label": label} if state else None,
        "tests": plan_test_ids(doc) if doc.tier != "epic" else [],
        "gates": {g: gates[g] for g in GATE_ROWS if g in gates},
        "impediments": impediments(doc),
        "next_step": spec_state(project, doc)[1],
    }


def build_export(project: Project) -> dict:
    plan = compute_waves(project)
    running, batch, held = next_batch(project, plan) if plan else ([], [], [])
    return {
        "sdd_tool_version": SDD_TOOL_VERSION,
        "generated_at": today(),
        "tracker": project.tracker(),
        "specs": [spec_record(project, project.specs[s], plan) for s in sorted(project.specs)],
        "adrs": [{"id": a.id, "title": a.title, "status": a.status, "origin": a.scalar("origin") or None,
                  "enforced_by": a.scalar("enforced_by") or None, "path": project.rel(a.path)}
                 for a in sorted(project.adrs.values(), key=lambda d: d.id)],
        "plan": {"cycle": plan is None, "running": running, "next_batch": batch,
                 "held": [{"id": s, "reason": r} for s, r in held]},
        "validation": [{"level": l, "subject": s, "message": m} for l, s, m in validate(project)],
    }


def cmd_export(project: Project, out):
    text = json.dumps(build_export(project), ensure_ascii=False, indent=2)
    if out:
        Path(out).write_text(text + "\n", encoding="utf-8")
        print(f"escrito: {out}")
    else:
        print(text)
    return 0


def required_structure(project: Project) -> dict:
    t = project.tracker()
    return {
        "statuses": sorted({v for v in t["statuses"].values() if v}),
        "issue_types": sorted({v for v in t["issue_types"].values() if v}),
        "labels": sorted(set(t["labels"]) | {t["blocked_label"]}),
        "relations": ["parent (épico → spec)", "blocks (dependência → dependente)"],
    }


def structure_operations(project: Project, structure: dict):
    ops = []
    need = required_structure(project)
    for key, op in (("statuses", "create_status"), ("issue_types", "create_issue_type"), ("labels", "create_label")):
        have = {norm(str(x)) for x in (structure.get(key) or [])}
        for value in need[key]:
            if norm(value) not in have:
                ops.append({"op": op, "name": value})
    return ops


def sync_operations(project: Project, board):
    t = project.tracker()
    provider, statuses = t["provider"], t["statuses"]
    ops = []
    for doc in sorted(project.specs.values(), key=lambda d: d.id):
        ref = project.external_ref(doc)
        if not ref:
            if doc.status == "deprecated":
                continue
            parent = project.specs.get(doc.scalar("parent"))
            ops.append({
                "op": "create_item", "spec": doc.id, "title": f"[{doc.id}] {doc.title}",
                "issue_type": t["issue_types"].get("epic" if doc.tier == "epic" else doc.type, ""),
                "parent": (project.external_ref(parent) or f"{parent.id} (crie o épico primeiro e rode sync de novo)")
                          if parent else "",
                "labels": list(t["labels"]) + [doc.id], "status": statuses.get(doc.status, ""),
                "spec_path": project.rel(doc.path),
                "then": f"set {doc.id} external=[{provider}:<chave-criada>]",
            })
            continue
        if board is None:
            continue
        item = board.get(ref)
        if item is None:
            ops.append({"op": "missing_on_board", "spec": doc.id, "ref": ref})
            continue
        want_status = statuses.get(doc.status, "")
        if want_status and norm(str(item.get("status", ""))) != norm(want_status):
            ops.append({"op": "transition", "spec": doc.id, "ref": ref, "from": item.get("status"), "to": want_status})
        stops = open_impediments(doc)
        if bool(item.get("blocked")) != bool(stops):
            ops.append({"op": "set_blocked" if stops else "clear_blocked", "spec": doc.id, "ref": ref,
                        "label": t["blocked_label"],
                        "comment": "; ".join(f"{doc.id}/{impediment_label(i)}: {i['Descrição']}" for i in stops)})
        for dep in doc.list("depends_on"):
            dep_ref = project.external_ref(project.specs[dep]) if dep in project.specs else ""
            if dep_ref and ref not in (board.get(dep_ref, {}).get("blocks") or []):
                ops.append({"op": "link", "type": "blocks", "from": dep_ref, "to": ref, "spec": doc.id})
    if board is not None:
        known = {project.external_ref(d) for d in project.specs.values()}
        for ref in sorted(set(board) - known):
            ops.append({"op": "orphan", "ref": ref, "note": "item com label SDD no board sem spec correspondente"})
    return ops


def cmd_sync(project: Project, board_file, as_json: bool, check: bool, structure: bool = False):
    t = project.tracker()
    if structure:
        print(json.dumps({"provider": t["provider"], "project": t["project"], "structure": required_structure(project)},
                         ensure_ascii=False, indent=2))
        return 0
    if t["provider"] == "none":
        print("tracker.provider = none — o repositório é o único board. Nada a sincronizar.")
        return 0
    board, structure_ops = None, []
    if board_file:
        raw = json.loads(Path(board_file).read_text(encoding="utf-8"))
        board = {i["ref"]: i for i in raw.get("items", []) if isinstance(i, dict) and i.get("ref")}
        if isinstance(raw.get("structure"), dict):
            structure_ops = structure_operations(project, raw["structure"])
    ops = structure_ops + sync_operations(project, board)
    if as_json:
        print(json.dumps({"provider": t["provider"], "project": t["project"], "operations": ops}, ensure_ascii=False, indent=2))
    else:
        print(f"Board: {t['provider']} · {t['project']} — {len(ops)} operação(ões)")
        if board is None:
            print("(sem --board: só itens faltantes; gere o snapshot pelo adaptador para conferir status, bloqueios e vínculos)")
        for op in ops:
            details = ", ".join(f"{k}={v}" for k, v in op.items() if k != "op" and v not in ("", [], None))
            print(f"  {op['op']:<16} {details}")
    return 1 if check and ops else 0


# ---------------------------------------------------------------- pull requests (agnostic)

class _Blank(dict):
    def __missing__(self, key):
        return ""


def sub_text(doc: Doc, section: str, sub: str) -> str:
    content = COMMENT_RE.sub("", doc.section(section) or "")
    return (find_sub(parse_subsections(content), sub) or "").strip()


def labeled_line(doc: Doc, section: str, label: str) -> str:
    content = COMMENT_RE.sub("", doc.section(section) or "")
    m = re.search(rf"\*\*{re.escape(label)}:\*\*\s*(.+)", content)
    return m.group(1).strip() if m else ""


PR_TEMPLATE_CANDIDATES = (".github/pull_request_template.md", ".github/PULL_REQUEST_TEMPLATE.md",
                          "docs/pull_request_template.md", ".gitlab/merge_request_templates/Default.md",
                          ".azuredevops/pull_request_template.md")
PR_SECTION_KEYWORDS = (("risk", ("risco", "risk", "rollback", "rollout")),
                       ("test", ("teste", "test", "como testar", "how to test")),
                       ("summary", ("resumo", "summary", "descricao", "description", "o que", "what")))


def detect_team_conventions(first_parent_parents, subjects, files, hooks_path="") -> dict:
    """Convenções do time: forma de merge, padrão de mensagens, gerenciador de hooks e template de PR."""
    if not first_parent_parents:
        merge = "desconhecida"
    else:
        merges = sum(1 for n in first_parent_parents if n > 1)
        merge = "merge" if merges / len(first_parent_parents) >= 0.2 else "linear"
    recent = list(subjects)[:50]
    if not recent:
        commits = "desconhecida"
    else:
        conventional = sum(1 for s in recent if CONVENTIONAL_RE.match(s))
        commits = "conventional" if conventional / len(recent) >= 0.8 else "livre"
    hooks = "core.hooksPath" if hooks_path else next((label for name, label in TEAM_HOOK_MARKERS if name in files), "nenhum")
    template = next((c for c in PR_TEMPLATE_CANDIDATES if c in files), "")
    return {"merge": merge, "commits": commits, "hooks": hooks, "pr_template": template}


def team_conventions(project: Project) -> dict:
    root = project.root
    base = project.cfg("base_branch", "main")
    if run_git(root, "rev-parse", "--verify", "--quiet", base)[0] != 0:
        base = "HEAD"
    parents = [len(line.split()) for line in
               run_git(root, "log", "--first-parent", "-n", "200", "--format=%P", base)[1].splitlines()]
    subjects = run_git(root, "log", "--no-merges", "-n", "50", "--format=%s", base)[1].splitlines()
    files = {name for name in PR_TEMPLATE_CANDIDATES + tuple(n for n, _ in TEAM_HOOK_MARKERS) if (root / name).exists()}
    hooks_path = run_git(root, "config", "--get", "core.hooksPath")[1].strip()
    return detect_team_conventions(parents, subjects, files, hooks_path)


def write_team_block(config_path: Path, conv: dict):
    """Grava (ou troca) o bloco `team:` no sdd-config do spec_root."""
    lines = config_path.read_text(encoding="utf-8").split("\n") if config_path.exists() else []
    out, skipping = [], False
    for line in lines:
        if line.startswith(("team:", "# Convenções do time")):
            skipping = True
            continue
        if skipping and (line.startswith((" ", "\t")) or not line.strip()):
            continue
        skipping = False
        out.append(line)
    block = ["# Convenções do time (detectadas por `private init`/`private detect`; corrija se preciso)", "team:"]
    block += [f'  {key}: "{value}"' if key == "pr_template" else f"  {key}: {value}" for key, value in conv.items()]
    text = "\n".join(out).rstrip("\n") + "\n\n" + "\n".join(block) + "\n"
    config_path.write_text(text, encoding="utf-8")


def describe_team(conv: dict) -> str:
    return (f"Detectado: merge {conv['merge']} · commits {conv['commits']} · hooks {conv['hooks']} · "
            f"template {conv['pr_template'] or 'nenhum'}")


def plan_sentences(doc: Doc):
    """Testes do plano em linguagem natural, sem IDs nem `→ caminho::nome`."""
    out = []
    for line in COMMENT_RE.sub("", doc.section("Plano de Testes") or "").splitlines():
        if not TEST_ID_RE.search(line):
            continue
        text = re.sub(r"^\s*-\s*\*\*[A-Z0-9]+-\d+\*\*\s*[—–-]\s*", "", line)
        text = TEST_TARGET_RE.sub("", text)
        text = re.sub(r"\(guarda[^)]*\)\s*", "", text).strip()
        if text:
            out.append(f"- {text}")
    return out


def private_pr_parts(docs) -> dict:
    summary, tests, risk = [], [], []
    for d in docs:
        overview = COMMENT_RE.sub("", d.section("Visão Geral") or d.section("Problema") or "").strip()
        motivation = labeled_line(d, "Motivação", "Motivação")
        summary += [p for p in (overview, motivation) if p]
        tests += plan_sentences(d)
        rollout = COMMENT_RE.sub("", d.section("Plano de Rollout") or "").strip()
        rollback = labeled_line(d, "Mudança Proposta", "Rollback")
        risk += [rollout] if rollout else ([f"Rollback: {rollback}"] if rollback else [])
    return {"summary": "\n\n".join(summary) or "—", "test": "\n".join(tests) or "—", "risk": "\n\n".join(risk) or "—"}


def fill_team_template(template: str, parts: dict) -> str:
    """Preenche as seções do template do time por título; as demais ficam como estão."""
    out, filling = [], False
    for line in template.split("\n"):
        if line.startswith("#"):
            heading = norm(line.lstrip("#"))
            kind = next((k for k, words in PR_SECTION_KEYWORDS if any(w in heading for w in words)), None)
            if filling:
                out.append("")
            out.append(line)
            filling = kind is not None
            if filling:
                out += [parts[kind]]
            continue
        if filling:
            if line.strip().startswith("<!--"):
                out.insert(len(out) - 1, line)
            continue
        out.append(line)
    return "\n".join(out).rstrip("\n") + "\n"


def private_pr(project: Project, docs):
    team = project.cfg_map("team")
    if len(docs) == 1:
        title, conv = docs[0].title, CONVENTIONAL_BY_TYPE.get(docs[0].type, "feat")
    else:
        parents = {d.scalar("parent") for d in docs}
        epic = project.specs.get(parents.pop()) if len(parents) == 1 else None
        title = epic.title if epic else "; ".join(d.title for d in docs)
        convs = {CONVENTIONAL_BY_TYPE.get(d.type, "feat") for d in docs}
        conv = "feat" if "feat" in convs else min(convs)
    if team.get("commits") == "conventional":
        lowered = title[0].lower() + title[1:] if len(title) > 1 and title[0].isupper() and title[1].islower() else title
        title = f"{conv}: {lowered}"
    parts = private_pr_parts(docs)
    template_path = project.root / team["pr_template"] if team.get("pr_template") else None
    if template_path and template_path.exists():
        body = fill_team_template(template_path.read_text(encoding="utf-8"), parts)
    else:
        body = (f"## O que muda\n\n{parts['summary']}\n\n## Como testar\n\n{parts['test']}\n\n"
                f"## Riscos e rollback\n\n{parts['risk']}\n")
    return title, body


def pr_title(project: Project, docs) -> str:
    fmt = project.cfg("pr_title", PR_TITLE_DEFAULT)
    if len(docs) == 1:
        d = docs[0]
        values = {"conv": CONVENTIONAL_BY_TYPE.get(d.type, "feat"), "type": d.type, "title": d.title,
                  "id": d.id, "scope": d.scalar("scope")}
    else:
        parents = {d.scalar("parent") for d in docs}
        epic = project.specs.get(parents.pop()) if len(parents) == 1 else None
        convs = {CONVENTIONAL_BY_TYPE.get(d.type, "feat") for d in docs}
        values = {"conv": "feat" if "feat" in convs else min(convs), "type": "wave",
                  "title": epic.title if epic else "; ".join(d.title for d in docs),
                  "id": ", ".join(d.id for d in docs), "scope": ""}
    title = values["title"]
    if len(title) > 1 and title[0].isupper() and title[1].islower():
        values["title"] = title[0].lower() + title[1:]
    return re.sub(r"\(\)", "", fmt.format_map(_Blank(values))).strip()


def pr_body(project: Project, docs) -> str:
    many = len(docs) > 1
    out = [f"<!-- sdd: {' '.join(d.id for d in docs)} — gerado por spec_graph.py pr -->", "", "## Resumo", ""]
    for d in docs:
        summary = (sub_text(d, "Relatório de Entrega", "O que foi entregue")
                   or COMMENT_RE.sub("", d.section("Visão Geral") or d.section("Problema") or "").strip())
        why = labeled_line(d, "Motivação", "Motivação") or COMMENT_RE.sub("", d.section("Causa Raiz") or "").strip()
        prefix = f"**{d.id}** — " if many else ""
        out.append(f"{prefix}{summary}".strip())
        if why:
            out += ["", f"**Por quê:** {why}"]
        out.append("")
    out += ["## Specs", "", "| Spec | Título | Tier · Tipo | Status | Board |", "|---|---|---|---|---|"]
    for d in docs:
        out.append(f"| [{d.id}]({project.rel(d.path)}) | {md_cell(d.title)} | {d.tier} · {d.type} | {d.status} "
                   f"| {md_cell(', '.join(d.list('external')) or '—')} |")
    out.append("")
    for heading, render in (("Como foi feito", pr_how), ("Arquitetura", pr_architecture),
                            ("Como foi testado", pr_tests), ("Risco, rollout e rollback", pr_rollout),
                            ("Breaking changes", pr_breaking), ("Gates", pr_gates)):
        out += [f"## {heading}", ""]
        for d in docs:
            if many:
                out += [f"### {d.id} — {d.title}", ""]
            out += [render(project, d), ""]
    extras = [d for d in docs if impediments(d) or parse_tables(COMMENT_RE.sub("", d.section("Emendas") or "")) and
              any(rows for _, rows in parse_tables(COMMENT_RE.sub("", d.section("Emendas") or "")))]
    if extras:
        out += ["## Impedimentos e emendas", ""]
        for d in extras:
            if many:
                out += [f"### {d.id}", ""]
            for name in ("Registro de Impedimentos", "Emendas"):
                content = COMMENT_RE.sub("", d.section(name) or "").strip()
                if any(rows for _, rows in parse_tables(content)):
                    out += [f"**{name}**", "", content, ""]
    out += ["## Checklist", "",
            "- [ ] Spec(s) aprovada(s) (H1) e gates G0–G4 com evidência",
            "- [ ] Testes do plano (unitários, integração, contrato, E2E) passando no CI",
            "- [ ] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado",
            "- [ ] Nenhuma alteração fora do escopo (`touches`) da spec",
            "- [ ] Documentação e CHANGELOG (Keep a Changelog) atualizados quando aplicável",
            "- [ ] Sem segredos ou dados sensíveis no código ou nos logs", ""]
    refs = [d.id for d in docs] + [e.split(":", 1)[1] for d in docs for e in d.list("external")]
    out.append("Refs: " + ", ".join(refs))
    return "\n".join(out) + "\n"


def capitalized(text: str) -> str:
    return text[:1].upper() + text[1:]


def pr_how(project, d):
    return capitalized(sub_text(d, "Relatório de Entrega", "Como foi feito") or
                       labeled_line(d, "Decisão Arquitetural", "Decisão") or
                       COMMENT_RE.sub("", d.section("Mudança Proposta") or "").strip() or "—")


def pr_architecture(project, d):
    lines = []
    ref = labeled_line(d, "Decisão Arquitetural", "Contexto") or labeled_line(d, "Mudança Proposta", "Padrão seguido")
    if ref:
        lines.append(f"- **Padrão seguido:** {ref}")
    deviation = labeled_line(d, "Decisão Arquitetural", "Desvio do padrão existente")
    lines.append(f"- **Desvio do padrão existente:** {deviation or 'Nenhum'}")
    for aid in d.list("adrs"):
        a = project.adrs.get(aid)
        lines.append(f"- {aid} — {a.title} ({a.status})" if a else f"- {aid}")
    return "\n".join(lines)


def pr_tests(project, d):
    verification = sub_text(d, "Relatório de Entrega", "Verificação")
    if any(rows for _, rows in parse_tables(verification)):
        return verification
    return "Testes do plano (ainda sem Relatório de Entrega): " + (", ".join(f"`{d.id}:{t}`" for t in plan_test_ids(d)) or "—")


def pr_rollout(project, d):
    rollout = COMMENT_RE.sub("", d.section("Plano de Rollout") or "").strip()
    return rollout or (f"- **Rollback:** {labeled_line(d, 'Mudança Proposta', 'Rollback')}"
                       if labeled_line(d, "Mudança Proposta", "Rollback") else "—")


def pr_breaking(project, d):
    """O que quebra vem da chave `breaking` da spec; uma emenda (contract_version > 1) não é quebra por si só."""
    if d.scalar("breaking"):
        return d.scalar("breaking")
    v = contract_version(d)
    return f"Nenhuma (contrato em v{v}; emendas compatíveis — ver Emendas)" if v > 1 else "Nenhuma"


def pr_gates(project, d):
    return COMMENT_RE.sub("", d.section("Registro de Gates") or "").strip() or "—"


def cmd_pr(project: Project, ids, title_only: bool, out):
    docs = []
    for sid in ids:
        d = project.specs.get(sid)
        if d is None:
            die(f"{sid} não encontrada")
        docs += [k for k in project.children(sid) if k.status in APPROVED_PLUS] if d.tier == "epic" else [d]
    if not docs:
        die("nenhuma spec executável aprovada para o PR")
    if project.private:
        title, body = private_pr(project, docs)
        patterns, allow = leak_rules(project)
        found = leak_findings(title, "título", patterns, allow)
        found += leak_findings("" if title_only else body, "corpo do PR", patterns, allow)
        if found:
            for origin, number, label, snippet in found:
                print(f"[LEAK] FAIL {origin}:{number} — {label} — \"{snippet}\"")
            print("pr: o PR teria marcas do SDD — ajuste a spec e gere de novo (nada foi gravado)")
            return 1
        if title_only:
            print(title)
        elif out:
            Path(out).write_text(body, encoding="utf-8")
            print(title)
        else:
            print(title + "\n\n" + body)
        return 0
    title = pr_title(project, docs)
    if title_only:
        print(title)
        return 0
    body = pr_body(project, docs)
    if out:
        Path(out).write_text(body, encoding="utf-8")
        print(title)
    else:
        print(title + "\n\n" + body)
    return 0


def cmd_pr_check(project: Project, title, body, body_file, base):
    title = title if title is not None else os.environ.get("PR_TITLE", "")
    if body_file:
        body = Path(body_file).read_text(encoding="utf-8")
    body = body if body is not None else os.environ.get("PR_BODY", "")
    results = []
    # Only declared specs count (title, "Refs:" line, sdd marker); narrative mentions in the body don't.
    declared = set(SPEC_REF_RE.findall(title))
    for line in body.splitlines():
        if re.match(r"^\s*(refs\s*:|<!--\s*sdd:)", line, re.IGNORECASE):
            declared |= set(SPEC_REF_RE.findall(line))
    refs = sorted(declared) if declared else sorted(set(SPEC_REF_RE.findall(body)))
    title_level = "WARN" if project.cfg("pr_title_policy", "fail") == "warn" else "FAIL"
    bare = re.sub(r"\s*\[no-spec\]\s*", " ", title, flags=re.IGNORECASE).replace(" :", ":").strip()
    m = CONVENTIONAL_RE.match(bare)
    if not m or m.group(1).lower() not in COMMIT_TYPES:
        results.append((title_level, "PR", (f"título fora do Conventional Commits: '{title[:60]}' — use "
                                            f"<tipo>(escopo)?: descrição [SPEC-NNNN] (tipos: {', '.join(COMMIT_TYPES)}); "
                                            "gere com `pr SPEC-X --title`")))
    try:
        max_len = int(project.cfg("pr_title_max", "100"))
    except ValueError:
        max_len = 100
    if len(title) > max_len:
        results.append((title_level, "PR", f"título com {len(title)} caracteres (máximo {max_len})"))
    docs = []
    if not refs:
        if "[no-spec]" in title.lower():
            results.append(("WARN", "PR", "PR marcado [no-spec] — reviewer confirma que não há mudança de comportamento"))
            base_ref = base or project.cfg("base_branch", "main")
            code, mb, _ = run_git(project.root, "merge-base", base_ref, "HEAD")
            if code == 0:
                changed = [f for f in run_git(project.root, "diff", "--name-only", mb.strip(), "HEAD")[1].splitlines() if f]
                code_files = [f for f in changed if not (f.endswith(".md") or f.startswith("docs/"))]
                if code_files:
                    results.append(("WARN", "PR", "[no-spec] altera arquivos que não são documentação: " + ", ".join(code_files[:8])
                                    + " — se muda comportamento, precisa de spec (mesmo lite, criada na hora)"))
        else:
            results.append(("FAIL", "PR", ("PR não referencia nenhuma spec (SPEC-NNNN no título ou corpo); "
                                           "mudança sem comportamento: marque [no-spec] no título")))
    for ref in refs:
        d = project.specs.get(ref)
        if d is None:
            results.append(("FAIL", "PR", f"{ref} não existe em docs/specs"))
        elif d.tier == "epic":
            results.append(("INFO", "PR", f"{ref} é épico — verificando só as specs executáveis citadas"))
        elif d.status not in APPROVED_PLUS:
            results.append(("FAIL", "H1", f"{ref} está '{d.status}' — só entra com spec aprovada pelo humano"))
        else:
            docs.append(d)
    if docs:
        ids = {d.id for d in docs}
        for level, subject, msg in validate(project, ids):
            if level == "ERROR" and subject in ids:
                results.append(("FAIL", "G0", f"{subject}: {msg}"))
        for d in docs:
            gates = gate_table(d) or {}
            missing = [g for g in MERGE_GATES if not gate_ok(gates, g)]
            if missing:
                results.append(("FAIL", "gates", f"{d.id}: sem PASS/N/A com evidência em {', '.join(missing)} antes do merge"))
            for item in open_impediments(d):
                results.append(("FAIL", "IMP", f"{d.id}: impedimento aberto {impediment_label(item)} — {item['Descrição']}"))
            _, spec_results = verify_spec(project, d.id, base)
            results += [(lvl, gate, f"{d.id}: {msg}") for lvl, gate, msg in spec_results if lvl in ("FAIL", "WARN")]
        base_ref = base or project.cfg("base_branch", "main")
        code, merge_base, _ = run_git(project.root, "merge-base", base_ref, "HEAD")
        if code == 0:
            changed = [f for f in run_git(project.root, "diff", "--name-only", merge_base.strip(), "HEAD")[1].splitlines() if f]
            allowed = [p for d in docs for p in d.list("touches")] + project.scope_exempt()
            outside = [f for f in changed if not matches_any(f, allowed)]
            if outside:
                results.append(("FAIL", "G4", "arquivos do PR fora do `touches` das specs citadas: " + ", ".join(outside[:10])
                                + (" …" if len(outside) > 10 else "")))
    if refs and "## Como foi testado" not in body:
        results.append(("WARN", "PR", "corpo fora do padrão — gere com `spec_graph.py pr <SPEC-ID>`"))
    header = f"pr-check — specs: {', '.join(refs) or 'nenhuma'}"
    return print_results(header, results)


# ---------------------------------------------------------------- vendor (enforcement outside the model)

PR_TEMPLATE_PATHS = {
    "github": ".github/pull_request_template.md",
    "gitlab": ".gitlab/merge_request_templates/Default.md",
    "azure": ".azuredevops/pull_request_template.md",
}
CI_TARGETS = {
    "github": ("ci/github-actions.yml", ".github/workflows/sdd.yml"),
    "gitlab": ("ci/gitlab-ci.yml", "ci/sdd.gitlab-ci.yml"),
    "generic": ("ci/sdd-check.sh", "tools/sdd/ci-check.sh"),
}
AGENT_FILES = ("AGENTS.md", "CLAUDE.md", "GEMINI.md")
AGENT_BLOCK_RE = re.compile(r"<!-- sdd:start -->.*?<!-- sdd:end -->\n?", re.DOTALL)


CODEOWNERS_PATHS = {"github": ".github/CODEOWNERS", "gitlab": ".gitlab/CODEOWNERS"}


def cmd_vendor(project: Project, ci, pr_template, hooks: bool, agents: bool, changelog: bool, force: bool,
               codeowners=None, owner=None, overwrite=False):
    notes = []

    def put(rel: str, content: str, executable: bool = False, always: bool = False):
        dest = project.root / rel
        if dest.exists():
            current = dest.read_text(encoding="utf-8")
            if current == content:
                print(f"inalterado: {rel}")
                return
            if not (always or overwrite):
                changed = sum(1 for l in difflib.unified_diff(current.splitlines(), content.splitlines(), lineterm="")
                              if l[:1] in "+-" and l[:3] not in ("+++", "---"))
                print(f"mantido (customizado no projeto, {changed} linha(s) diferem do modelo da skill): {rel} — "
                      "revise e, se quiser o modelo, use --overwrite")
                return
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(content, encoding="utf-8")
        if executable:
            dest.chmod(0o755)
        print(f"escrito: {rel}")

    def asset(name: str) -> str:
        path = ASSETS_DIR / name
        if not path.exists():
            die(f"asset não encontrado: {path}")
        return path.read_text(encoding="utf-8")

    put(VENDORED_TOOL, Path(__file__).read_text(encoding="utf-8"), executable=True, always=True)
    if ci:
        src, dest = CI_TARGETS[ci]
        put(dest, asset(src), executable=dest.endswith(".sh"))
        if ci == "gitlab":
            notes.append(f"inclua no .gitlab-ci.yml: `include: {{ local: '{dest}' }}`")
        if ci == "generic":
            notes.append(f"no seu CI, exporte PR_TITLE, PR_BODY e BASE_REF e rode `{dest}` com histórico completo do git")
    if pr_template:
        put(PR_TEMPLATE_PATHS[pr_template], asset("pull_request_template.md"))
    if hooks:
        put(".githooks/commit-msg", asset("hooks/commit-msg"), executable=True)
        put(".githooks/pre-commit", asset("hooks/pre-commit"), executable=True)
        notes.append("ative os hooks (decisão do time): `git config core.hooksPath .githooks` "
                     "— ou chame os mesmos scripts pelo husky/lefthook/pre-commit que o projeto já usa")
    if codeowners:
        if not owner:
            die("--codeowners exige --owner (ex: @usuario ou @org/time)")
        extra = [CI_TARGETS[ci][1]] if ci else []
        lines = ["# SDD: arquivos de controle exigem revisão de quem responde pelo processo"] + \
                [f"/{path} {owner}" for path in CONTROL_PATHS + extra]
        if codeowners in CODEOWNERS_PATHS:
            put(CODEOWNERS_PATHS[codeowners], "\n".join(lines) + "\n")
            notes.append("ative 'exigir revisão de code owners' na proteção da branch para o arquivo ter efeito")
        else:
            notes.append("plataforma sem CODEOWNERS: configure revisores obrigatórios por caminho para: "
                         + ", ".join(CONTROL_PATHS + extra))
    if changelog and not (project.root / "CHANGELOG.md").exists():
        put("CHANGELOG.md", asset("CHANGELOG.template.md"))
    if agents:
        block = asset("agents-snippet.md")
        existing = [f for f in AGENT_FILES if (project.root / f).exists()] or ["AGENTS.md"]
        for name in existing:
            path = project.root / name
            text = path.read_text(encoding="utf-8") if path.exists() else ""
            new = AGENT_BLOCK_RE.sub(block, text) if AGENT_BLOCK_RE.search(text) else (text.rstrip() + "\n\n" + block if text else block)
            if new != text:
                path.write_text(new, encoding="utf-8")
                print(f"atualizado: {name} (bloco SDD)")
        if existing == ["AGENTS.md"] and not any((project.root / f).exists() for f in ("CLAUDE.md", "GEMINI.md")):
            notes.append("criado AGENTS.md; Claude Code lê CLAUDE.md e Gemini lê GEMINI.md — crie-os com `@AGENTS.md` se usar essas ferramentas")
    for note in notes:
        print(f"→ {note}")
    return 0


def create_from_template(project: Project, template: str, folder: Path, prefix: str, title: str, slug):
    path = TEMPLATES_DIR / template
    if not path.exists():
        die(f"template não encontrado: {path}")
    number = project.next_number(prefix)
    new_id = f"{prefix}-{number:04d}"
    text = path.read_text(encoding="utf-8")
    text = text.replace("{{id}}", new_id).replace("{{created}}", today()).replace("{{title}}", title)
    text = set_meta(text, "title", title)
    folder.mkdir(parents=True, exist_ok=True)
    dest = folder / f"{new_id}-{slugify(slug or title)}.md"
    return new_id, dest, text


def cmd_new(project: Project, tier: str, title: str, slug, parent):
    if parent:
        p = project.specs.get(parent)
        if p is None or p.tier != "epic":
            die(f"--parent {parent} não existe ou não é um épico")
        if tier == "epic":
            die("épico não pode ter parent")
    new_id, dest, text = create_from_template(project, f"template-{tier}.md", project.specs_dir, "SPEC", title, slug)
    if parent:
        text = set_meta(text, "parent", parent)
    dest.write_text(text, encoding="utf-8")
    print(f"{new_id}  {project.rel(dest)}")
    return 0


def cmd_new_adr(project: Project, title, slug, baseline=None):
    template = "template-adr.md"
    if baseline:
        if baseline not in PILLARS:
            die(f"pilar desconhecido '{baseline}' ({', '.join(PILLARS)})")
        template = f"adr-baseline/{baseline}.md"
        title = title or PILLARS[baseline][0]
    if not title:
        die("informe --title (ou --baseline <pilar>)")
    new_id, dest, text = create_from_template(project, template, project.adr_dir, "ADR", title, slug)
    dest.write_text(text, encoding="utf-8")
    print(f"{new_id}  {project.rel(dest)}")
    return 0


def cmd_set(project: Project, args):
    ids = [a for a in args if "=" not in a]
    pairs = [a.split("=", 1) for a in args if "=" in a]
    if not ids or not pairs:
        die("uso: set ID [ID ...] chave=valor [chave=valor ...]")
    docs = []
    for i in ids:
        doc = project.specs.get(i) or project.adrs.get(i)
        if doc is None:
            die(f"{i} não encontrado")
        docs.append(doc)
    for doc in docs:
        text = doc.text
        for key, raw in pairs:
            if raw == "today":
                value = today()
            elif raw.strip().startswith("["):
                value = parse_scalar(raw)
            else:
                value = raw
            if key == "status":
                allowed = ADR_STATUSES if doc.id.startswith("ADR-") else STATUSES
                if value not in allowed:
                    die(f"status '{value}' inválido para {doc.id} ({' | '.join(allowed)})")
            if key == "tier" and value not in TIERS:
                die(f"tier '{value}' inválido ({' | '.join(TIERS)})")
            text = set_meta(text, key, value)
            print(f"{doc.id}: {key} = {fmt_value(value)}")
        doc.path.write_text(text, encoding="utf-8")
    return 0


def cmd_next_id(project: Project, kind: str):
    prefix = "SPEC" if kind == "spec" else "ADR"
    print(f"{prefix}-{project.next_number(prefix):04d}")
    return 0


# ---------------------------------------------------------------- upgrade (bring old specs to the current templates)

def template_parts(tier: str):
    text = (TEMPLATES_DIR / f"template-{tier}.md").read_text(encoding="utf-8")
    fm, body = split_frontmatter(text)
    keys = [line.split(":", 1)[0].strip() for line in (fm or "").splitlines() if re.match(r"^[A-Za-z_][\w-]*\s*:", line)]
    meta = parse_yaml_subset(fm or "")
    return keys, meta, parse_sections(body)


def strip_number(heading: str) -> str:
    return re.sub(r"^\d+(\.\d+)*\.?\s*", "", heading).strip()


def upgrade_doc(doc: Doc):
    keys, tmeta, tsections = template_parts(doc.tier)
    text, changes = doc.text, []
    for key in keys:
        if key in doc.meta:
            continue
        default = tmeta.get(key, "")
        if isinstance(default, str) and "{{" in default:
            continue
        text = set_meta(text, key, default)
        changes.append(f"frontmatter +{key}: {fmt_value(default) or '(vazio)'}")
    lines = text.split("\n")
    required = [kw for kw, _ in REQUIRED_SECTIONS[doc.tier]]
    tnames = [strip_number(h) for h, _ in tsections]
    for keyword in required:
        if section_bounds(lines, keyword) is not None:
            continue
        idx = next((i for i, name in enumerate(tnames) if norm(keyword) in norm(name)), None)
        if idx is None:
            continue
        content = tsections[idx][1].replace("{{id}}", doc.id)
        if doc.status != "proposed":
            content = PLACEHOLDER_RE.sub("", content.replace(
                "{{- [ ] dúvida que precisa de resposta e quem responde | Nenhuma}}", "Nenhuma (seção adicionada por upgrade)"))
        block = [f"## {tnames[idx]}"] + content.rstrip("\n").split("\n") + [""]
        insert_at = None
        for later in tnames[idx + 1:]:
            bounds = section_bounds(lines, later)
            if bounds is not None:
                insert_at = bounds[0]
                break
        if insert_at is None:
            if lines and lines[-1].strip():
                lines.append("")
            lines += block
        else:
            lines[insert_at:insert_at] = block
        changes.append(f"seção +{tnames[idx]}")
    text = "\n".join(lines)
    gate_rows = [row for h, c in tsections if norm("Registro de Gates") in norm(h)
                 for _, rows in parse_tables(c) for row in rows]
    probe = Doc(doc.path, text)
    existing = gate_table(probe)
    if existing is not None:
        missing = [row for row in gate_rows if row[0].split()[0].upper() not in existing]
        if missing:
            lines = text.split("\n")
            start, end = section_bounds(lines, "Registro de Gates")
            last = max(i for i in range(start + 1, end) if lines[i].strip().startswith("|"))
            lines[last + 1:last + 1] = [f"| {row[0]} | PENDING | | |" for row in missing]
            text = "\n".join(lines)
            changes.append("gates +" + ", ".join(row[0].split()[0] for row in missing))
    return text, changes


def cmd_upgrade(project: Project, ids, apply: bool):
    targets = [project.specs[i] for i in ids] if ids else [d for _, d in sorted(project.specs.items())]
    total = 0
    for doc in targets:
        if doc.legacy or doc.tier not in TIERS:
            continue
        text, changes = upgrade_doc(doc)
        if not changes:
            continue
        total += 1
        print(f"{doc.id} [{doc.status}]")
        for change in changes:
            print(f"  {change}")
        if doc.status == "implemented" and doc.section("Relatório de Entrega") is None:
            print("  ⚠ implemented sem Relatório de Entrega: preencha a seção adicionada ou marque `legacy: true`")
        if apply:
            doc.path.write_text(text, encoding="utf-8")
    if not total:
        print("Todas as specs já estão no formato atual.")
    elif not apply:
        print(f"\nSimulação: {total} spec(s) seriam atualizadas. Rode com --apply para gravar.")
    else:
        print(f"\nAtualizadas {total} spec(s). Rode `validate` para ver o que ainda precisa ser preenchido.")
    return 0


def legacy_title(body: str, fallback: str) -> str:
    for line in body.split("\n"):
        if line.startswith("# "):
            title = re.sub(r"^(especifica[cç][aã]o t[eé]cnica|technical spec(ification)?|spec)\s*[:—-]\s*", "",
                           line[2:].strip(), flags=re.IGNORECASE)
            return title or fallback
    return fallback


def cmd_migrate(project: Project, apply: bool):
    items = []
    for folder in LEGACY_DIRS:
        d = project.specs_dir / folder
        if not d.is_dir():
            continue
        for p in sorted(d.glob("*.md")):
            m = re.match(r"^(\d{4}-\d{2}-\d{2})-(.+)$", p.stem)
            if m:
                created, slug = m.group(1), m.group(2)
            else:
                created, slug = dt.date.fromtimestamp(p.stat().st_mtime).isoformat(), p.stem
            items.append((created, p.name, folder, p, slug))
    if not items:
        print("Nada a migrar: nenhum .md em docs/specs/{proposed,approved,implemented,deprecated}/.")
        return 0
    items.sort()
    number = project.next_number("SPEC")
    plan = []
    for created, _, folder, src, slug in items:
        sid = f"SPEC-{number:04d}"
        number += 1
        plan.append((sid, created, folder, src, project.specs_dir / f"{sid}-{slugify(slug)}.md"))
    for sid, created, folder, src, dst in plan:
        print(f"{sid}  [{folder}]  {project.rel(src)}  →  {project.rel(dst)}")
    if not apply:
        print("\nSimulação. Rode novamente com --apply para executar.")
        return 0

    in_git = run_git(project.root, "rev-parse", "--is-inside-work-tree")[0] == 0
    for sid, created, folder, src, dst in plan:
        text = src.read_text(encoding="utf-8")
        fm, body = split_frontmatter(text)
        existing = parse_yaml_subset(fm) if fm is not None else {}
        meta = {
            "id": sid, "title": existing.get("title") or legacy_title(body, src.stem), "tier": "full",
            "type": "feature", "user_facing": "", "status": folder, "created": created, "legacy": True, "parent": "", "depends_on": [],
            "consumes_contract": [], "contract_version": "1", "touches": [], "adrs": [], "size": "M",
            "approved_by": "legacy" if folder in APPROVED_PLUS else "",
            "approved_at": created if folder in APPROVED_PLUS else "",
        }
        for k, v in existing.items():
            if k not in ("id", "status", "legacy"):
                meta[k] = v
        header = "\n".join(f"{k}: {fmt_value(v)}".rstrip() for k, v in meta.items())
        new_text = f"---\n{header}\n---\n{body.lstrip(chr(10))}"
        tracked = in_git and run_git(project.root, "ls-files", "--error-unmatch", str(src))[0] == 0
        if tracked:
            code, _, err = run_git(project.root, "mv", str(src), str(dst))
            if code != 0:
                die(f"git mv falhou para {src.name}: {err.strip()}")
        else:
            src.rename(dst)
        dst.write_text(new_text, encoding="utf-8")
    for folder in LEGACY_DIRS:
        d = project.specs_dir / folder
        if d.is_dir() and not any(d.iterdir()):
            d.rmdir()
    print(f"\nMigradas {len(plan)} spec(s) com `legacy: true` (validate checa só o frontmatter delas).")
    print("Specs abertas: reescreva no template atual, preencha depends_on/touches e remova `legacy` antes de planejar.")
    print("Links antigos por caminho (proposed/…/arquivo.md) precisam ser trocados por referências de ID.")
    if (project.specs_dir / "references").is_dir():
        print("docs/specs/references/ (template antigo) pode ser removida: os templates vivem na skill.")
    return 0


# ---------------------------------------------------------------- cli

def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="spec_graph.py",
        description="Ferramentas determinísticas do SDD: G0, ondas de execução, índice e G1/G4 via git.")
    parser.add_argument("--root", help="raiz do projeto (padrão: detectada a partir do diretório atual)")
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("validate", help="G0: valida specs/ADRs (todos ou os IDs informados; épico inclui filhas)")
    p.add_argument("ids", nargs="*")
    p.add_argument("--staged", action="store_true", help="só specs/ADRs no stage do git, exceto rascunhos (pre-commit)")
    p.add_argument("--exclude-drafts", action="store_true", help="ignora erros de specs 'proposed' (CI)")
    p = sub.add_parser("status", help="fase atual e próximo passo obrigatório (todas as abertas ou os IDs)")
    p.add_argument("ids", nargs="*")
    p = sub.add_parser("export", help="estado completo em JSON neutro (para adaptadores de board e automações)")
    p.add_argument("--out")
    p = sub.add_parser("sync", help="operações para alinhar o board ao repositório (repo → board)")
    p.add_argument("--board", help="snapshot neutro do board em JSON (gerado pelo adaptador)")
    p.add_argument("--json", action="store_true")
    p.add_argument("--check", action="store_true", help="exit 1 se houver divergência")
    p.add_argument("--structure", action="store_true", help="imprime a estrutura que o board precisa ter (preparação)")
    p = sub.add_parser("pr", help="título e corpo padronizados de PR para uma ou mais specs (épico = filhas aprovadas)")
    p.add_argument("ids", nargs="+")
    p.add_argument("--title", action="store_true", help="imprime só o título")
    p.add_argument("--out", help="escreve o corpo nesse arquivo e imprime o título")
    p = sub.add_parser("pr-check", help="CI: PR cita specs aprovadas, gates G0–G4, TDD, escopo (usa PR_TITLE/PR_BODY)")
    p.add_argument("--title")
    p.add_argument("--body")
    p.add_argument("--body-file")
    p.add_argument("--base", help="ref da branch alvo (ex: origin/main)")
    p = sub.add_parser("vendor", help="instala no projeto o controle fora do modelo: script, CI, template de PR, hooks, bloco em AGENTS.md")
    p.add_argument("--ci", choices=tuple(CI_TARGETS))
    p.add_argument("--pr-template", choices=tuple(PR_TEMPLATE_PATHS))
    p.add_argument("--hooks", action="store_true")
    p.add_argument("--agents", action="store_true")
    p.add_argument("--changelog", action="store_true", help="cria CHANGELOG.md (Keep a Changelog) se não existir")
    p.add_argument("--codeowners", help="github | gitlab | outra (lista caminhos para revisores obrigatórios)")
    p.add_argument("--owner", help="dono dos arquivos de controle (ex: @usuario)")
    p.add_argument("--force", action="store_true", help="(compatibilidade) a ferramenta em tools/sdd é sempre atualizada")
    p.add_argument("--overwrite", action="store_true", help="substitui também arquivos customizados (workflow, template, hooks)")
    sub.add_parser("waves", help="plano de execução em ondas + próximo lote")
    sub.add_parser("next", help="próximo lote pronto para despachar")
    p = sub.add_parser("index", help="gera docs/specs/INDEX.md")
    p.add_argument("--stdout", action="store_true")
    p = sub.add_parser("verify", help="G1/G4 via git: red antes do green, tags de teste, escopo")
    p.add_argument("id")
    p.add_argument("--base", help="branch base (padrão: base_branch do sdd-config.yml ou main)")
    p = sub.add_parser("impacted", help="specs afetadas por mudança no contrato/implementação de uma spec")
    p.add_argument("id")
    p = sub.add_parser("report", help="resumo de entrega (o que, como, verificação, gates) para PR/changelog")
    p.add_argument("id")
    p.add_argument("--changelog", action="store_true", help="entrada no formato Keep a Changelog")
    p = sub.add_parser("gate", help="registra um gate: --run gera a evidência executando os comandos do sdd-config")
    p.add_argument("id")
    p.add_argument("gate")
    p.add_argument("--run", action="store_true", help="executa validate/verify/comandos e grava PASS ou FAIL com a evidência")
    p.add_argument("--cwd", help="diretório onde rodar comandos e git (ex: worktree da spec); o registro vai para a spec do projeto atual")
    p.add_argument("--na", help="registra N/A com motivo (G3/G6: aponte SPEC-NNNN ou dispensa de pilar)")
    p.add_argument("--fail", help="registra FAIL com motivo")
    p.add_argument("--by", help="gates humanos: quem aprovou; G4: veredito do Reviewer")
    p.add_argument("--evidence", help="texto de evidência (com --manual) ou complemento")
    p.add_argument("--manual", action="store_true", help="PASS sem execução (marcado MANUAL:, gera aviso)")
    p.add_argument("--allow-dirty", action="store_true")
    p.add_argument("--expect-green", action="store_true", help="G1: todos os testes novos são de guarda/caracterização")
    p.add_argument("--at", help="G1: roda no commit red indicado, numa worktree temporária")
    p = sub.add_parser("impede", help="registra impedimento (spec | decisão | trabalho | externo | falha)")
    p.add_argument("id")
    p.add_argument("--type", required=True)
    p.add_argument("--reason", required=True, help="o que falta para prosseguir")
    p.add_argument("--owner", required=True, help="quem precisa agir")
    p.add_argument("--phase", help="fase ou gate em que parou (ex: G2, EXECUTE)")
    p.add_argument("--tried", help="o que já foi tentado")
    p = sub.add_parser("resolve", help="fecha impedimento apontando a resolução (emenda, ADR, SPEC-NNNN ou ação)")
    p.add_argument("id")
    p.add_argument("imp_id")
    p.add_argument("--resolution", required=True)
    p = sub.add_parser("new", help="cria spec a partir do template com o próximo ID")
    p.add_argument("--tier", choices=TIERS, required=True)
    p.add_argument("--title", required=True)
    p.add_argument("--slug")
    p.add_argument("--parent")
    p = sub.add_parser("new-adr", help="cria ADR a partir do template (ou do ADR de base de um pilar com --baseline)")
    p.add_argument("--title")
    p.add_argument("--baseline", help="pilar do catálogo (veja `pillars`)")
    p.add_argument("--slug")
    p = sub.add_parser("set", help="atualiza frontmatter: set ID [ID ...] chave=valor (valor 'today' = data de hoje)")
    p.add_argument("args", nargs="+")
    p = sub.add_parser("next-id", help="próximo ID livre")
    p.add_argument("kind", choices=("spec", "adr"))
    p = sub.add_parser("worktree", help="caminho e branch padronizados da worktree de uma spec (--create cria)")
    p.add_argument("id")
    p.add_argument("--create", action="store_true")
    p.add_argument("--base", help="branch de partida (padrão: integração do épico ou base_branch)")
    sub.add_parser("journeys", help="cobertura das jornadas críticas (critical_journeys) por testes E2E")
    p = sub.add_parser("living-docs", help="cobertura do guia de jornadas e do mapa do sistema (living_docs); --init cria os ausentes")
    p.add_argument("--init", action="store_true")
    p = sub.add_parser("whats-new", help="novidades da skill desde a versão do projeto e chaves novas do sdd-config; "
                                         "--ack grava sdd_version")
    p.add_argument("--ack", action="store_true", help="grava sdd_version = versão da skill no sdd-config (depois de revisar)")
    sub.add_parser("pillars", help="cobertura dos pilares de entrega (perfil do sdd-config) e N/A recorrente")
    p = sub.add_parser("upgrade", help="atualiza specs antigas para os templates atuais (seções, chaves, gates); simulação por padrão")
    p.add_argument("ids", nargs="*")
    p.add_argument("--apply", action="store_true")
    p = sub.add_parser("leak-check", help="modo privado: marcas do SDD no diff, mensagens e branch (sem fonte: tudo)")
    p.add_argument("--staged", action="store_true")
    p.add_argument("--range", dest="rng", help="intervalo de commits A..B (A = 000… → commits fora dos remotos)")
    p.add_argument("--message", help="arquivo com a mensagem de commit")
    p.add_argument("--branch", help="nome de branch")
    p = sub.add_parser("private", help="modo privado: SDD fora do repositório do time (init | status | off)")
    p.add_argument("action", choices=["init", "status", "off", "detect"])
    p.add_argument("--spec-root", help="pasta das specs (padrão: <default_root>/<org>/<repo>)")
    p.add_argument("--sync", choices=["none", "push"], help="push do spec_root após cada commit (padrão: none)")
    p = sub.add_parser("migrate", help="converte o layout legado (pastas de status + nomes com data)")
    p.add_argument("--apply", action="store_true")
    args = parser.parse_args(argv)

    root = Path(args.root).resolve() if args.root else find_root(Path.cwd())
    project = Project(root)
    if project.private:
        print(f"{PRIVATE_BANNER} — nada de SDD no repositório (specs em {project.spec_root})", file=sys.stderr)
        if args.command in ("vendor", "pr-check"):
            die(f"{args.command} não se aplica em modo privado: o repositório é do time e o SDD fica no spec_root")
    try:
        return dispatch(args, project)
    finally:
        if project.private and writes_records(args):
            ids = getattr(args, "ids", None) or [getattr(args, "id", None) or ""]
            commit_records(project, f"sdd: {args.command} {' '.join(i for i in ids if i)}".strip())


def writes_records(args) -> bool:
    """Comando que grava registros: `living-docs` só com --init e `whats-new` só com --ack (sem eles, só leem)."""
    if args.command == "living-docs":
        return args.init
    if args.command == "whats-new":
        return args.ack
    return args.command in RECORD_COMMANDS


def dispatch(args, project: Project):
    if args.command == "validate":
        return cmd_validate(project, args.ids, args.staged, args.exclude_drafts)
    if args.command == "status":
        return cmd_status(project, args.ids)
    if args.command == "export":
        return cmd_export(project, args.out)
    if args.command == "sync":
        return cmd_sync(project, args.board, args.json, args.check, args.structure)
    if args.command == "pr":
        return cmd_pr(project, args.ids, args.title, args.out)
    if args.command == "pr-check":
        return cmd_pr_check(project, args.title, args.body, args.body_file, args.base)
    if args.command == "vendor":
        return cmd_vendor(project, args.ci, args.pr_template, args.hooks, args.agents, args.changelog, args.force,
                          args.codeowners, args.owner, args.overwrite)
    if args.command == "waves":
        return cmd_waves(project)
    if args.command == "next":
        return cmd_next(project)
    if args.command == "index":
        return cmd_index(project, args.stdout)
    if args.command == "verify":
        return cmd_verify(project, args.id, args.base)
    if args.command == "impacted":
        return cmd_impacted(project, args.id)
    if args.command == "report":
        return cmd_report(project, args.id, args.changelog)
    if args.command == "gate":
        return cmd_gate(project, args.id, args.gate, args.run, args.cwd, args.na, args.fail, args.by, args.evidence,
                        args.manual, args.allow_dirty, args.expect_green, args.at)
    if args.command == "impede":
        return cmd_impede(project, args.id, args.type, args.reason, args.owner, args.phase, args.tried)
    if args.command == "resolve":
        return cmd_resolve(project, args.id, args.imp_id, args.resolution)
    if args.command == "new":
        return cmd_new(project, args.tier, args.title, args.slug, args.parent)
    if args.command == "new-adr":
        return cmd_new_adr(project, args.title, args.slug, args.baseline)
    if args.command == "set":
        return cmd_set(project, args.args)
    if args.command == "next-id":
        return cmd_next_id(project, args.kind)
    if args.command == "worktree":
        return cmd_worktree(project, args.id, args.create, args.base)
    if args.command == "journeys":
        return cmd_journeys(project)
    if args.command == "living-docs":
        return cmd_living_docs(project, args.init)
    if args.command == "whats-new":
        return cmd_whats_new(project, args.ack)
    if args.command == "pillars":
        return cmd_pillars(project)
    if args.command == "upgrade":
        return cmd_upgrade(project, args.ids, args.apply)
    if args.command == "leak-check":
        return cmd_leak_check(project, args.staged, args.rng, args.message, args.branch)
    if args.command == "private":
        return cmd_private(project, args.action, args.spec_root, args.sync)
    if args.command == "migrate":
        return cmd_migrate(project, args.apply)
    return 2


if __name__ == "__main__":
    sys.exit(main())
