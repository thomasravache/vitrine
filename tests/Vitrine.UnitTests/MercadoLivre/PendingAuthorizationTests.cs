using Vitrine.MercadoLivre.Domain.Authorizations;

namespace Vitrine.UnitTests.MercadoLivre;

public sealed class PendingAuthorizationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    [Fact]
    [Trait("spec", "SPEC-0020:UT-03")]
    public void Create_amarra_ao_usuario_e_workspace_e_expira_em_10_minutos()
    {
        // Dado o relógio falso; quando a autorização pendente é criada
        var clock = new TestClock(Start);
        var authorization = PendingAuthorization.Create(_workspaceId, _userId, "hash-do-state", "verifier-cifrado", clock.GetUtcNow());

        // Então guarda workspace, usuário, hash do state e verifier, expira em 10 minutos e ainda não foi usada
        Assert.NotEqual(Guid.Empty, authorization.Id);
        Assert.Equal(_workspaceId, authorization.WorkspaceId);
        Assert.Equal(_userId, authorization.UserId);
        Assert.Equal("hash-do-state", authorization.StateHash);
        Assert.Equal("verifier-cifrado", authorization.CodeVerifier);
        Assert.Equal(TimeSpan.FromMinutes(10), PendingAuthorization.Lifetime);
        Assert.Equal(Start.AddMinutes(10), authorization.ExpiresAt);
        Assert.Null(authorization.UsedAt);
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-03")]
    public void Primeira_consumacao_dentro_de_10_minutos_pelo_mesmo_usuario_e_aceita_e_marca_o_uso()
    {
        // Dado uma autorização pendente criada agora
        var clock = new TestClock(Start);
        var authorization = PendingAuthorization.Create(_workspaceId, _userId, "hash-do-state", "verifier-cifrado", clock.GetUtcNow());

        // Quando o mesmo usuário consome pouco antes de vencer
        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        var result = authorization.Consume(_userId, clock.GetUtcNow());

        // Então é aceita e fica marcada como usada no instante da consumação
        Assert.Equal(PendingAuthorizationConsumption.Accepted, result);
        Assert.Equal(clock.GetUtcNow(), authorization.UsedAt);
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-03")]
    public void Segunda_consumacao_e_recusada_como_ja_usada()
    {
        // Dado uma autorização já consumida
        var clock = new TestClock(Start);
        var authorization = PendingAuthorization.Create(_workspaceId, _userId, "hash-do-state", "verifier-cifrado", clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(PendingAuthorizationConsumption.Accepted, authorization.Consume(_userId, clock.GetUtcNow()));
        var usedAt = authorization.UsedAt;

        // Quando consumida de novo, ainda dentro da validade
        clock.Advance(TimeSpan.FromSeconds(30));
        var result = authorization.Consume(_userId, clock.GetUtcNow());

        // Então é recusada (um state nunca serve duas vezes) e o uso original não muda
        Assert.Equal(PendingAuthorizationConsumption.AlreadyUsed, result);
        Assert.Equal(usedAt, authorization.UsedAt);
    }

    [Theory]
    [Trait("spec", "SPEC-0020:UT-03")]
    [InlineData(10 * 60)]
    [InlineData((10 * 60) + 1)]
    [InlineData(24 * 60 * 60)]
    public void Consumacao_a_partir_de_10_minutos_e_recusada_como_expirada(int elapsedSeconds)
    {
        // Dado uma autorização pendente; quando o relógio falso avança até ou além da validade
        var clock = new TestClock(Start);
        var authorization = PendingAuthorization.Create(_workspaceId, _userId, "hash-do-state", "verifier-cifrado", clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));

        // Então a consumação pelo mesmo usuário é recusada como expirada
        Assert.Equal(PendingAuthorizationConsumption.Expired, authorization.Consume(_userId, clock.GetUtcNow()));
        Assert.Null(authorization.UsedAt);
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-03")]
    public void Consumacao_por_outro_usuario_e_recusada()
    {
        // Dado uma autorização pendente iniciada por um usuário
        var clock = new TestClock(Start);
        var authorization = PendingAuthorization.Create(_workspaceId, _userId, "hash-do-state", "verifier-cifrado", clock.GetUtcNow());

        // Quando outro usuário tenta consumir dentro da validade
        clock.Advance(TimeSpan.FromMinutes(1));
        var result = authorization.Consume(Guid.NewGuid(), clock.GetUtcNow());

        // Então é recusada
        Assert.Equal(PendingAuthorizationConsumption.OtherUser, result);
    }
}
