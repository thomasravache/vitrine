namespace Vitrine.MercadoLivre.PublicApi;

/// <summary>A conta não existe (ex.: desconectada). Contrato v1 da SPEC-0021.</summary>
public sealed class MercadoLivreAccountNotFoundException : Exception
{
    public MercadoLivreAccountNotFoundException()
    {
    }

    public MercadoLivreAccountNotFoundException(string message)
        : base(message)
    {
    }

    public MercadoLivreAccountNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MercadoLivreAccountNotFoundException(Guid accountId)
        : base($"Conta do Mercado Livre {accountId:D} não encontrada.")
    {
        AccountId = accountId;
    }

    /// <summary>Conta pedida.</summary>
    public Guid AccountId { get; }
}

/// <summary>
/// A conta já estava ou acabou de ficar em <c>reauthorization_required</c>: o Owner precisa conectar de novo. Contrato v1
/// da SPEC-0021.
/// </summary>
public sealed class MercadoLivreReauthorizationRequiredException : Exception
{
    public MercadoLivreReauthorizationRequiredException()
    {
    }

    public MercadoLivreReauthorizationRequiredException(string message)
        : base(message)
    {
    }

    public MercadoLivreReauthorizationRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MercadoLivreReauthorizationRequiredException(Guid accountId)
        : base($"Conta do Mercado Livre {accountId:D} precisa ser reconectada.")
    {
        AccountId = accountId;
    }

    /// <summary>Conta que precisa ser reconectada.</summary>
    public Guid AccountId { get; }
}

/// <summary>
/// Mercado Livre indisponível para a conta: retries esgotados, circuito aberto, timeout ou resposta inválida. O estado
/// da conta não muda. Contrato v1 da SPEC-0021.
/// </summary>
public sealed class MercadoLivreUnavailableException : Exception
{
    public MercadoLivreUnavailableException()
    {
    }

    public MercadoLivreUnavailableException(string message)
        : base(message)
    {
    }

    public MercadoLivreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MercadoLivreUnavailableException(Guid accountId, string reason)
        : base($"Mercado Livre indisponível para a conta {accountId:D}: {reason}")
    {
        AccountId = accountId;
        Reason = reason;
    }

    public MercadoLivreUnavailableException(Guid accountId, string reason, Exception innerException)
        : base($"Mercado Livre indisponível para a conta {accountId:D}: {reason}", innerException)
    {
        AccountId = accountId;
        Reason = reason;
    }

    /// <summary>Conta da chamada.</summary>
    public Guid AccountId { get; }

    /// <summary>Motivo, sem dados sensíveis (ex.: <c>retries esgotados</c>, <c>circuito aberto</c>).</summary>
    public string Reason { get; } = string.Empty;
}
