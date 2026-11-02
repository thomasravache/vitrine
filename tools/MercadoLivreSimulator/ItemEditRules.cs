namespace Vitrine.MercadoLivreSimulator;

/// <summary>Atributo pedido no <c>PUT /items/{id}</c>: <c>{ id, value_id? | value_name? }</c>.</summary>
internal sealed record AttributeUpdate(string Id, string? ValueId, string? ValueName);

/// <summary>Corpo do <c>PUT /items/{id}</c>: <c>{ title?, attributes? }</c>; campo nulo não é alterado.</summary>
internal sealed record ItemUpdate(string? Title, IReadOnlyList<AttributeUpdate>? Attributes);

/// <summary>
/// Resultado da edição: <see cref="Item"/> é o anúncio atualizado quando <see cref="Error"/> é nulo, ou o anúncio
/// original sem mudança nenhuma quando a edição foi recusada (atômica).
/// </summary>
internal sealed record ItemEditResult(ItemInfo Item, ApiError? Error);

/// <summary>
/// Regras de edição do ML imitadas pelo simulador (SPEC-0034, seção 6): puras, sem I/O e independentes da
/// <c>ListingChangeRules</c> do Vitrine (o simulador é um oráculo à parte).
/// </summary>
internal static class ItemEditRules
{
    /// <summary>Aplica o <c>PUT /items/{id}</c> ao anúncio, ou devolve o erro do ML sem mudar nada.</summary>
    public static ItemEditResult Apply(ItemInfo item, SimulatorCategory category, ItemUpdate update, DateTimeOffset now) =>
        throw new NotImplementedException("SPEC-0034: scaffold.");

    /// <summary>Erro do ML para a descrição em texto puro; <c>null</c> quando é aceita.</summary>
    public static ApiError? ValidateDescription(SimulatorCategory category, string plainText) =>
        throw new NotImplementedException("SPEC-0034: scaffold.");
}
