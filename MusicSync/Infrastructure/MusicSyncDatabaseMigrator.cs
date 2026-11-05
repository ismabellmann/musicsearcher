using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MusicSync.Infrastructure;

public static class MusicSyncDatabaseMigrator
{
    private const string HistoryTable = "__EFMigrationsHistory";

    // Tables created by migrations after InitialCreate. Legacy baselining checks only the InitialCreate schema.
    private static readonly string[] MigrationOnlyTables = { "TrackArtists", "DownloadJobCandidates", "LibraryImports", "LibraryImportCandidates" };

    // Columns added after InitialCreate, keyed by table.
    private static readonly Dictionary<string, string[]> MigrationOnlyColumns = new(StringComparer.Ordinal)
    {
        ["DownloadJobs"] = new[] { "NextAttemptAt", "AutomaticRetries" },
        ["Tracks"] = new[] { "SourceUrl", "ReleaseGroupMbid", "Year" }
    };

    public static async Task MigrateAsync(MusicSyncDbContext db, CancellationToken ct = default)
    {
        await BaselineEnsureCreatedDatabaseAsync(db, ct);
        await db.Database.MigrateAsync(ct);
    }

    private static async Task BaselineEnsureCreatedDatabaseAsync(MusicSyncDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsSqlite())
            throw new InvalidOperationException("Legacy schema baselining is supported only for SQLite.");

        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
            await connection.OpenAsync(ct);

        try
        {
            if (await TableExistsAsync(connection, HistoryTable, ct))
                return;

            var actualTables = await GetTableNamesAsync(connection, ct);
            if (actualTables.Count == 0)
                return;

            var expectedTables = GetExpectedTables(db);
            foreach (var migrationOnlyTable in MigrationOnlyTables)
            {
                actualTables.Remove(migrationOnlyTable);
                expectedTables.Remove(migrationOnlyTable);
            }

            if (!actualTables.SetEquals(expectedTables.Keys))
            {
                throw new InvalidOperationException(
                    "The existing SQLite database has no EF migration history and does not match the known EnsureCreated schema. " +
                    "The database was left unchanged; back it up and perform a schema-specific migration.");
            }

            foreach (var (tableName, expectedColumnsAll) in expectedTables)
            {
                var expectedColumns = new HashSet<string>(expectedColumnsAll, StringComparer.Ordinal);
                var actualColumns = await GetColumnNamesAsync(connection, tableName, ct);
                if (MigrationOnlyColumns.TryGetValue(tableName, out var laterColumns))
                {
                    expectedColumns.ExceptWith(laterColumns);
                    actualColumns.ExceptWith(laterColumns);
                }

                if (!actualColumns.SetEquals(expectedColumns))
                {
                    throw new InvalidOperationException(
                        $"The existing SQLite table '{tableName}' does not match the known EnsureCreated schema. " +
                        "The database was left unchanged; back it up and perform a schema-specific migration.");
                }
            }

            var initialMigration = db.Database.GetMigrations().FirstOrDefault()
                ?? throw new InvalidOperationException("No EF Core migrations are available to baseline.");

            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using (var createHistory = connection.CreateCommand())
            {
                createHistory.Transaction = transaction;
                createHistory.CommandText =
                    """
                    CREATE TABLE "__EFMigrationsHistory" (
                        "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                        "ProductVersion" TEXT NOT NULL
                    );
                    """;
                await createHistory.ExecuteNonQueryAsync(ct);
            }

            await using (var recordBaseline = connection.CreateCommand())
            {
                recordBaseline.Transaction = transaction;
                recordBaseline.CommandText =
                    """
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES (@migrationId, @productVersion);
                    """;

                var migrationParameter = recordBaseline.CreateParameter();
                migrationParameter.ParameterName = "@migrationId";
                migrationParameter.Value = initialMigration;
                recordBaseline.Parameters.Add(migrationParameter);

                var versionParameter = recordBaseline.CreateParameter();
                versionParameter.ParameterName = "@productVersion";
                var version = typeof(DbContext).Assembly.GetName().Version
                    ?? throw new InvalidOperationException("Unable to determine the EF Core assembly version.");
                versionParameter.Value = $"{version.Major}.{version.Minor}.{version.Build}";
                recordBaseline.Parameters.Add(versionParameter);

                await recordBaseline.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        finally
        {
            if (wasClosed)
                await connection.CloseAsync();
        }
    }

    private static Dictionary<string, HashSet<string>> GetExpectedTables(MusicSyncDbContext db)
    {
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null)
                continue;

            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            if (!tables.TryGetValue(tableName, out var columns))
            {
                columns = new HashSet<string>(StringComparer.Ordinal);
                tables.Add(tableName, columns);
            }

            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName(storeObject)
                    ?? throw new InvalidOperationException(
                        $"Unable to determine the database column for '{entityType.Name}.{property.Name}'.");
                columns.Add(columnName);
            }
        }

        return tables;
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @tableName;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<HashSet<string>> GetTableNamesAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";

        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            tables.Add(reader.GetString(0));

        return tables;
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(
        DbConnection connection,
        string tableName,
        CancellationToken ct)
    {
        var quotedTableName = tableName.Replace("\"", "\"\"", StringComparison.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{quotedTableName}\");";

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(1));

        return columns;
    }
}
