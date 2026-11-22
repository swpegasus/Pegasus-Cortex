using Pegasus.AgentOps.Windows.Registry;

namespace Pegasus.AgentOps.Windows.Registry.Tests;

// Portable in-memory fixture. It never constructs the Windows implementation,
// calls a Windows API, opens a registry key or persists a registry body.
internal sealed class FixtureRegistry : IRegistryNative
{
    private sealed class KeyState
    {
        internal long Generation;
        internal Dictionary<string, RegistryNativeValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, KeyState> _keys = new(StringComparer.OrdinalIgnoreCase);
    private long _generation = 1;
    internal int Calls { get; private set; }
    internal int Writes { get; private set; }
    internal int NamesRead { get; private set; }
    internal int LargestReadBound { get; private set; }
    internal Action<string>? BeforeCall { get; set; }
    internal Action<string>? AfterWrite { get; set; }

    internal void SeedKey(RegistryKeyIdentity key)
    {
        if (key.KeyPath.Length != 0)
        {
            var parent = Parent(key);
            if (!_keys.ContainsKey(Id(parent))) SeedKey(parent);
            _keys[Id(parent)].Generation = ++_generation;
        }
        _keys[Id(key)] = new KeyState { Generation = ++_generation };
    }

    internal void SeedValue(RegistryValueIdentity identity, RegistryValueType type, byte[] data)
    {
        if (!_keys.ContainsKey(Id(identity.Key))) SeedKey(identity.Key);
        var state = _keys[Id(identity.Key)];
        state.Values[identity.ValueName] = new(type, (byte[])data.Clone());
        state.Generation = ++_generation;
    }

    internal void Touch(RegistryKeyIdentity key) => _keys[Id(key)].Generation = ++_generation;

    public RegistryNativeKeyInfo? InspectKey(RegistryKeyIdentity key)
    {
        Call("inspect");
        return _keys.TryGetValue(Id(key), out var state)
            ? new(state.Generation, ChildPaths(key).Count, state.Values.Count) : null;
    }

    public RegistryNativeValue? ReadValue(RegistryValueIdentity identity, int maxBytes)
    {
        Call("read");
        LargestReadBound = Math.Max(LargestReadBound, maxBytes);
        var state = Required(identity.Key);
        if (!state.Values.TryGetValue(identity.ValueName, out var value)) return null;
        if (value.Data.Length > maxBytes) throw new RegistryNativeException(RegistryNativeFailure.LimitExceeded, "fixture_value_limit");
        return new(value.Type, (byte[])value.Data.Clone());
    }

    public RegistryBrowseEntry? ReadName(RegistryKeyIdentity key, RegistryBrowseKind kind, int index, int maxNameChars)
    {
        Call("name");
        NamesRead++;
        var state = Required(key);
        RegistryBrowseEntry? entry;
        if (kind == RegistryBrowseKind.Keys)
        {
            var children = ChildPaths(key);
            entry = index < children.Count ? new(children[index][(children[index].LastIndexOf('\\') + 1)..], null) : null;
        }
        else
        {
            var names = state.Values.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
            entry = index < names.Count ? new(names[index], (uint)state.Values[names[index]].Type) : null;
        }
        if (entry is not null && entry.Name.Length > maxNameChars)
            throw new RegistryNativeException(RegistryNativeFailure.LimitExceeded, "fixture_name_limit");
        return entry;
    }

    public bool CreateKey(RegistryKeyIdentity key)
    {
        Call("create");
        Required(Parent(key));
        if (_keys.ContainsKey(Id(key))) return false;
        SeedKey(key);
        Writes++;
        AfterWrite?.Invoke("create");
        return true;
    }

    public void SetValue(RegistryValueIdentity identity, RegistryValueType type, byte[] data)
    {
        Call("set");
        Required(identity.Key);
        SeedValue(identity, type, data);
        Writes++;
        AfterWrite?.Invoke("set");
    }

    public void DeleteValue(RegistryValueIdentity identity)
    {
        Call("delete-value");
        var state = Required(identity.Key);
        if (!state.Values.Remove(identity.ValueName)) throw new RegistryNativeException(RegistryNativeFailure.NotFound, "fixture_value_missing");
        state.Generation = ++_generation;
        Writes++;
        AfterWrite?.Invoke("delete-value");
    }

    public void DeleteEmptyKey(RegistryKeyIdentity key)
    {
        Call("delete-key");
        var state = Required(key);
        if (state.Values.Count != 0 || ChildPaths(key).Count != 0)
            throw new RegistryNativeException(RegistryNativeFailure.Conflict, "fixture_key_not_empty");
        _keys.Remove(Id(key));
        Touch(Parent(key));
        Writes++;
        AfterWrite?.Invoke("delete-key");
    }

    private void Call(string operation) { Calls++; BeforeCall?.Invoke(operation); }
    private KeyState Required(RegistryKeyIdentity key) => _keys.TryGetValue(Id(key), out var state) ? state :
        throw new RegistryNativeException(RegistryNativeFailure.NotFound, "fixture_key_missing");
    private static string Id(RegistryKeyIdentity key) => $"{(int)key.Hive}:{(int)key.View}:{key.KeyPath}";
    private static RegistryKeyIdentity Parent(RegistryKeyIdentity key)
    {
        var split = key.KeyPath.LastIndexOf('\\');
        return key with { KeyPath = split < 0 ? "" : key.KeyPath[..split] };
    }

    private List<string> ChildPaths(RegistryKeyIdentity key)
    {
        var prefix = Id(key) + (key.KeyPath.Length == 0 ? "" : "\\");
        return _keys.Keys.Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            candidate.Length > prefix.Length && !candidate[prefix.Length..].Contains('\\'))
            .Select(candidate => candidate[(candidate.IndexOf(':', candidate.IndexOf(':') + 1) + 1)..])
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

internal sealed class FixtureAdmission(Func<RegistryAccessRequest, bool>? allow = null) : IRegistryAdmission
{
    internal List<RegistryAccessRequest> Requests { get; } = [];
    public bool IsAllowed(RegistryAccessRequest request)
    {
        Requests.Add(request);
        return allow?.Invoke(request) ?? true;
    }
}
