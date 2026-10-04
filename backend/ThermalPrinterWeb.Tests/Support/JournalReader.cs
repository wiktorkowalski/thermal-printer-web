using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Tests.Support;

// Reads the print journal of a test host.
internal static class JournalReader
{
    // Done when the journal writer stored, or gave up, what the requests so far handed over.
    public static Task JournalIdleAsync(this TestApp app) => app.Services.GetRequiredService<PrintJournal>().DrainAsync();

    // Every row, oldest first.
    public static async Task<List<PrintJob>> JournalRowsAsync(this TestApp app)
    {
        await app.JournalIdleAsync();

        await using var db = app.JournalDb();
        return await db.PrintJobs.Include(job => job.Payload).OrderBy(job => job.CreatedAt).ThenBy(job => job.Id).ToListAsync();
    }

    public static JournalDbContext JournalDb(this TestApp app) => app.Services.GetRequiredService<JournalDatabase>().CreateContext();
}
