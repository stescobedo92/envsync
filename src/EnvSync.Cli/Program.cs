using System.Runtime.InteropServices;
using EnvSync.Cli;

// Ctrl+C, SIGTERM and SIGHUP all request a graceful stop: resolving is abandoned, and a running child gets a grace period
// (it receives Ctrl+C itself) before it is stopped. envsync never dies before its child does.
using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    cancellation.Cancel();
});

using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
{
    context.Cancel = true;
    cancellation.Cancel();
});

return await CliApplication.RunAsync(args, CliEnvironment.FromProcess(), services: null, cancellation.Token);
