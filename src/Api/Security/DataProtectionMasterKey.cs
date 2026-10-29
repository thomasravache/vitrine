using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Vitrine.Api.Security;

/// <summary>Configuração da chave mestra (<c>DataProtection__MasterKey</c> no ambiente — contrato da SPEC-0008).</summary>
internal sealed class DataProtectionMasterKeyOptions
{
    public const string Section = "DataProtection";

    /// <summary>32 bytes aleatórios em Base64 (<c>openssl rand -base64 32</c>). Secret do Environment (ADR-0006).</summary>
    public string? MasterKey { get; set; }

    /// <summary>
    /// Só durante a rotação (runbook <c>rotacao-chave-mestra</c>): a chave mestra anterior, ainda aceita para ler o anel de
    /// chaves; o modo <c>--migrate</c> recifra com <see cref="MasterKey"/> tudo o que ainda estiver sob ela.
    /// </summary>
    public string? PreviousMasterKey { get; set; }
}

/// <summary>
/// Chave mestra que cifra (AES-256-GCM) o anel de chaves do Data Protection persistido no PostgreSQL. Vem de
/// <c>DataProtection:MasterKey</c>; fora de Development ela é obrigatória (a subida falha com mensagem clara). Em
/// Development sem chave configurada, uma chave efêmera é gerada por processo e o anel fica só em memória (sessões locais
/// não sobrevivem ao reinício; nada inválido é gravado no banco).
/// </summary>
internal sealed class DataProtectionMasterKey
{
    public const int KeySizeInBytes = 32;

    public const string MissingMessage =
        "DataProtection:MasterKey (variável DataProtection__MasterKey) não configurada: obrigatória fora de Development. "
        + "Gere 32 bytes aleatórios em Base64 (openssl rand -base64 32) e grave como secret do Environment "
        + "(docs/runbooks/rotacao-chave-mestra.md).";

    public const string InvalidMessage =
        "DataProtection:MasterKey/PreviousMasterKey (variáveis DataProtection__MasterKey/__PreviousMasterKey) inválida: "
        + "deve ser Base64 de exatamente 32 bytes (openssl rand -base64 32).";

    public DataProtectionMasterKey(IOptions<DataProtectionMasterKeyOptions> options, IHostEnvironment environment, ILogger<DataProtectionMasterKey> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = options.Value;
        if (!string.IsNullOrWhiteSpace(configured.MasterKey))
        {
            Current = TryDecode(configured.MasterKey) ?? throw new InvalidOperationException(InvalidMessage);
        }
        else if (environment.IsDevelopment())
        {
            Current = RandomNumberGenerator.GetBytes(KeySizeInBytes);
            IsEphemeral = true;
            DataProtectionLog.EphemeralMasterKey(logger);
        }
        else
        {
            throw new InvalidOperationException(MissingMessage);
        }

        if (!string.IsNullOrWhiteSpace(configured.PreviousMasterKey))
        {
            Previous = TryDecode(configured.PreviousMasterKey) ?? throw new InvalidOperationException(InvalidMessage);
        }
    }

    /// <summary>Chave gerada por processo (só Development sem chave configurada): o anel fica só em memória.</summary>
    public bool IsEphemeral { get; }

    /// <summary>A chave que cifra as chaves novas (32 bytes; nunca logada).</summary>
    internal byte[] Current { get; }

    /// <summary>A chave anterior, aceita só para leitura durante a rotação.</summary>
    internal byte[]? Previous { get; }

    /// <summary>Valida o formato sem guardar a chave (usado na validação de opções na subida).</summary>
    public static bool IsValid(string? value) => TryDecode(value) is not null;

    private static byte[]? TryDecode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var buffer = new byte[KeySizeInBytes + 3];
        return Convert.TryFromBase64String(value.Trim(), buffer, out var written) && written == KeySizeInBytes
            ? buffer[..KeySizeInBytes]
            : null;
    }
}

internal static partial class DataProtectionLog
{
    [LoggerMessage(EventId = 8001, Level = LogLevel.Warning,
        Message = "DataProtection:MasterKey ausente em Development: usando chave mestra efêmera e anel de chaves só em memória (sessões não sobrevivem ao reinício).")]
    public static partial void EphemeralMasterKey(ILogger logger);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Information,
        Message = "Rotação da chave mestra: {Reencrypted} chave(s) do Data Protection recifrada(s) com a chave atual; {Current} já estava(m) nela.")]
    public static partial void Reencrypted(ILogger logger, int reencrypted, int current);
}
