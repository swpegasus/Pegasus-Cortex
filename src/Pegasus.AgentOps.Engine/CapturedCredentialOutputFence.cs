namespace Pegasus.AgentOps.Engine;

/// <summary>
/// Prevents the known credential-header helper from entering captured tool output.
/// This is an accidental-output fence, not isolation from arbitrary same-user code.
/// </summary>
internal static class CapturedCredentialOutputFence
{
    internal const string EnvironmentVariable = "PEGASUS_AGENTOPS_CAPTURED";
    internal const string EnvironmentValue = "1";
    internal const string RefusalCode = "credential_output_forbidden";

    internal static void RequireAllowed(ToolInvocation invocation)
    {
        var executable = Path.GetFileName(invocation.ExecutablePath);
        if ((CortexImage(executable, dll: false) && Headers(invocation.Arguments, 0)) ||
            (Dotnet(executable) && DotnetHeaders(invocation.Arguments)))
            throw new AgentOpsException(RefusalCode);
    }

    internal static ToolInvocation MarkCaptured(ToolInvocation invocation)
    {
        // Invocation.Environment is the final caller/tool merge. Remove every
        // casing of the key, then add the fixed marker; callers cannot clear it.
        var values = invocation.Environment.Entries
            .Where(item => !item.Key.Equals(EnvironmentVariable, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase);
        values.Add(EnvironmentVariable, EnvironmentValue);
        return invocation.WithEnvironment(ProcessEnvironment.Replace(values));
    }

    private static bool Headers(IReadOnlyList<string> arguments, int start) =>
        arguments.Count == start + 4 && arguments[start] == "mcp" &&
        arguments[start + 1] == "headers" && arguments[start + 2] == "--connection";

    private static bool DotnetHeaders(IReadOnlyList<string> arguments)
    {
        var index = arguments.Count > 0 && arguments[0] == "exec" ? 1 : 0;
        while (index < arguments.Count && arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            var option = arguments[index];
            var equals = option.IndexOf('=');
            var name = equals >= 0 ? option[..equals] : option;
            if (name is not ("--runtimeconfig" or "--depsfile" or "--additionalprobingpath" or
                "--additional-deps" or "--fx-version" or "--roll-forward" or "--roll-forward-on-no-candidate-fx"))
                return false;
            if (equals >= 0) { if (equals == option.Length - 1) return false; index++; }
            else { if (index + 1 >= arguments.Count) return false; index += 2; }
        }
        return index < arguments.Count && CortexImage(Path.GetFileName(arguments[index]), dll: true) &&
            Headers(arguments, index + 1);
    }

    private static bool CortexImage(string name, bool dll) =>
        name.Equals(dll ? "PegasusCortex.dll" : "PegasusCortex.exe", StringComparison.OrdinalIgnoreCase);

    private static bool Dotnet(string name) =>
        name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
}
