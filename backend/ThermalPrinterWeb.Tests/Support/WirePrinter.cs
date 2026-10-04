using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ThermalPrinterWeb.Tests.Support;

// A printer on a loopback port: answers each status query as ready and keeps the bytes of each job.
internal sealed class WirePrinter : IAsyncDisposable
{
    // Online, cover closed, paper present.
    public const byte ReadyStatus = 0x12;

    // DLE EOT n: a status query is three bytes.
    private const byte Dle = 0x10;
    private const byte Eot = 0x04;
    private const int StatusQueryLength = 3;

    private const int ReadBufferBytes = 64 * 1024;

    // A job that does not come fails the test; it must not hang the run.
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan JobPollInterval = TimeSpan.FromMilliseconds(10);

    private static readonly byte[] ReadyAnswer = [ReadyStatus];

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Task> _connections = [];
    private readonly Task _accept;

    public WirePrinter()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptAsync();
    }

    public int Port { get; }

    // One entry per connection that sent something other than status queries.
    public ConcurrentQueue<byte[]> Jobs { get; } = [];

    // The job connection closes after the print call answers: wait for the bytes.
    public async Task<byte[]> NextJobAsync()
    {
        using var timeout = new CancellationTokenSource(JobTimeout);
        byte[]? job;
        while (!Jobs.TryDequeue(out job))
            await Task.Delay(JobPollInterval, timeout.Token);
        return job;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
                _connections.Enqueue(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
        }
        catch (OperationCanceledException)
        {
            // DisposeAsync stopped the listener.
        }
    }

    // The number of status queries the bytes hold; 0 when they are anything else.
    private static int StatusQueries(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % StatusQueryLength != 0)
            return 0;

        for (var i = 0; i < bytes.Length; i += StatusQueryLength)
        {
            if (bytes[i] != Dle || bytes[i + 1] != Eot)
                return 0;
        }

        return bytes.Length / StatusQueryLength;
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var job = new MemoryStream();
            var buffer = new byte[ReadBufferBytes];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                {
                    // On a slow machine two queries can come in one read: each one gets its answer.
                    if (job.Length == 0 && StatusQueries(buffer.AsSpan(0, read)) is var queries and > 0)
                    {
                        for (var i = 0; i < queries; i++)
                            await stream.WriteAsync(ReadyAnswer, _stop.Token);
                    }
                    else
                    {
                        job.Write(buffer, 0, read);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The test ended, or the app closed the connection: what came so far is the job.
            }

            if (job.Length > 0)
                Jobs.Enqueue(job.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Dispose();
        await _accept;
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }
}
