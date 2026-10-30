using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Vitrine.MercadoLivre.Infrastructure;

/// <summary>
/// Validação de <see cref="MercadoLivreOptions"/> na subida (SPEC-0020): URLs oficiais em Production, salvo
/// <c>AllowNonOfficialEndpoints</c>; <c>RedirectUri</c> em HTTPS fora de Development. Scaffold do contrato.
/// </summary>
public sealed class MercadoLivreOptionsValidator(IHostEnvironment environment) : IValidateOptions<MercadoLivreOptions>
{
    public ValidateOptionsResult Validate(string? name, MercadoLivreOptions options) =>
        throw new NotImplementedException($"SPEC-0020: implementado na fase Green ({environment.EnvironmentName}).");
}
