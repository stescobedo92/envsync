namespace EnvSync.Application.Abstractions;

/// <summary>Handles a query: a request that only reads and never changes the world.</summary>
public interface IQueryHandler<in TQuery, TResult>
{
    ValueTask<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
