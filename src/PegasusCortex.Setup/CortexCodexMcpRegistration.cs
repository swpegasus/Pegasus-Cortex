using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pegasus.Cortex.Setup;

public sealed record CortexCodexMcpRegistrationReceiptV2(
    string Contract, int Version, string Target, string Disposition, string RegistrationName,
    string Url, bool LegacyAliasRemoved, bool LegacyPluginDisabled, bool LegacyPluginChanged,
    string OriginalSha256, string ResultSha256, int ResultBytes);

internal sealed record CortexCodexMcpRegistrationPlan(
    CortexCodexFileUpdate Update, string Url, bool LegacyAliasRemoved, bool LegacyPluginDisabled, bool LegacyPluginChanged)
{
    internal CortexCodexMcpRegistrationReceiptV2 Apply()
    {
        Update.Apply();
        return new("pegasus-cortex-codex-mcp-registration", 2, Update.Target, Update.Disposition,
            CortexCodexMcpRegistration.RegistrationName, Url, LegacyAliasRemoved, LegacyPluginDisabled, LegacyPluginChanged,
            Update.OriginalSha256, Update.ResultSha256, Update.Result.Length);
    }
}

internal static class CortexCodexMcpRegistration
{
    internal const string RegistrationName = "Cortex";
    internal const string LegacyRegistrationName = "pegasus_cortex";
    internal const string LegacyPluginId = "pegasus-cortex-client@pegasus-suite";
    internal const string DefaultSharedEndpoint = "https://localhost:47831/mcp/cortex";
    private const int MaximumConfigBytes = 1_048_576;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Statement(int Start, int End, string Text);
    private sealed record Edit(int Start, int Length, string Text);
    private sealed record Composition(string Text, bool LegacyAliasRemoved, bool LegacyPluginDisabled, bool LegacyPluginChanged);
    private sealed class Route(string name)
    {
        internal string Name { get; } = name;
        internal int Start = -1, End = -1;
        internal readonly Dictionary<string, (Statement Statement, string Value)> Fields = new(StringComparer.Ordinal);
        internal bool Present => Start >= 0 || Fields.Count > 0;
    }

    internal static CortexCodexMcpRegistrationPlan Prepare(string configPath, string installRoot,
        string sharedEndpoint = DefaultSharedEndpoint) => PrepareFile(configPath, installRoot, sharedEndpoint, install: true);
    internal static CortexCodexMcpRegistrationPlan PrepareRemove(string configPath, string installRoot,
        string sharedEndpoint = DefaultSharedEndpoint) => PrepareFile(configPath, installRoot, sharedEndpoint, install: false);

    private static CortexCodexMcpRegistrationPlan PrepareFile(string configPath, string installRoot, string endpoint, bool install)
    {
        var target = CortexCodexFileUpdate.RequireTarget(configPath, "config.toml");
        var existed = File.Exists(target);
        var bytes = CortexCodexFileUpdate.ReadBounded(target, MaximumConfigBytes);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var input = Utf8.GetString(bytes.AsSpan(hasBom ? Encoding.UTF8.Preamble.Length : 0));
        var composed = Build(input, installRoot, endpoint, install);
        var payload = Utf8.GetBytes(composed.Text);
        byte[] result = hasBom ? [.. Encoding.UTF8.Preamble, .. payload] : payload;
        if (result.Length > MaximumConfigBytes) throw InvalidConfig();
        return new(new(target, existed, bytes, result), RequireEndpoint(endpoint).AbsoluteUri, composed.LegacyAliasRemoved,
            composed.LegacyPluginDisabled, composed.LegacyPluginChanged);
    }

    internal static (string Text, bool LegacyAliasRemoved, bool LegacyPluginDisabled, bool LegacyPluginChanged) Compose(
        string input, string installRoot, string sharedEndpoint = DefaultSharedEndpoint)
    {
        var result = Build(input, installRoot, sharedEndpoint, install: true);
        return (result.Text, result.LegacyAliasRemoved, result.LegacyPluginDisabled, result.LegacyPluginChanged);
    }
    internal static string Remove(string input, string installRoot, string sharedEndpoint = DefaultSharedEndpoint) =>
        Build(input, installRoot, sharedEndpoint, install: false).Text;

