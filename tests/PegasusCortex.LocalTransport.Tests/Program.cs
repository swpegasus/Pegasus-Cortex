using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Pegasus.Cortex.LocalTransport.Configuration;
using Pegasus.Cortex.LocalTransport.Pipes;
using Pegasus.Cortex.LocalTransport.Protocol;

int? concurrentClientCount = null;
var tests = new (string Name, Func<Task> Run)[]
{
    ("endpoint identity ignores Pegasus environment variables", EndpointIdentityAsync),
    ("local transport assembly has no network-stack reference", NetworkDependencyClosureAsync),
    ("server limits have explicit validated defaults", ServerLimitBoundaryAsync),
    ("current-user pipe transports response and notification frames", PipeRoundTripAsync),
    ("raw length-prefixed request and response frames round-trip", RawFrameRoundTripAsync),
    ("one client serializes concurrent calls without response crossover", ConcurrentCallsAsync),
    ("bounded concurrent clients complete without starvation or timeout", async () =>
        concurrentClientCount = await ConcurrentClientFairnessAsync().ConfigureAwait(false)),
    ("client disposal waits for an active exchange and rejects later calls", ConcurrentDisposalAsync),
    ("client-side request rejection preserves the framing connection", ClientRequestBoundaryAsync),
    ("malformed and truncated raw frames are isolated by session", MalformedFrameRecoveryAsync),
    ("empty handler responses emit no marker and the server recovers", EmptyResponseRecoveryAsync),
    ("oversized handler responses emit no marker and the server recovers", OversizedResponseRecoveryAsync),
    ("handler faults terminate one session and allow reconnect", HandlerFaultRecoveryAsync),
    ("non-cooperative handlers expire and allow reconnect", HandlerTimeoutRecoveryAsync),
    ("idle sessions expire so a queued client receives service", IdleSessionFairnessAsync),
    ("request budgets retire a session and allow reconnect", RequestLimitReconnectAsync),
    ("server cancellation remains observable during a connected read", ServerCancellationAsync),
    ("client rejects unbounded connection timeouts", ConnectionTimeoutBoundaryAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}");
    }
}

if (failures.Count != 0)
    return 1;

if (concurrentClientCount is not 8)
{
    Console.Error.WriteLine("FAIL concurrent-client-evidence");
    return 1;
}

Console.WriteLine(
    $"METRIC concurrent-clients clients={concurrentClientCount.Value} starvation=0 timeouts=0");
return 0;

static Task EndpointIdentityAsync()
{
    var original = Environment.GetEnvironmentVariable("PEGASUS_CORTEX_ENDPOINT");
    try
    {
        var expected = CortexLocalEndpoint.Create("test-profile");
        Environment.SetEnvironmentVariable("PEGASUS_CORTEX_ENDPOINT", "tcp://untrusted.example");
        var actual = CortexLocalEndpoint.Create("test-profile");
        Require(expected == actual, "Environment configuration changed the local endpoint.");
        Require(
            !actual.PipeName.Contains("tcp", StringComparison.OrdinalIgnoreCase),
            "The local endpoint exposed a network transport.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PEGASUS_CORTEX_ENDPOINT", original);
    }

    return Task.CompletedTask;
}

static Task NetworkDependencyClosureAsync()
{
    var references = typeof(CortexLocalPipeServer).Assembly.GetReferencedAssemblies();
    Require(
        references.All(reference =>
            reference.Name is null ||
            !reference.Name.StartsWith("System.Net", StringComparison.Ordinal)),
        "The standalone local transport acquired a network-stack assembly reference.");
    return Task.CompletedTask;
}

