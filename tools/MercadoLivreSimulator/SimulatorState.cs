using System.Globalization;
using System.Security.Cryptography;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>Vendedor simulado (visão imutável).</summary>
internal sealed record SellerInfo(long Id, string Nickname, Site Site, DateTimeOffset RegisteredAt);

/// <summary>Anúncio simulado (visão imutável), com os campos lidos pelo Vitrine no <c>/items/bulk</c>.</summary>
internal sealed record ItemInfo(
    string Id,
    long SellerId,
    string Title,
    decimal Price,
    string CurrencyId,
    int AvailableQuantity,
    int SoldQuantity,
    string Status,
    string Permalink,
    string Thumbnail,
    string SecureThumbnail,
    string ListingTypeId,
    DateTimeOffset LastUpdated);

/// <summary>Alteração parcial de um anúncio (<c>POST /__sim/sellers/{id}/items/{itemId}</c>).</summary>
internal sealed record ItemChange(string? Title, decimal? Price, int? AvailableQuantity, int? SoldQuantity, string? Status);

/// <summary><c>code</c> emitido pela autorização (uso único, 10 min).</summary>
internal sealed record AuthorizationCode(string ClientId, string RedirectUri, string CodeChallenge, long SellerId, DateTimeOffset ExpiresAt);

/// <summary>Resultado de uma operação de controle sobre um vendedor ou anúncio.</summary>
internal enum ChangeResult
{
    Changed,
    SellerNotFound,
    ItemNotFound,
    Invalid,
}

/// <summary>
/// Estado em memória do simulador (SPEC-0024): vendedores, anúncios e codes de autorização. Perde-se ao reiniciar;
/// <see cref="Reset"/> volta à massa inicial. Acesso serializado por um único lock (volume de dev/E2E).
/// </summary>
internal sealed class SimulatorState
{
    /// <summary><c>VENDEDOR_TESTE</c> (MLB, 15 ativos, 6 pausados, 4 encerrados).</summary>
    public const long SellerTesteId = 3_000_000_001;

    /// <summary><c>VENDEDOR_AR</c> (MLA: site não suportado pelo Vitrine).</summary>
    public const long SellerArId = 3_000_000_002;

    /// <summary>Menor id dos vendedores criados por <c>POST /__sim/sellers</c>.</summary>
    public const long FirstCreatedSellerId = 3_000_001_000;

    /// <summary>
    /// Época fixa da base dos ids criados (SPEC-0031): a base de cada execução é <see cref="FirstCreatedSellerId"/> mais
    /// os milissegundos desde esta data no relógio do DI, para não repetir ids entre reinícios (o banco do Vitrine persiste).
    /// </summary>
    public static readonly DateTimeOffset SellerIdEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Validade do <c>code</c> de autorização.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Status aceitos na busca e na alteração de anúncios.</summary>
    public static readonly IReadOnlySet<string> Statuses = new HashSet<string>(["active", "paused", "closed"], StringComparer.Ordinal);

    private const long FirstItemNumber = 9_000_000_001;

    private static readonly string[] Products =
    [
        "Fone de ouvido bluetooth", "Capa para celular de silicone", "Garrafa térmica 1 L", "Mochila para notebook",
        "Luminária de mesa LED", "Teclado sem fio", "Caneca de cerâmica", "Carregador USB-C 20 W", "Mouse óptico",
        "Organizador de gaveta",
    ];

    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly List<Seller> _sellers = [];
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthorizationCode> _codes = new(StringComparer.Ordinal);
    private long _nextSellerId;
    private long _nextItemNumber;

    public SimulatorState(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        var elapsed = (long)Math.Max(0, (timeProvider.GetUtcNow() - SellerIdEpoch).TotalMilliseconds);
        _nextSellerId = FirstCreatedSellerId + elapsed;
        Reset();
    }