    private static Composition Build(string input, string installRoot, string endpoint, bool install)
    {
        if (Utf8.GetByteCount(input) > MaximumConfigBytes) throw new InvalidDataException("The Codex config is oversized.");
        var expected = RequireEndpoint(endpoint);
        var root = CortexCodexFileUpdate.RequireCanonical(installRoot);
        var legacyCommands = new[] { Path.Combine(root, "PegasusCortex.exe"), Path.Combine(root, "bin", "semantics", "PegasusCortex.exe") };
        var newline = input.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var shared = new Route(RegistrationName); var legacy = new Route(LegacyRegistrationName);
        var edits = new List<Edit>();
        string[] table = [];
        var pluginPresent = false; var pluginEnabledSeen = false; var pluginDisabled = false;
        var pluginChanged = false; var pluginInsert = input.Length;
        foreach (var statement in Statements(input))
        {
            var text = statement.Text.Trim();
            if (text.StartsWith('['))
            {
                var array = text.StartsWith("[[", StringComparison.Ordinal);
                var trim = array ? 2 : 1;
                if (!text.EndsWith(array ? "]]" : "]", StringComparison.Ordinal)) throw InvalidConfig();
                var next = Keys(text[trim..^trim]);
                if (table.SequenceEqual(["plugins", LegacyPluginId])) pluginInsert = statement.Start;
                table = next;
                foreach (var route in new[] { shared, legacy })
                    if (Prefix(table, ["mcp_servers", route.Name]))
                    {
                        if (array || table.Length != 2 || route.Present) throw InvalidConfig();
                        route.Start = statement.Start; route.End = statement.End;
                    }
                if (table.SequenceEqual(["plugins", LegacyPluginId]))
                {
                    if (array || pluginPresent) throw InvalidConfig();
                    pluginPresent = true;
                }
                continue;
            }
            var equals = FindEquals(text);
            if (equals < 1) throw InvalidConfig();
            var keys = table.Concat(Keys(text[..equals])).ToArray();
            var value = text[(equals + 1)..].Trim();
            foreach (var route in new[] { shared, legacy })
            {
                if (Prefix(["mcp_servers", route.Name], keys)) throw InvalidConfig();
                if (!Prefix(keys, ["mcp_servers", route.Name])) continue;
                if (keys.Length != 3 || !route.Fields.TryAdd(keys[2], (statement, value))) throw InvalidConfig();
                if (route.Start >= 0) route.End = statement.End;
                ValidateRouteField(keys[2], value, route == shared);
            }
            if (install && keys is ["mcp_servers", var alias, "command"] &&
                alias is not (RegistrationName or LegacyRegistrationName) &&
                string.Equals(Path.GetFileName(StringValue(value)), "PegasusCortex.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An unrecognized Cortex MCP alias already exists; resolve it explicitly before registration.");
            if (install && keys is ["mcp_servers", var urlAlias, "url"] &&
                urlAlias is not (RegistrationName or LegacyRegistrationName) && EndpointMatches(StringValue(value), expected))
                throw new InvalidDataException("The shared Cortex endpoint is registered under another alias; resolve it explicitly before registration.");
            if (Prefix(["plugins", LegacyPluginId], keys)) throw InvalidConfig();
            if (keys.SequenceEqual(["plugins", LegacyPluginId, "enabled"]))
            {
                if (pluginEnabledSeen || value is not ("true" or "false")) throw InvalidConfig();
                pluginPresent = true; pluginEnabledSeen = true; pluginDisabled = value == "false";
                if (install && !pluginDisabled) { edits.Add(ValueEdit(input, statement, "false")); pluginChanged = true; }
            }
        }
        if (shared.Present && (!shared.Fields.TryGetValue("url", out var url) || !EndpointMatches(StringValue(url.Value), expected)))
            throw new InvalidDataException("The existing Cortex alias is not the selected shared HTTPS endpoint.");
        if (legacy.Present && (!legacy.Fields.TryGetValue("command", out var command) ||
            !legacy.Fields.TryGetValue("args", out var arguments) || SingleArgument(arguments.Value) != "stdio" ||
            !legacyCommands.Contains(StringValue(command.Value), StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("The existing pegasus_cortex alias is not an exact managed legacy Cortex stdio route.");

        // Legacy stdio is recognized for exact removal only. It is never emitted,
        // enabled, upgraded to another executable or retained as a fallback.
        RemoveRoute(legacy, edits);
        if (!install) RemoveRoute(shared, edits);
        if (install && pluginPresent && !pluginEnabledSeen)
        {
            var separator = pluginInsert > 0 && input[pluginInsert - 1] is not ('\r' or '\n') ? newline : "";
            edits.Add(new(pluginInsert, 0, separator + "enabled = false" + newline)); pluginChanged = true;
        }
        var result = input;
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
            result = result[..edit.Start] + edit.Text + result[(edit.Start + edit.Length)..];
        if (install && !shared.Present)
        {
            if (result.Length > 0 && !result.EndsWith(newline, StringComparison.Ordinal)) result += newline;
            if (result.Length > 0 && !result.EndsWith(newline + newline, StringComparison.Ordinal)) result += newline;
            result += "[mcp_servers." + RegistrationName + "]" + newline +
                "url = " + JsonSerializer.Serialize(expected.AbsoluteUri) + newline + "enabled = true" + newline;
        }
        if (Utf8.GetByteCount(result) > MaximumConfigBytes) throw InvalidConfig();
        return new(result, legacy.Present, pluginPresent && (install || pluginDisabled), pluginChanged);
    }

    private static void RemoveRoute(Route route, List<Edit> edits)
    {
        if (!route.Present) return;
        if (route.Start >= 0) edits.Add(new(route.Start, route.End - route.Start, ""));
        else foreach (var field in route.Fields.Values) edits.Add(new(field.Statement.Start, field.Statement.End - field.Statement.Start, ""));
    }
    private static void ValidateRouteField(string name, string value, bool shared)
    {
        if (name == "enabled") { if (value is not ("true" or "false")) throw InvalidConfig(); return; }
        if (name is "startup_timeout_sec" or "tool_timeout_sec" or "required" or "enabled_tools" or "disabled_tools" or "default_tools_approval_mode") return;
        if (shared && name == "url" || !shared && name is ("command" or "args")) return;
        throw InvalidConfig();
    }
    private static Uri RequireEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsLoopback ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/mcp/cortex")
            throw new InvalidDataException("The shared Cortex registration requires an explicit loopback HTTPS Cortex endpoint.");
        return uri;
    }
    private static bool EndpointMatches(string value, Uri expected) =>
        Uri.TryCreate(value, UriKind.Absolute, out var existing) && existing.Scheme == Uri.UriSchemeHttps && existing.IsLoopback &&
        existing.UserInfo.Length == 0 && existing.Query.Length == 0 && existing.Fragment.Length == 0 &&
        existing.AbsolutePath == "/mcp/cortex" && existing.Equals(expected);

    private static bool Prefix(string[] path, string[] prefix) => path.Length >= prefix.Length &&
        path.Take(prefix.Length).SequenceEqual(prefix);
    private static InvalidDataException InvalidConfig() => new("The Codex configuration contains conflicting or ambiguous integration coordinates.");

    private static Edit ValueEdit(string input, Statement statement, string replacement)
    {
        var raw = input[statement.Start..statement.End];
        var equals = FindEquals(raw);
        var start = equals + 1;
        while (start < raw.Length && char.IsWhiteSpace(raw[start])) start++;
        // The scanner removes comments from Text while retaining the exact
        // value prefix. Disable only the exact legacy plugin boolean, leaving
        // trailing whitespace and comments byte-for-byte.
        var length = statement.Text.TrimEnd().Length - start;
        return new(statement.Start + start, length, replacement);
    }

    private static int FindEquals(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == '\\' && quote == '"') { index++; continue; }
                if (character == quote) quote = '\0';
            }
            else if (character is '"' or '\'') quote = character;
            else if (character == '=') return index;
        }
        return -1;
    }

