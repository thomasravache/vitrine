# Runbook — Rollback

> SPEC-0005. Rollback de aplicação em um passo (ADR-0004). Migrações são sempre compatíveis com a versão anterior (expand/contract, ADR-0011): voltar a aplicação não exige voltar o schema.

## Automático
`deploy/deploy.sh` já volta sozinho para a tag anterior quando o health check da nova versão falha (sai com código 1).
Migração que falha não troca a versão (nada a desfazer).

## Manual (um passo)
```bash
deploy/rollback.sh
```
Implanta a tag de `deploy/.state/previous` com o `deploy/.env` atual (roda o `migrate` da versão antiga — não aplica nada novo — e espera o health).
Depois do rollback, `current` e `previous` trocam de lugar: rodar de novo volta para a versão de onde se saiu.

No servidor: `ssh deploy@<host> 'cd /opt/vitrine && deploy/rollback.sh'`.
Para uma tag específica (mais antiga que a anterior): `deploy/deploy.sh <sha>`; pelo GitHub: *Run workflow* em `Deploy` com a `tag`.

## Conferir
```bash
cat deploy/.state/current deploy/.state/previous
docker compose -p vitrine ps
curl -fsS https://<SITE_ADDRESS>/health/live   # local: -k --resolve vitrine.localhost:443:127.0.0.1
```

## Código
Reverter o merge commit na `main` (`git revert -m 1 <merge>`) — o CI publica uma nova imagem e o deploy segue o caminho normal.
