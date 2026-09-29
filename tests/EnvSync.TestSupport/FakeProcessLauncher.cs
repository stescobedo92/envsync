using EnvSync.Application.Abstractions;
using EnvSync.Domain;

namespace EnvSync.TestSupport;

/// <summary>Launcher double: never starts a process, remembers the request it was given.</summary>
public sealed class FakeProcessLauncher : IProcessLauncher
{
    public Result<int> Response { get; set; } = Result<int>.Success(0);

    public int Calls { get; private set; }

    public ProcessLaunchRequest LastRequest { get; private set; }

    public ValueTask<Result<int>> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;
        return ValueTask.FromResult(Response);
    }
}
