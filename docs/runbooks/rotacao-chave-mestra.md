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

Sem a chave (ou com valor que não seja Base64 de 32 bytes), a Api não sobe fora de Development: o container sai com
`DataProtection:MasterKey ... não configurada` / `... inválida` e o `deploy.sh` volta para a versão anterior. O Compose
também recusa subir sem `DATA_PROTECTION_MASTER_KEY`.

## Rotação (sem derrubar sessões nem perder campos cifrados)
Quando: suspeita de vazamento, saída de alguém com acesso ao cofre ou ao Environment, ou rotina anual.

1. Gere a nova chave (acima) e guarde-a no cofre.
2. No `DEPLOY_ENV_FILE` do Environment:
   - `DATA_PROTECTION_PREVIOUS_MASTER_KEY=<chave atual>`
   - `DATA_PROTECTION_MASTER_KEY=<chave nova>`
3. Rode o deploy da versão atual (GitHub: *Run workflow* em `Deploy` com a mesma `tag`; ou no servidor
   `deploy/deploy.sh <tag atual>` depois de atualizar o `deploy/.env`). O passo `migrate` recifra com a chave nova
   todas as chaves do anel que ainda estão sob a anterior e registra no log:
   `Rotação da chave mestra: N chave(s) do Data Protection recifrada(s) com a chave atual; M já estava(m) nela.`
   Durante a troca, a Api aceita as duas chaves para leitura e cifra o que for novo só com a nova.
4. Confira:
   ```bash
   docker compose -p vitrine logs migrate | grep 'Rotação da chave mestra'
   curl -fsS https://<SITE_ADDRESS>/health/ready
   ```
   Uma sessão aberta antes da rotação continua válida (abra `/painel` num navegador já logado).
5. Remova `DATA_PROTECTION_PREVIOUS_MASTER_KEY` do `DEPLOY_ENV_FILE` (deixe vazia) e faça um novo deploy. Confira de
   novo que a sessão aberta antes da rotação continua válida: se continuar, nenhuma chave dependia mais da anterior.
6. Apague a chave anterior do cofre só depois do passo 5 (e de um backup do banco feito já com a chave nova).

## Se a chave mestra foi perdida
Não há recuperação: as chaves do anel ficam ilegíveis. Gere uma chave nova e faça o deploy; o Data Protection cria um
anel novo (todos os usuários precisam entrar de novo; os campos cifrados com `IFieldProtector` precisam ser obtidos
de novo — ex.: reconectar as contas do Mercado Livre). Registre o incidente.

## Restauração de backup
O backup do banco leva o anel de chaves cifrado; restaurar exige a chave mestra que o cifrava na data do backup.
Guarde no cofre a chave anterior até que exista backup feito depois da rotação (passo 6).
