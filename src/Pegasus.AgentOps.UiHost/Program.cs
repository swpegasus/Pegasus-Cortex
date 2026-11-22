using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.AgentOps.Engine;

if (args.Length != 1 || !args[0].Equals("--stdio-v1", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Pegasus AgentOps UiHost requires --stdio-v1.");
    return 2;
}

await using var input = Console.OpenStandardInput();
await using var output = Console.OpenStandardOutput();
using var provider = new AutoItXUiAutomationProvider(AppContext.BaseDirectory);
return await UiHostProtocol.RunAsync(input, output, provider, CancellationToken.None);

public static class UiHostProtocol
{
    public const int ProtocolVersion = 1;
    public const int MaxRequestBytes = 256 * 1024;
    public const int MaxResponseBytes = 512 * 1024;
    private const int MaxIdBytes = 128;
    private const int MaxQueryBytes = 8 * 1024;
    private const int MaxSelectorBytes = 4 * 1024;
    private const int MaxSetTextBytes = 64 * 1024;
    private const int MaxPathBytes = 32 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static async Task<int> RunAsync(
        Stream input,
        Stream output,
        IUiAutomationProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(provider);
        var header = new byte[4];
        try
        {
            while (true)
            {
                var headerRead = await ReadAtMostAsync(input, header, cancellationToken).ConfigureAwait(false);
                if (headerRead == 0) return 0;
                if (headerRead != header.Length) return 8;
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is < 2 or > MaxRequestBytes) return 8;
                var payload = new byte[length];
                if (await ReadAtMostAsync(input, payload, cancellationToken).ConfigureAwait(false) != payload.Length) return 8;

                UiHostRequest? request = null;
                UiHostResponse response;
                try
                {
                    request = JsonSerializer.Deserialize<UiHostRequest>(payload, Json)
                        ?? throw new UiAutomationException("invalid", remedy: "Submit one versioned typed UiHost request.");
                    response = Dispatch(provider, request);
                }
                catch (JsonException ex)
                {
                    response = Failure(request, "invalid", null, "Correct the typed request fields and retry.", ex);
                }
                catch (UiAutomationException ex)
                {
                    response = Failure(request, ex.Code, ex.Field, ex.Remedy, ex);
                }
                catch (Exception ex)
                {
                    response = Failure(request, "internal", null, "Inspect bounded UiHost diagnostics and retry only if no mutation was issued.", ex);
                }
                finally
                {
                    // Release the only managed reference to ephemeral set-text input.
                    request = null;
                    Array.Clear(payload);
                }

                var encoded = JsonSerializer.SerializeToUtf8Bytes(response, Json);
                if (encoded.Length > MaxResponseBytes)
                {
                    response = Failure(null, "internal", null, "The provider response exceeded the bounded protocol frame.", null);
                    encoded = JsonSerializer.SerializeToUtf8Bytes(response, Json);
                }
                BinaryPrimitives.WriteInt32LittleEndian(header, encoded.Length);
                await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static UiHostResponse Dispatch(IUiAutomationProvider provider, UiHostRequest request)
    {
        ValidateCommon(request);
        object data = request.Operation switch
        {
            UiOperation.Status => provider.Status(),
            UiOperation.FindWindow => provider.FindWindow(RequireQuery(request)),
            UiOperation.WindowInfo => provider.GetWindowInfo(RequireWindow(request)),
            UiOperation.ControlResolve => provider.ResolveControl(RequireWindow(request), RequireSelector(request)),
            UiOperation.ControlInfo => provider.GetControlInfo(RequireWindow(request), RequireControlHandle(request)),
            UiOperation.Activate => provider.Activate(RequireWindow(request)),
            UiOperation.Move => provider.Move(RequireWindow(request), RequireBounds(request)),
            UiOperation.ControlFocus => provider.FocusControl(RequireWindow(request), RequireControlHandle(request)),
            UiOperation.ControlClick => provider.ClickControl(
                RequireWindow(request), RequireControlHandle(request), RequireButton(request),
                request.Clicks ?? 1, request.X ?? -1, request.Y ?? -1),
            UiOperation.ControlSetText => provider.SetControlText(
                RequireWindow(request), RequireControlHandle(request), RequireText(request)),
            UiOperation.CaptureWindow => provider.CaptureWindow(
                RequireWindow(request), RequireDestination(request), RequireFormat(request)),
            _ => throw new UiAutomationException("invalid", "operation", "Select one advertised fixed UI operation.")
        };
        return new(ProtocolVersion, request.Id, request.Operation, true, "done", Data: data);
    }

    private static void ValidateCommon(UiHostRequest request)
    {
        if (request.Version != ProtocolVersion) throw new UiAutomationException("protocol", "version", "Use UiHost protocol version 1.");
        Bound(request.Id, MaxIdBytes, "id", allowEmpty: false);
        if (request.Operation == UiOperation.ControlClick)
        {
            if (request.Clicks is < 1 or > 3) throw new UiAutomationException("invalid", "clicks", "Use clicks from 1 through 3.");
            if (request.X is < -1 or > 16_384 || request.Y is < -1 or > 16_384)
                throw new UiAutomationException("invalid", "x", "Use -1 for the control center or bounded control-relative coordinates.");
        }
    }

    private static UiWindowQuery RequireQuery(UiHostRequest request)
    {
        var query = request.Query ?? throw new UiAutomationException("invalid", "query", "Provide a bounded window title query.");
        Bound(query.Title, MaxQueryBytes, "query.title", allowEmpty: false);
        // An omitted optional text filter and an explicit empty filter both
        // mean title-only lookup; required fields still reject null in Bound.
        Bound(query.Text ?? string.Empty, MaxQueryBytes, "query.text", allowEmpty: true);
        if (query.Match is not ("exact" or "contains"))
            throw new UiAutomationException("invalid", "query.match", "Use exact or contains title matching.");
        return query;
    }

    private static UiTargetIdentity RequireWindow(UiHostRequest request) =>
        request.Window is { Handle: > 0, ProcessId: > 0, ProcessStartUtcTicks: > 0 } window
            ? window
            : throw new UiAutomationException("invalid", "window", "Provide HWND, PID, and exact process start UTC ticks from find_window or window_info.");

    private static long RequireControlHandle(UiHostRequest request) =>
        request.ControlHandle is > 0 ? request.ControlHandle.Value :
            throw new UiAutomationException("invalid", "control_handle", "Resolve and supply the exact control HWND.");

    private static string RequireSelector(UiHostRequest request)
    {
        Bound(request.Control, MaxSelectorBytes, "control", allowEmpty: false);
        return request.Control!;
    }

    private static UiBounds RequireBounds(UiHostRequest request)
    {
        var bounds = request.Bounds ?? throw new UiAutomationException("invalid", "bounds", "Provide x, y, width, and height.");
        if (bounds.X is < -32_768 or > 32_768 || bounds.Y is < -32_768 or > 32_768 ||
            bounds.Width is < 1 or > 16_384 || bounds.Height is < 1 or > 16_384)
            throw new UiAutomationException("invalid", "bounds", "Use positive bounded dimensions and desktop-range coordinates.");
        return bounds;
    }

    private static string RequireButton(UiHostRequest request)
    {
        var button = request.Button ?? "left";
        if (button is not ("left" or "right" or "middle" or "primary" or "secondary"))
            throw new UiAutomationException("invalid", "button", "Use left, right, middle, primary, or secondary.");
        return button;
    }

    private static string RequireText(UiHostRequest request)
    {
        Bound(request.Text, MaxSetTextBytes, "text", allowEmpty: true);
        return request.Text!;
    }

    private static string RequireDestination(UiHostRequest request)
    {
        Bound(request.Destination, MaxPathBytes, "destination", allowEmpty: false);
        var destination = request.Destination!;
        if (!Path.IsPathFullyQualified(destination))
            throw new UiAutomationException("invalid", "destination", "Provide one frozen fully-qualified destination path.");
        return destination;
    }

    private static string RequireFormat(UiHostRequest request)
    {
        var format = request.Format ?? "png";
        if (format != "png") throw new UiAutomationException("invalid", "format", "Use png for bounded UI capture.");
        return format;
    }

    private static void Bound(string? value, int maxUtf8Bytes, string field, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && value.Length == 0) || value.Contains('\0') || Encoding.UTF8.GetByteCount(value) > maxUtf8Bytes)
            throw new UiAutomationException("invalid", field, $"Supply a valid bounded '{field}' value.");
    }

    private static UiHostResponse Failure(
        UiHostRequest? request,
        string code,
        string? field,
        string? remedy,
        Exception? exception)
    {
        var ui = exception as UiAutomationException;
        return new(
            ProtocolVersion,
            request?.Id ?? string.Empty,
            request?.Operation ?? UiOperation.Status,
            false,
            ui?.RecoveryRequired == true ? "recovery_required" : "failed",
            code,
            field,
            remedy,
            ui?.Partial == true,
            ui?.RecoveryRequired == true);
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
