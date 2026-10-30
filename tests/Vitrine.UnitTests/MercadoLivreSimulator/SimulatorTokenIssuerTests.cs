using Vitrine.MercadoLivreSimulator;

namespace Vitrine.UnitTests.MercadoLivreSimulator;

public sealed class SimulatorTokenIssuerTests
{
    private const long Seller = 3_000_000_001;
    private const long OtherSeller = 3_000_000_002;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(21_600);

    private static TokenIssuer NewIssuer() => new(new FixedClock(), Lifetime);

    [Fact]
    [Trait("spec", "SPEC-0024:UT-02")]
    public void Emissao_devolve_tokens_distintos_validos_para_o_vendedor_com_a_validade_configurada()
    {
        // Dado o emissor de tokens; quando emite para um vendedor
        var issuer = NewIssuer();
        var tokens = issuer.Issue(Seller);

        // Então access e refresh são distintos, o access identifica o vendedor e expires_in é a validade configurada
        Assert.Equal(Seller, tokens.SellerId);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.NotEqual(tokens.AccessToken, tokens.RefreshToken);
        Assert.Equal(21_600, tokens.ExpiresIn);
        Assert.Equal(Seller, issuer.ValidateAccessToken(tokens.AccessToken));
        Assert.Null(issuer.ValidateAccessToken("APP_USR-desconhecido"));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:UT-02")]
    public void Apos_um_refresh_so_o_refresh_token_novo_vale()
    {
        // Dado tokens emitidos
        var issuer = NewIssuer();
        var first = issuer.Issue(Seller);

        // Quando renova com o refresh token emitido
        var second = issuer.Refresh(first.RefreshToken);

        // Então recebe access e refresh novos do mesmo vendedor
        Assert.NotNull(second);
        Assert.Equal(Seller, second.SellerId);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.Equal(Seller, issuer.ValidateAccessToken(second.AccessToken));

        // E o refresh anterior deixa de valer (uso único), enquanto o novo vale
        Assert.Null(issuer.Refresh(first.RefreshToken));
        var third = issuer.Refresh(second.RefreshToken);
        Assert.NotNull(third);
        Assert.Null(issuer.Refresh(second.RefreshToken));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:UT-02")]
    public void Refresh_token_desconhecido_e_recusado()
    {
        var issuer = NewIssuer();
        issuer.Issue(Seller);

        Assert.Null(issuer.Refresh("TG-desconhecido"));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:UT-02")]
    public void Revoke_invalida_access_e_refresh_so_do_vendedor()
    {
        // Dado tokens de dois vendedores
        var issuer = NewIssuer();
        var tokens = issuer.Issue(Seller);
        var other = issuer.Issue(OtherSeller);

        // Quando revoga o primeiro
        issuer.Revoke(Seller);

        // Então access e refresh dele deixam de valer
        Assert.Null(issuer.ValidateAccessToken(tokens.AccessToken));
        Assert.Null(issuer.Refresh(tokens.RefreshToken));

        // E os do outro vendedor continuam valendo
        Assert.Equal(OtherSeller, issuer.ValidateAccessToken(other.AccessToken));
        Assert.NotNull(issuer.Refresh(other.RefreshToken));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:UT-02")]
    public void Expire_access_token_invalida_so_o_access()
    {
        // Dado tokens emitidos
        var issuer = NewIssuer();
        var tokens = issuer.Issue(Seller);

        // Quando o access token é expirado à força
        issuer.ExpireAccessToken(Seller);

        // Então o access é recusado
        Assert.Null(issuer.ValidateAccessToken(tokens.AccessToken));

        // E o refresh continua valendo, devolvendo um access novo válido
        var renewed = issuer.Refresh(tokens.RefreshToken);
        Assert.NotNull(renewed);
        Assert.Equal(Seller, issuer.ValidateAccessToken(renewed.AccessToken));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:UT-02")]
    public void Access_token_vence_depois_da_validade()
    {
        // Dado tokens emitidos com validade de 6 h
        var clock = new FixedClock();
        var issuer = new TokenIssuer(clock, Lifetime);
        var tokens = issuer.Issue(Seller);

        // Quando o relógio passa da validade
        clock.Advance(Lifetime + TimeSpan.FromSeconds(1));

        // Então o access é recusado
        Assert.Null(issuer.ValidateAccessToken(tokens.AccessToken));
    }

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
