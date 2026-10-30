using System.Globalization;
using Microsoft.Extensions.Options;
using Vitrine.MercadoLivre.Application.Abstractions;

namespace Vitrine.MercadoLivre.Infrastructure.Notifications;

/// <summary><c>application_id</c> esperado: o <c>MercadoLivre:ClientId</c> (numérico), lido na subida.</summary>
internal sealed class MercadoLivreApplication(IOptions<MercadoLivreOptions> options) : IMercadoLivreApplication
{
    public long? ApplicationId { get; } =
        long.TryParse(options.Value.ClientId?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}
