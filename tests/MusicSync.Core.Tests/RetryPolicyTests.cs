using MusicSync.Core;
using Xunit;

namespace MusicSync.Core.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void Delays_DoubleFromBase_AndAreCapped()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(100));

        Assert.Equal(TimeSpan.Zero, policy.DelayForRetry(0));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.DelayForRetry(1));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.DelayForRetry(2));
        Assert.Equal(TimeSpan.FromSeconds(100), policy.DelayForRetry(3));
        Assert.Equal(TimeSpan.FromSeconds(100), policy.DelayForRetry(10));
    }

    [Fact]
    public void Constructor_RejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(-1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(1, TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(typeof(TransientAcquisitionException), true)]
    [InlineData(typeof(MetadataUnavailableException), true)]
    [InlineData(typeof(TimeoutException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(ArgumentException), false)]
    public void TransientErrors_ClassifyByExceptionType(Type exceptionType, bool expected)
    {
        Exception exception = exceptionType.Name switch
        {
            nameof(TransientAcquisitionException) => new TransientAcquisitionException("message"),
            nameof(MetadataUnavailableException) => new MetadataUnavailableException("search", 503),
            nameof(TimeoutException) => new TimeoutException("message"),
            _ => new InvalidOperationException("message")
        };

        Assert.Equal(expected, TransientErrors.IsTransient(exception));
    }
}
