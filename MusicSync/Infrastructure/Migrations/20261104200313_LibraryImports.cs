using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LibraryImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LibraryImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ArtistName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AlbumTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IdentifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TrackId = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryImportCandidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryImportId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordingMbid = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ArtistsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AlbumTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Isrc = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Score = table.Column<float>(type: "real", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryImportCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryImportCandidates_LibraryImports_LibraryImportId",
                        column: x => x.LibraryImportId,
                        principalTable: "LibraryImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryImportCandidates_LibraryImportId_Rank",
                table: "LibraryImportCandidates",
                columns: new[] { "LibraryImportId", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryImports_RelativePath",
                table: "LibraryImports",
                column: "RelativePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryImports_Status",
                table: "LibraryImports",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryImportCandidates");

            migrationBuilder.DropTable(
                name: "LibraryImports");
        }
    }
}
