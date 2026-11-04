using MusicSync.Core;
using Xunit;

namespace MusicSync.Core.Tests;

public class AccountRulesTests
{
    [Theory]
    [InlineData("ab", false)]
    [InlineData("abc", true)]
    [InlineData("a.b_c-d", true)]
    [InlineData("a/b", false)]
    [InlineData("../etc", false)]
    [InlineData("", false)]
    public void Username_Rules(string username, bool valid)
    {
        Assert.Equal(valid, AccountRules.ValidateUsername(username) is null);
    }

    [Fact]
    public void Username_MaxLength_Is50()
    {
        Assert.True(AccountRules.ValidateUsername(new string('a', 50)) is null);
        Assert.False(AccountRules.ValidateUsername(new string('a', 51)) is null);
    }

    [Fact]
    public void Password_MinLength_Is12()
    {
        Assert.False(AccountRules.ValidatePassword(new string('x', 11)) is null);
        Assert.True(AccountRules.ValidatePassword(new string('x', 12)) is null);
        Assert.False(AccountRules.ValidatePassword(null) is null);
    }
}
