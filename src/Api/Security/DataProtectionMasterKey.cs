using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Vitrine.Api.Security;

/// <summary>Configuração da chave mestra (<c>DataProtection__MasterKey</c> no ambiente — contrato da SPEC-0008).</summary>
internal sealed class DataProtectionMasterKeyOptions
{
    public const string Section = "DataProtection";

    /// <summary>32 bytes aleatórios em Base64 (<c>openssl rand -base64 32</c>). Secret do Environment (ADR-0006).</summary>
    public string? MasterKey { get; set; }
}

/// <summary>
/// Chave mestra que cifra (AES-256-GCM) o anel de chaves do Data Protection persistido no PostgreSQL. Vem de
/// <c>DataProtection:MasterKey</c>; fora de Development ela é obrigatória (a subida falha com mensagem clara). Em
/// Development sem chave configurada, uma chave efêmera é gerada por processo: as chaves gravadas por um processo anterior
/// deixam de ser legíveis e o Data Protection cria outras (sessões locais não sobrevivem ao reinício).
/// </summary>
internal sealed class DataProtectionMasterKey
{
    public const int KeySizeInBytes = 32;

    public DataProtectionMasterKey(IOptions<DataProtectionMasterKeyOptions> options, IHostEnvironment environment, ILogger<DataProtectionMasterKey> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = options.Value.MasterKey;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Bytes = TryDecode(configured) ?? throw new InvalidOperationException(InvalidMessage);
            return;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(MissingMessage);
        }

        Bytes = RandomNumberGenerator.GetBytes(KeySizeInBytes);
        IsEphemeral = true;
        DataProtectionLog.EphemeralMasterKey(logger);
    }

    public const string MissingMessage =
        "DataProtection:MasterKey (variável DataProtection__MasterKey) não configurada: obrigatória fora de Development. "
        + "Gere 32 bytes aleatórios em Base64 (openssl rand -base64 32) e grave como secret do Environment (runbook rotacao-chave-mestra).";

    public const string InvalidMessage =
        "DataProtection:MasterKey (variável DataProtection__MasterKey) inválida: deve ser Base64 de exatamente 32 bytes "
        + "(openssl rand -base64 32).";

    /// <summary>Os 32 bytes da chave (nunca logados).</summary>
    internal byte[] Bytes { get; }

    /// <summary>Chave gerada por processo (só Development sem chave configurada).</summary>
    public bool IsEphemeral { get; }

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
        Message = "DataProtection:MasterKey ausente em Development: usando chave mestra efêmera (sessões não sobrevivem ao reinício).")]
    public static partial void EphemeralMasterKey(ILogger logger);
}
