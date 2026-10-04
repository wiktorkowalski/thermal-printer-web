using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThermalPrinterWeb.Services.Journal.Migrations
{
    /// <inheritdoc />
    public partial class InitialJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrintJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Transport = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", nullable: true),
                    RemoteIp = table.Column<string>(type: "TEXT", nullable: true),
                    Result = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    HttpStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    BlockCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ByteCount = table.Column<int>(type: "INTEGER", nullable: true),
                    PaperDots = table.Column<int>(type: "INTEGER", nullable: true),
                    PrinterStatus = table.Column<string>(type: "TEXT", nullable: true),
                    RequestBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    AppVersion = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrintJobPayloads",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Request = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Bytes = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Blocks = table.Column<string>(type: "TEXT", nullable: true),
                    Options = table.Column<string>(type: "TEXT", nullable: true),
                    PlainText = table.Column<string>(type: "TEXT", nullable: true),
                    Headers = table.Column<string>(type: "TEXT", nullable: false),
                    Exception = table.Column<string>(type: "TEXT", nullable: true),
                    Log = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintJobPayloads", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_PrintJobPayloads_PrintJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PrintJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_CreatedAt",
                table: "PrintJobs",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrintJobPayloads");

            migrationBuilder.DropTable(
                name: "PrintJobs");
        }
    }
}
