using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Drawing.Imaging;
using Pegasus.AgentOps.Engine;

public sealed class AutoItXUiAutomationProvider : IUiAutomationProvider
{
    private const string ManagedName = "AutoItX3.Assembly.dll";
    private const string NativeName = "AutoItX3_x64.dll";
    private const int MaxReturnedChars = 64 * 1024;
    private static readonly object RuntimeGate = new();
    private static Type? s_autoIt;
    private static string? s_version;
    private static nint s_nativeHandle;

    private readonly Type? _autoIt;
    private readonly string? _version;
    private readonly string? _loadFailure;

    public AutoItXUiAutomationProvider(string runtimeDirectory)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new UiAutomationException("platform", remedy: "Run the packaged x64 UiHost on Windows.");
            var root = Path.GetFullPath(runtimeDirectory);
            var managedPath = Path.Combine(root, ManagedName);
            var nativePath = Path.Combine(root, NativeName);
            ValidateRuntimeFile(managedPath);
            ValidateRuntimeFile(nativePath);
            lock (RuntimeGate)
            {
                if (s_autoIt is null)
                {
                    var nativeHandle = NativeLibrary.Load(nativePath);
                    try
                    {
                        s_nativeHandle = nativeHandle;
                        var assembly = Assembly.LoadFrom(managedPath);
                        NativeLibrary.SetDllImportResolver(assembly, (libraryName, _, _) =>
                            libraryName.Equals(NativeName, StringComparison.OrdinalIgnoreCase) ? s_nativeHandle : 0);
                        var autoIt = assembly.GetType("AutoIt.AutoItX", throwOnError: true, ignoreCase: false)!;
                        var init = autoIt.GetMethod("Init", BindingFlags.Public | BindingFlags.Static, binder: null, Type.EmptyTypes, modifiers: null)
                            ?? throw new MissingMethodException(autoIt.FullName, "Init");
                        _ = init.Invoke(null, null);
                        s_autoIt = autoIt;
                        s_version = FileVersionInfo.GetVersionInfo(managedPath).FileVersion;
                    }
                    catch
                    {
                        s_nativeHandle = 0;
                        NativeLibrary.Free(nativeHandle);
                        throw;
                    }
                }
                _autoIt = s_autoIt;
                _version = s_version;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _loadFailure = ex.GetType().Name;
        }
    }

    public UiProviderStatus Status() => new(
        _autoIt is not null && IsInteractiveOwnerSession(),
        "autoitx-registration-free",
        _version,
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        IsInteractiveOwnerSession());

    public UiTargetIdentity FindWindow(UiWindowQuery query)
    {
        EnsureAvailable();
        var matches = new List<nint>(2);
        Exception? failure = null;
        var scanned = 0;
        _ = EnumWindows((handle, _) =>
        {
            try
            {
                if (++scanned > 4096) throw new UiAutomationException("ui_unavailable", remedy: "Too many top-level windows were present for a bounded lookup.");
                if (!IsWindowVisible(handle)) return true;
                var title = BoundReturned((string?)Invoke("WinGetTitle", [typeof(nint), typeof(int)], [handle, MaxReturnedChars]));
                var titleMatches = query.Match == "exact"
                    ? title.Equals(query.Title, StringComparison.OrdinalIgnoreCase)
                    : title.Contains(query.Title, StringComparison.OrdinalIgnoreCase);
                if (!titleMatches) return true;
                if (query.Text is { Length: > 0 })
                {
                    var windowText = BoundReturned((string?)Invoke("WinGetText", [typeof(nint), typeof(int)], [handle, MaxReturnedChars]));
                    if (!windowText.Contains(query.Text, StringComparison.OrdinalIgnoreCase)) return true;
                }
                matches.Add(handle);
                return matches.Count < 2;
            }
            catch (Exception ex)
            {
                failure = ex;
                return false;
            }
        }, 0);
        if (failure is not null) throw failure;
        if (matches.Count == 0) throw NotFound("query", "No window matched the bounded title/text query.");
        if (matches.Count > 1)
            throw new UiAutomationException("ui_ambiguous", "query", "Refine the title or window_text so exactly one visible window matches.");
        return CaptureIdentity(matches[0]);
    }

    public UiWindowInfo GetWindowInfo(UiTargetIdentity window)
    {
        var handle = ValidateWindow(window);
        var title = BoundReturned((string?)Invoke("WinGetTitle", [typeof(nint), typeof(int)], [handle, MaxReturnedChars]));
        var classes = BoundReturned((string?)Invoke("WinGetClassList", [typeof(nint), typeof(int)], [handle, MaxReturnedChars]));
        var bounds = Rectangle((System.Drawing.Rectangle)Invoke("WinGetPos", [typeof(nint)], [handle])!);
        var state = (int)Invoke("WinGetState", [typeof(nint)], [handle])!;
        return new(window, title, classes, bounds, state);
    }

    public long ResolveControl(UiTargetIdentity window, string selector)
    {
        var handle = ValidateWindow(window);
        var control = (nint)Invoke("ControlGetHandle", [typeof(nint), typeof(string)], [handle, selector])!;
        if (control == 0 || !IsChild(handle, control))
            throw NotFound("control", "No child control matched the bounded selector on the admitted window.");
        return control;
    }

    public UiControlInfo GetControlInfo(UiTargetIdentity window, long controlHandle)
    {
        var handle = ValidateWindow(window);
        var control = ValidateControl(handle, controlHandle);
        var text = BoundReturned((string?)Invoke("ControlGetText", [typeof(nint), typeof(nint), typeof(int)], [handle, control, MaxReturnedChars]));
        var bounds = Rectangle((System.Drawing.Rectangle)Invoke("ControlGetPos", [typeof(nint), typeof(nint)], [handle, control])!);
        return new(window, control, text, bounds);
    }

    public UiMutationResult Activate(UiTargetIdentity window)
    {
        var handle = ValidateWindow(window);
        return Mutate(
            () => (int)Invoke("WinActivate", [typeof(nint)], [handle])!,
            () => (int)Invoke("WinActive", [typeof(nint)], [handle])! != 0);
    }

    public UiMutationResult Move(UiTargetIdentity window, UiBounds bounds)
    {
        var handle = ValidateWindow(window);
        return Mutate(
            () => (int)Invoke(
                "WinMove",
                [typeof(nint), typeof(int), typeof(int), typeof(int), typeof(int)],
                [handle, bounds.X, bounds.Y, bounds.Width, bounds.Height])!,
            () => Rectangle((System.Drawing.Rectangle)Invoke("WinGetPos", [typeof(nint)], [handle])!) == bounds);
    }

    public UiMutationResult FocusControl(UiTargetIdentity window, long controlHandle)
    {
        var handle = ValidateWindow(window);
        var control = ValidateControl(handle, controlHandle);
        return Mutate(
            () => (int)Invoke("ControlFocus", [typeof(nint), typeof(nint)], [handle, control])!,
            () =>
            {
                var focused = (string?)Invoke("ControlGetFocus", [typeof(nint), typeof(int)], [handle, 4096]);
                if (string.IsNullOrEmpty(focused)) return false;
                return (nint)Invoke("ControlGetHandle", [typeof(nint), typeof(string)], [handle, focused])! == control;
            });
    }

    public UiMutationResult ClickControl(
        UiTargetIdentity window,
        long controlHandle,
        string button,
        int clicks,
        int x,
        int y)
    {
        var handle = ValidateWindow(window);
        var control = ValidateControl(handle, controlHandle);
        return Mutate(() => (int)Invoke(
            "ControlClick",
            [typeof(nint), typeof(nint), typeof(string), typeof(int), typeof(int), typeof(int)],
            [handle, control, button, clicks, x, y])!);
    }

    public UiMutationResult SetControlText(UiTargetIdentity window, long controlHandle, string text)
    {
        var handle = ValidateWindow(window);
        var control = ValidateControl(handle, controlHandle);
        // Never place text in an exception, result, or diagnostic.
        return Mutate(
            () => (int)Invoke(
                "ControlSetText",
                [typeof(nint), typeof(nint), typeof(string)],
                [handle, control, text])!,
            () => string.Equals(
                (string?)Invoke("ControlGetText", [typeof(nint), typeof(nint), typeof(int)], [handle, control, MaxReturnedChars]),
                text,
                StringComparison.Ordinal));
    }

    public UiCaptureResult CaptureWindow(UiTargetIdentity window, string destination, string format)
    {
        EnsureAvailable();
        var handle = ValidateWindow(window);
        if (format != "png") throw new UiAutomationException("invalid", "format", "Use png for bounded UI capture.");
        destination = Path.GetFullPath(destination);
        ValidateCaptureDestination(destination);
        if (!GetWindowRect(handle, out var rectangle))
            throw new UiAutomationException("ui_unsupported", "window", "The exact window bounds could not be captured.");
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width is < 1 or > 16_384 || height is < 1 or > 16_384 || (long)width * height > 67_108_864)
            throw new UiAutomationException("ui_unsupported", "window", "Use a visible window no larger than 16,384 pixels per side and 67,108,864 pixels total.");

        var parent = Path.GetDirectoryName(destination) ?? throw new UiAutomationException("invalid", "destination");
        var temporary = Path.Combine(parent, "." + Path.GetFileName(destination) + ".pao-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    if (!PrintWindow(handle, hdc, 2))
                        throw new UiAutomationException("ui_unsupported", "window", "The application did not support an exact bounded window capture.");
                }
                finally { graphics.ReleaseHdc(hdc); }
                bitmap.Save(temporary, ImageFormat.Png);
            }
            if (!File.Exists(temporary) || new FileInfo(temporary).Length is <= 0 or > 256L * 1024 * 1024)
                throw new UiAutomationException("ui_unsupported", "window", "The captured image was empty or exceeded the 256 MiB limit.");
            // Revalidate the exact process-bound HWND immediately before publish.
            _ = ValidateWindow(window);
            File.Move(temporary, destination, overwrite: false);
            using var stream = File.OpenRead(destination);
            var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return new(destination, format, width, height, stream.Length, sha);
        }
        catch (UiAutomationException) { throw; }
        catch (UnauthorizedAccessException ex) { throw new UiAutomationException("access", "destination", "Choose an admitted user-writable destination.", inner: ex); }
        catch (IOException ex) when (File.Exists(destination)) { throw new UiAutomationException("collision", "destination", "Choose an absent exact destination.", inner: ex); }
        catch (Exception ex) when (ex is IOException or ExternalException or ArgumentException)
        {
            throw new UiAutomationException("ui_unsupported", "window", "Capture failed before a verified image could be published.", inner: ex);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    // DllImportResolver is process-wide for the loaded wrapper assembly. Keep
    // its verified native module alive until UiHost exits instead of leaving a
    // resolver that points at a released handle after a provider is disposed.
    public void Dispose() { }

    private static void ValidateRuntimeFile(string path)
    {
        if (!File.Exists(path) || Directory.Exists(path))
            throw new FileNotFoundException("Required registration-free AutoItX runtime file is absent.", Path.GetFileName(path));
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("AutoItX runtime files cannot be reparse points.");
    }

    private static void ValidateCaptureDestination(string path)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || File.Exists(path) || Directory.Exists(path))
            throw new UiAutomationException(File.Exists(path) || Directory.Exists(path) ? "collision" : "invalid", "destination", "Choose an absent exact .png destination.");
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new UiAutomationException("not_found", "destination", "Create and admit the ordinary destination directory first.");
        for (var current = new DirectoryInfo(parent); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UiAutomationException("reparse_boundary", "destination", "Use a destination with no reparse-point ancestor.");
    }

    private void EnsureAvailable()
    {
        if (_autoIt is null)
            throw new UiAutomationException(
                "ui_unavailable",
                remedy: $"Stage {ManagedName} and {NativeName} beside the x64 UiHost from the admitted AutoItX distribution ({_loadFailure ?? "load failed"}).");
        if (!IsInteractiveOwnerSession())
            throw new UiAutomationException(
                "user_dispatch_required",
                remedy: "Launch UiHost in the authenticated signed-in owner's nonzero interactive session.");
    }

    private object? Invoke(string method, Type[] types, object?[] arguments)
    {
        EnsureAvailable();
        try
        {
            var member = _autoIt!.GetMethod(
                method,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types,
                modifiers: null) ?? throw new MissingMethodException(_autoIt.FullName, method);
            return member.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex)
        {
            throw new UiAutomationException(
                "provider_failed",
                remedy: "Verify the exact window/control identity and inspect bounded UiHost health before retrying.",
                inner: ex.InnerException ?? ex);
        }
        catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or ArgumentException)
        {
            throw new UiAutomationException(
                "ui_unavailable",
                remedy: "Repair the admitted AutoItX runtime to the package-supported version.",
                inner: ex);
        }
    }

    private static UiTargetIdentity CaptureIdentity(nint handle)
    {
        if (!IsWindow(handle)) throw NotFound("window", "The window no longer exists.");
        _ = GetWindowThreadProcessId(handle, out var rawPid);
        if (rawPid == 0 || rawPid > int.MaxValue) throw NotFound("window", "The window process identity is unavailable.");
        try
        {
            using var process = Process.GetProcessById((int)rawPid);
            return new(handle, process.Id, process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new UiAutomationException("stale_target", "window", "Resolve the window again and use its new HWND/PID/start identity.", inner: ex);
        }
    }

    private static nint ValidateWindow(UiTargetIdentity expected)
    {
        var handle = (nint)expected.Handle;
        var actual = CaptureIdentity(handle);
        if (actual.ProcessId != expected.ProcessId || actual.ProcessStartUtcTicks != expected.ProcessStartUtcTicks)
            throw new UiAutomationException("stale_target", "window", "Resolve the window again; its process identity changed.");
        return handle;
    }

    private static nint ValidateControl(nint window, long controlHandle)
    {
        var control = (nint)controlHandle;
        if (control == 0 || !IsWindow(control) || !IsChild(window, control))
            throw NotFound("control_handle", "Resolve the child control again on the admitted window.");
        return control;
    }

    private static UiMutationResult Mutate(Func<int> operation, Func<bool>? verify = null)
    {
        var issued = false;
        try
        {
            issued = true;
            var result = operation();
            if (result == 0)
                throw new UiAutomationException(
                    "recovery_required",
                    remedy: "Inspect the target UI state before retrying; AutoItX did not confirm the issued mutation.",
                    partial: true,
                    recoveryRequired: true);
            if (verify is not null && !verify())
                throw new UiAutomationException(
                    "postcondition_failed",
                    remedy: "Inspect the target UI state before retrying; the issued mutation did not satisfy its postcondition.",
                    partial: true,
                    recoveryRequired: true);
            return new(true, true);
        }
        catch (UiAutomationException ex) when (issued && !ex.RecoveryRequired)
        {
            throw new UiAutomationException(
                "recovery_required",
                remedy: "Inspect the target UI state before retrying; completion is uncertain after dispatch.",
                partial: true,
                recoveryRequired: true,
                inner: ex);
        }
        catch (Exception ex) when (issued)
        {
            throw new UiAutomationException(
                "recovery_required",
                remedy: "Inspect the target UI state before retrying; completion is uncertain after dispatch.",
                partial: true,
                recoveryRequired: true,
                inner: ex);
        }
    }

    private static UiBounds Rectangle(System.Drawing.Rectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    private static string BoundReturned(string? value) =>
        value is null ? string.Empty : value.Length <= MaxReturnedChars ? value : value[..MaxReturnedChars];

    private static UiAutomationException NotFound(string field, string remedy) =>
        new("ui_not_found", field, remedy);

    private static bool IsInteractiveOwnerSession()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
            return false;
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value is { } sid && !sid.Equals("S-1-5-18", StringComparison.Ordinal);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    private delegate bool EnumWindowsCallback(nint window, nint state);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint state);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint child);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint deviceContext, uint flags);
}
