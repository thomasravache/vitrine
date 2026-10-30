#:package Aspire.Hosting.Testing
#:project ../../../src/AppHost/Vitrine.AppHost.csproj
#:property PublishAot=false

// AppHostFixture (SPEC-0003): sobe a aplicação inteira pelo AppHost com Aspire.Hosting.Testing — o mesmo
// AppHost do desenvolvimento —, espera a SPA responder, escreve "ML_SIMULATOR_URL=<url>" (simulador do Mercado
// Livre, SPEC-0024) e depois "BASE_URL=<url>" na saída padrão e mantém tudo no ar até a entrada padrão fechar.
// Iniciado por app-host-fixture.ts (global-setup do Playwright).
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var cancellationToken = startup.Token;

var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Vitrine_AppHost>(cancellationToken);
// Só avisos e erros dos recursos na saída do E2E; os logs completos ficam no dashboard do `aspire run`.
builder.Services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
await using var app = await builder.BuildAsync(cancellationToken);
await app.StartAsync(cancellationToken);

await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken);
await app.ResourceNotifications.WaitForResourceHealthyAsync("ml-simulator", cancellationToken);
await app.ResourceNotifications.WaitForResourceAsync("web", KnownResourceStates.Running, cancellationToken);

var baseUrl = app.GetEndpoint("web", "http");
using (var http = new HttpClient { BaseAddress = baseUrl })
{
    // "Running" significa processo iniciado; espera o servidor da SPA responder de fato.
    while (true)
    {
        try
        {
            using var response = await http.GetAsync(new Uri("/", UriKind.Relative), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                break;
            }
        }
        catch (HttpRequestException)
        {
        }

        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
    }
}

// ML_SIMULATOR_URL vem antes: BASE_URL é o sinal de pronto que o Playwright espera.
Console.WriteLine($"ML_SIMULATOR_URL={app.GetEndpoint("ml-simulator", "http").ToString().TrimEnd('/')}");
Console.WriteLine($"BASE_URL={baseUrl.ToString().TrimEnd('/')}");

// Entrada padrão fechada (fim do E2E ou processo pai encerrado) → derruba a aplicação.
await Console.In.ReadToEndAsync(CancellationToken.None);
await app.StopAsync(CancellationToken.None);
