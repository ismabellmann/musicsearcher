using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicSync.Infrastructure;
using Xunit;

namespace MusicSync.Infrastructure.Tests;

public sealed class AccountServiceTests
{
    private const string Password = "correct horse battery";

    private static async Task<(SqliteConnection Connection, MusicSyncDbContext Db)> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new MusicSyncDbContext(new DbContextOptionsBuilder<MusicSyncDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }

    [Fact]
    public async Task Setup_CreatesOneAccount_AndRefusesASecondOne()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;
        var accounts = new AccountService(db);

        Assert.False(await accounts.IsSetupCompleteAsync(CancellationToken.None));
        var first = await accounts.CreateInitialAccountAsync("owner", Password, CancellationToken.None);
        var second = await accounts.CreateInitialAccountAsync("intruder", Password, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.True(await accounts.IsSetupCompleteAsync(CancellationToken.None));
        Assert.Equal(1, await db.UserAccounts.CountAsync());
    }

    [Fact]
    public async Task Password_IsStoredAsHash_NotPlaintext()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;

        await new AccountService(db).CreateInitialAccountAsync("owner", Password, CancellationToken.None);

        var stored = await db.UserAccounts.SingleAsync();
        Assert.NotEqual(Password, stored.PasswordHash);
        Assert.DoesNotContain(Password, stored.PasswordHash);
    }

    [Fact]
    public async Task Verify_AcceptsCorrectPassword_RejectsWrongPasswordAndUnknownUser()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;
        var accounts = new AccountService(db);
        await accounts.CreateInitialAccountAsync("owner", Password, CancellationToken.None);

        Assert.NotNull(await accounts.VerifyAsync("owner", Password, CancellationToken.None));
        Assert.Null(await accounts.VerifyAsync("owner", "wrong password here", CancellationToken.None));
        Assert.Null(await accounts.VerifyAsync("nobody", Password, CancellationToken.None));
    }

    [Fact]
    public async Task ChangePassword_RequiresCurrentPassword_AndReplacesIt()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;
        var accounts = new AccountService(db);
        var account = await accounts.CreateInitialAccountAsync("owner", Password, CancellationToken.None);
        const string newPassword = "a brand new passphrase";

        Assert.False(await accounts.ChangePasswordAsync(account!.Id, "not the current one", newPassword, CancellationToken.None));
        Assert.True(await accounts.ChangePasswordAsync(account.Id, Password, newPassword, CancellationToken.None));

        Assert.Null(await accounts.VerifyAsync("owner", Password, CancellationToken.None));
        Assert.NotNull(await accounts.VerifyAsync("owner", newPassword, CancellationToken.None));
    }
}
