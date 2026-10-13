# src/Modules

Módulos de negócio do monólito modular ([ADR-0001](../../docs/adr/ADR-0001-arquitetura-e-fronteiras.md)).
Pasta vazia na fundação: cada módulo nasce na spec que o introduz (o primeiro é `Identity`, na SPEC-0006).

## Estrutura de um módulo

```text
src/Modules/<Modulo>/
  Vitrine.<Modulo>.Domain/              entidades, value objects, regras de negócio puras
  Vitrine.<Modulo>.Application/         casos de uso (comandos/consultas e handlers)
  Vitrine.<Modulo>.Infrastructure/      DbContext próprio (schema <modulo>), gateways externos
  Vitrine.<Modulo>.Presentation/        endpoints HTTP do módulo
  Vitrine.<Modulo>.IntegrationEvents/   contrato público: eventos de integração
  Vitrine.<Modulo>.PublicApi/           (opcional) consultas síncronas, só leitura
```

## Regras (verificadas por testes de arquitetura — SPEC-0003)

- `Domain` não referencia `Application`, `Infrastructure` nem `Presentation`.
- `Application` não referencia `Infrastructure` nem `Presentation`.
- Um módulo só depende do contrato público de outro (`IntegrationEvents` / `PublicApi`), nunca de projetos internos.
- Sem ciclos de dependência entre módulos.
- Cada módulo tem seu schema no PostgreSQL e não lê nem escreve tabelas de outro schema.
- Comunicação entre módulos: eventos de integração via outbox (ADR-0014) ou `PublicApi` (síncrona, só leitura).
- O host `src/Api` compõe os módulos; mudar uma fronteira exige um ADR novo.
