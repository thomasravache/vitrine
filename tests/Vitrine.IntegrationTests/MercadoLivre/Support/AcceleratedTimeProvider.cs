using System.Collections.Concurrent;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>
/// <see cref="TimeProvider"/> de teste registrado no host: o relógio é o real, mas todo timer de
/// <see cref="Threshold"/> ou mais corre <see cref="Factor"/> vezes mais rápido (ex.: 30 s viram 1,5 s), e cada prazo
/// pedido (na criação ou em <c>Change</c>) é registrado em <see cref="Requested"/>. Prova esperas e timeouts longos do
/// pipeline de resiliência (que usa o <see cref="TimeProvider"/> do DI) sem esperá-los de verdade.
/// </summary>
internal sealed class AcceleratedTimeProvider : TimeProvider
{
    public const int Factor = 20;

    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<TimeSpan> _requested = new();

    /// <summary>Prazos pedidos aos timers (sem aceleração), em ordem.</summary>
    public IReadOnlyList<TimeSpan> Requested => [.. _requested];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Record(dueTime);
        return new AcceleratedTimer(this, System.CreateTimer(callback, state, Scale(dueTime), Scale(period)));
    }

    public void ForgetRequested() => _requested.Clear();

    private void Record(TimeSpan dueTime)
    {
        if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            _requested.Enqueue(dueTime);
        }
    }

    private static TimeSpan Scale(TimeSpan value) =>
        value >= Threshold && value != Timeout.InfiniteTimeSpan ? value / Factor : value;

    private sealed class AcceleratedTimer(AcceleratedTimeProvider owner, ITimer inner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Record(dueTime);
            return inner.Change(Scale(dueTime), Scale(period));
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
