using DrawnUi.Draw;

namespace DrawnChatList;

/// <summary>
/// Simulated remote chat API: owns the message history (as a server would) and serves ascending ranges
/// with latency. Implements the lib <see cref="IWindowDataSource{T}"/> so <c>WindowedSource</c> never holds
/// the full list — it pages from here on demand, and a loading spinner shows while a fetch is in flight.
/// </summary>
public sealed class MockChatService : IWindowDataSource<ChatMessage>
{
    private readonly List<ChatMessage> _all = new();
    private readonly int _latencyMs;

    public MockChatService(int total, int latencyMs)
    {
        _latencyMs = latencyMs;
        for (int i = 0; i < total; i++)
            _all.Add(ChatMessage.CreateMock(i));
    }

    public int Count => _all.Count;

    public async Task<int> GetCountAsync(CancellationToken cancel = default)
    {
        if (_latencyMs > 0) await Task.Delay(_latencyMs, cancel);
        return _all.Count;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetRangeAsync(int from, int count, CancellationToken cancel = default)
    {
        if (_latencyMs > 0) await Task.Delay(_latencyMs, cancel);
        return _all.GetRange(from, count); // ascending global order; WindowedSource owns the inversion
    }

    /// <summary>The cached instance at a global index (e.g. to flag highlight). Null if out of range.</summary>
    public ChatMessage At(int index) => index >= 0 && index < _all.Count ? _all[index] : null;

    /// <summary>Append a newly sent/received message (live). It becomes part of future range fetches.</summary>
    public void Append(ChatMessage message) => _all.Add(message);
}
