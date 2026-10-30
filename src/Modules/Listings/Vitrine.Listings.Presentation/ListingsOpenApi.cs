using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Vitrine.Listings.Presentation;

/// <summary>Ajustes do documento OpenAPI dos endpoints do Listings (contrato da SPEC-0022, emenda v1.3).</summary>
internal static class ListingsOpenApi
{
    /// <summary>
    /// <c>accountId</c> e <c>limit</c> chegam como texto (o valor inválido vira 400 <c>VALIDATION_FAILED</c>, não erro de
    /// binding), mas o contrato documenta uuid e inteiro.
    /// </summary>
    public static Task DocumentQueryTypes(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
        {
            parameter.Schema = parameter.Name switch
            {
                "accountId" => new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                "limit" => new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
                _ => parameter.Schema,
            };
        }

        return Task.CompletedTask;
    }

    /// <summary>O corpo de <c>POST /sync</c> é opcional (ausente = todas as contas do workspace).</summary>
    public static Task OptionalRequestBody(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.RequestBody is OpenApiRequestBody body)
        {
            body.Required = false;
        }

        return Task.CompletedTask;
    }
}
