# Changelog

Todas as mudanças relevantes deste projeto são documentadas aqui.

O formato segue [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/)
e o projeto adota [Versionamento Semântico](https://semver.org/lang/pt-BR/).
Cada entrada cita a spec de origem (`SPEC-NNNN`); gere-as com `spec_graph.py report SPEC-NNNN --changelog`.

## [Unreleased]
### Added
- Repositório, ferramentas e convenções: solução .NET 10 com Aspire, SPA React/Vite, regras de build e lint, hooks de commit e proteção da `main` (SPEC-0002)
- Harness de testes: unitário, integração com PostgreSQL real, arquitetura (ArchUnitNET), E2E Playwright com axe e cobertura de linhas alteradas (SPEC-0003)
- Pipeline de CI com 7 checks obrigatórios, varreduras de segurança e publicação da imagem no GHCR (SPEC-0004)
- Infraestrutura como código (não provisionada) e deploy por script com migração, health check e rollback, testado em ambiente local prod-like (SPEC-0005)
- Cadastro, login, logout e painel vazio no celular e no desktop, com cadastro controlado por flag por ambiente e evento `WorkspaceCreated` na outbox; E2E de fumaça contra o ambiente implantado (SPEC-0006)
- Observabilidade de base: `/health/ready`, logs JSON com `trace_id` e dados pessoais mascarados, métricas de acesso (`vitrine.auth.*`), spans de banco, Sentry no backend e na SPA (só com DSN), Grafana Alloy opcional, alertas como código e runbooks (SPEC-0007)
- Segurança de base: autorização negada por padrão guardada por teste de arquitetura, cabeçalhos de segurança (CSP, HSTS), limite de tentativas no cadastro e login (429), cifra de campos sensíveis (AES-256-GCM) com chaves no PostgreSQL protegidas pela chave mestra e runbook de rotação (SPEC-0008)
- O CI reprova o PR quando o cliente da API da SPA (`web/src/api`) está desatualizado em relação ao OpenAPI (SPEC-0011)
- Conectar e desconectar contas do Mercado Livre pelo OAuth oficial com PKCE, com tokens cifrados e eventos de conta conectada/desconectada (SPEC-0020)
- Gateway interno da API do Mercado Livre: renovação automática do token sem repetir o refresh, reconexão sinalizada por evento, busca de anúncios com `scan` e leitura em lote pelo `/items/bulk` (SPEC-0021)
- Importação e sincronização dos anúncios do Mercado Livre: importação ao conectar, atualização por notificação, reconciliação diária e sincronização manual; listagem com busca, filtros e paginação e resumo por status (SPEC-0022)
- Webhook de notificações do Mercado Livre: anúncios alterados no ML são atualizados no Vitrine em instantes, com deduplicação, limite por IP e retenção do registro (SPEC-0023)

### Fixed
- Testes de arquitetura dão o mesmo resultado localmente e no CI quando um projeto não é referenciado (SPEC-0027)
- Sincronização e atualização de anúncios rodam com o gateway real do Mercado Livre (geração de código do Wolverine) (SPEC-0028)
- Notificação forjada não grava mais anúncio de outro vendedor no catálogo da conta (SPEC-0029)
- Cifra de campos (`UseFieldProtection`) não cria mais um provedor interno do EF Core por protetor; o módulo do Mercado Livre volta ao caminho padrão (SPEC-0026)
- O Sentry não recebe segredos de `Exception.Data` guardados em objetos, contexts, extras, JSON, coleções ou URLs com credenciais, e o log de erro nunca falha por causa do mascaramento (SPEC-0018)
- `tests/infra/run.sh` derruba só a stack local que ele mesmo subiu (`--keep` preserva) (SPEC-0012)
- Valores de `Exception.Data` enviados ao Sentry passam pelo mascaramento de dados sensíveis (SPEC-0015)
- Checagem do anel de chaves recusa chave em claro disfarçada (segredo fora do descritor ou com outro decifrador) (SPEC-0017)
- Testes de integração liberam as conexões do PostgreSQL ao descartar cada host (pico de 97 para 9 conexões) (SPEC-0016)
- `npm run api:generate` voltou a funcionar sem PostgreSQL e sem chave mestra (SPEC-0011)
- Exceção enviada ao Sentry repetia em claro o valor de uma propriedade sensível do mesmo log (SPEC-0013)
- Login sem e-mail válido responde 400 antes do Identity; revogações no anel de chaves não impedem mais a subida da API (SPEC-0014)
- Dependências com avisos de segurança novos quebravam o build: Aspire 13.6.4 (MessagePack 2.5.307), undici 7.30.1 e happy-dom 21.0.4, com teste que barra o retorno dessas versões (SPEC-0010)
