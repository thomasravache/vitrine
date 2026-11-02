using System.Text.Json;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator.Support;

/// <summary>
/// Fixtures do ML em <c>tests/Fixtures/MercadoLivre/</c> (copiadas para <c>MercadoLivre/Fixtures</c> na saída) e a
/// comparação de forma JSON usada pelos testes de contrato do simulador.
/// </summary>
internal static class MlFixtures
{
    public static JsonElement Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MercadoLivre", "Fixtures", name))).RootElement.Clone();

    /// <summary>
    /// Cada campo (caminho com ponto; segmento numérico = índice de lista) existe na fixture e na resposta com o mesmo
    /// tipo JSON. Nulo na resposta só é aceito onde a fixture também traz nulo.
    /// </summary>
    public static void AssertSameShape(string fixture, JsonElement expected, JsonElement actual, params string[] fields)
    {
        var problems = new List<string>();
        foreach (var field in fields)
        {
            var want = Find(expected, field);
            Assert.True(want is not null, $"A fixture {fixture} não tem o campo '{field}' (fixture incompleta).");
            var got = Find(actual, field);
            if (got is null)
            {
                problems.Add($"'{field}' ausente (fixture: {want.Value.ValueKind})");
            }
            else if (Kind(got.Value) != Kind(want.Value))
            {
                problems.Add($"'{field}' é {got.Value.ValueKind}; a fixture tem {want.Value.ValueKind}");
            }
        }

        Assert.True(problems.Count == 0, $"Resposta do simulador difere de {fixture}: {string.Join("; ", problems)}\nResposta: {actual}");
    }

    private static JsonElement? Find(JsonElement element, string path)
    {
        var current = element;
        foreach (var part in path.Split('.'))
        {
            if (int.TryParse(part, out var index))
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
            }
            else if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static JsonValueKind Kind(JsonElement element) =>
        element.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValueKind.True : element.ValueKind;
}
