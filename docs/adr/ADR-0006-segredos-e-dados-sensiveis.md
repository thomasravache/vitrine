---
id: ADR-0006
title: Segredos e dados sensíveis
status: accepted
origin: decision
date: 2026-10-13
pillars: [segredos-dados]
baseline: segredos-dados@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: gitleaks no CI (security_scan) + teste de mascaramento de logs + teste de criptografia de tokens
---

# ADR-0006 — Segredos e dados sensíveis

## Contexto e Problema
Credencial em código ou log e dado pessoal sem controle são as falhas de segurança e de conformidade mais comuns. O Vitrine guarda **tokens OAuth do Mercado Livre** que permitem alterar anúncios e responder perguntas em nome do vendedor — vazar um deles é dano direto ao cliente. Também trata dados pessoais sob a LGPD: dados de cadastro do vendedor, e texto/apelido de compradores nas perguntas. Os servidores ficam fora do Brasil (ADR-0004), o que caracteriza transferência internacional.

## Direcionadores da Decisão
- VPS + GitHub Actions (sem gerenciador de segredos de nuvem gerenciado).
- LGPD: base legal, finalidade, retenção e transferência internacional documentadas.
- Tokens do ML com o menor tempo de exposição possível.

## Opções Consideradas
- GitHub Environment secrets → `.env` (permissão 600) gravado no servidor no deploy; tokens do ML cifrados na aplicação (AES-256-GCM, chave mestra vinda do ambiente, via ASP.NET Core Data Protection com chaves persistidas no banco e protegidas por certificado/chave do ambiente)
- Infisical/Doppler como gerenciador de segredos externo
- SOPS + age com segredos cifrados no repositório

## Resultado da Decisão
**Opção escolhida:** "GitHub Environment secrets + criptografia de tokens na aplicação", porque não acrescenta serviço novo, separa staging/produção por Environment e mantém tokens cifrados mesmo com acesso ao banco.

**Regras (verificáveis):**
- Nenhum segredo no repositório (que é **público**); varredura de segredos (gitleaks) no CI bloqueia o merge e o *push protection* de secret scanning do GitHub fica ligado.
- Segredos ficam em GitHub Environment secrets (`staging`, `production`), separados por ambiente, com acesso mínimo; no servidor só no `.env` do Compose (dono root, permissão 600).
- Tokens de acesso e refresh do Mercado Livre e chaves de API (Anthropic, pagamento) são cifrados em repouso na aplicação; nunca são devolvidos pela API nem enviados à SPA.
- Rotação: chave da Anthropic, chaves SSH de deploy e segredo do app ML a cada 6 meses e imediatamente em caso de vazamento; procedimento descrito no runbook.
- Dados pessoais classificados, com base legal, finalidade e prazo de retenção registrados (tabela abaixo).
- Dados pessoais e segredos nunca aparecem em logs, métricas ou traces (mascaramento verificado por teste).
- Criptografia em trânsito (HTTPS/TLS em toda conexão externa e com o banco gerenciado) e em repouso (disco do banco gerenciado + cifra de campo para tokens).
- Texto enviado à IA contém apenas dados do anúncio e da pergunta; nunca tokens nem dados de cadastro do vendedor.

**Inventário de dados pessoais (LGPD):**
| Dado | Titular | Base legal | Finalidade | Retenção |
|---|---|---|---|---|
| Nome, e-mail, senha (hash) | Usuário do Vitrine | Execução de contrato | Conta e acesso | Enquanto ativa + 90 dias após o cancelamento |
| Tokens OAuth e ID do vendedor no ML | Vendedor | Execução de contrato | Operar anúncios/perguntas autorizados | Até desconectar a conta (revogação imediata) |
| Texto e apelido do comprador na pergunta | Comprador | Legítimo interesse do vendedor (controlador); Vitrine é operador | Sugerir/enviar resposta e histórico | 12 meses ou até o cancelamento da conta + 90 dias |
| Dados de cobrança | Cliente pagante | Execução de contrato / obrigação legal | Assinatura | Conforme obrigação fiscal; cartão nunca armazenado (tokenizado no provedor) |

Transferência internacional (servidores nos EUA, Anthropic nos EUA): coberta nos Termos de Uso e na Política de Privacidade, que são pré-requisito do lançamento público (spec própria no épico de produto).

### Consequências
- **Boa**, porque um dump do banco não expõe tokens utilizáveis, e segredos nunca ficam no repositório.
- **Ruim**, porque a perda da chave mestra torna os tokens ilegíveis (os vendedores teriam de reconectar); mitigação: chave guardada também no cofre pessoal do responsável (fora do servidor).

### Confirmação (G3)
Varredura de segredos (gitleaks) em `security_scan`; teste de mascaramento de logs e teste de que tokens persistidos não estão em texto claro no plano de testes da SPEC-0008.

## Prós e Contras das Opções
| Critério (peso) | GitHub secrets + cifra na app | Infisical/Doppler | SOPS + age |
|---|---|---|---|
| Sem serviço novo / custo (4) | 5 | 2 | 5 |
| Separação por ambiente e auditoria (3) | 4 | 5 | 3 |
| Simplicidade para time pequeno (3) | 5 | 4 | 2 |
| **Total ponderado** | **47** | 35 | 35 |

## Mais Informações
Referências: OWASP ASVS 5.0 (V13 Configuration, V11 Cryptography); LGPD (Lei 13.709/2018), arts. 7, 33 (transferência internacional) e 39 (operador). gitleaks: ferramenta de varredura de segredos (versão definida na SPEC-0004).
Versão do gitleaks e detalhes do Data Protection do .NET 10: não verificado nesta sessão — confirmar na SPEC-0004/SPEC-0008.
