using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

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
    DateTimeOffset LastUpdated)
{
    /// <summary>Categoria sintética do anúncio (<c>category_id</c>, SPEC-0034).</summary>
    public string CategoryId { get; init; } = string.Empty;

    /// <summary>Anúncio de catálogo (<c>catalog_listing</c>, SPEC-0034).</summary>
    public bool CatalogListing { get; init; }

    /// <summary>Atributos preenchidos do anúncio (SPEC-0034).</summary>
    public IReadOnlyList<ItemAttribute> Attributes { get; init; } = [];
}

/// <summary>Atributo preenchido de um anúncio (<c>attributes[]</c> do <c>GET /items/{id}</c>).</summary>
internal sealed record ItemAttribute(string Id, string Name, string? ValueId, string? ValueName);

/// <summary>Descrição de um anúncio (<c>GET /items/{id}/description</c>).</summary>
internal sealed record ItemDescription(string PlainText, DateTimeOffset LastUpdated, DateTimeOffset DateCreated);

/// <summary>
/// Alteração parcial de um anúncio (<c>POST /__sim/sellers/{id}/items/{itemId}</c>): campo nulo não muda;
/// <see cref="Description"/> vazia apaga a descrição; <see cref="Attributes"/> substitui a lista.
/// </summary>
internal sealed record ItemChange(string? Title, decimal? Price, int? AvailableQuantity, int? SoldQuantity, string? Status)
{
    public bool? CatalogListing { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<AttributeUpdate>? Attributes { get; init; }
}

/// <summary>Alvo de uma escrita da API do ML: o anúncio (<c>PUT /items/{id}</c>) ou a descrição.</summary>
internal enum WriteTarget
{
    Item,
    Description,
}

/// <summary>Resposta forçada para as próximas <see cref="Times"/> escritas num alvo (<c>fail-next-write</c>).</summary>
internal sealed record PendingWriteFailure(WriteTarget Target, ApiError Error, int Times);

/// <summary>Escrita recebida pela API do ML para um anúncio (<c>GET /__sim/.../writes</c>).</summary>
internal sealed record WriteRecord(string Method, string Path, object? Body, int Status, string At);

/// <summary>
/// Resultado de uma escrita da API do ML: o status HTTP e o erro do ML, ou o anúncio/descrição gravados. <see cref="SellerId"/>
/// é o dono do anúncio (para a notificação); <see cref="Accepted"/> diz se a escrita foi aplicada.
/// </summary>
internal sealed record WriteResult(int Status, ApiError? Error, ItemInfo? Item, ItemDescription? Description, long SellerId)
{
    public bool Accepted => Error is null;
}

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
/// Estado em memória do simulador (SPEC-0024, SPEC-0034): vendedores, anúncios (com descrição, falhas forçadas e escritas
/// recebidas) e codes de autorização. Perde-se ao reiniciar;
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

