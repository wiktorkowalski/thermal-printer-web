using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalPrinterWeb.Controllers;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Tests;

// Takes the decode slot and keeps it until the test disposes it.
internal sealed class HeldSlot : IAsyncDisposable
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _holder;

    // The slot is free, so RunAsync takes it before it returns.
    public HeldSlot(DecodeQueue queue) => _holder = queue.RunAsync(() => _release.Task);

    public async ValueTask DisposeAsync()
    {
        _release.TrySetResult();
        await _holder;
    }
}

public sealed class DecodeQueueTests
{
    // Longer than any test: a waiter leaves the queue only when the slot is free.
    internal static readonly TimeSpan NoTimeout = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Waiters_UpToTheLimit_RunAndOneMoreIsRejectedAtOnce()
    {
        var queue = new DecodeQueue(maxWaiters: 2, NoTimeout);
        var ran = 0;
        Task Decode() => queue.RunAsync(() =>
        {
            ran++;
            return Task.CompletedTask;
        });

        Task first, second;
        await using (new HeldSlot(queue))
        {
            first = Decode();
            second = Decode();
            Assert.Equal(2, queue.Waiting);

            // No await before the assert: the rejection does not wait for the slot.
            var rejected = Decode();
            Assert.True(rejected.IsFaulted);
            var ex = await Assert.ThrowsAsync<PrintBusyException>(() => rejected);
            Assert.Equal("the image decode queue is full (limit 2 waiting jobs)", ex.Message);

            Assert.Equal(2, queue.Waiting);
            Assert.False(first.IsCompleted || second.IsCompleted);
            Assert.Equal(0, ran);
        }

        await Task.WhenAll(first, second);
        Assert.Equal(2, ran);
        Assert.Equal(0, queue.Waiting);
    }

    [Fact]
    public async Task Decodes_FromManyCallers_RunOneAtATime()
    {
        var queue = new DecodeQueue(maxWaiters: 8, NoTimeout);
        var running = 0;
        var mostAtOnce = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => queue.RunAsync(async () =>
        {
            mostAtOnce = Math.Max(mostAtOnce, Interlocked.Increment(ref running));
            await Task.Yield();
            Interlocked.Decrement(ref running);
        }))));

        Assert.Equal(1, mostAtOnce);
    }

    public static TheoryData<Func<Task>> FailingDecodes() => new()
    {
        () => throw new InvalidOperationException("before the first await"),
        async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("after an await");
        }
    };

    // No waiter is allowed, so the second decode runs only when the first one gave the slot back.
    [Theory]
    [MemberData(nameof(FailingDecodes))]
    public async Task Slot_AfterADecodeThrows_IsFree(Func<Task> failingDecode)
    {
        var queue = new DecodeQueue(maxWaiters: 0, NoTimeout);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RunAsync(failingDecode));

        var ran = false;
        await queue.RunAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        Assert.True(ran);
    }

    [Fact]
    public async Task Waiter_PastTheTimeout_IsRejectedAndLeavesTheQueue()
    {
        var queue = new DecodeQueue(maxWaiters: 1, TimeSpan.FromMilliseconds(50));
        var ran = 0;
        Task Decode() => queue.RunAsync(() =>
        {
            ran++;
            return Task.CompletedTask;
        });

        Task next;
        await using (new HeldSlot(queue))
        {
            var ex = await Assert.ThrowsAsync<PrintBusyException>(Decode);

            Assert.Equal("no image decode slot after 50 ms (limit 1 waiting jobs)", ex.Message);
            Assert.Equal(0, queue.Waiting);
            Assert.Equal(0, ran);

            // The waiter that gave up left its place: the next one is accepted, not rejected as "queue full".
            next = Decode();
            Assert.Equal(1, queue.Waiting);
        }

        // The timeout did not take the slot with it.
        await next;
        Assert.Equal(1, ran);
    }
}

// The busy path from the handler to the response. No test here reaches the network:
// a busy job stops while the document is built.
public sealed class DecodeQueueBusyTests(ClosedPortApp app) : IClassFixture<ClosedPortApp>
{
    private static string PngBase64() => TestImages.PngBase64(64, 64);

    [Fact]
    public async Task Handler_NoPlaceInTheQueue_ThrowsBusyAndAddsNoOutput()
    {
        var queue = new DecodeQueue(maxWaiters: 0, DecodeQueueTests.NoTimeout);
        var ctx = TestBlocks.NewContext();

        await using (new HeldSlot(queue))
            await Assert.ThrowsAsync<PrintBusyException>(() => new ImageBlockHandler(queue).HandleAsync(TestBlocks.ImageBlock(PngBase64()), ctx));

        Assert.Empty(ctx.Output);
    }

