using System.Net;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>Tokens, code, verifier e client secret nunca em claro no banco, nas respostas nem nos logs (SPEC-0020, ADR-0006).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class TokenProtectionTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0020:IT-07")]
    public async Task Conexao_concluida_nao_deixa_tokens_code_verifier_nem_segredo_em_claro_no_banco_nas_respostas_nem_nos_logs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var logs = new RecordingLoggerProvider();
        await using var factory = MercadoLivreHost.Create(connectionString).WithLogs(logs);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        await api.SignUpAsync(cancellationToken);
        var seller = MlSeller.New();
        var tokens = MlTokens.New();
        var code = MercadoLivreApi.NewCode();
        var bodies = new List<string>();

        // Dado uma conexão concluída com tokens conhecidos do ML simulado (todas as respostas guardadas)
        fake.Accepts(seller, tokens);
        string state;
        using (var start = await api.StartAsync(cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, start, cancellationToken);
            var body = await start.Content.ReadAsStringAsync(cancellationToken);
            bodies.Add(body);
            state = StartedConnection.From(new Uri(System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("authorizationUrl").GetString()!)).State;
        }

        ConnectedAccount account;
        using (var complete = await api.CompleteAsync(code, state, cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.Created, complete, cancellationToken);
            bodies.Add(await complete.Content.ReadAsStringAsync(cancellationToken));
            account = await ConnectedAccount.ReadAsync(complete, cancellationToken);
        }

        var verifier = fake.LastTokenForm().GetValueOrDefault("code_verifier") ?? string.Empty;
        Assert.False(string.IsNullOrEmpty(verifier), "O ML simulado não recebeu code_verifier.");

        // Então access_token/refresh_token e code_verifier não estão em claro no Postgres (em nenhuma coluna)
        var row = Assert.Single(await MercadoLivreDatabase.ReadAccountsAsync(connectionString, cancellationToken));
        Assert.DoesNotContain(tokens.AccessToken, row.AccessToken, StringComparison.Ordinal);
        Assert.DoesNotContain(tokens.RefreshToken, row.RefreshToken, StringComparison.Ordinal);
        var accountsDump = await MercadoLivreDatabase.DumpAsync(connectionString, "accounts", cancellationToken);
        var pendingDump = await MercadoLivreDatabase.DumpAsync(connectionString, "pending_authorizations", cancellationToken);
        Assert.NotEmpty(pendingDump);
        foreach (var secret in new[] { tokens.AccessToken, tokens.RefreshToken, verifier, code, state })
        {
            Assert.DoesNotContain(secret, accountsDump, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, pendingDump, StringComparison.Ordinal);
        }

        // E nenhuma resposta dos quatro endpoints contém os tokens, o verifier ou o client secret
        using (var list = await api.ListAsync(cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, list, cancellationToken);
            bodies.Add(await list.Content.ReadAsStringAsync(cancellationToken));
        }

        using (var disconnect = await api.DisconnectAsync(account.AccountId, cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, disconnect, cancellationToken);
            bodies.Add(await disconnect.Content.ReadAsStringAsync(cancellationToken));
        }

        foreach (var body in bodies)
        {
            foreach (var secret in new[] { tokens.AccessToken, tokens.RefreshToken, verifier, MercadoLivreHost.ClientSecret })
            {
                Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            }
        }

        // E os logs capturados (provedor de log de teste, antes de qualquer máscara) não contêm token, code, verifier
        // nem client secret
        var records = logs.Records;
        Assert.NotEmpty(records);
        foreach (var record in records)
        {
            var text = record.ToString();
            foreach (var (name, secret) in new[]
            {
                ("access token", tokens.AccessToken),
                ("refresh token", tokens.RefreshToken),
                ("code", code),
                ("code_verifier", verifier),
                ("client secret", MercadoLivreHost.ClientSecret),
            })
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{name} apareceu em log:\n{text}");
            }
        }
    }
}
