using System.Text.Json;
using Bureau.Infra;

namespace Bureau.Modules;

/// <summary>Amostra CPU/RAM/rede/temperatura a cada segundo e mantém 2 minutos de histórico.</summary>
public sealed class SystemMonitor(EventHub hub, AlertEngine alerts, MetricsStore metrics, ILogger<SystemMonitor> log) : BackgroundService
{
    const int HistoryLength = 120;
    readonly Ring _cpu = new(HistoryLength), _mem = new(HistoryLength), _rx = new(HistoryLength), _tx = new(HistoryLength);
    readonly Lock _lock = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        ISystemProbe probe = OperatingSystem.IsLinux() ? new LinuxProbe() : new FakeProbe();
        hub.SetReplay("history", () =>
        {
            lock (_lock)
                return JsonSerializer.Serialize(
                    new MetricsHistory(_cpu.ToArray(), _mem.ToArray(), _rx.ToArray(), _tx.ToArray()),
                    AppJson.Default.MetricsHistory);
        });

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tick = 0;
        do
        {
            try
            {
                if (tick % 30 == 0)
                {
                    var host = probe.ReadHost();
                    hub.Publish("host", host, AppJson.Default.HostInfo);
                    alerts.OnHost(host);
                }
                var s = probe.Sample();
                lock (_lock)
                {
                    _cpu.Add(s.Cpu);
                    _mem.Add(s.MemTotal == 0 ? 0 : Math.Round(100.0 * s.MemUsed / s.MemTotal, 1));
                    _rx.Add(s.RxRate);
                    _tx.Add(s.TxRate);
                }
                hub.Publish("sys", s, AppJson.Default.SystemSnapshot, dedupe: false);
                alerts.OnSystem(s);
                metrics.Add(s);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Falha ao amostrar o sistema");
            }
            tick++;
        } while (await timer.WaitForNextTickAsync(ct));
    }

    sealed class Ring(int size)
    {
        readonly double[] _buf = new double[size];
        int _next, _count;

        public void Add(double v)
        {
            _buf[_next] = v;
            _next = (_next + 1) % size;
            if (_count < size) _count++;
        }

        public double[] ToArray()
        {
            var r = new double[_count];
            for (var k = 0; k < _count; k++) r[k] = _buf[(_next - _count + k + size) % size];
            return r;
        }
    }
}

public interface ISystemProbe
{
    SystemSnapshot Sample();
    HostInfo ReadHost();
}