    // The cheap checks run before the queue: a bad image gets its 400 while the server is busy.
    [Fact]
    public async Task Handler_RejectedImage_DoesNotWaitForTheQueue()
    {
        var queue = new DecodeQueue(maxWaiters: 0, DecodeQueueTests.NoTimeout);

        await using (new HeldSlot(queue))
            await Assert.ThrowsAsync<PrintContentException>(() => new ImageBlockHandler(queue).HandleAsync(TestBlocks.ImageBlock("not base64 !!"), TestBlocks.NewContext()));
    }

    // No waiter is allowed, so the second image prints only when the failed decode gave the slot back.
    [Fact]
    public async Task Handler_AfterADamagedImage_PrintsTheNextOne()
    {
        var handler = new ImageBlockHandler(new DecodeQueue(maxWaiters: 0, DecodeQueueTests.NoTimeout));
        // The cut lands inside the pixel data: the header check passes, the decode fails.
        var noise = TestImages.NoisePng(seed: 75);

        var ex = await Assert.ThrowsAsync<PrintContentException>(
            () => handler.HandleAsync(TestBlocks.ImageBlock(Convert.ToBase64String(noise[..(noise.Length / 2)])), TestBlocks.NewContext()));
        Assert.Contains("file is damaged", ex.Message);

        var ctx = TestBlocks.NewContext();
        await handler.HandleAsync(TestBlocks.ImageBlock(PngBase64()), ctx);
        Assert.NotEmpty(ctx.Output);
    }

    [Fact]
    public async Task PrintAsync_NoPlaceInTheQueue_IsABusyFailureLoggedOnce()
    {
        var queue = new DecodeQueue(maxWaiters: 0, DecodeQueueTests.NoTimeout);
        var logger = new RecordingLogger<PrinterService>();
        var service = new PrinterService(logger, [new ImageBlockHandler(queue)], NoPrinter.Options);

        PrintResult result;
        await using (new HeldSlot(queue))
            result = await service.PrintAsync([TestBlocks.ImageBlock(PngBase64())]);

        Assert.Same(PrintResult.Busy, result);
        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Rejected print: server busy, the image decode queue is full (limit 0 waiting jobs)", entry.Message);
    }

    [Fact]
    public async Task Print_BusyFailure_Returns503WithRetryAfter()
    {
        var printer = new RecordingPrinter { Result = PrintResult.Busy };
        var controller = new PrinterController(printer, new PrintJobLog(NullLogger<PrintJobLog>.Instance, new HttpContextAccessor()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var response = Assert.IsAssignableFrom<ObjectResult>(await controller.Print(
            new PrintRequest { Content = [TestBlocks.ImageBlock(PngBase64())] }, TimeProvider.System, TestBlocks.NewSignature(printer)));

        Assert.Equal(503, response.StatusCode);
        Assert.Equal(new PrintResponse(false, PrintResult.Busy.Error, "busy"), response.Value);
        Assert.Equal("5", controller.Response.Headers.RetryAfter);
    }

    // The real pipeline and the process-wide queue: one decode holds the slot, the queue is full, one more job arrives.
    [Fact]
    public async Task PostPrinter_SharedQueueFull_Returns503AndLeavesTheQueue()
    {
        var queue = ImageBlockHandler.SharedQueue;
        var client = app.CreateClient();
        Task[] waiters;
        HttpResponseMessage response;

        await using (new HeldSlot(queue))
        {
            waiters = [.. Enumerable.Range(0, ImageBlockHandler.MaxDecodeWaiters).Select(_ => queue.RunAsync(() => Task.CompletedTask))];
            Assert.Equal(ImageBlockHandler.MaxDecodeWaiters, queue.Waiting);

            response = await client.PostAsJsonAsync("/api/printer", new { content = new[] { new { type = "Image", content = PngBase64() } } });

            Assert.Equal(ImageBlockHandler.MaxDecodeWaiters, queue.Waiting);
        }

        await Task.WhenAll(waiters);
        Assert.Equal(0, queue.Waiting);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        Assert.Equal(
            new PrintResponse(false, "Server busy: too many image jobs wait for a decode. Send the job again in a few seconds.", "busy"),
            await response.Content.ReadFromJsonAsync<PrintResponse>());
    }
}
