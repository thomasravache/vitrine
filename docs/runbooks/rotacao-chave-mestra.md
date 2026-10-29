# Runbook — Chave mestra do Data Protection (geração e rotação)

> SPEC-0008, ADR-0006. A chave mestra (`DataProtection__MasterKey`, 32 bytes aleatórios em Base64) cifra, com
> AES-256-GCM, cada chave do anel do ASP.NET Core Data Protection guardado no PostgreSQL (`security.data_protection_keys`).
> O anel protege os cookies de sessão e antiforgery e os campos cifrados com `IFieldProtector` (ex.: tokens do
> Mercado Livre). **Perder a chave mestra = perder as sessões e todos os campos cifrados.**

## Onde ela fica
- Secret do Environment (`staging`, `production`) dentro de `DEPLOY_ENV_FILE`, como `DATA_PROTECTION_MASTER_KEY=<base64>`;
  o `deploy.sh` grava no `deploy/.env` (600) do servidor e o Compose a entrega à Api como `DataProtection__MasterKey`.
- Uma cópia no cofre pessoal do responsável, **fora do servidor** (ADR-0006).
- Nunca em arquivo versionado, log, issue ou chat. O valor de `deploy/local/.env.example` é sintético e público: só
  para o ambiente local prod-like.

## Gerar (primeira vez ou rotação)
```bash
openssl rand -base64 32
```
Grave o valor no cofre pessoal **antes** de colocá-lo no Environment.

Sem a chave (ou com valor que não seja Base64 de 32 bytes, ou com uma chave que não decifra nenhuma chave do anel já
gravado), a Api não sobe fora de Development: o container sai com `DataProtection:MasterKey ... não configurada` /
`... inválida` / `... não decifra nenhuma chave do anel` e o `deploy.sh` volta para a versão anterior. O Compose
também recusa subir sem `DATA_PROTECTION_MASTER_KEY`.

## Rotação (sem derrubar sessões nem perder campos cifrados)
Quando: suspeita de vazamento, saída de alguém com acesso ao cofre ou ao Environment, ou rotina anual.

1. Gere a nova chave (acima) e guarde-a no cofre.
2. No `DEPLOY_ENV_FILE` do Environment:
   - `DATA_PROTECTION_PREVIOUS_MASTER_KEY=<chave atual>`
   - `DATA_PROTECTION_MASTER_KEY=<chave nova>`
3. Rode o deploy da versão atual (GitHub: *Run workflow* em `Deploy` com a mesma `tag`; ou no servidor
   `deploy/deploy.sh <tag atual>` depois de atualizar o `deploy/.env`). O passo `migrate` recifra com a chave nova
   todas as chaves do anel que ainda estão sob a anterior e registra no log (saída do `deploy.sh`):
   `Rotação da chave mestra: N chave(s) do Data Protection recifrada(s) com a chave atual; M já estava(m) nela.`
   Durante a troca, a Api nova aceita as duas chaves para leitura e cifra o que for novo só com a nova.
4. Confira:
   ```bash
   curl -fsS https://<SITE_ADDRESS>/health/ready
   ```
   Uma sessão aberta antes da rotação continua válida (abra `/painel` num navegador já logado).
5. **Antes de remover a chave anterior**, rode o `migrate` de novo, ainda com as duas chaves no `deploy/.env` (a
   versão antiga, que só conhecia a chave anterior, pode ter gravado uma chave nova no anel entre o `migrate` do passo 3
   e a troca de versão):
   ```bash
   cd /opt/vitrine   # local: raiz do repositório
   docker compose -p vitrine --project-directory deploy --env-file deploy/.env \
     -f deploy/compose.yaml -f deploy/compose.override.yaml run --rm migrate 2>&1 | grep 'Rotação da chave mestra'
   ```
   Exija `Rotação da chave mestra: 0 chave(s) do Data Protection recifrada(s) ...`. Se vier N > 0, repita este passo
   até dar 0 (cada execução recifra o que restou).
6. Só então remova `DATA_PROTECTION_PREVIOUS_MASTER_KEY` do `DEPLOY_ENV_FILE` (deixe vazia) e faça um novo deploy. Se
   alguma chave do anel ainda dependesse só da anterior e nenhuma decifrasse com a nova, a Api não subiria
   (`DataProtection:MasterKey ... não decifra nenhuma chave do anel`) e o `deploy.sh` voltaria à versão em execução.
   Confira de novo que a sessão aberta antes da rotação continua válida.
7. Apague a chave anterior do cofre só depois do passo 6 (e de um backup do banco feito já com a chave nova).

## Se a chave mestra foi perdida
Não há recuperação: as chaves do anel ficam ilegíveis, e a Api se recusa a subir com uma chave que não decifra o anel.
Gere uma chave nova, apague o anel antigo e faça o deploy; o Data Protection cria um anel novo (todos os usuários
precisam entrar de novo; os campos cifrados com `IFieldProtector` precisam ser obtidos de novo — ex.: reconectar as
contas do Mercado Livre). Registre o incidente.
```bash
# Só com a chave perdida de fato: apaga as chaves ilegíveis (backup antes).
docker compose -p vitrine exec postgres psql -U vitrine -d vitrine -c 'delete from security.data_protection_keys'
# Produção (banco gerenciado): o mesmo comando com psql na connection string do Environment.
```

## Chave do anel sem a cifra da chave mestra
Fora de Development a Api não sobe se alguma linha de `security.data_protection_keys` não estiver cifrada pela chave
mestra (`... chave sem a cifra da chave mestra`): alguém gravou o anel por outro meio. Investigue a origem (incidente),
apague só essas linhas e faça o deploy de novo:
```bash
docker compose -p vitrine exec postgres psql -U vitrine -d vitrine \
  -c "delete from security.data_protection_keys where xml not like '%<masterKeyEncrypted%'"
```

## Restauração de backup
O backup do banco leva o anel de chaves cifrado; restaurar exige a chave mestra que o cifrava na data do backup.
Guarde no cofre a chave anterior até que exista backup feito depois da rotação (passo 6).
