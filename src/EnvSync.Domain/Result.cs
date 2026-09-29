using System.Collections.Immutable;

namespace EnvSync.Domain;

/// <summary>
/// Outcome of an operation that can fail in expected ways. The success path allocates nothing;
/// a failure carries every error found, so a caller can report them all in one pass.
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly ImmutableArray<Error> _errors;

    private Result(T? value, ImmutableArray<Error> errors)
    {
        _value = value;
        _errors = errors;
    }

    public bool IsSuccess => _errors.IsDefaultOrEmpty;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("The result is a failure and carries no value.");

    public ImmutableArray<Error> Errors => _errors.IsDefault ? [] : _errors;

    public static Result<T> Success(T value) => new(value, default);

    public static Result<T> Failure(Error error) => new(default, [error]);

    public static Result<T> Failure(ImmutableArray<Error> errors)
    {
        if (errors.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A failure needs at least one error.", nameof(errors));
        }

        return new Result<T>(default, errors);
    }
}
