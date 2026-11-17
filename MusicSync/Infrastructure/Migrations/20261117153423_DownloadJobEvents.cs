using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DownloadJobEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DownloadJobEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadJobEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadJobEvents_CreatedAt",
                table: "DownloadJobEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadJobEvents_JobId_Id",
                table: "DownloadJobEvents",
                columns: new[] { "JobId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadJobEvents");
        }
    }
}
