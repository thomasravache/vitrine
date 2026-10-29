using System.Security.Cryptography;

namespace Vitrine.IntegrationTests.Support;

/// <summary>
/// Chaves de configuração de segurança do host (SPEC-0008) usadas pelos testes. <c>DataProtection:MasterKey</c> é a do
/// contrato (<c>DataProtection__MasterKey</c> no ambiente); as demais são os pontos de ajuste que os testes exigem do host.
/// </summary>
public static class SecuritySettings
{
    /// <summary>Chave mestra que protege o anel de chaves do Data Protection persistido no PostgreSQL (contrato).</summary>
    public const string MasterKey = "DataProtection:MasterKey";

    /// <summary>Limite por IP, por minuto, em <c>/api/v1/auth/*</c> (contrato: 10).</summary>
    public const string AuthPerIpPermitLimit = "RateLimiting:AuthPerIp:PermitLimit";

    /// <summary>Limite de logins por e-mail, por minuto (contrato v2: 10; o bloqueio de 5 falhas da SPEC-0006 vem antes).</summary>
    public const string LoginPerEmailPermitLimit = "RateLimiting:LoginPerEmail:PermitLimit";

    /// <summary>Rede(s) do proxy (Caddy na rede do Compose), em CIDR, das quais os cabeçalhos X-Forwarded-* são aceitos.</summary>
    public const string ForwardedKnownNetwork = "ForwardedHeaders:KnownNetworks:0";

    /// <summary>
    /// Chave mestra de teste gerada por execução (256 bits em Base64): nada fixo no código. O formato é o esperado da
    /// variável de ambiente <c>DataProtection__MasterKey</c>.
    /// </summary>
    public static string NewMasterKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
