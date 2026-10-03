namespace ThermalPrinterWeb.Services.Printing;

// Server load, not a payload fault: the same job passes when it is sent again.
// The message holds queue facts only. It goes to the log, not to the caller.
internal sealed class PrintBusyException(string message) : Exception(message);

// One image decode at a time, and a limit on the jobs that wait for it:
// each waiter keeps its image in memory, and the endpoint is public.
internal sealed class DecodeQueue(int maxWaiters, TimeSpan waitTimeout)
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private int _waiting;

    internal int Waiting => Volatile.Read(ref _waiting);

    // Throws PrintBusyException when the queue is full or the wait passes the timeout.
    public async Task RunAsync(Func<Task> decode)
    {
        await EnterAsync();
        try
        {
            await decode();
        }
        finally
        {
            _slot.Release();
        }
    }

    private async Task EnterAsync()
    {
        // A free slot is taken at once. Release gives the slot to a waiter directly, so this does not pass the queue.
        if (_slot.Wait(0))
            return;

        try
        {
            if (Interlocked.Increment(ref _waiting) > maxWaiters)
                throw new PrintBusyException($"the image decode queue is full (limit {maxWaiters} waiting jobs)");

            if (!await _slot.WaitAsync(waitTimeout))
                throw new PrintBusyException($"no image decode slot after {(long)waitTimeout.TotalMilliseconds} ms (limit {maxWaiters} waiting jobs)");
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }
}
