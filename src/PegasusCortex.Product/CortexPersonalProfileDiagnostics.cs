using System.Text;

namespace Pegasus.Cortex.Product;

/// <summary>Best-effort transition-only diagnostics with no profile or record content.</summary>
public sealed class CortexPersonalProfileDiagnostics(string logDirectory)
{
    private readonly object _gate = new();
    private string? _lastState;
    private string? _lastIssue;

    public void Report(string state, string? issueCode)
    {
        lock (_gate)
        {
            if (state == _lastState && issueCode == _lastIssue) return;
            _lastState = state;
            _lastIssue = issueCode;
            if (state is not (CortexPersonalProfilePipe.Loaded or CortexPersonalProfilePipe.NotLoaded) ||
                issueCode is { Length: > 64 } || issueCode?.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) == true) return;
            var line = $"{DateTimeOffset.UtcNow:O} event=personal_profile state={state} issue={issueCode ?? "none"}{Environment.NewLine}";
            try { Console.Error.Write(line); } catch { }
            try
            {
                var directory = CortexProductLayout.RequireCanonicalAbsoluteDirectory(logDirectory, nameof(logDirectory));
                if (string.Equals(Path.TrimEndingDirectorySeparator(directory),
                        Path.TrimEndingDirectorySeparator(Path.GetPathRoot(directory)!), StringComparison.OrdinalIgnoreCase)) return;
                CortexProductLayout.RejectExistingReparseComponents(directory);
                var path = Path.Combine(directory, "personal-profile.log");
                var previous = Path.Combine(directory, "personal-profile.previous.log");
                if (path.Length > CortexProductLayout.MaximumWindowsPathCharacters ||
                    previous.Length > CortexProductLayout.MaximumWindowsPathCharacters) return;
                foreach (var target in new[] { path, previous })
                {
                    CortexProductLayout.RejectExistingReparseComponents(target);
                    if (Directory.Exists(target)) return;
                }
                Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length >= 64 * 1024)
                    File.Move(path, previous, overwrite: true);
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must not block the mandatory core or host, including
                // when storage is unavailable or another local process rotates a log.
            }
        }
    }
}
