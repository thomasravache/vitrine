using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.Infrastructure;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>
/// Host da Api para os testes do módulo MercadoLivre (SPEC-0020): o do Identity (cadastro aberto e assinantes de teste
/// descobertos pelo Wolverine), com <c>MercadoLivre:*</c> configurado com valores fictícios e o cliente HTTP nomeado do
/// ML atendido pelo WireMock (<see cref="ApiFactory.RedirectToWireMock"/>, padrão da SPEC-0003:IT-03). As URLs do ML
/// ficam nos padrões oficiais: nada sai para a rede.
/// </summary>
internal static class MercadoLivreHost
{
    public const string ClientId = "1234567890123456";
    public const string RedirectUri = "https://vitrine.localhost/contas/mercado-livre/retorno";

    /// <summary>Propósito do <c>IFieldProtector</c> dos tokens e do verifier (contrato da SPEC-0020).</summary>
    public const string TokensPurpose = "mercadolivre.tokens";

    /// <summary>Segredo fictício, gerado por execução (nada fixo no código).</summary>
    public static readonly string ClientSecret = "cs-teste-" + Guid.NewGuid().ToString("N");

    /// <summary>Chaves obrigatórias de <c>MercadoLivre:*</c> (sem uma delas: 503 ML_NOT_CONFIGURED).</summary>
    public static readonly string[] RequiredKeys = ["ClientId", "ClientSecret", "RedirectUri"];

    /// <summary>Api com o ML configurado; <paramref name="omit"/> deixa uma das <see cref="RequiredKeys"/> vazia.</summary>
    public static ApiFactory Create(string connectionString, string? omit = null)
    {
        var factory = IdentityHost.Create(connectionString)
            .RedirectToWireMock(MercadoLivreModule.HttpClientName)
            .RedirectToWireMock(MercadoLivreModule.ApiHttpClientName)
            .RedirectToWireMock(MercadoLivreModule.OAuthHttpClientName);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ClientId"] = ClientId,
            ["ClientSecret"] = ClientSecret,
            ["RedirectUri"] = RedirectUri,
        };
        foreach (var (key, value) in settings)
        {
            factory.WithSetting($"MercadoLivre:{key}", key == omit ? string.Empty : value);
        }

        return factory;
    }

    /// <summary>Troca o <see cref="TimeProvider"/> do host pelo relógio do teste.</summary>
    public static ApiFactory WithClock(this ApiFactory factory, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.WithServices(services => services.AddSingleton(clock));
    }

    /// <summary>Acrescenta um provedor de log de teste que vê todo registro do host (antes de qualquer máscara).</summary>
    public static ApiFactory WithLogs(this ApiFactory factory, ILoggerProvider logs)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.WithServices(services => services.AddSingleton(logs));
    }
}
