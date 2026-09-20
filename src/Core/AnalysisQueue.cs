using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NetCompare.Core;

public sealed class AnalysisQueue
{
    private readonly Channel<string> channel = Channel.CreateUnbounded<string>();
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private readonly object queuedLock = new();

    public void EnqueueSession(string sessionId)
    {
        lock (queuedLock)
        {
            if (!queued.Add(sessionId)) return;
        }
        channel.Writer.TryWrite(sessionId);
    }

    public async IAsyncEnumerable<string> Dequeue([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await channel.Reader.WaitToReadAsync(cancellationToken))
        {
            while (channel.Reader.TryRead(out var sessionId))
            {
                lock (queuedLock) queued.Remove(sessionId);
                yield return sessionId;
            }
        }
    }
}

public sealed class AnalysisWorker(IServiceProvider services) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IStateStore>();
        var queue = scope.ServiceProvider.GetRequiredService<AnalysisQueue>();

        await store.Mutate(state =>
        {
            foreach (var session in state.Sessions.Values.Where(s =>
                s.State is SessionState.Queued or SessionState.Analyzing))
            {
                session.State = SessionState.Queued;
                queue.EnqueueSession(session.Id);
            }
            return Task.CompletedTask;
        }, stoppingToken);

        await foreach (var sessionId in queue.Dequeue(stoppingToken))
        {
            try
            {
                await AnalysisRunner.RunOnce(scope.ServiceProvider, sessionId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // The session remains Queued/Analyzing in persisted state; startup recovery retries it.
            }
        }
    }
}
