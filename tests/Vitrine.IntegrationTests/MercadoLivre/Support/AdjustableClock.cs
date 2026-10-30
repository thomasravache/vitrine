namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>
/// <see cref="TimeProvider"/> falso registrado no host: começa no instante real e só avança quando o teste pede
/// (ex.: vencer a autorização pendente de 10 minutos sem esperar).
/// </summary>
internal sealed class AdjustableClock : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _offsetTicks, delta.Ticks);
}
