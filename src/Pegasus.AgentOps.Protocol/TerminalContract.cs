using System.Globalization;
using System.Text;

namespace Pegasus.AgentOps.Protocol;

/// <summary>Local terminal v1. Terminal payloads are never durable job requests or receipts.</summary>
public static class TerminalContract
{
    public const int MaxInputBytes = 4096;
    public static readonly string[] Operations =
        ["list", "connect", "status", "disconnect", "reconnect", "close", "read", "send", "input", "recall", "repeat", "resize", "interrupt", "screen", "release", "input_status"];

    public static string[] Fields(string operation) => operation switch
    {
        "list" => ["op", "rid"],
        "connect" => ["op", "name", "cwd", "ws", "width", "height", "rid"],
        "read" => ["op", "sid", "cursor", "max", "fmt", "rid"],
        "input_status" => ["op", "sid", "iid", "original_rid", "rid"],
        "send" or "input" => ["op", "sid", "rid"],
        "repeat" => ["op", "sid", "cid", "rid"],
        "resize" => ["op", "sid", "width", "height", "rid"],
        _ => ["op", "sid", "rid"]
    };

    public static bool HasBody(string operation) => operation is "send" or "input";
    public static bool Writes(string operation) => operation is "connect" or "send" or "input" or "repeat" or "interrupt" or "resize";

    public static bool Validate(IReadOnlyDictionary<string, string> arguments, string? body, bool requireBody = true)
    {
        var op = arguments.GetValueOrDefault("op");
        if (op is null || !Operations.Contains(op, StringComparer.Ordinal)) return false;
        var allowed = Fields(op);
        if (arguments.Keys.Any(key => key != OperationRequest.BodyOmittedKey && !allowed.Contains(key, StringComparer.Ordinal))) return false;
        if (op == "connect")
        {
            var name = arguments.GetValueOrDefault("name");
            if (name is null || name.Length is < 1 or > 64 ||
                name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) return false;
        }
        else if (op != "list" && !Handle(arguments.GetValueOrDefault("sid"))) return false;
        if (op == "repeat" && !Id(arguments.GetValueOrDefault("cid"))) return false;
        if (arguments.ContainsKey("iid") && arguments.ContainsKey("original_rid")) return false;
        if (arguments.TryGetValue("iid", out var iid) && !Id(iid)) return false;
        if (arguments.TryGetValue("original_rid", out var originalRid) && (originalRid.Length is < 1 or > 128 || originalRid.Any(char.IsControl))) return false;
        if (arguments.TryGetValue("rid", out var rid) && (rid.Length is < 1 or > 128 || rid.Any(char.IsControl))) return false;
        if (!Number(arguments, "width", 1, 512) || !Number(arguments, "height", 1, 200) ||
            !Number(arguments, "max", 4, 65536)) return false;
        if (op == "resize" && (!arguments.ContainsKey("width") || !arguments.ContainsKey("height"))) return false;
        if (arguments.TryGetValue("cursor", out var cursor) &&
            (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)) return false;
        if (arguments.TryGetValue("fmt", out var format) && format is not ("text" or "base64")) return false;
        if (!HasBody(op)) return body is null;
        if (body is null) return !requireBody;
        try
        {
            if (body.Length == 0 || new UTF8Encoding(false, true).GetByteCount(body) > MaxInputBytes || body.Contains('\0')) return false;
        }
        catch (EncoderFallbackException) { return false; }
        // send deliberately remembers NONSECRET single-line shell text. input is
        // unremembered interactive input; it must not become command history.
        return op != "send" || body.IndexOfAny(['\r', '\n']) < 0 && !body.Any(c => char.IsControl(c) && c != '\t');
    }

    public static bool Id(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool Handle(string? value) => value is { Length: 65 } && value[32] == ':' && Id(value[..32]) && Id(value[33..]);
    private static bool Number(IReadOnlyDictionary<string, string> values, string key, int minimum, int maximum) =>
        !values.TryGetValue(key, out var raw) || int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
        value >= minimum && value <= maximum;
}
