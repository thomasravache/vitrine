using System.Reflection;
using Vitrine.Api.Security;

namespace Vitrine.Api;

/// <summary>
/// Modo de geração do documento OpenAPI no build (SPEC-0011): o GetDocument.Insider sobe o host para descrever os
/// endpoints (`npm run api:generate` e o passo de drift do CI), sem banco, mensageria nem chave mestra, em qualquer
/// ambiente.
/// </summary>
public static class OpenApiGeneration
{
    /// <summary>Nome do assembly de entrada do processo que gera o documento (Microsoft.Extensions.ApiDescription.Server).</summary>
    public const string DocumentGeneratorAssemblyName = "GetDocument.Insider";

    /// <summary>Verdadeiro só quando o processo é o gerador do documento OpenAPI.</summary>
    public static bool IsGeneratingDocument(Assembly? entryAssembly) =>
        string.Equals(entryAssembly?.GetName().Name, DocumentGeneratorAssemblyName, StringComparison.Ordinal);

    /// <summary>
    /// Registra a segurança de base do host (<see cref="SecurityExtensions.AddSecurity"/>) e, na geração do documento,
    /// desliga a validação do contêiner no build: sem o Wolverine, serviços dos módulos que dependem dele (ex.:
    /// IDbContextOutbox) não resolvem, e a validação (ligada em Development) reprovaria um host que só descreve os
    /// endpoints. Fora da geração nada muda: a chave mestra continua obrigatória fora de Development.
    /// </summary>
    public static WebApplicationBuilder Configure(WebApplicationBuilder builder, bool generatingOpenApi)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (generatingOpenApi)
        {
            builder.Host.UseDefaultServiceProvider(options => options.ValidateOnBuild = false);
        }

        return builder.AddSecurity(generatingOpenApi);
    }
}
