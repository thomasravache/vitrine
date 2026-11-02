using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Editor diante do ML recusando ou falhando (SPEC-0033, ADR-0010): erros traduzidos para os códigos do contrato, 409
/// com uma nova tentativa depois de 2 s, nenhuma repetição de escrita pelo pipeline, 401 com renovação forçada e nada de
/// token em log ou exceção.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ListingEditorFailureTests(PostgresFixture postgres)
{
    /// <summary>Tentativas de uma leitura (SPEC-0021, emenda v1.1).</summary>
    private const int ReadAttempts = 3;

    [Fact]
    [Trait("spec", "SPEC-0033:IT-05")]
    public async Task Titulo_nao_modificavel_e_recusado_e_um_segundo_PUT_leva_so_os_atributos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado o ML respondendo ao PUT com título e atributos 400 de título não modificável e depois 200
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.ItemUpdateResponds(itemId, MlReply.FromErrorFixture("error-title-not-modifiable.json"), new MlReply(200, "{}"));

        // Quando ApplyAsync recebe título e atributos
        var changes = ListingFixtures.ChangesFromRequestFixtures() with { Description = null };
        var result = await ListingGateway.ApplyAsync(factory, account.AccountId, itemId, changes, cancellationToken);

        // Então o título fica Rejected(FIELD_NOT_UPDATABLE)
        Assert.Equal("FIELD_NOT_UPDATABLE", Assert.IsType<FieldResult.Rejected>(result.Title).Code);

        // E houve um 2º PUT só com os atributos, que voltam Applied
        var puts = fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId));
        Assert.Equal(2, puts.Count);
        ListingEditorTests.AssertJsonEqual(ListingFixtures.ItemUpdateRequest(), MercadoLivreListingFake.BodyOf(puts[0]));
        var attributesOnly = ListingFixtures.ItemUpdateRequest().AsObject();
        attributesOnly.Remove("title");
        ListingEditorTests.AssertJsonEqual(attributesOnly, MercadoLivreListingFake.BodyOf(puts[1]));
        Assert.Equal(["COLOR", "MODEL"], result.Attributes.Keys.Order(StringComparer.Ordinal));
        Assert.All(result.Attributes.Values, field => Assert.IsType<FieldResult.Applied>(field));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-05")]
    public async Task Field_not_updatable_sem_title_recusa_o_PUT_inteiro_sem_segundo_PUT()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado o ML respondendo ao PUT com título e atributos 400 field_not_updatable de um atributo (sem citar title)
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.ItemUpdateResponds(itemId, MlReply.FromErrorFixture("error-field-not-updatable-attribute.json"), new MlReply(200, "{}"));

        // Quando ApplyAsync recebe título e atributos
        var changes = ListingFixtures.ChangesFromRequestFixtures() with { Description = null };
        var result = await ListingGateway.ApplyAsync(factory, account.AccountId, itemId, changes, cancellationToken);

        // Então título e atributos ficam Rejected(ML_REJECTED), com um único PUT (emenda v1.1)
        Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId)));
        Assert.Equal("ML_REJECTED", Assert.IsType<FieldResult.Rejected>(result.Title).Code);
        Assert.Equal(["COLOR", "MODEL"], result.Attributes.Keys.Order(StringComparer.Ordinal));
        Assert.All(result.Attributes.Values, field => Assert.Equal("ML_REJECTED", Assert.IsType<FieldResult.Rejected>(field).Code));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-05")]
    public async Task Descricao_recusada_pelo_ML_fica_Rejected_DESCRIPTION_INVALID()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado o ML respondendo ao PUT da descrição 400 item.description.type.invalid
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.WritesSucceed(itemId);
        fake.DescriptionUpdateResponds(itemId, MlReply.FromErrorFixture("error-description-invalid.json"));

        // Quando ApplyAsync recebe uma descrição válida pelas regras locais
        var result = await ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, new ListingChanges(null, ListingFixtures.ChangesFromRequestFixtures().Description, null), cancellationToken);

        // Então a descrição fica Rejected(DESCRIPTION_INVALID), com 1 só PUT da descrição
        Assert.Equal("DESCRIPTION_INVALID", Assert.IsType<FieldResult.Rejected>(result.Description).Code);
        Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.DescriptionPath(itemId)));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-05")]
    public async Task Escrita_com_403_fica_Rejected_ML_FORBIDDEN_sem_repeticao()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado o ML respondendo 403 ao PUT do item (ex.: token sem o escopo write)
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.ItemUpdateResponds(itemId, MlReply.Error(403, "forbidden"));

        // Quando ApplyAsync recebe um título
        var result = await ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, new ListingChanges(ListingFixtures.ChangesFromRequestFixtures().Title, null, null), cancellationToken);

        // Então o título fica Rejected(ML_FORBIDDEN), com 1 só PUT
        Assert.Equal("ML_FORBIDDEN", Assert.IsType<FieldResult.Rejected>(result.Title).Code);
        Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId)));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-06")]
    public async Task Conflito_409_uma_vez_reenvia_depois_de_2_s_e_aplica()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o relógio de teste do host e o PUT respondendo 409 e depois 200
        var clock = new RecordingTimeProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithClock(clock);
        var fake = new MercadoLivreListingFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.ItemUpdateResponds(itemId, MlReply.FromErrorFixture("error-conflict.json"), new MlReply(200, "{}"));

        // Quando ApplyAsync recebe um título
        var result = await ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, new ListingChanges(ListingFixtures.ChangesFromRequestFixtures().Title, null, null), cancellationToken);

        // Então há exatamente 2 PUTs, com intervalo de ≥ 2 s pedido ao relógio de teste, e o título fica Applied
        var puts = fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId));
        Assert.Equal(2, puts.Count);
        AssertWaitedTwoSeconds(clock, puts[0], puts[1]);
        Assert.IsType<FieldResult.Applied>(result.Title);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-06")]
    public async Task Conflito_409_duas_vezes_fica_Rejected_ML_CONFLICT_com_2_PUTs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o PUT respondendo 409 sempre
        var clock = new RecordingTimeProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithClock(clock);
        var fake = new MercadoLivreListingFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.ItemUpdateResponds(itemId, MlReply.FromErrorFixture("error-conflict.json"));

        // Quando ApplyAsync recebe um título
        var result = await ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, new ListingChanges(ListingFixtures.ChangesFromRequestFixtures().Title, null, null), cancellationToken);

        // Então há exatamente 2 PUTs (uma única nova tentativa) e o título fica Rejected(ML_CONFLICT)
        var puts = fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId));
        Assert.Equal(2, puts.Count);
        AssertWaitedTwoSeconds(clock, puts[0], puts[1]);
        Assert.Equal("ML_CONFLICT", Assert.IsType<FieldResult.Rejected>(result.Title).Code);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-07")]
    public async Task PUT_com_503_nao_e_repetido_e_interrompe_a_descricao()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logs = new RecordingLoggerProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithFastResilience()
            .WithLogs(logs);
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado o PUT do item respondendo 503 sempre
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.WritesSucceed(itemId);
        fake.ItemUpdateResponds(itemId, MlReply.Error(503, "service_unavailable"));

        // Quando ApplyAsync recebe título, descrição e atributos
        var result = await ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, ListingFixtures.ChangesFromRequestFixtures(), cancellationToken);

        // Então houve exatamente 1 PUT (sem retry do pipeline numa escrita)
        Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId)));

        // E título e atributos ficam Rejected(ML_UNAVAILABLE); a descrição, NotAttempted(ML_UNAVAILABLE), sem chamada
        Assert.Equal("ML_UNAVAILABLE", Assert.IsType<FieldResult.Rejected>(result.Title).Code);
        Assert.Equal(["COLOR", "MODEL"], result.Attributes.Keys.Order(StringComparer.Ordinal));
        Assert.All(result.Attributes.Values, field => Assert.Equal("ML_UNAVAILABLE", Assert.IsType<FieldResult.Rejected>(field).Code));
        Assert.Equal(new FieldResult.NotAttempted("ML_UNAVAILABLE"), result.Description);
        Assert.Empty(fake.CallsTo("PUT", MercadoLivreListingFake.DescriptionPath(itemId)));
        Assert.Empty(fake.CallsTo("POST", MercadoLivreListingFake.DescriptionPath(itemId)));

        AssertNoSecrets(logs, [], account.Tokens);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-07")]
    public async Task Leitura_do_item_com_503_persistente_lanca_Unavailable_sem_nenhuma_escrita()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logs = new RecordingLoggerProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithFastResilience()
            .WithLogs(logs);
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado o GET /items/{id} respondendo 503 sempre
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = MercadoLivreListingFake.NewItemId();
        fake.ItemFails(itemId, 503);
        fake.DescriptionResponds(itemId, ListingFixtures.Description());
        fake.CategoryResponds(ListingFixtures.Category(), ListingFixtures.CategoryAttributes());
        fake.WritesSucceed(itemId);

        // Quando ApplyAsync é chamado; então lança Unavailable depois das 3 tentativas de leitura
        var thrown = await Assert.ThrowsAsync<MercadoLivreUnavailableException>(() => ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, ListingFixtures.ChangesFromRequestFixtures(), cancellationToken));
        Assert.Equal(account.AccountId, thrown.AccountId);
        Assert.Equal(ReadAttempts, fake.CallsTo("GET", MercadoLivreListingFake.ItemPath(itemId)).Count);

        // E nada foi escrito
        Assert.Empty(fake.Writes(itemId));

        AssertNoSecrets(logs, [thrown], account.Tokens);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-07")]
    public async Task PUT_com_401_uma_vez_renova_o_token_e_reenvia_uma_vez()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logs = new RecordingLoggerProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithLogs(logs);
        var fake = new MercadoLivreListingFake(factory.ExternalServices);
        var oauth = new MercadoLivreFake(factory.ExternalServices);

        // Dado o PUT do item aceitando só o token renovado (o atual recebe 401) e o refresh emitindo esse token
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var renewed = MlTokens.New();
        oauth.TokenResponds(renewed, account.Seller.Id);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.ItemUpdateResponds(itemId, new MlReply(200, "{}", RequiredToken: renewed.AccessToken));

        // Quando ApplyAsync recebe um título
        var result = await ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, new ListingChanges(ListingFixtures.ChangesFromRequestFixtures().Title, null, null), cancellationToken);

        // Então houve 1 renovação forçada e 1 reenvio, com o token novo, que concluiu
        Assert.Single(oauth.TokenCalls);
        var puts = fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId));
        Assert.Equal(2, puts.Count);
        Assert.Equal($"Bearer {account.Tokens.AccessToken}", MercadoLivreListingFake.AuthorizationOf(puts[0]));
        Assert.Equal($"Bearer {renewed.AccessToken}", MercadoLivreListingFake.AuthorizationOf(puts[1]));
        Assert.IsType<FieldResult.Applied>(result.Title);

        AssertNoSecrets(logs, [], account.Tokens, renewed);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-07")]
    public async Task Descricao_com_401_duplo_propaga_ReauthorizationRequired_depois_de_registrar_o_titulo_aplicado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logs = new RecordingLoggerProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithLogs(logs);
        var fake = new MercadoLivreListingFake(factory.ExternalServices);
        var oauth = new MercadoLivreFake(factory.ExternalServices);
        using var metrics = new ListingEditsListener(factory.Services.GetRequiredService<IMeterFactory>());

        // Dado o PUT do item aceito e o PUT da descrição respondendo 401 mesmo depois da renovação forçada
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var renewed = MlTokens.New();
        oauth.TokenResponds(renewed, account.Seller.Id);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.ItemUpdateResponds(itemId);
        fake.DescriptionUpdateResponds(itemId, new MlReply(200, "{}", RequiredToken: "APP_USR-token-nunca-emitido"));

        // Quando ApplyAsync recebe título e descrição; então ReauthorizationRequired propaga
        var changes = ListingFixtures.ChangesFromRequestFixtures() with { Attributes = null };
        var thrown = await Assert.ThrowsAsync<MercadoLivreReauthorizationRequiredException>(
            () => ListingGateway.ApplyAsync(factory, account.AccountId, itemId, changes, cancellationToken));
        Assert.Equal(account.AccountId, thrown.AccountId);
        Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId)));
        Assert.Equal(2, fake.CallsTo("PUT", MercadoLivreListingFake.DescriptionPath(itemId)).Count);

        // E o título, já aplicado antes da exceção, foi registrado em listing_edits (emenda v1.1)
        Assert.True(metrics.Total("title", "applied") == 1, $"listing_edits{{title, applied}} esperado 1 antes da exceção. {metrics.Describe()}");

        AssertNoSecrets(logs, [thrown], account.Tokens, renewed);
    }

    /// <summary>A 2ª escrita chegou ≥ 2 s depois da 1ª, e a espera de 2 s foi pedida ao <see cref="TimeProvider"/> do host.</summary>
    private static void AssertWaitedTwoSeconds(RecordingTimeProvider clock, WireMock.Logging.ILogEntry first, WireMock.Logging.ILogEntry second)
    {
        Assert.True(
            clock.Requested.Contains(TimeSpan.FromSeconds(2)),
            $"A espera de 2 s depois do 409 não foi pedida ao relógio de teste: [{string.Join(", ", clock.Requested)}].");
        var interval = second.RequestMessage!.DateTime - first.RequestMessage!.DateTime;
        Assert.True(interval >= TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(20), $"O reenvio depois do 409 veio cedo demais ({interval}).");
    }

    /// <summary>Nenhum access token ou refresh token nos logs capturados nem nas mensagens das exceções.</summary>
    private static void AssertNoSecrets(RecordingLoggerProvider logs, IReadOnlyList<Exception> exceptions, params MlTokens[] tokens)
    {
        var secrets = tokens.SelectMany(token => new[] { token.AccessToken, token.RefreshToken }).ToList();
        foreach (var text in logs.Records.Select(record => record.ToString()).Concat(exceptions.Select(exception => exception.ToString())))
        {
            foreach (var secret in secrets)
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"Token apareceu em log ou exceção:\n{text}");
            }
        }
    }
}
