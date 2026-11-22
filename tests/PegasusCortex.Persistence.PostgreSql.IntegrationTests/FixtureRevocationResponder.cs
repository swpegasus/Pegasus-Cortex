using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

/// <summary>
/// Serves one signed public CRL from a literal loopback endpoint for the contained
/// integration fixture. It is never composed into the Cortex product runtime.
/// </summary>
internal sealed class FixtureRevocationResponder : IAsyncDisposable
{
    private const int MaximumRequestHeaderBytes = 4096;

    private readonly FixtureRevocationEvidence _evidence;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _worker;
    private int _servedRequests;
    private int _disposed;

    private FixtureRevocationResponder(
        FixtureRevocationEvidence evidence,
        TcpListener listener,
        CancellationTokenSource lifetime)
    {
        _evidence = evidence;
        _listener = listener;
        _lifetime = lifetime;
        _worker = RunAsync();
    }

    internal int ServedRequests => Volatile.Read(ref _servedRequests);

    internal static async ValueTask<FixtureRevocationResponder> StartAsync(
        FixtureRevocationEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        var endpoint = new IPEndPoint(
            IPAddress.Parse(evidence.DistributionPointUri.Host),
            evidence.DistributionPointUri.Port);
        var listener = new TcpListener(endpoint)
        {
            ExclusiveAddressUse = true
        };
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        FixtureRevocationResponder? responder = null;
        try
        {
            listener.Start(backlog: 4);
            responder = new FixtureRevocationResponder(evidence, listener, lifetime);
            await responder.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return responder;
        }
        catch (FixtureConfigurationException)
        {
            if (responder is not null)
                await responder.DisposeAsync().ConfigureAwait(false);
            else
            {
                listener.Stop();
                lifetime.Dispose();
            }
            throw;
        }
        catch (Exception exception) when (
            exception is SocketException or IOException or HttpRequestException or
            OperationCanceledException)
        {
            if (responder is not null)
                await responder.DisposeAsync().ConfigureAwait(false);
            else
            {
                listener.Stop();
                lifetime.Dispose();
            }
            if (exception is OperationCanceledException)
                throw;
            throw new FixtureConfigurationException(
                "fixture_revocation_responder_unavailable",
                exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();
        _listener.Stop();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the owner cancels or disposes the bounded listener.
        }
        catch (SocketException) when (_lifetime.IsCancellationRequested)
        {
            // TcpListener.Stop interrupts an outstanding accept during disposal.
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener
                    .AcceptTcpClientAsync(_lifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }

            using (client)
            {
                await HandleAsync(client, _lifetime.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var requestNumber = Interlocked.Increment(ref _servedRequests);
        if (requestNumber > _evidence.MaximumRequests)
        {
            await WriteResponseAsync(
                    client.GetStream(),
                    "429 Too Many Requests",
                    "text/plain",
                    [],
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(_evidence.RequestTimeout);
        var stream = client.GetStream();
        byte[] request;
        try
        {
            request = await ReadRequestAsync(stream, requestTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var firstLine = ReadFirstLine(request);
        var expectedPath = _evidence.DistributionPointUri.PathAndQuery;
        if (!string.Equals(
                firstLine,
                $"GET {expectedPath} HTTP/1.1",
                StringComparison.Ordinal) &&
            !string.Equals(
                firstLine,
                $"GET {expectedPath} HTTP/1.0",
                StringComparison.Ordinal))
        {
            await WriteResponseAsync(
                    stream,
                    "404 Not Found",
                    "text/plain",
                    [],
                    requestTimeout.Token)
                .ConfigureAwait(false);
            return;
        }

        await WriteResponseAsync(
                stream,
                "200 OK",
                "application/pkix-crl",
                _evidence.CrlBytes,
                requestTimeout.Token)
            .ConfigureAwait(false);
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = _evidence.RequestTimeout,
            UseCookies = false,
            UseProxy = false
        };
        using var client = new HttpClient(handler)
        {
            Timeout = _evidence.RequestTimeout,
            MaxResponseContentBufferSize = _evidence.MaximumResponseBytes
        };
        try
        {
            using var response = await client
                .GetAsync(
                    _evidence.DistributionPointUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentLength != _evidence.CrlBytes.Length ||
                !string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "application/pkix-crl",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new FixtureConfigurationException(
                    "fixture_revocation_responder_probe_invalid");
            }

            var bytes = await response.Content
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (bytes.Length > _evidence.MaximumResponseBytes ||
                !bytes.AsSpan().SequenceEqual(_evidence.CrlBytes))
            {
                throw new FixtureConfigurationException(
                    "fixture_revocation_responder_probe_invalid");
            }
        }
        catch (FixtureConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new FixtureConfigurationException(
                "fixture_revocation_responder_unreachable",
                exception);
        }
    }

    private static async Task<byte[]> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumRequestHeaderBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            count += read;
            if (HeaderTerminated(buffer.AsSpan(0, count)))
                return buffer[..count];
        }
        throw new FixtureConfigurationException(
            "fixture_revocation_responder_request_invalid");
    }

    private static bool HeaderTerminated(ReadOnlySpan<byte> bytes) =>
        bytes.IndexOf("\r\n\r\n"u8) >= 0;

    private static string ReadFirstLine(byte[] request)
    {
        var end = request.AsSpan().IndexOf("\r\n"u8);
        if (end <= 0)
            throw new FixtureConfigurationException(
                "fixture_revocation_responder_request_invalid");
        return Encoding.ASCII.GetString(request, 0, end);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        string status,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