static Task ServerLimitBoundaryAsync()
{
    Require(
        CortexLocalPipeServer.DefaultFrameIdleTimeout == TimeSpan.FromSeconds(5),
        "The frame idle timeout default changed unexpectedly.");
    Require(
        CortexLocalPipeServer.DefaultMaximumRequestsPerSession == 64,
        "The request-budget default changed unexpectedly.");

    var endpoint = CortexLocalEndpoint.Create($"limits-{Guid.NewGuid():N}");
    var handler = new RoutingHandler();
    _ = new CortexLocalPipeServer(endpoint, handler);
    _ = new CortexLocalPipeServer(
        endpoint,
        handler,
        frameIdleTimeout: CortexLocalPipeServer.MinimumFrameIdleTimeout,
        maximumRequestsPerSession: 1);
    _ = new CortexLocalPipeServer(
        endpoint,
        handler,
        frameIdleTimeout: CortexLocalPipeServer.MaximumFrameIdleTimeout,
        maximumRequestsPerSession: CortexLocalPipeServer.MaximumRequestsPerSessionLimit);

    Expect<ArgumentOutOfRangeException>(() =>
        _ = new CortexLocalPipeServer(
            endpoint,
            handler,
            frameIdleTimeout: CortexLocalPipeServer.MinimumFrameIdleTimeout -
                TimeSpan.FromMilliseconds(1)));
    Expect<ArgumentOutOfRangeException>(() =>
        _ = new CortexLocalPipeServer(
            endpoint,
            handler,
            frameIdleTimeout: CortexLocalPipeServer.MaximumFrameIdleTimeout +
                TimeSpan.FromMilliseconds(1)));
    Expect<ArgumentOutOfRangeException>(() =>
        _ = new CortexLocalPipeServer(endpoint, handler, maximumRequestsPerSession: 0));
    Expect<ArgumentOutOfRangeException>(() =>
        _ = new CortexLocalPipeServer(
            endpoint,
            handler,
            maximumRequestsPerSession:
                CortexLocalPipeServer.MaximumRequestsPerSessionLimit + 1));
    return Task.CompletedTask;
}

static async Task PipeRoundTripAsync()
{
    await using var fixture = new LocalServerFixture(new RoutingHandler());
    await using var client = await fixture.ConnectAsync().ConfigureAwait(false);

    var response = await client.CallAsync(
        Encoding.UTF8.GetBytes("bounded local request"),
        fixture.Token).ConfigureAwait(false);
    RequireResponse(response, "BOUNDED LOCAL REQUEST");

    var notification = await client.CallAsync(
        Encoding.UTF8.GetBytes("notify"),
        fixture.Token).ConfigureAwait(false);
    Require(notification is null, "A notification unexpectedly produced response bytes.");
    Require(fixture.Faults.IsEmpty, "A valid local pipe session reported a fault.");
}

static async Task RawFrameRoundTripAsync()
{
    await using var fixture = new LocalServerFixture(new RoutingHandler());
    await using var pipe = await ConnectRawAsync(
        fixture.Endpoint,
        fixture.Token).ConfigureAwait(false);

    await WriteRawRequestAsync(
        pipe,
        Encoding.UTF8.GetBytes("raw-frame"),
        fixture.Token).ConfigureAwait(false);
    var response = await ReadRawResponseAsync(pipe, fixture.Token).ConfigureAwait(false);

    RequireResponse(response, "RAW-FRAME");
    Require(fixture.Faults.IsEmpty, "The valid raw frame reported a session fault.");
}

static async Task ConcurrentCallsAsync()
{
    await using var fixture = new LocalServerFixture(new RoutingHandler());
    await using var client = await fixture.ConnectAsync().ConfigureAwait(false);

    var calls = Enumerable.Range(0, 24).Select(async index =>
    {
        var request = $"concurrent-{index:D2}";
        var response = await client.CallAsync(
            Encoding.UTF8.GetBytes(request),
            fixture.Token).ConfigureAwait(false);
        RequireResponse(response, request.ToUpperInvariant());
    });

    await Task.WhenAll(calls).ConfigureAwait(false);
    Require(fixture.Faults.IsEmpty, "Concurrent calls faulted the pipe session.");
}

