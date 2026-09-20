using Microsoft.Extensions.DependencyInjection;
using NetCompare.Core;

namespace NetCompare.Tests;

public sealed class PersistenceServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "netcompare-tests-" + Guid.NewGuid());

    [Fact]
    public async Task FingerprintChangesCreateNewSessionAndPriorDecisionBecomesSuggestion()
    {
        var queue = new AnalysisQueue();
        await using var store = new FileStateStore(directory);
        var service = new ComparisonService(store, queue);
        var rules = await service.SaveRules(new SaveRulesInput("r1", "rules", DemoRules()));
        var left = await service.SaveNetlist(new SaveNetlistInput("left", "left", DocumentJson("A")));
        var right = await service.SaveNetlist(new SaveNetlistInput("right", "right", DocumentJson("B")));
        var first = await service.CreateSession(new CreateSessionInput(left.NetlistId, right.NetlistId, rules.RulesId));

        await service.AddDecision(first.Id, new DecisionInput("$root/R1", "$root/R1", DecisionKind.Locked, []));
        await service.SaveNetlist(new SaveNetlistInput("left", "left", DocumentJson("A2")));
        var second = await service.CreateSession(new CreateSessionInput(left.NetlistId, right.NetlistId, rules.RulesId));

        Assert.NotEqual(first.Id, second.Id);
        var suggestion = Assert.Single(second.Decisions);
        Assert.Equal(DecisionKind.Suggestion, suggestion.Kind);
        Assert.Equal(ReviewState.NeedsReview, suggestion.ReviewState);
        var accepted = await service.ReviewSuggestion(second.Id, suggestion.Id, true);
        Assert.Equal(DecisionKind.Locked, accepted.Decisions.Single().Kind);
    }

    [Fact]
    public async Task DuplicateBatchSubmissionReturnsSameBatchAndPublishesOnlyAfterAllPairs()
    {
        var queue = new AnalysisQueue();
        await using var store = new FileStateStore(directory);
        var service = new ComparisonService(store, queue);
        var rules = await service.SaveRules(new SaveRulesInput("r1", "rules", DemoRules()));
        var left = await service.SaveNetlist(new SaveNetlistInput("left", "left", DocumentJson("A")));
        var rightA = await service.SaveNetlist(new SaveNetlistInput("right-a", "right-a", DocumentJson("B")));
        var rightB = await service.SaveNetlist(new SaveNetlistInput("right-b", "right-b", DocumentJson("C")));
        var input = new CreateBatchInput("batch-key", rules.RulesId,
        [
            new("p1", left.NetlistId, rightA.NetlistId),
            new("p2", left.NetlistId, rightB.NetlistId)
        ]);

        var first = await service.CreateBatch(input);
        var duplicate = await service.CreateBatch(input);

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(BatchState.Running, duplicate.State);
        foreach (var sessionId in first.Pairs.Select(p => p.SessionId))
            await AnalysisRunner.RunOnce(CreateProvider(store, queue), sessionId);
        var published = (await service.Snapshot()).Batches[first.Id];
        Assert.Equal(BatchState.Completed, published.State);
        Assert.All(published.Pairs, pair => Assert.NotNull(pair.Status));
    }

    [Fact]
    public async Task RecoveredJobDoesNotDuplicateCompletedResult()
    {
        var queue = new AnalysisQueue();
        await using var store = new FileStateStore(directory);
        var service = new ComparisonService(store, queue);
        var rules = await service.SaveRules(new SaveRulesInput("r1", "rules", DemoRules()));
        var left = await service.SaveNetlist(new SaveNetlistInput("left", "left", DocumentJson("A")));
        var right = await service.SaveNetlist(new SaveNetlistInput("right", "right", DocumentJson("B")));
        var session = await service.CreateSession(new CreateSessionInput(left.NetlistId, right.NetlistId, rules.RulesId));

        var services = CreateProvider(store, queue);
        await AnalysisRunner.RunOnce(services, session.Id);
        await AnalysisRunner.RunOnce(services, session.Id);
        var stored = await service.Snapshot();

        Assert.Equal(SessionState.Completed, stored.Sessions[session.Id].State);
        Assert.Single(stored.Sessions[session.Id].Events, e => e.Type == "AnalysisCompleted");
    }

    private static IServiceProvider CreateProvider(IStateStore store, AnalysisQueue queue)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(queue);
        return services.BuildServiceProvider();
    }

    private static RuleSet DemoRules() => new()
    {
        Version = "service-test",
        SearchTimeoutMs = 1000,
        SwappablePins = [new SwappablePinRule { DeviceType = "resistor", Pins = ["A", "B"] }]
    };

    private static string DocumentJson(string marker) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "n-" + marker.ToLowerInvariant(),
            name = marker,
            rootModuleId = "top",
            modules = new[]
            {
                new
                {
                    id = "top",
                    name = "top",
                    ports = Array.Empty<object>(),
                    devices = new[]
                    {
                        new
                        {
                            id = "R1",
                            type = "resistor",
                            pins = new Dictionary<string, string> { ["A"] = "X-" + marker, ["B"] = "Y-" + marker },
                            parameters = new Dictionary<string, string> { ["resistance"] = "100 ohm" }
                        }
                    },
                    instances = Array.Empty<object>(),
                    nets = Array.Empty<object>()
                }
            }
        });

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
