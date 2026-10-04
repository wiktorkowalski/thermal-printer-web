using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ThermalPrinterWeb.Tests.Support;

// A printer on a loopback port: answers each status query as ready and keeps the bytes of each job.
internal sealed class WirePrinter : IAsyncDisposable
{
    // Online, cover closed, paper present.
    private const byte ReadyStatus = 0x12;

    // DLE EOT n: the first two bytes of a status query.
    private const byte Dle = 0x10;
    private const byte Eot = 0x04;

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        byte[]? job;
        while (!Jobs.TryDequeue(out job))
            await Task.Delay(10, timeout.Token);
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
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var job = new MemoryStream();
            var buffer = new byte[64 * 1024];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                {
                    if (job.Length == 0 && read == 3 && buffer[0] == Dle && buffer[1] == Eot)
                        await stream.WriteAsync(new[] { ReadyStatus }, _stop.Token);
                    else
                        job.Write(buffer, 0, read);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
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
