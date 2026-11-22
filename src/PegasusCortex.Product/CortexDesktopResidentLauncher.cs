using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pegasus.Cortex.Product;

/// <summary>Launches an already-admitted resident image through the actual user desktop.</summary>
[SupportedOSPlatform("windows")]
public static class CortexDesktopResidentLauncher
{
    public static void Launch(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new InvalidDataException("The admitted resident executable is unavailable.");
        object? shell = null, windows = null, desktop = null, document = null, application = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!;
            shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("The user shell is unavailable.");
            windows = Invoke(shell, "Windows", BindingFlags.InvokeMethod, []);
            // A newly created Shell.Application runs in the requesting process.
            // Its own ShellExecute would retain that process's kill-on-close job.
            // Resolve the desktop's automation object, which lives in Explorer,
            // then obtain its Application. No window, URL or command is submitted.
            object?[] find = [0, null, 8, 0, 1]; // Desktop, empty root, SWC_DESKTOP, HWND, SWFO_NEEDDISPATCH.
            var byReference = new ParameterModifier(find.Length);
            byReference[3] = true;
            desktop = windows.GetType().InvokeMember("FindWindowSW", BindingFlags.InvokeMethod, null, windows,
                find, [byReference], CultureInfo.InvariantCulture, null) ??
                throw new InvalidOperationException("The signed-in desktop is unavailable.");
            document = Invoke(desktop, "Document", BindingFlags.GetProperty, []);
            application = Invoke(document, "Application", BindingFlags.GetProperty, []);
            _ = application.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, application,
                [executable, string.Empty, Path.GetDirectoryName(executable)!, "open", 0], CultureInfo.InvariantCulture);
        }
        finally
        {
            // Release only the references acquired here; never disconnect a shared RCW.
            foreach (var value in new[] { application, document, desktop, windows, shell })
                if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value);
        }
    }

    private static object Invoke(object target, string member, BindingFlags flags, object?[] arguments) =>
        target.GetType().InvokeMember(member, flags, null, target, arguments, CultureInfo.InvariantCulture) ??
        throw new InvalidOperationException("The signed-in desktop automation object is unavailable.");
}