    private static string[] Keys(string value)
    {
        var keys = new List<string>(); var start = 0; var quote = '\0';
        for (var index = 0; index <= value.Length; index++)
        {
            if (index == value.Length || quote == '\0' && value[index] == '.')
            {
                var key = value[start..index].Trim();
                keys.Add(key.StartsWith('"') || key.StartsWith('\'') ? StringValue(key) :
                    Regex.IsMatch(key, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant) ? key : throw InvalidConfig());
                start = index + 1;
            }
            else if (quote != '\0')
            {
                if (value[index] == '\\' && quote == '"') index++;
                else if (value[index] == quote) quote = '\0';
            }
            else if (value[index] is '"' or '\'') quote = value[index];
        }
        if (quote != '\0') throw InvalidConfig();
        return keys.ToArray();
    }

    private static string StringValue(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' && !value[1..^1].Contains('\'')) return value[1..^1];
        try
        {
            if (value.StartsWith('"') && value.EndsWith('"')) return JsonSerializer.Deserialize<string>(value) ?? throw InvalidConfig();
        }
        catch (JsonException) { throw InvalidConfig(); }
        throw InvalidConfig();
    }

    private static string SingleArgument(string value)
    {
        if (!value.StartsWith('[') || !value.EndsWith(']')) throw InvalidConfig();
        var argument = value[1..^1].Trim();
        if (argument.EndsWith(',')) argument = argument[..^1].TrimEnd();
        return StringValue(argument);
    }

    // This lexical scanner recognizes statement boundaries without interpreting
    // or rewriting unrelated TOML values, including arrays and multiline strings.
    private static IReadOnlyList<Statement> Statements(string input)
    {
        var statements = new List<Statement>(); var text = new StringBuilder();
        var start = -1; var depth = 0; var quote = '\0'; var triple = false; var comment = false;
        for (var index = 0; index <= input.Length; index++)
        {
            var character = index == input.Length ? '\n' : input[index];
            if (comment)
            {
                if (character is not ('\r' or '\n')) continue;
                comment = false;
            }
            if (quote != '\0')
            {
                text.Append(character);
                if (character == '\\' && quote == '"' && index + 1 < input.Length) { text.Append(input[++index]); continue; }
                if (character == quote)
                {
                    if (!triple) quote = '\0';
                    else if (index + 2 < input.Length && input[index + 1] == quote && input[index + 2] == quote)
                    { text.Append(input[++index]); text.Append(input[++index]); quote = '\0'; triple = false; }
                }
                continue;
            }
            if (character == '#') { comment = true; continue; }
            if (character is '\n' or '\r' && depth == 0)
            {
                if (start >= 0) { statements.Add(new(start, index, text.ToString())); text.Clear(); start = -1; }
                continue;
            }
            if (start < 0 && char.IsWhiteSpace(character)) continue;
            if (start < 0) start = index;
            text.Append(character);
            if (character is '"' or '\'')
            {
                quote = character;
                triple = index + 2 < input.Length && input[index + 1] == quote && input[index + 2] == quote;
                if (triple) { text.Append(input[++index]); text.Append(input[++index]); }
            }
            else if (character is '[' or '{') depth++;
            else if (character is ']' or '}') { if (--depth < 0) throw InvalidConfig(); }
        }
        if (quote != '\0' || depth != 0) throw InvalidConfig();
        return statements;
    }
}
