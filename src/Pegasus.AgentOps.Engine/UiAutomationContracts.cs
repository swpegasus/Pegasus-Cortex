using System.Text.Json.Serialization;

namespace Pegasus.AgentOps.Engine;

[JsonConverter(typeof(JsonStringEnumConverter<UiOperation>))]
public enum UiOperation
{
    Status,
    FindWindow,
    WindowInfo,
    ControlResolve,
    ControlInfo,
    Activate,
    Move,
    ControlFocus,
    ControlClick,
    ControlSetText,
    CaptureWindow
}

public sealed record UiTargetIdentity(long Handle, int ProcessId, long ProcessStartUtcTicks);
public sealed record UiWindowQuery(string Title, string? Text = null, string Match = "exact");
public sealed record UiBounds(int X, int Y, int Width, int Height);
public sealed record UiProviderStatus(bool Available, string Provider, string? Version, string Architecture, bool InteractiveUser);
public sealed record UiWindowInfo(UiTargetIdentity Target, string Title, string ClassList, UiBounds Bounds, int State);
public sealed record UiControlInfo(UiTargetIdentity Window, long Handle, string Text, UiBounds Bounds);
public sealed record UiMutationResult(bool ActionIssued, bool Changed);
public sealed record UiCaptureResult(string Destination, string Format, int Width, int Height, long Bytes, string Sha256);

public sealed record UiHostRequest(
    int Version,
    string Id,
    UiOperation Operation,
    UiWindowQuery? Query = null,
    UiTargetIdentity? Window = null,
    long? ControlHandle = null,
    string? Control = null,
    UiBounds? Bounds = null,
    string? Button = null,
    int? Clicks = null,
    int? X = null,
    int? Y = null,
    // ControlSetText is process-memory-only. Hosts must release the request at
    // completion and must never copy Text into responses, logs, or persistence.
    string? Text = null,
    string? Destination = null,
    string? Format = null);

public sealed record UiHostResponse(
    int Version,
    string Id,
    UiOperation Operation,
    bool Ok,
    string State,
    string? Err = null,
    string? Field = null,
    string? Remedy = null,
    bool Partial = false,
    bool RecoveryRequired = false,
    object? Data = null);

public sealed class UiAutomationException : Exception
{
    public UiAutomationException(
        string code,
        string? field = null,
        string? remedy = null,
        bool partial = false,
        bool recoveryRequired = false,
        Exception? inner = null)
        : base(code, inner)
    {
        Code = code;
        Field = field;
        Remedy = remedy;
        Partial = partial;
        RecoveryRequired = recoveryRequired;
    }

    public string Code { get; }
    public string? Field { get; }
    public string? Remedy { get; }
    public bool Partial { get; }
    public bool RecoveryRequired { get; }
}

public interface IUiAutomationProvider : IDisposable
{
    UiProviderStatus Status();
    UiTargetIdentity FindWindow(UiWindowQuery query);
    UiWindowInfo GetWindowInfo(UiTargetIdentity window);
    long ResolveControl(UiTargetIdentity window, string selector);
    UiControlInfo GetControlInfo(UiTargetIdentity window, long controlHandle);
    UiMutationResult Activate(UiTargetIdentity window);
    UiMutationResult Move(UiTargetIdentity window, UiBounds bounds);
    UiMutationResult FocusControl(UiTargetIdentity window, long controlHandle);
    UiMutationResult ClickControl(UiTargetIdentity window, long controlHandle, string button, int clicks, int x, int y);
    UiMutationResult SetControlText(UiTargetIdentity window, long controlHandle, string text);
    UiCaptureResult CaptureWindow(UiTargetIdentity window, string destination, string format);
}
