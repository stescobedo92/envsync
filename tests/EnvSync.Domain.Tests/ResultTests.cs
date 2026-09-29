using System.Collections.Immutable;
using EnvSync.Domain;
using EnvSync.Testing;

namespace EnvSync.Domain.Tests;

public sealed class ResultTests
{
    private static readonly Error First = new(ErrorKind.SecretNotFound, "DB_PASSWORD", "missing");
    private static readonly Error Second = new(ErrorKind.AuthenticationFailed, "kv", "denied");

    [Fact]
    public void Success_ExposesTheValueAndHasNoErrors()
    {
        var result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Failure_WithOneError_IsNotSuccessful()
    {
        var result = Result<int>.Failure(First);

        Assert.False(result.IsSuccess);
        Assert.Equal(First, Assert.Single(result.Errors));
    }

    [Fact]
    public void Failure_WithSeveralErrors_KeepsAllOfThemInOrder()
    {
        var result = Result<string>.Failure([First, Second]);

        Assert.False(result.IsSuccess);
        Assert.Equal([First, Second], result.Errors);
    }

    [Fact]
    public void Value_OnAFailure_Throws()
    {
        var result = Result<int>.Failure(First);

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Failure_WithNoErrors_Throws()
    {
        Assert.Throws<ArgumentException>(() => Result<int>.Failure(ImmutableArray<Error>.Empty));
    }

    [Fact]
    public void Default_IsAFailureNotAnAccidentalSuccess()
    {
        var uninitialized = default(Result<int>);

        Assert.False(uninitialized.IsSuccess);
        Assert.Equal(ErrorKind.Internal, Assert.Single(uninitialized.Errors).Kind);
        Assert.Throws<InvalidOperationException>(() => uninitialized.Value);
    }

    [Fact]
    public void Success_DoesNotAllocate()
    {
        var allocated = AllocationProbe.Measure(() => Result<int>.Success(7));

        Assert.Equal(0, allocated);
    }
}
