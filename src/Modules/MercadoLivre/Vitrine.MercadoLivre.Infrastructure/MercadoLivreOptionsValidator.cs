using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Vitrine.MercadoLivre.Infrastructure;

/// <summary>
/// Validação de <see cref="MercadoLivreOptions"/> na subida (SPEC-0020): URLs absolutas; em Production, só as oficiais,
/// salvo <c>AllowNonOfficialEndpoints</c>; <c>RedirectUri</c> em HTTPS fora de Development. Sem <c>ClientId</c>,
/// <c>ClientSecret</c> ou <c>RedirectUri</c> a subida continua: os endpoints respondem 503 <c>ML_NOT_CONFIGURED</c>.
/// </summary>
public sealed class MercadoLivreOptionsValidator(IHostEnvironment environment) : IValidateOptions<MercadoLivreOptions>
{
    public ValidateOptionsResult Validate(string? name, MercadoLivreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        CheckEndpoint(failures, nameof(options.AuthorizationBaseUrl), options.AuthorizationBaseUrl, MercadoLivreOptions.OfficialAuthorizationBaseUrl, options);
        CheckEndpoint(failures, nameof(options.ApiBaseUrl), options.ApiBaseUrl, MercadoLivreOptions.OfficialApiBaseUrl, options);

        if (!string.IsNullOrWhiteSpace(options.RedirectUri))
        {
            if (!Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out var redirect))
            {
                failures.Add($"{MercadoLivreOptions.SectionName}:RedirectUri precisa ser uma URL absoluta.");
            }
            else if (redirect.Scheme != Uri.UriSchemeHttps && !environment.IsDevelopment())
            {
                failures.Add($"{MercadoLivreOptions.SectionName}:RedirectUri precisa usar HTTPS fora de Development.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void CheckEndpoint(List<string> failures, string key, string value, string official, MercadoLivreOptions options)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            failures.Add($"{MercadoLivreOptions.SectionName}:{key} precisa ser uma URL absoluta.");
            return;
        }

        if (environment.IsProduction()
            && !options.AllowNonOfficialEndpoints
            && !string.Equals(value.TrimEnd('/'), official, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                $"{MercadoLivreOptions.SectionName}:{key} difere da URL oficial ({official}) em Production; "
                + $"só com {MercadoLivreOptions.SectionName}:AllowNonOfficialEndpoints=true.");
        }
    }
}
