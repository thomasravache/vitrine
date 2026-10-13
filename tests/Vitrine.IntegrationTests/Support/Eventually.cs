namespace Vitrine.IntegrationTests.Support;

internal static class Eventually
{
    /// <summary>Espera, por polling, até a condição ser verdadeira; falha com <paramref name="because"/> no timeout.</summary>
    public static async Task TrueAsync(Func<bool> condition, TimeSpan timeout, string because, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        while (!condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Assert.Fail($"Condição não atendida em {timeout}: {because}");
            }
        }
    }
}
