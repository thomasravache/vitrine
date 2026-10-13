# Testes

Harness de testes do Vitrine ([SPEC-0003](../docs/specs/SPEC-0003-harness-de-testes-e-arquitetura.md)),
com as ferramentas por nível da [ADR-0002](../docs/adr/ADR-0002-estrategia-de-testes.md). Toda spec escreve
seus testes sobre esta base. Comandos rodam na raiz do repositório; os projetos .NET usam xUnit v3 no
Microsoft Testing Platform (`global.json`).

## Níveis

| Nível | Onde | Ferramentas | Precisa de |
|---|---|---|---|
| Unitário (C#) | `tests/Vitrine.UnitTests` | xUnit v3 | — |
| Unitário (SPA) | `web/src/**/*.test.tsx` | Vitest + happy-dom + Testing Library | Node 24 |
| Integração | `tests/Vitrine.IntegrationTests` | xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory`, WireMock.Net | Docker |
| Arquitetura | `tests/Vitrine.ArchitectureTests` | ArchUnitNET (`TngTech.ArchUnitNET.xUnitV3`) | — |
| E2E | `web/e2e/**/*.spec.ts` | Playwright (desktop + celular) + @axe-core/playwright | Docker e navegadores do Playwright |
| Ferramentas do repositório | `tests/Vitrine.Tooling.Tests` | xUnit v3, Aspire.Hosting.Testing | — (SPEC-0002) |

## Comandos

| O quê | Comando |
|---|---|
| Unitários (C# + SPA) | `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` |
| Integração | `dotnet test tests/Vitrine.IntegrationTests` |
| Arquitetura | `dotnet test tests/Vitrine.ArchitectureTests` |
| E2E (sobe a aplicação pelo AppHost) | `npm --prefix web run e2e` |
| E2E contra um ambiente já no ar | `BASE_URL=https://staging.exemplo npm --prefix web run e2e` |
| Suíte .NET completa | `dotnet test --solution Vitrine.slnx` |

Navegadores do Playwright (uma vez por máquina): `npx --prefix web playwright install --with-deps chromium`.

Filtrar por spec ou por teste (opções do xUnit v3 depois de `--`):

```bash
dotnet test tests/Vitrine.UnitTests -- --filter-trait "spec=SPEC-0003:UT-01"
npm --prefix web run e2e -- --grep "SPEC-0003:E2E-01"
```

## Convenção de tag

Todo teste do plano de uma spec carrega o ID dela, para o rastreio spec → teste → evidência:

- **C#:** `[Trait("spec", "SPEC-NNNN:<ID>")]` no método (ex.: `[Trait("spec", "SPEC-0003:UT-01")]`).
- **TypeScript (Vitest e Playwright):** o título do teste começa com o ID —
  `test("SPEC-NNNN:E2E-01 — <descrição>", ...)` / `it("SPEC-NNNN:UT-02 — <descrição>", ...)`.

Nomes de teste descrevem o comportamento (Dado/Quando/Então nos comentários do corpo).

## Fixtures

### `PostgresFixture` (integração)

Um PostgreSQL real (Testcontainers, mesma imagem do AppHost) por coleção de testes. Cada teste pede um banco
próprio e vazio com `CreateDatabaseAsync()`, que devolve a connection string; nada é compartilhado entre
testes e tudo some com o container no fim da coleção.

```csharp
[Collection(PostgresTestGroup.Name)]
public sealed class MeusTestes(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-NNNN:IT-01")]
    public async Task Exemplo()
    {
        var connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        // ...
    }
}
```

### `ApiFactory` (integração)

`WebApplicationFactory<Program>` da `src/Api` apontando para o banco do teste (`ConnectionStrings:vitrine`),
com Wolverine persistindo nele (ADR-0014). `CreateClient()` devolve um `HttpClient` que mantém cookies.

- `WithServices(services => ...)` acrescenta ou troca registros de serviço do host de teste.
- `RedirectToWireMock("<cliente nomeado>")` faz o `HttpClient` nomeado ser atendido por `ExternalServices`
  (um `WireMockServer`): toda requisição dele é reescrita para o WireMock, então nada sai para a internet.
  Cenários do servidor falso seguem a documentação oficial do serviço (Mercado Livre, Anthropic).

Configure antes do primeiro uso de `Services` ou `CreateClient()`.

### Regras de arquitetura (`ArchitectureRules`, `SolutionArchitecture`)

`ArchitectureRules` codifica as fronteiras da ADR-0001 por convenção de namespace `…<Modulo>.<Camada>[.…]`
(camadas `Domain`, `Application`, `Infrastructure`, `Presentation`, `IntegrationEvents`, `PublicApi`):
camadas dentro do módulo, isolamento entre módulos (só o contrato público), ausência de ciclos entre módulos e
`Common` sem dependência de módulos. Valem para todo módulo novo sem cadastro.
`SolutionArchitecture.Load()` carrega o host `Vitrine.Api` e todo assembly `Vitrine.*` que ele referencia —
um módulo entra nas regras quando o host passa a referenciá-lo. Os assemblies de `Samples/` existem só para
provar que as regras falham (UT-03, UT-04).

### `AppHostFixture` (E2E)

Sem `BASE_URL`, o global-setup do Playwright roda `web/e2e/support/AppHostFixture.cs` (app de arquivo único
do .NET), que sobe a aplicação inteira pelo AppHost com Aspire.Hosting.Testing — o mesmo AppHost do
desenvolvimento, com PostgreSQL, Api e SPA —, espera a SPA responder e informa a URL; o Playwright usa essa URL
como `baseURL` e derruba a aplicação no fim. Com `BASE_URL` definido, nada é subido e os testes rodam contra
o ambiente informado (ex.: smoke em staging).

`expectNoSeriousA11yViolations(page)` (`web/e2e/support/axe.ts`) falha com violações axe sérias ou críticas.

## Dados de teste

Só dados sintéticos, criados pelo próprio teste; nenhum dado real (pessoal ou de cliente) em fixtures.
Testes não dependem de ordem nem do relógio real.
