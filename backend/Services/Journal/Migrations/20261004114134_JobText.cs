using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThermalPrinterWeb.Services.Journal.Migrations
{
    /// <inheritdoc />
    public partial class JobText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrintJobTexts",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintJobTexts", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_PrintJobTexts_PrintJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PrintJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The rows from before this table: the same cut as PrintJobEntry.MaxSearchTextLength
            // (substr counts characters, so a text with emoji can be a little longer). A reprint row has no text.
            migrationBuilder.Sql(
                "INSERT INTO PrintJobTexts (JobId, Text) "
                + "SELECT JobId, substr(PlainText, 1, 10000) FROM PrintJobPayloads WHERE PlainText IS NOT NULL AND PlainText <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrintJobTexts");
        }
    }
}
