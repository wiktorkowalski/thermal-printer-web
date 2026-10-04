using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ThermalPrinterWeb.Services.Journal;

// The schema version is the migration list: table __EFMigrationsHistory.
// To change the schema: change the entities, then in backend/ run
// "dotnet ef migrations add <Name> --output-dir Services/Journal/Migrations --namespace ThermalPrinterWeb.Services.Journal.Migrations".
// A migration only adds: the rows of the journal stay.
internal sealed class JournalDbContext(DbContextOptions<JournalDbContext> options) : DbContext(options)
{
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<PrintJobPayload> PrintJobPayloads => Set<PrintJobPayload>();
    public DbSet<PrintJobText> PrintJobTexts => Set<PrintJobText>();

    // No pool: the last connection that closes folds the write-ahead log into the one database file.
    public static string ConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PrintJob>(job =>
        {
            job.HasKey(j => j.Id);
            job.HasIndex(j => j.CreatedAt);
            job.HasOne(j => j.Payload)
                .WithOne()
                .HasForeignKey<PrintJobPayload>(payload => payload.JobId)
                .OnDelete(DeleteBehavior.Cascade);
            job.HasOne(j => j.Text)
                .WithOne()
                .HasForeignKey<PrintJobText>(text => text.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PrintJobPayload>().HasKey(payload => payload.JobId);
        modelBuilder.Entity<PrintJobText>().HasKey(text => text.JobId);
    }
}

// For "dotnet ef": the tool builds the context here and does not start the app.
internal sealed class JournalDbContextDesignFactory : IDesignTimeDbContextFactory<JournalDbContext>
{
    public JournalDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlite(JournalDbContext.ConnectionString("design-time.db"))
            .Options);
}
