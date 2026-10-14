using System.Buffers.Binary;

namespace Bureau.Modules;

/// <summary>
/// Histórico persistido em dois arquivos circulares de tamanho fixo (sem banco de dados):
/// metrics-1m.bin — 1 ponto por minuto, 7 dias · metrics-1h.bin — 1 ponto por hora, 90 dias.
/// Registro (32 bytes): ts(i64) + cpu, mem, temp, rx, tx, bateria (f32; NaN = sem dado).
/// </summary>
public sealed class MetricsStore
{
    const int Rec = 32;
    const int MinuteSlots = 7 * 24 * 60;
    const int HourSlots = 90 * 24;

    readonly string _minutePath, _hourPath;
    readonly Lock _lock = new();
    readonly double[] _sum = new double[6];
    readonly int[] _cnt = new int[6];
    long _minute = -1;

    public MetricsStore(string dataDir)
    {
        _minutePath = Path.Combine(dataDir, "metrics-1m.bin");
        _hourPath = Path.Combine(dataDir, "metrics-1h.bin");
    }

    /// <summary>Chamado a cada amostra (1 s). Fecha o minuto anterior e, na virada da hora, consolida a hora.</summary>
    public void Add(SystemSnapshot s, long? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var minute = now / 60;
        lock (_lock)
        {
            if (_minute >= 0 && minute != _minute)
            {
                Flush(_minute * 60);
                if (minute / 60 != _minute / 60) Consolidate(_minute / 60 * 3600);
            }
            _minute = minute;
            Acc(0, s.Cpu);
            Acc(1, s.MemTotal == 0 ? double.NaN : 100.0 * s.MemUsed / s.MemTotal);
            Acc(2, s.TempC ?? double.NaN);
            Acc(3, s.RxRate);
            Acc(4, s.TxRate);
            Acc(5, s.Battery?.Percent ?? double.NaN);
        }
    }

    void Acc(int i, double v)
    {
        if (double.IsNaN(v)) return;
        _sum[i] += v;
        _cnt[i]++;
    }

    void Flush(long ts)
    {
        var v = new float[6];
        for (var i = 0; i < 6; i++)
        {
            v[i] = _cnt[i] == 0 ? float.NaN : (float)(_sum[i] / _cnt[i]);
            _sum[i] = 0;
            _cnt[i] = 0;
        }
        Write(_minutePath, MinuteSlots, ts / 60 % MinuteSlots, ts, v);
    }

    /// <summary>Média da hora a partir dos minutos gravados (sobrevive a reinícios no meio da hora).</summary>
    void Consolidate(long hourTs)
    {
        var pts = ReadRange(_minutePath, MinuteSlots, hourTs, hourTs + 3599);
        if (pts.Count == 0) return;
        var v = new float[6];
        for (var i = 0; i < 6; i++)
        {
            var vals = pts.Select(p => p.V[i]).Where(x => !float.IsNaN(x)).ToArray();
            v[i] = vals.Length == 0 ? float.NaN : vals.Average();
        }
        Write(_hourPath, HourSlots, hourTs / 3600 % HourSlots, hourTs, v);
    }

    static void Write(string path, int slots, long slot, long ts, float[] v)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (fs.Length < (long)slots * Rec) fs.SetLength((long)slots * Rec);
            Span<byte> buf = stackalloc byte[Rec];
            BinaryPrimitives.WriteInt64LittleEndian(buf, ts);
            for (var i = 0; i < 6; i++) BinaryPrimitives.WriteSingleLittleEndian(buf[(8 + i * 4)..], v[i]);
            fs.Position = slot * Rec;
            fs.Write(buf);
        }
        catch { /* disco cheio/sem permissão: só não grava o histórico */ }
    }

    readonly record struct Point(long Ts, float[] V);

    static List<Point> ReadRange(string path, int slots, long from, long to)
    {
        var list = new List<Point>();
        try
        {
            if (!File.Exists(path)) return list;
            var bytes = File.ReadAllBytes(path);
            for (var off = 0; off + Rec <= bytes.Length && off < slots * Rec; off += Rec)
            {
                var ts = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(off));
                if (ts < from || ts > to) continue;
                var v = new float[6];
                for (var i = 0; i < 6; i++) v[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(off + 8 + i * 4));
                list.Add(new Point(ts, v));
            }
        }
        catch { /* arquivo em escrita */ }
        list.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        return list;
    }

    /// <summary>Série para o período pedido, reduzida a no máximo ~360 pontos (médias por intervalo).</summary>
    public MetricsSeries Query(string range)
    {
        var (seconds, hourly) = range switch
        {
            "1h" => (3600L, false),
            "6h" => (6 * 3600L, false),
            "7d" => (7 * 86400L, true),
            "30d" => (30 * 86400L, true),
            "90d" => (90 * 86400L, true),
            _ => (86400L, false),
        };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        List<Point> pts;
        lock (_lock)
            pts = ReadRange(hourly ? _hourPath : _minutePath, hourly ? HourSlots : MinuteSlots, now - seconds, now);

        var bucket = Math.Max(hourly ? 3600 : 60, seconds / 360);
        var groups = pts.GroupBy(p => p.Ts / bucket).OrderBy(g => g.Key).ToArray();
        double? Avg(IEnumerable<Point> g, int i)
        {
            var vals = g.Select(p => p.V[i]).Where(x => !float.IsNaN(x)).ToArray();
            return vals.Length == 0 ? null : Math.Round(vals.Average(), i is 3 or 4 ? 0 : 1);
        }
        return new MetricsSeries(range, bucket,
            [.. groups.Select(g => g.Key * bucket)],
            [.. groups.Select(g => Avg(g, 0))], [.. groups.Select(g => Avg(g, 1))], [.. groups.Select(g => Avg(g, 2))],
            [.. groups.Select(g => Avg(g, 3))], [.. groups.Select(g => Avg(g, 4))], [.. groups.Select(g => Avg(g, 5))]);
    }
}
