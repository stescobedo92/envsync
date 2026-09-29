namespace EnvSync.Application.Abstractions;

/// <summary>
/// Handles a command: a request that changes the world (here: launches a process).
/// Commands are small <c>readonly record struct</c>s passed by value, because <c>async</c> methods cannot take <c>in</c> parameters.
/// </summary>
public interface ICommandHandler<in TCommand, TResult>
{
    ValueTask<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
