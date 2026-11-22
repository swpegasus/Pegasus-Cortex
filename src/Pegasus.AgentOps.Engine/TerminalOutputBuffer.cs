using System.Text;

namespace Pegasus.AgentOps.Engine;

/// <summary>Host-lifetime RAM only. Cursors are absolute byte offsets, never read acknowledgements.</summary>
internal sealed class TerminalOutputBuffer(int capacity = 1024 * 1024)
{
    private readonly byte[] _bytes = new byte[capacity >= 4 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private readonly object _gate = new();
    private long _end;
    private bool _ended;
    internal void Append(byte[] data, int count)
    {
        lock (_gate)
        {
            for (var i = Math.Max(0, count - _bytes.Length); i < count; i++)
                _bytes[(_end + i) % _bytes.Length] = data[i];
            _end = checked(_end + count);
            Monitor.PulseAll(_gate);
        }
    }
    internal void End() { lock (_gate) { _ended = true; Monitor.PulseAll(_gate); } }

    internal IReadOnlyDictionary<string, object?> Read(long? cursor, int maximum, bool binary)
    {
        lock (_gate)
        {
            var earliest = Math.Max(0, _end - _bytes.Length);
            var requested = cursor ?? 0;
            if (requested > _end || requested < 0) throw new AgentOpsException("terminal_cursor_invalid");
            var start = Math.Max(requested, earliest);
            var gap = start != requested;
            var count = checked((int)Math.Min(maximum, _end - start));
            var data = new byte[count];
            for (var i = 0; i < count; i++) data[i] = _bytes[(start + i) % _bytes.Length];
            var skip = 0;
            if (!binary)
            {
                // A retained range may start after eviction in the middle of a scalar.
                while (skip < data.Length && (data[skip] & 0xc0) == 0x80) skip++;
                if (skip > 0) gap = true;
                if (!_ended) count -= IncompleteSuffix(data.AsSpan(skip));
            }
            var next = start + count;
            var result = new Dictionary<string, object?> { ["cursor"] = next };
            if (binary) result["base64"] = Convert.ToBase64String(data, 0, count);
            else
            {
                var part = data.AsSpan(skip, Math.Max(0, count - skip));
                try { result["text"] = new UTF8Encoding(false, true).GetString(part); }
                catch (DecoderFallbackException)
                { result["text"] = Encoding.UTF8.GetString(part); result["encodingLoss"] = true; }
            }
            if (gap) { result["gap"] = true; result["earliest"] = earliest; }
            if (next < _end) result["more"] = true;
            if (_ended) result["ended"] = true;
            return result;
        }
    }

    private static int IncompleteSuffix(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return 0;
        var index = bytes.Length - 1;
        while (index > 0 && (bytes[index] & 0xc0) == 0x80) index--;
        var lead = bytes[index];
        var length = lead is >= 0xc2 and <= 0xdf ? 2 : lead is >= 0xe0 and <= 0xef ? 3 : lead is >= 0xf0 and <= 0xf4 ? 4 : 1;
        return bytes.Length - index < length ? bytes.Length - index : 0;
    }
}
