namespace Vitrine.UnitTests.MercadoLivre;

/// <summary><see cref="TimeProvider"/> falso: o instante só muda quando o teste avança o relógio.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
