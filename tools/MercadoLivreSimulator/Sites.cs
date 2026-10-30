namespace Vitrine.MercadoLivreSimulator;

/// <summary>Site do ML simulado: país, moeda e domínio dos permalinks.</summary>
internal sealed record Site(string Id, string CountryId, string CurrencyId, string Domain)
{
    private static readonly Dictionary<string, Site> Known = new[]
    {
        new Site("MLB", "BR", "BRL", "mercadolivre.com.br"),
        new Site("MLA", "AR", "ARS", "mercadolibre.com.ar"),
        new Site("MLM", "MX", "MXN", "mercadolibre.com.mx"),
        new Site("MLC", "CL", "CLP", "mercadolibre.cl"),
        new Site("MCO", "CO", "COP", "mercadolibre.com.co"),
        new Site("MLU", "UY", "UYU", "mercadolibre.com.uy"),
        new Site("MPE", "PE", "PEN", "mercadolibre.com.pe"),
    }.ToDictionary(site => site.Id, StringComparer.Ordinal);

    public static Site? Find(string? siteId) =>
        siteId is not null && Known.TryGetValue(siteId.ToUpperInvariant(), out var site) ? site : null;
}
