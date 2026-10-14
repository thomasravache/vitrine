using Vitrine.IntegrationTests.Support;
using Wolverine;

namespace Vitrine.IntegrationTests.Identity.Support;

/// <summary>Host da Api para os testes do módulo Identity (SPEC-0006).</summary>
internal static class IdentityHost
{
    /// <summary>
    /// Api sobre o banco do teste com <c>Features:SignupOpen</c> definido e o assinante de teste de
    /// <c>WorkspaceCreated</c> (<see cref="WorkspaceCreatedTestHandler"/>) descoberto pelo Wolverine.
    /// </summary>
    public static ApiFactory Create(string connectionString, bool signupOpen = true) =>
        new ApiFactory(connectionString)
            .WithSetting("Features:SignupOpen", signupOpen ? "true" : "false")
            .WithServices(services => services.ConfigureWolverine(options =>
                options.Discovery.IncludeAssembly(typeof(WorkspaceCreatedTestHandler).Assembly)));
}