    /// <summary>
    /// Volta à massa inicial: só <c>VENDEDOR_TESTE</c> e <c>VENDEDOR_AR</c>, sem codes pendentes. Os ids de vendedores
    /// criados continuam crescendo (não voltam à base), para não repetir ids já conectados no Vitrine.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _sellers.Clear();
            _items.Clear();
            _codes.Clear();
            _nextItemNumber = FirstItemNumber;
            AddSellerLocked(SellerTesteId, "VENDEDOR_TESTE", Site.Find("MLB")!, active: 15, paused: 6, closed: 4);
            AddSellerLocked(SellerArId, "VENDEDOR_AR", Site.Find("MLA")!, active: 3, paused: 0, closed: 0);
        }
    }

    /// <summary>Todos os vendedores, na ordem de criação.</summary>
    public IReadOnlyList<SellerInfo> Sellers()
    {
        lock (_gate)
        {
            return [.. _sellers.Select(seller => seller.Info)];
        }
    }

    public SellerInfo? FindSeller(long sellerId)
    {
        lock (_gate)
        {
            return FindSellerLocked(sellerId)?.Info;
        }
    }

    /// <summary>Cria um vendedor com id único (crescente a partir da base da execução) e a massa pedida.</summary>
    public SellerInfo CreateSeller(string? nickname, Site site, int active, int paused, int closed)
    {
        lock (_gate)
        {
            var id = _nextSellerId++;
            var name = string.IsNullOrWhiteSpace(nickname) ? string.Create(CultureInfo.InvariantCulture, $"VENDEDOR_SIM_{id}") : nickname.Trim();
            return AddSellerLocked(id, name, site, active, paused, closed).Info;
        }
    }

    /// <summary>Acrescenta <paramref name="count"/> anúncios ativos sintéticos ao vendedor.</summary>
    public ChangeResult AddItems(long sellerId, int count)
    {
        lock (_gate)
        {
            var seller = FindSellerLocked(sellerId);
            if (seller is null)
            {
                return ChangeResult.SellerNotFound;
            }

            AddItemsLocked(seller, "active", count);
            return ChangeResult.Changed;
        }
    }

    /// <summary>Altera os campos informados do anúncio do vendedor e atualiza <c>last_updated</c>.</summary>
    public ChangeResult UpdateItem(long sellerId, string itemId, ItemChange change)
    {
        lock (_gate)
        {
            if (FindSellerLocked(sellerId) is null)
            {
                return ChangeResult.SellerNotFound;
            }

            if (!_items.TryGetValue(itemId, out var item) || item.SellerId != sellerId)
            {
                return ChangeResult.ItemNotFound;
            }

            if ((change.Status is not null && !Statuses.Contains(change.Status))
                || change.Price is < 0 || change.AvailableQuantity is < 0 || change.SoldQuantity is < 0
                || (change.Title is not null && string.IsNullOrWhiteSpace(change.Title)))
            {
                return ChangeResult.Invalid;
            }

            item.Title = change.Title ?? item.Title;
            item.Price = change.Price ?? item.Price;
            item.AvailableQuantity = change.AvailableQuantity ?? item.AvailableQuantity;
            item.SoldQuantity = change.SoldQuantity ?? item.SoldQuantity;
            item.Status = change.Status ?? item.Status;
            item.LastUpdated = _timeProvider.GetUtcNow();
            return ChangeResult.Changed;
        }
    }

    /// <summary>IDs dos anúncios do vendedor (na ordem de criação), filtrados por status quando informado.</summary>
    public IReadOnlyList<string> ItemIds(long sellerId, string? status)
    {
        lock (_gate)
        {
            var seller = FindSellerLocked(sellerId);
            return seller is null
                ? []
                : [.. seller.ItemIds.Where(id => status is null || _items[id].Status == status)];
        }
    }

    public ItemInfo? FindItem(string itemId)
    {
        lock (_gate)
        {
            return _items.TryGetValue(itemId, out var item) ? item.Info : null;
        }
    }

    /// <summary>Emite um <c>code</c> de uso único para o vendedor, válido por <see cref="CodeLifetime"/>.</summary>
    public string IssueCode(string clientId, string redirectUri, string codeChallenge, long sellerId)
    {
        var code = $"TG-{RandomNumberGenerator.GetHexString(24, lowercase: true)}-{sellerId.ToString(CultureInfo.InvariantCulture)}";
        lock (_gate)
        {
            _codes[code] = new AuthorizationCode(clientId, redirectUri, codeChallenge, sellerId, _timeProvider.GetUtcNow() + CodeLifetime);
        }

        return code;
    }

    /// <summary>Consome o <c>code</c> (uso único): devolve-o se ainda não venceu; senão <c>null</c>.</summary>
    public AuthorizationCode? TakeCode(string code)
    {
        lock (_gate)
        {
            return _codes.Remove(code, out var found) && _timeProvider.GetUtcNow() <= found.ExpiresAt && FindSellerLocked(found.SellerId) is not null
                ? found
                : null;
        }
    }

    private Seller? FindSellerLocked(long sellerId) => _sellers.Find(seller => seller.Info.Id == sellerId);

    private Seller AddSellerLocked(long id, string nickname, Site site, int active, int paused, int closed)
    {
        var seller = new Seller(new SellerInfo(id, nickname, site, _timeProvider.GetUtcNow()));
        _sellers.Add(seller);
        AddItemsLocked(seller, "active", active);
        AddItemsLocked(seller, "paused", paused);
        AddItemsLocked(seller, "closed", closed);
        return seller;
    }

    private void AddItemsLocked(Seller seller, string status, int count)
    {
        var site = seller.Info.Site;
        for (var i = 0; i < count; i++)
        {
            var number = _nextItemNumber++;
            var index = (int)(number % 1_000);
            var id = string.Create(CultureInfo.InvariantCulture, $"{site.Id}{number}");
            var image = string.Create(CultureInfo.InvariantCulture, $"http2.mlstatic.com/D_NQ_NP_{number}-{site.Id}{number}_102026-O.jpg");
            var item = new Item(id, seller.Info.Id)
            {
                Title = string.Create(CultureInfo.InvariantCulture, $"{Products[index % Products.Length]} — anúncio simulado {number}"),
                Price = 19.9m + (index % 50 * 7.5m),
                CurrencyId = site.CurrencyId,
                AvailableQuantity = status == "active" ? 5 + (index % 20) : 0,
                SoldQuantity = index % 37,
                Status = status,
                Permalink = string.Create(CultureInfo.InvariantCulture, $"https://produto.{site.Domain}/{site.Id}-{number}-anuncio-simulado-_JM"),
                Thumbnail = "http://" + image,
                SecureThumbnail = "https://" + image,
                ListingTypeId = index % 2 == 0 ? "gold_special" : "gold_pro",
                LastUpdated = _timeProvider.GetUtcNow(),
            };
            _items[id] = item;
            seller.ItemIds.Add(id);
        }
    }

    private sealed class Seller(SellerInfo info)
    {
        public SellerInfo Info { get; } = info;

        public List<string> ItemIds { get; } = [];
    }

    private sealed class Item(string id, long sellerId)
    {
        public string Id { get; } = id;

        public long SellerId { get; } = sellerId;

        public required string Title { get; set; }

        public required decimal Price { get; set; }

        public required string CurrencyId { get; init; }

        public required int AvailableQuantity { get; set; }

        public required int SoldQuantity { get; set; }

        public required string Status { get; set; }

        public required string Permalink { get; init; }

        public required string Thumbnail { get; init; }

        public required string SecureThumbnail { get; init; }

        public required string ListingTypeId { get; init; }

        public required DateTimeOffset LastUpdated { get; set; }

        public ItemInfo Info => new(
            Id, SellerId, Title, Price, CurrencyId, AvailableQuantity, SoldQuantity, Status, Permalink, Thumbnail, SecureThumbnail,
            ListingTypeId, LastUpdated);
    }
}
