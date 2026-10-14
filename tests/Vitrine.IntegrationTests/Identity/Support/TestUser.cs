namespace Vitrine.IntegrationTests.Identity.Support;

/// <summary>Dados sintéticos de cadastro; cada teste cria os seus (e-mail único).</summary>
internal sealed record TestUser(string Name, string Email, string Password)
{
    public const string DefaultPassword = "senha-forte-2026";

    public static TestUser New(string name = "Marina Souza") =>
        new(name, $"vendedor.{Guid.NewGuid():N}@example.com", DefaultPassword);
}

/// <summary>Resposta 201 de <c>POST /api/v1/auth/register</c>.</summary>
internal sealed record RegisteredAccount(Guid UserId, Guid WorkspaceId);