    /// <summary>Categoria sintética de cada produto de <see cref="Products"/>, na mesma ordem.</summary>
    private static readonly string[] ProductCategories =
    [
        "MLB90001", "MLB90001", "MLB90002", "MLB90003", "MLB90001", "MLB90001", "MLB90002", "MLB90001", "MLB90001", "MLB90002",
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

            var now = _timeProvider.GetUtcNow();
            item.Title = change.Title ?? item.Title;
            item.Price = change.Price ?? item.Price;
            item.AvailableQuantity = change.AvailableQuantity ?? item.AvailableQuantity;
            item.SoldQuantity = change.SoldQuantity ?? item.SoldQuantity;
            item.Status = change.Status ?? item.Status;
            item.CatalogListing = change.CatalogListing ?? item.CatalogListing;
            if (change.Attributes is not null)
            {
                item.Attributes = [.. change.Attributes.Select(attribute => ControlAttribute(item.CategoryId, attribute))];
            }

            if (change.Description is not null)
            {
                item.Description = change.Description.Length == 0
                    ? null
                    : new ItemDescription(change.Description, now, item.Description?.DateCreated ?? now);
            }

            item.LastUpdated = now;
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

    /// <summary>Descrição do anúncio; <c>null</c> quando o anúncio não existe ou não tem descrição (SPEC-0034).</summary>
    public ItemDescription? FindDescription(string itemId)
    {
        lock (_gate)
        {
            return _items.TryGetValue(itemId, out var item) ? item.Description : null;
        }
    }

    /// <summary>Força a resposta das próximas escritas no alvo do anúncio do vendedor (<c>fail-next-write</c>).</summary>
    public ChangeResult FailNextWrite(long sellerId, string itemId, PendingWriteFailure failure)
    {
        lock (_gate)
        {
            if (FindOwnedLocked(sellerId, itemId, out var result) is not { } item)
            {
                return result;
            }

            item.Failures[failure.Target] = failure;
            return ChangeResult.Changed;
        }
    }

    /// <summary>Escritas recebidas para o anúncio do vendedor, na ordem; <c>null</c> quando não existe.</summary>
    public IReadOnlyList<WriteRecord>? Writes(long sellerId, string itemId)
    {
        lock (_gate)
        {
            return FindOwnedLocked(sellerId, itemId, out _) is { } item ? [.. item.Writes] : null;
        }
    }

    /// <summary>
    /// <c>PUT /items/{id}</c>: Bearer do dono, falha forçada pendente e as regras do ML (<see cref="ItemEditRules"/>),
    /// de forma atômica; a escrita fica registrada. <c>null</c> quando o anúncio não existe.
    /// </summary>
    public WriteResult? EditItem(string itemId, long? caller, string path, string body)
    {
        lock (_gate)
        {
            return WriteLocked(itemId, caller, WriteTarget.Item, "PUT", path, body, item =>
            {
                if (Parse<ItemUpdate>(body) is not { } update)
                {
                    return Rejected(item, BadRequest("body must be { title?, attributes?: [{ id, value_id?, value_name? }] }."));
                }

                var category = SimulatorCategories.Find(item.CategoryId)!;
                var result = ItemEditRules.Apply(item.Info, category, update, _timeProvider.GetUtcNow());
                if (result.Error is not null)
                {
                    return Rejected(item, result.Error);
                }

                item.Title = result.Item.Title;
                item.Attributes = result.Item.Attributes;
                item.LastUpdated = result.Item.LastUpdated;
                return new WriteResult(StatusCodes.Status200OK, null, item.Info, null, item.SellerId);
            });
        }
    }

    /// <summary>
    /// <c>POST /items/{id}/description</c> (<paramref name="create"/>: 201; já existe: 400) ou
    /// <c>PUT /items/{id}/description</c> (200; sem descrição: 404). <c>null</c> quando o anúncio não existe.
    /// </summary>
    public WriteResult? WriteDescription(string itemId, long? caller, bool create, string path, string body)
    {
        lock (_gate)
        {
            return WriteLocked(itemId, caller, WriteTarget.Description, create ? "POST" : "PUT", path, body, item =>
            {
                if (Parse<DescriptionBody>(body) is not { PlainText: { } text })
                {
                    return Rejected(item, BadRequest("body must be { plain_text }."));
                }

                if (create && item.Description is not null)
                {
                    return Rejected(item, BadRequest($"Item {item.Id} already has a description; use PUT ?api_version=2."));
                }

                if (!create && item.Description is null)
                {
                    return Rejected(item, new ApiError($"Description of item {item.Id} not found", "not_found", StatusCodes.Status404NotFound, []));
                }

                if (ItemEditRules.ValidateDescription(SimulatorCategories.Find(item.CategoryId)!, text) is { } error)
                {
                    return Rejected(item, error);
                }

                var now = _timeProvider.GetUtcNow();
                item.Description = new ItemDescription(text, now, item.Description?.DateCreated ?? now);
                return new WriteResult(create ? StatusCodes.Status201Created : StatusCodes.Status200OK, null, null, item.Description, item.SellerId);
            });
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

    private static WriteResult Rejected(Item item, ApiError error) => new(error.Status, error, null, null, item.SellerId);

    private static ApiError BadRequest(string message) => new(message, "bad_request", StatusCodes.Status400BadRequest, []);

    private static T? Parse<T>(string body)
        where T : class
    {
        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<T>(body, MlJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Corpo da escrita para o registro: o JSON recebido, ou o texto quando não é JSON.</summary>
    private static object? RecordedBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return body;
        }
    }

    /// <summary>Atributo do controle: completa o nome (e o <c>value_name</c> de um <c>value_id</c> da lista) pela categoria.</summary>
    private static ItemAttribute ControlAttribute(string categoryId, AttributeUpdate attribute)
    {
        var definition = SimulatorCategories.Find(categoryId)?.Attributes.FirstOrDefault(a => a.Id == attribute.Id);
        var valueName = attribute.ValueName
            ?? definition?.Values.FirstOrDefault(value => value.Id == attribute.ValueId)?.Name;
        return new ItemAttribute(attribute.Id, definition?.Name ?? attribute.Id, attribute.ValueId, valueName);
    }

    /// <summary>
    /// Escrita da API do ML no anúncio: 401 sem token, 403 de outro vendedor, a falha forçada pendente no alvo, senão
    /// <paramref name="apply"/>. Toda escrita a um anúncio existente fica no registro. <c>null</c>: anúncio inexistente.
    /// </summary>
    private WriteResult? WriteLocked(
        string itemId, long? caller, WriteTarget target, string method, string path, string body, Func<Item, WriteResult> apply)
    {
        if (!_items.TryGetValue(itemId, out var item))
        {
            return null;
        }

        WriteResult result;
        if (caller is null)
        {
            result = Rejected(item, new ApiError("invalid access token", "unauthorized", StatusCodes.Status401Unauthorized, []));
        }
        else if (caller != item.SellerId)
        {
            result = Rejected(item, new ApiError("caller.id does not match the item owner.", "forbidden", StatusCodes.Status403Forbidden, []));
        }
        else if (item.Failures.TryGetValue(target, out var failure))
        {
            if (failure.Times <= 1)
            {
                item.Failures.Remove(target);
            }
            else
            {
                item.Failures[target] = failure with { Times = failure.Times - 1 };
            }

            result = Rejected(item, failure.Error);
        }
        else
        {
            result = apply(item);
        }

        item.Writes.Add(new WriteRecord(method, path, RecordedBody(body), result.Status, MlJson.Date(_timeProvider.GetUtcNow())));
        return result;
    }

    private Item? FindOwnedLocked(long sellerId, string itemId, out ChangeResult result)
    {
        if (FindSellerLocked(sellerId) is null)
        {
            result = ChangeResult.SellerNotFound;
            return null;
        }

        if (!_items.TryGetValue(itemId, out var item) || item.SellerId != sellerId)
        {
            result = ChangeResult.ItemNotFound;
            return null;
        }

        result = ChangeResult.Changed;
        return item;
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
            var product = Products[index % Products.Length];
            var category = SimulatorCategories.Find(ProductCategories[index % Products.Length])!;

            // SPEC-0034: 1º ativo do vendedor com vendas, 2º de catálogo, demais ativos sem vendas; pausados e
            // encerrados mantêm índice % 37.
            var activePosition = status == "active" ? seller.ActiveCreated++ : -1;
            var item = new Item(id, seller.Info.Id)
            {
                Title = string.Create(CultureInfo.InvariantCulture, $"{product} — anúncio simulado {number}"),
                Price = 19.9m + (index % 50 * 7.5m),
                CurrencyId = site.CurrencyId,
                AvailableQuantity = status == "active" ? 5 + (index % 20) : 0,
                SoldQuantity = activePosition switch
                {
                    0 => 12,
                    > 0 => 0,
                    _ => index % 37,
                },
                Status = status,
                Permalink = string.Create(CultureInfo.InvariantCulture, $"https://produto.{site.Domain}/{site.Id}-{number}-anuncio-simulado-_JM"),
                Thumbnail = "http://" + image,
                SecureThumbnail = "https://" + image,
                ListingTypeId = index % 2 == 0 ? "gold_special" : "gold_pro",
                LastUpdated = _timeProvider.GetUtcNow(),
                CategoryId = category.Id,
                CatalogListing = activePosition == 1,
                Attributes = SeedAttributes(category),
                Description = number % 2 == 0
                    ? new ItemDescription($"{product} simulado. Cor: Preto. Marca: Marca Simulada. Anúncio de teste.", _timeProvider.GetUtcNow(), _timeProvider.GetUtcNow())
                    : null,
            };
            _items[id] = item;
            seller.ItemIds.Add(id);
        }
    }

    /// <summary>Atributos da massa: BRAND e ITEM_CONDITION preenchidos; COLOR (obrigatório) e MODEL vazios.</summary>
    private static List<ItemAttribute> SeedAttributes(SimulatorCategory category)
    {
        var brand = category.Attributes.First(attribute => attribute.Id == "BRAND");
        var condition = category.Attributes.First(attribute => attribute.Id == "ITEM_CONDITION");
        return
        [
            new ItemAttribute(brand.Id, brand.Name, null, "Marca Simulada"),
            new ItemAttribute(condition.Id, condition.Name, condition.Values[0].Id, condition.Values[0].Name),
        ];
    }

    /// <summary>Corpo da descrição na escrita: <c>{ plain_text }</c>.</summary>
    private sealed record DescriptionBody(string? PlainText);

    private sealed class Seller(SellerInfo info)
    {
        public SellerInfo Info { get; } = info;

        public List<string> ItemIds { get; } = [];

        /// <summary>Ativos já criados para o vendedor (o 1º tem vendas e o 2º é de catálogo).</summary>
        public int ActiveCreated { get; set; }
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

        public required string CategoryId { get; init; }

        public required bool CatalogListing { get; set; }

        public required IReadOnlyList<ItemAttribute> Attributes { get; set; }

        public ItemDescription? Description { get; set; }

        /// <summary>Falhas forçadas pendentes por alvo (<c>fail-next-write</c>).</summary>
        public Dictionary<WriteTarget, PendingWriteFailure> Failures { get; } = [];

        /// <summary>Escritas recebidas pela API do ML, na ordem.</summary>
        public List<WriteRecord> Writes { get; } = [];

        public ItemInfo Info => new(
            Id, SellerId, Title, Price, CurrencyId, AvailableQuantity, SoldQuantity, Status, Permalink, Thumbnail, SecureThumbnail,
            ListingTypeId, LastUpdated)
        {
            CategoryId = CategoryId,
            CatalogListing = CatalogListing,
            Attributes = Attributes,
        };
    }
}
