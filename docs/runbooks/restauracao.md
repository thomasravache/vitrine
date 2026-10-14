# Runbook — Restauração do banco

> Procedimento escrito na SPEC-0005; **executado e cronometrado pela primeira vez na SPEC-0009** (nada provisionado antes).
> Alvos (ADR-0010): RPO ≤ 15 min, RTO ≤ 4 h; ensaio trimestral registrado.

## Produção — PostgreSQL gerenciado (PITR, 7 dias)
1. Escolha o instante alvo (UTC) anterior ao incidente.
2. Painel DigitalOcean → Databases → `vitrine-production-pg` → *Backups* → *Restore from point in time* (ou `doctl databases fork vitrine-production-pg-restore --restore-from-cluster-id <id> --restore-from-timestamp <ISO-8601>`). A restauração cria um **cluster novo**; o original fica intacto.
3. Confira o cluster restaurado (contagens das tabelas críticas, `wolverine.wolverine_incoming_envelopes`) com um usuário de leitura.
4. Coloque a aplicação em manutenção (pare a API: `docker compose -p vitrine stop api`) para não gravar no banco antigo.
5. Aponte `DB_CONNECTION_STRING` do Environment `production` para o cluster restaurado e rode o deploy da tag atual (o `migrate` alinha o schema, se preciso).
6. Atualize o OpenTofu (`tofu import` do cluster novo ou troca de nome) para o estado refletir o banco em uso; remova o cluster antigo só depois de 7 dias.
7. Registre: instante alvo, início/fim, RPO e RTO medidos, quem executou.

## Staging e local — Postgres em container
Dados descartáveis/sintéticos: `docker compose -p vitrine down -v` e novo deploy (o `migrate` recria o schema).
Cópia pontual, se necessário: `docker compose -p vitrine exec postgres pg_dump -U vitrine vitrine > dump.sql` (nunca versionar).