static async Task<int> ConcurrentClientFairnessAsync()
{
    const int clientCount = 8;
    await using var fixture = new LocalServerFixture(new RoutingHandler());
    var elapsed = Stopwatch.StartNew();
    var calls = Enumerable.Range(0, clientCount).Select(async index =>
    {
        await using var client = await CortexLocalPipeClient.ConnectAsync(
            fixture.Endpoint,
            TimeSpan.FromSeconds(5),
            fixture.Token).ConfigureAwait(false);
        var request = $"client-{index:D2}";
        var response = await client.CallAsync(
            Encoding.UTF8.GetBytes(request),
            fixture.Token).ConfigureAwait(false);
        RequireResponse(response, request.ToUpperInvariant());
    });

    await Task.WhenAll(calls).WaitAsync(
        TimeSpan.FromSeconds(8),
        fixture.Token).ConfigureAwait(false);
    elapsed.Stop();
    Require(
        elapsed.Elapsed < TimeSpan.FromSeconds(8),
        $"Concurrent client completion exceeded the bounded deadline: {elapsed.Elapsed}.");
    Require(
        fixture.Faults.IsEmpty,
        "Bounded concurrent clients reported a server-session fault.");
    return clientCount;
}

static async Task ConcurrentDisposalAsync()
{
    var handler = new BlockingHandler();
    await using var fixture = new LocalServerFixture(handler);
    var client = await fixture.ConnectAsync().ConfigureAwait(false);
    try
    {
        var callTask = client.CallAsync(
            Encoding.UTF8.GetBytes("active-call"),
            fixture.Token).AsTask();
        await handler.Entered.WaitAsync(fixture.Token).ConfigureAwait(false);

        var queuedCalls = Enumerable.Range(0, 8)
            .Select(index => client.CallAsync(
                Encoding.UTF8.GetBytes($"queued-{index}"),
                fixture.Token).AsTask())
            .ToArray();
        var disposeTask = client.DisposeAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50), fixture.Token).ConfigureAwait(false);
        Require(
            !disposeTask.IsCompleted,
            "Client disposal did not wait for the active framed exchange.");

        handler.Release();
        RequireResponse(await callTask.ConfigureAwait(false), "ACTIVE-CALL");
        await disposeTask.ConfigureAwait(false);
        foreach (var queuedCall in queuedCalls)
        {
            await ExpectAsync<ObjectDisposedException>(async () =>
            {
                _ = await queuedCall.ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        await ExpectAsync<ObjectDisposedException>(async () =>
        {
            _ = await client.CallAsync(
                Encoding.UTF8.GetBytes("after-dispose"),
                fixture.Token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
    finally
    {
        handler.Release();
        await client.DisposeAsync().ConfigureAwait(false);
    }

    Require(fixture.Faults.IsEmpty, "Orderly client disposal reported a session fault.");
}

static async Task ClientRequestBoundaryAsync()
{
    await using var fixture = new LocalServerFixture(new RoutingHandler());
    await using var client = await fixture.ConnectAsync().ConfigureAwait(false);

    await ExpectAsync<ArgumentOutOfRangeException>(async () =>
    {
        _ = await client.CallAsync(
            ReadOnlyMemory<byte>.Empty,
            fixture.Token).ConfigureAwait(false);
    }).ConfigureAwait(false);
    await ExpectAsync<ArgumentOutOfRangeException>(async () =>
    {
        _ = await client.CallAsync(
            new byte[CortexLocalFrameProtocol.MaximumRequestBytes + 1],
            fixture.Token).ConfigureAwait(false);
    }).ConfigureAwait(false);

    var response = await client.CallAsync(
        Encoding.UTF8.GetBytes("connection-preserved"),
        fixture.Token).ConfigureAwait(false);
    RequireResponse(response, "CONNECTION-PRESERVED");
    Require(
        fixture.Faults.IsEmpty,
        "Locally rejected request bytes reached and faulted the server.");
}

static async Task MalformedFrameRecoveryAsync()
{
    await using var fixture = new LocalServerFixture(new RoutingHandler());
    var cases = new (string Name, Func<NamedPipeClientStream, CancellationToken, Task> Write,
        Func<Exception, bool> ExpectedFault)[]
    {
        (
            "zero length",
            (pipe, token) => WriteRawPrefixAsync(pipe, 0, token),
            exception => exception is InvalidDataException),
        (
            "negative length",
            (pipe, token) => WriteRawPrefixAsync(pipe, -1, token),
            exception => exception is InvalidDataException),
        (
            "oversized length",
            (pipe, token) => WriteRawPrefixAsync(
                pipe,
                CortexLocalFrameProtocol.MaximumRequestBytes + 1,
                token),
            exception => exception is InvalidDataException),
        (
            "truncated prefix",
            WriteTruncatedPrefixAsync,
            exception => exception is EndOfStreamException),
        (
            "truncated payload",
            WriteTruncatedPayloadAsync,
            exception => exception is EndOfStreamException)
    };

    for (var index = 0; index < cases.Length; index++)
    {
        var malformed = cases[index];
        await using (var pipe = await ConnectRawAsync(
            fixture.Endpoint,
            fixture.Token).ConfigureAwait(false))
        {
            try
            {
                await malformed.Write(pipe, fixture.Token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The server may reject the prefix before the peer flush completes.
            }
        }

        var fault = await WaitForFaultAsync(
            fixture.Faults,
            index + 1,
            fixture.Token).ConfigureAwait(false);
        Require(
            malformed.ExpectedFault(fault),
            $"{malformed.Name} produced {fault.GetType().Name}, not its framing fault.");
        await AssertServerRoundTripAsync(
            fixture,
            $"recovered-{index}").ConfigureAwait(false);
    }
}

static Task EmptyResponseRecoveryAsync() =>
    InvalidResponseRecoveryAsync(
        "empty",
        exception => exception is ArgumentOutOfRangeException);

static Task OversizedResponseRecoveryAsync() =>
    InvalidResponseRecoveryAsync(
        "oversized",
        exception => exception is ArgumentOutOfRangeException);

static Task HandlerFaultRecoveryAsync() =>
    InvalidResponseRecoveryAsync(
        "fault",
        exception => exception is InvalidOperationException);

static async Task HandlerTimeoutRecoveryAsync()
{
    var blockedHandler = new NonCooperativeHandler();
    var sessions = 0;
    await using var fixture = new LocalServerFixture(
        () => Interlocked.Increment(ref sessions) == 1
            ? blockedHandler
            : new RoutingHandler(),
        frameIdleTimeout: TimeSpan.FromMilliseconds(150));

    await using (var client = await fixture.ConnectAsync().ConfigureAwait(false))
    {
        await ExpectSessionTerminationAsync(async () =>
        {
            _ = await client.CallAsync(
                Encoding.UTF8.GetBytes("blocked-handler"),
                fixture.Token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    var fault = await WaitForFaultAsync(
        fixture.Faults,
        expectedCount: 1,
        fixture.Token).ConfigureAwait(false);
    Require(
        fault is TimeoutException,
        $"The non-cooperative handler reported {fault.GetType().Name}, not TimeoutException.");
    await AssertServerRoundTripAsync(
        fixture,
        "after-handler-timeout").ConfigureAwait(false);
    blockedHandler.Release();
}

static async Task InvalidResponseRecoveryAsync(
    string request,
    Func<Exception, bool> expectedFault)
{
    await using var fixture = new LocalServerFixture(new RoutingHandler());
    await using (var pipe = await ConnectRawAsync(
        fixture.Endpoint,
        fixture.Token).ConfigureAwait(false))
    {
        await WriteRawRequestAsync(
            pipe,
            Encoding.UTF8.GetBytes(request),
            fixture.Token).ConfigureAwait(false);

        var markerObserved = false;
        try
        {
            var marker = new byte[1];
            markerObserved =
                await pipe.ReadAsync(marker, fixture.Token).ConfigureAwait(false) != 0;
        }
        catch (IOException)
        {
            // A broken pipe before any marker is the expected session boundary.
        }

        Require(
            !markerObserved,
            $"The '{request}' handler result emitted a response marker before validation.");
    }

    var fault = await WaitForFaultAsync(
        fixture.Faults,
        expectedCount: 1,
        fixture.Token).ConfigureAwait(false);
    Require(
        expectedFault(fault),
        $"The '{request}' handler path reported unexpected {fault.GetType().Name}.");
    await AssertServerRoundTripAsync(fixture, $"after-{request}").ConfigureAwait(false);
}

static async Task IdleSessionFairnessAsync()
{
    await using var fixture = new LocalServerFixture(
        new RoutingHandler(),
        frameIdleTimeout: TimeSpan.FromMilliseconds(150));
    await using var idlePipe = await ConnectRawAsync(
        fixture.Endpoint,
        fixture.Token).ConfigureAwait(false);

    var queuedClientTask = CortexLocalPipeClient.ConnectAsync(
        fixture.Endpoint,
        TimeSpan.FromSeconds(3),
        fixture.Token).AsTask();

    await using var queuedClient = await queuedClientTask.ConfigureAwait(false);
    var response = await queuedClient.CallAsync(
        Encoding.UTF8.GetBytes("fairness"),
        fixture.Token).ConfigureAwait(false);
    RequireResponse(response, "FAIRNESS");

    var fault = await WaitForFaultAsync(
        fixture.Faults,
        expectedCount: 1,
        fixture.Token).ConfigureAwait(false);
    Require(
        fault is TimeoutException,
        $"The idle session reported {fault.GetType().Name}, not TimeoutException.");
}

static async Task RequestLimitReconnectAsync()
{
    await using var fixture = new LocalServerFixture(
        new RoutingHandler(),
        maximumRequestsPerSession: 2);
    await using (var firstClient = await fixture.ConnectAsync().ConfigureAwait(false))
    {
        RequireResponse(
            await firstClient.CallAsync(
                Encoding.UTF8.GetBytes("budget-1"),
                fixture.Token).ConfigureAwait(false),
            "BUDGET-1");
        RequireResponse(
            await firstClient.CallAsync(
                Encoding.UTF8.GetBytes("budget-2"),
                fixture.Token).ConfigureAwait(false),
            "BUDGET-2");
        await ExpectSessionTerminationAsync(async () =>
        {
            _ = await firstClient.CallAsync(
                Encoding.UTF8.GetBytes("budget-3"),
                fixture.Token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    await AssertServerRoundTripAsync(fixture, "reconnected-budget").ConfigureAwait(false);
    Require(
        fixture.Faults.IsEmpty,
        "Normal request-budget retirement was reported as a server fault.");
}

static async Task ServerCancellationAsync()
{
    var endpoint = CortexLocalEndpoint.Create($"cancel-{Guid.NewGuid():N}");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var faults = new ConcurrentQueue<Exception>();
    var server = new CortexLocalPipeServer(
        endpoint,
        new RoutingHandler(),
        faults.Enqueue);
    var serverTask = server.RunAsync(cancellation.Token);
    await using var pipe = await ConnectRawAsync(endpoint, cancellation.Token)
        .ConfigureAwait(false);

    cancellation.Cancel();
    await ExpectAsync<OperationCanceledException>(
        () => serverTask).ConfigureAwait(false);
    Require(
        faults.IsEmpty,
        "Global server cancellation was incorrectly reported as a session fault.");
}

static async Task ConnectionTimeoutBoundaryAsync()
{
    await ExpectAsync<ArgumentOutOfRangeException>(() =>
        CortexLocalPipeClient.ConnectAsync(
            CortexLocalEndpoint.Create("timeout-test"),
            TimeSpan.FromMinutes(1),
            CancellationToken.None).AsTask()).ConfigureAwait(false);
}

static async Task AssertServerRoundTripAsync(
    LocalServerFixture fixture,
    string request)
{
    await using var client = await fixture.ConnectAsync().ConfigureAwait(false);
    var response = await client.CallAsync(
        Encoding.UTF8.GetBytes(request),
        fixture.Token).ConfigureAwait(false);
    RequireResponse(response, request.ToUpperInvariant());
}

static async Task<NamedPipeClientStream> ConnectRawAsync(
    CortexLocalEndpoint endpoint,
    CancellationToken cancellationToken)
{
    var pipe = new NamedPipeClientStream(
        ".",
        endpoint.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.WriteThrough);
    try
    {
        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return pipe;
    }
    catch
    {
        await pipe.DisposeAsync().ConfigureAwait(false);
        throw;
    }
}

static async Task WriteRawRequestAsync(
    Stream stream,
    ReadOnlyMemory<byte> payload,
    CancellationToken cancellationToken)
{
    await WriteRawPrefixCoreAsync(
        stream,
        payload.Length,
        cancellationToken,
        flush: false).ConfigureAwait(false);
    await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static Task WriteRawPrefixAsync(
    NamedPipeClientStream stream,
    int length,
    CancellationToken cancellationToken) =>
    WriteRawPrefixCoreAsync(stream, length, cancellationToken, flush: true);

static async Task WriteRawPrefixCoreAsync(
    Stream stream,
    int length,
    CancellationToken cancellationToken,
    bool flush)
{
    var prefix = new byte[sizeof(int)];
    BinaryPrimitives.WriteInt32BigEndian(prefix, length);
    await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
    if (flush)
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static async Task WriteTruncatedPrefixAsync(
    NamedPipeClientStream stream,
    CancellationToken cancellationToken)
{
    var prefix = new byte[sizeof(int)];
    BinaryPrimitives.WriteInt32BigEndian(prefix, 8);
    await stream.WriteAsync(
        prefix.AsMemory(0, prefix.Length - 1),
        cancellationToken).ConfigureAwait(false);
    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static async Task WriteTruncatedPayloadAsync(
    NamedPipeClientStream stream,
    CancellationToken cancellationToken)
{
    await WriteRawPrefixCoreAsync(
        stream,
        length: 8,
        cancellationToken,
        flush: false).ConfigureAwait(false);
    await stream.WriteAsync(
        Encoding.UTF8.GetBytes("short"),
        cancellationToken).ConfigureAwait(false);
    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static async Task<ReadOnlyMemory<byte>?> ReadRawResponseAsync(
    Stream stream,
    CancellationToken cancellationToken)
{
    var marker = new byte[1];
    await ReadExactlyAsync(stream, marker, cancellationToken).ConfigureAwait(false);
    if (marker[0] == 0)
        return null;
    Require(marker[0] == 1, $"The raw response marker was {marker[0]}.");

    var prefix = new byte[sizeof(int)];
    await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
    var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
    Require(
        length is >= 1 and <= CortexLocalFrameProtocol.MaximumResponseBytes,
        $"The raw response length {length} was outside the protocol boundary.");

    var payload = new byte[length];
    await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    return payload;
}

static async Task ReadExactlyAsync(
    Stream stream,
    Memory<byte> destination,
    CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < destination.Length)
    {
        var read = await stream.ReadAsync(
            destination[offset..],
            cancellationToken).ConfigureAwait(false);
        if (read == 0)
            throw new EndOfStreamException("The raw test frame was truncated.");
        offset += read;
    }
}

static async Task<Exception> WaitForFaultAsync(
    ConcurrentQueue<Exception> faults,
    int expectedCount,
    CancellationToken cancellationToken)
{
    var elapsed = Stopwatch.StartNew();
    while (faults.Count < expectedCount)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(
            elapsed.Elapsed < TimeSpan.FromSeconds(3),
            $"Timed out waiting for session fault {expectedCount}.");
        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
            .ConfigureAwait(false);
    }

    return faults.ToArray()[expectedCount - 1];
}

static void RequireResponse(
    ReadOnlyMemory<byte>? response,
    string expected)
{
    if (response is not { } payload)
    {
        throw new InvalidOperationException(
            $"Expected response '{expected}', but received a notification.");
    }

    var actual = Encoding.UTF8.GetString(payload.Span);
    Require(
        actual == expected,
        $"Expected response '{expected}', but received '{actual}'.");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Expect<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task ExpectAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action().ConfigureAwait(false);
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task ExpectSessionTerminationAsync(Func<Task> action)
{
    try
    {
        await action().ConfigureAwait(false);
    }
    catch (Exception exception) when (
        exception is IOException or ObjectDisposedException)
    {
        return;
    }

    throw new InvalidOperationException("Expected the server to retire the client session.");
}

sealed class LocalServerFixture : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown =
        new(TimeSpan.FromSeconds(15));

    public LocalServerFixture(
        ICortexLocalRequestHandler handler,
        TimeSpan? frameIdleTimeout = null,
        int maximumRequestsPerSession =
            CortexLocalPipeServer.DefaultMaximumRequestsPerSession)
        : this(
            () => handler,
            frameIdleTimeout,
            maximumRequestsPerSession)
    {
    }

    public LocalServerFixture(
        Func<ICortexLocalRequestHandler> handlerFactory,
        TimeSpan? frameIdleTimeout = null,
        int maximumRequestsPerSession =
            CortexLocalPipeServer.DefaultMaximumRequestsPerSession)
    {
        Endpoint = CortexLocalEndpoint.Create($"test-{Guid.NewGuid():N}");
        var server = new CortexLocalPipeServer(
            Endpoint,
            handlerFactory,
            Faults.Enqueue,
            frameIdleTimeout,
            maximumRequestsPerSession);
        ServerTask = server.RunAsync(_shutdown.Token);
    }

    public CortexLocalEndpoint Endpoint { get; }
    public ConcurrentQueue<Exception> Faults { get; } = new();
    public Task ServerTask { get; }
    public CancellationToken Token => _shutdown.Token;

    public ValueTask<CortexLocalPipeClient> ConnectAsync() =>
        CortexLocalPipeClient.ConnectAsync(
            Endpoint,
            TimeSpan.FromSeconds(5),
            Token);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await ServerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}

sealed class RoutingHandler : ICortexLocalRequestHandler
{
    public ValueTask<ReadOnlyMemory<byte>?> ProcessAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = Encoding.UTF8.GetString(request.Span);
        if (text == "notify")
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        if (text == "empty")
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(ReadOnlyMemory<byte>.Empty);
        if (text == "oversized")
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                new byte[CortexLocalFrameProtocol.MaximumResponseBytes + 1]);
        }
        if (text == "fault")
            throw new InvalidOperationException("Synthetic local handler fault.");

        ReadOnlyMemory<byte> response =
            Encoding.UTF8.GetBytes(text.ToUpperInvariant());
        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(response);
    }
}

sealed class BlockingHandler : ICortexLocalRequestHandler
{
    private readonly TaskCompletionSource<bool> _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult(true);

    public async ValueTask<ReadOnlyMemory<byte>?> ProcessAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        _entered.TrySetResult(true);
        await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(request.Span);
        return Encoding.UTF8.GetBytes(text.ToUpperInvariant());
    }
}

sealed class NonCooperativeHandler : ICortexLocalRequestHandler
{
    private readonly TaskCompletionSource<ReadOnlyMemory<byte>?> _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() =>
        _release.TrySetResult(Encoding.UTF8.GetBytes("released"));

    public ValueTask<ReadOnlyMemory<byte>?> ProcessAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken) =>
        new(_release.Task);
}
