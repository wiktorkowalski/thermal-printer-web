using System.Net;
using System.Net.Sockets;

namespace ThermalPrinterWeb.Tests.Support;

// A printer on a loopback port. It answers the DLE EOT status queries and keeps every print job it gets.
internal sealed class FakeStatusPrinter : IDisposable
{
    // What the real printer answers when it is ready (2026-10-04).
    public const byte IdleN1 = 0x16;
    public const byte Idle = 0x12;

    private const byte Dle = 0x10;
    private const byte Eot = 0x04;
    private const int QueryLength = 3;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<int> _queries = [];
    private readonly List<byte[]> _jobs = [];
    private readonly TaskCompletionSource<byte[]> _firstJob = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(10);

    public FakeStatusPrinter()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    // The answer to each query. Null: the printer stays silent.
    public byte? N1 { get; init; } = IdleN1;
    public byte? N2 { get; init; } = Idle;
    public byte? N3 { get; init; } = Idle;
    public byte? N4 { get; init; } = Idle;

    // The printer closes the connection when it gets DLE EOT 3.
    public bool DropsAtErrorQuery { get; init; }

    // The n of every DLE EOT query, in the order of arrival.
    public int[] Queries
    {
        get
        {
            lock (_lock)
                return [.. _queries];
        }
    }

    // Every connection that did not start with a status query.
    public byte[][] Jobs
    {
        get
        {
            lock (_lock)
                return [.. _jobs];
        }
    }

    // The first job, complete: the sender closed its connection.
    public Task<byte[]> FirstJobAsync() => _firstJob.Task.WaitAsync(JobTimeout);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
                _ = ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token));
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The test is over.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var connection = client;
        try
        {
            var stream = connection.GetStream();
            var head = new byte[QueryLength];
            while (true)
            {
                var read = await stream.ReadAtLeastAsync(head, QueryLength, throwOnEndOfStream: false, _stop.Token);
                if (read == 0)
                    return;

                if (read < QueryLength || head[0] != Dle || head[1] != Eot)
                {
                    using var job = new MemoryStream();
                    job.Write(head, 0, read);
                    await stream.CopyToAsync(job, _stop.Token);
                    var bytes = job.ToArray();
                    lock (_lock)
                        _jobs.Add(bytes);
                    _firstJob.TrySetResult(bytes);
                    return;
                }

                int n = head[2];
                lock (_lock)
                    _queries.Add(n);
                if (n == 3 && DropsAtErrorQuery)
                    return;

                var answer = n switch { 1 => N1, 2 => N2, 3 => N3, 4 => N4, _ => null };
                if (answer is { } value)
                    await stream.WriteAsync(new[] { value }, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The caller closed the connection, or the test is over.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        _stop.Dispose();
    }
}
