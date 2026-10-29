# Runbook — Alerta `synthetic-login-page`

> SPEC-0007. Regra em `deploy/alloy/grafana/alert-rules.yaml`. Responsável: thomas.

## Sintoma
A verificação sintética de `GET https://<SITE_ADDRESS>/entrar` (página de login da jornada `acesso`) falhou nas 3 últimas execuções: o vendedor não consegue entrar.

## Diagnóstico
1. `curl -sS -o /dev/null -w '%{http_code}\n' https://<SITE_ADDRESS>/entrar` — 200 esperado com o `index.html` da SPA.
2. Certificado: `curl -vI https://<SITE_ADDRESS>/ 2>&1 | grep -iE 'expire|SSL'` (Caddy renova sozinho; falha de renovação aparece em `docker compose -p vitrine logs caddy`).
3. `synthetic-ready` também disparou? Então a causa é a Api/banco — siga [synthetic-ready.md](synthetic-ready.md).
4. Só `/entrar` falha: imagem sem a SPA (`wwwroot/index.html`)? Erros da SPA no Sentry (projeto da SPA, release = SHA).

## Ação
- Caddy fora: `docker compose -p vitrine up -d caddy`.
- DNS/certificado: corrigir o registro DNS ou liberar as portas 80/443 para a renovação.
- Erro de build da SPA: reimplantar a última imagem boa.

## Rollback
Se começou com um deploy: `deploy/rollback.sh` (ver [rollback.md](rollback.md)).
