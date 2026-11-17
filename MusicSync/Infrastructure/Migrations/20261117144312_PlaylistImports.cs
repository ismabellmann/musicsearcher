using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PlaylistImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaylistImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    OwnerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TotalTracks = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlaylistImportItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlaylistImportId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalTrackId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ArtistName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AlbumName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    SelectedRecordingMbid = table.Column<Guid>(type: "TEXT", nullable: true),
                    TrackId = table.Column<int>(type: "INTEGER", nullable: true),
                    DownloadJobId = table.Column<int>(type: "INTEGER", nullable: true),
                    CandidatesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistImportItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaylistImportItems_PlaylistImports_PlaylistImportId",
                        column: x => x.PlaylistImportId,
                        principalTable: "PlaylistImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistImportItems_PlaylistImportId_Position",
                table: "PlaylistImportItems",
                columns: new[] { "PlaylistImportId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistImports_ExternalId",
                table: "PlaylistImports",
                column: "ExternalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaylistImportItems");

            migrationBuilder.DropTable(
                name: "PlaylistImports");
        }
    }
}
