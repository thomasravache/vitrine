using Microsoft.Extensions.Options;
using Vitrine.MercadoLivre.Infrastructure;
using Vitrine.MercadoLivre.Infrastructure.Api;

namespace Vitrine.UnitTests.MercadoLivre;

/// <summary>Renovação antecipada do access token: só com menos de <c>RefreshSkew</c> restante, ou forçada após 401 (SPEC-0021).</summary>
public sealed class TokenRefreshPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [Trait("spec", "SPEC-0021:UT-01")]
    [InlineData(6 * 60 * 60)] // recém-emitido (6 h)
    [InlineData(5 * 60 + 1)] // um segundo além do skew
    [InlineData(5 * 60)] // exatamente o skew: ainda não é "menos de 5 min"
    public void Com_RefreshSkew_ou_mais_restante_nao_renova(int secondsLeft)
    {
        // Dado um token que expira daqui a secondsLeft e o RefreshSkew padrão (5 min)
        var policy = Policy(new TestClock(Now));

        // Quando o provedor decide; então não renova
        Assert.False(policy.ShouldRefresh(Now.AddSeconds(secondsLeft), forced: false));
    }

    [Theory]
    [Trait("spec", "SPEC-0021:UT-01")]
    [InlineData(5 * 60 - 1)] // um segundo dentro do skew
    [InlineData(60)]
    [InlineData(0)] // expira agora
    [InlineData(-60 * 60)] // já expirado
    public void Com_menos_de_RefreshSkew_restante_renova(int secondsLeft)
    {
        var policy = Policy(new TestClock(Now));

        Assert.True(policy.ShouldRefresh(Now.AddSeconds(secondsLeft), forced: false));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-01")]
    public void Forcada_apos_401_renova_mesmo_com_token_valido()
    {
        // Dado um token válido por mais 6 h, rejeitado pelo ML com 401
        var policy = Policy(new TestClock(Now));

        // Quando a renovação é forçada; então renova
        Assert.True(policy.ShouldRefresh(Now.AddHours(6), forced: true));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-01")]
    public void Decisao_segue_o_TimeProvider_conforme_o_relogio_avanca()
    {
        // Dado um token que expira em 1 h, avaliado por um relógio falso
        var clock = new TestClock(Now);
        var policy = Policy(clock);
        var expiresAt = Now.AddHours(1);
        Assert.False(policy.ShouldRefresh(expiresAt, forced: false));

        // Quando o relógio chega a 5 min do vencimento; então ainda não renova
        clock.Advance(TimeSpan.FromMinutes(55));
        Assert.False(policy.ShouldRefresh(expiresAt, forced: false));

        // E um segundo depois renova
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(policy.ShouldRefresh(expiresAt, forced: false));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-01")]
    public void RefreshSkew_configurado_substitui_o_padrao()
    {
        // Dado MercadoLivre:RefreshSkew = 00:30:00
        var policy = Policy(new TestClock(Now), new MercadoLivreOptions { RefreshSkew = TimeSpan.FromMinutes(30) });

        // Então renova com 29 min restantes e não com 31
        Assert.True(policy.ShouldRefresh(Now.AddMinutes(29), forced: false));
        Assert.False(policy.ShouldRefresh(Now.AddMinutes(31), forced: false));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-01")]
    public void RefreshSkew_padrao_e_cinco_minutos()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new MercadoLivreOptions().RefreshSkew);
    }

    private static TokenRefreshPolicy Policy(TimeProvider clock, MercadoLivreOptions? options = null) =>
        new(clock, Options.Create(options ?? new MercadoLivreOptions()));
}
