using System.Globalization;
using System.Net;
using System.Text;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>
/// HTML da página de autorização do simulador: simples, sem a identidade visual do ML, com rótulos, botões nativos e
/// contraste AA (o E2E passa por ela). Deixa explícito que é um simulador.
/// </summary>
internal static class AuthorizationPage
{
    /// <summary>Rótulo exibido em toda página do simulador.</summary>
    public const string Title = "Simulador do Mercado Livre — ambiente de teste";

    /// <summary>Parâmetros da autorização repetidos como campos ocultos no formulário da decisão.</summary>
    public static readonly string[] ForwardedParameters =
        ["response_type", "client_id", "redirect_uri", "state", "code_challenge", "code_challenge_method"];

    private const string Style = """
        body { font-family: system-ui, sans-serif; margin: 0; background: #ffffff; color: #1a1a1a; line-height: 1.5; }
        main { max-width: 36rem; margin: 2rem auto; padding: 0 1rem; }
        .aviso { background: #fff4ce; border: 1px solid #8a6d00; padding: .75rem 1rem; border-radius: .5rem; }
        fieldset { border: 1px solid #595959; border-radius: .5rem; padding: .75rem 1rem; margin: 1.5rem 0; }
        legend { font-weight: 600; padding: 0 .25rem; }
        .vendedor { display: block; padding: .25rem 0; }
        .acoes { display: flex; gap: .75rem; }
        button { font: inherit; padding: .5rem 1.25rem; border-radius: .375rem; border: 2px solid #1d4ed8; cursor: pointer; }
        button[value="authorize"] { background: #1d4ed8; color: #ffffff; }
        button[value="cancel"] { background: #ffffff; color: #1d4ed8; }
        button:focus-visible, input:focus-visible { outline: 3px solid #b45309; outline-offset: 2px; }
        code { background: #f0f0f0; padding: 0 .25rem; }
        """;

    /// <summary>Página 200 com a escolha do vendedor e os botões Autorizar/Cancelar.</summary>
    public static string Render(IReadOnlyList<SellerInfo> sellers, IReadOnlyDictionary<string, string?> parameters)
    {
        var body = new StringBuilder();
        body.Append("""
            <p class="aviso">Esta página <strong>não é o Mercado Livre</strong>: é um simulador local para desenvolvimento e testes.
            Nenhuma conta real é usada.</p>
            <form method="post" action="/authorization/decision">
            """);
        foreach (var name in ForwardedParameters)
        {
            body.Append(CultureInfo.InvariantCulture, $"""<input type="hidden" name="{name}" value="{Encode(parameters.GetValueOrDefault(name))}">""").Append('\n');
        }

        body.Append("""<fieldset><legend>Escolha o vendedor que vai autorizar o acesso</legend>""").Append('\n');
        for (var i = 0; i < sellers.Count; i++)
        {
            var seller = sellers[i];
            var id = seller.Id.ToString(CultureInfo.InvariantCulture);
            body.Append(CultureInfo.InvariantCulture, $"""
                <label class="vendedor" for="seller-{id}"><input type="radio" id="seller-{id}" name="seller_id" value="{id}"{(i == 0 ? " checked" : string.Empty)} required>
                {Encode(seller.Nickname)} <span>(id {id}, site {Encode(seller.Site.Id)})</span></label>
                """).Append('\n');
        }

        body.Append("""
            </fieldset>
            <div class="acoes">
            <button type="submit" name="decision" value="authorize">Autorizar</button>
            <button type="submit" name="decision" value="cancel">Cancelar</button>
            </div>
            </form>
            """);
        return Document(body.ToString());
    }

    /// <summary>Página 400 com o parâmetro recusado e o motivo (sem redirecionar).</summary>
    public static string RenderError(string parameter, string reason) =>
        Document($"""
            <p class="aviso">Pedido de autorização recusado pelo simulador.</p>
            <p>Parâmetro recusado: <code>{Encode(parameter)}</code>.</p>
            <p>{Encode(reason)}</p>
            """);

    private static string Document(string content) => $"""
        <!doctype html>
        <html lang="pt-BR">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{Encode(Title)}</title>
        <style>{Style}</style>
        </head>
        <body>
        <main>
        <h1>{Encode(Title)}</h1>
        {content}
        </main>
        </body>
        </html>
        """;

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
