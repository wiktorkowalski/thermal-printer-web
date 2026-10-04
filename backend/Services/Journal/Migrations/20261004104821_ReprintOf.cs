using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThermalPrinterWeb.Services.Journal.Migrations
{
    /// <inheritdoc />
    public partial class ReprintOf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReprintOf",
                table: "PrintJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReprintOf",
                table: "PrintJobs");
        }
    }
}
