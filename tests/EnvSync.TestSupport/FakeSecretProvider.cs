using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.TestSupport;

/// <summary>
/// Hand-written provider double. Besides canned answers it can delay, hang until cancelled, throw,
/// and it records how many calls ran at the same time so concurrency limits can be asserted.
/// </summary>
public sealed class FakeSecretProvider : ISecretProvider, IDisposable
{
    private readonly Dictionary<string, Result<SecretValue>> _responses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hanging = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _throwing = new(StringComparer.Ordinal);
    private int _calls;
    private int _inFlight;
    private int _maxInFlight;

    public TimeSpan Delay { get; init; }

    /// <summary>Makes <see cref="Dispose"/> throw, to prove a failing provider cannot break the others' cleanup.</summary>
    public bool ThrowOnDispose { get; init; }

    public int CallCount => Volatile.Read(ref _calls);

    public int MaxConcurrency => Volatile.Read(ref _maxInFlight);

    public bool IsDisposed { get; private set; }

    public FakeSecretProvider Returns(string reference, string value)
    {
        _responses[reference] = Result<SecretValue>.Success(new SecretValue(value));
        return this;
    }

    public FakeSecretProvider Fails(string reference, ErrorKind kind, string detail = "")
    {
        _responses[reference] = Result<SecretValue>.Failure(new Error(kind, reference, detail));
        return this;
    }

    public FakeSecretProvider Hangs(string reference)
    {
        _hanging.Add(reference);
        return this;
    }

    public FakeSecretProvider Throws(string reference, Exception exception)
    {
        _throwing[reference] = exception;
        return this;
    }

    public async ValueTask<Result<SecretValue>> GetAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        TrackConcurrency(Interlocked.Increment(ref _inFlight));

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            var key = reference.Field is null ? reference.Path : $"{reference.Path}#{reference.Field}";

            if (_hanging.Contains(key))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_throwing.TryGetValue(key, out var exception))
            {
                throw exception;
            }

            return _responses.TryGetValue(key, out var response)
                ? response
                : Result<SecretValue>.Failure(new Error(ErrorKind.SecretNotFound, reference.Path, "not configured in the fake"));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public void Dispose()
    {
        IsDisposed = true;

        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("dispose failed");
        }
    }

    private void TrackConcurrency(int current)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxInFlight);
        }
        while (current > observed && Interlocked.CompareExchange(ref _maxInFlight, current, observed) != observed);
    }
}
