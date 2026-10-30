using Vitrine.MercadoLivre.Domain.Authorizations;

namespace Vitrine.MercadoLivre.Application.Abstractions;

/// <summary>Persistência das autorizações OAuth pendentes (gravadas no commit da <see cref="IMercadoLivreUnitOfWork"/>).</summary>
public interface IPendingAuthorizations
{
    void Add(PendingAuthorization authorization);

    /// <summary>Autorização pelo hash do <c>state</c> (o state nunca é gravado); nula se não existe.</summary>
    Task<PendingAuthorization?> FindByStateHashAsync(string stateHash, CancellationToken cancellationToken);
}
