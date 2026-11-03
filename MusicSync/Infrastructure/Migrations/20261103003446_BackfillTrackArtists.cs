using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicSync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillTrackArtists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"INSERT INTO ""Artists"" (""Mbid"", ""Name"")
                  SELECT NULL, t.""ArtistName""
                  FROM (SELECT DISTINCT ""ArtistName"" FROM ""Tracks"" WHERE ""ArtistName"" <> '') AS t
                  WHERE NOT EXISTS (
                      SELECT 1 FROM ""Artists"" a WHERE a.""Mbid"" IS NULL AND a.""Name"" = t.""ArtistName"");");

            migrationBuilder.Sql(
                @"INSERT INTO ""TrackArtists"" (""TrackId"", ""Position"", ""ArtistId"")
                  SELECT tr.""Id"", 0, a.""Id""
                  FROM ""Tracks"" tr
                  JOIN ""Artists"" a ON a.""Mbid"" IS NULL AND a.""Name"" = tr.""ArtistName""
                  WHERE tr.""ArtistName"" <> ''
                    AND NOT EXISTS (SELECT 1 FROM ""TrackArtists"" ta WHERE ta.""TrackId"" = tr.""Id"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
