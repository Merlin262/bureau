using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace Bureau.Infra;

/// <summary>
/// Barramento de eventos em memória. Cada módulo publica seu estado por tópico;
/// cada cliente conectado (SSE) recebe na hora o último estado de todos os tópicos
/// e depois só as mudanças. Assim a interface nunca espera por uma requisição.
/// </summary>
public sealed class EventHub
{
    readonly ConcurrentDictionary<long, Channel<string>> _subs = new();
    readonly ConcurrentDictionary<string, string> _last = new();
    readonly ConcurrentDictionary<string, Func<string>> _replay = new();
    TaskCompletionSource _join = NewTcs();
    long _ids;

    public int Count => _subs.Count;

    /// <summary>Completa quando um novo cliente se conecta (coletores acordam e atualizam na hora).</summary>
    public Task NextJoin => Volatile.Read(ref _join).Task;

    public void Publish<T>(string topic, T value, JsonTypeInfo<T> type, bool dedupe = true)
    {
        var msg = $"event: {topic}\ndata: {JsonSerializer.Serialize(value, type)}\n\n";
        if (dedupe && _last.TryGetValue(topic, out var prev) && prev == msg) return;
        _last[topic] = msg;
        foreach (var ch in _subs.Values) ch.Writer.TryWrite(msg);
    }

    /// <summary>Tópico enviado apenas na conexão (ex.: histórico), gerado sob demanda.</summary>
    public void SetReplay(string topic, Func<string> factory) => _replay[topic] = factory;

    public Subscription Subscribe()
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        foreach (var (topic, factory) in _replay) ch.Writer.TryWrite($"event: {topic}\ndata: {factory()}\n\n");
        foreach (var msg in _last.Values) ch.Writer.TryWrite(msg);

        var id = Interlocked.Increment(ref _ids);
        _subs[id] = ch;
        Interlocked.Exchange(ref _join, NewTcs()).TrySetResult();
        return new Subscription(this, id, ch.Reader);
    }

    internal void Remove(long id)
    {
        if (_subs.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class Subscription(EventHub hub, long id, ChannelReader<string> reader) : IDisposable
{
    public ChannelReader<string> Reader => reader;
    public void Dispose() => hub.Remove(id);
}
