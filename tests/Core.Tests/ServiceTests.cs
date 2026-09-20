using PairwiseGsb.Core;
using PairwiseGsb.Core.Storage;

namespace Core.Tests;

public sealed class ServiceTests : IAsyncLifetime
{
    private readonly string dataPath = Path.Combine(Path.GetTempPath(), $"pairwise-gsb-{Guid.NewGuid():N}.json");
    private JsonDocumentStore store = null!;
    private ComparisonService comparisons = null!;
    private BatchService batches = null!;

    [Fact]
    public async Task Fingerprint_change_creates_new_session_without_copying_active_decision()
    {
        var firstLeft = await comparisons.ImportNetlistAsync(new ImportNetlistRequest(null, TestData.Json(TestData.Simple("a"))));
        var firstRight = await comparisons.ImportNetlistAsync(new ImportNetlistRequest(null, TestData.Json(TestData.Simple("b"))));
        var first = await comparisons.CreateSessionAsync(new CreateSessionRequest(null, firstLeft.Id, firstRight.Id, null));
        await comparisons.LockAsync(first.Session.Id, new LockRequest("r1", "r1"));

        var changedRightDocument = TestData.WithParameter(TestData.Simple("b-new"), new ParameterValue("R", 1200, "ohm", null));
        var changedRight = await comparisons.ImportNetlistAsync(new ImportNetlistRequest(null, TestData.Json(changedRightDocument)));
        var second = await comparisons.CreateSessionAsync(new CreateSessionRequest(null, firstLeft.Id, changedRight.Id, null));

        Assert.NotEqual(first.Session.Id, second.Session.Id);
        Assert.All(second.Decisions, decision => Assert.Equal(DecisionStatus.NeedsReview, decision.Status));
        Assert.All(second.Decisions, decision => Assert.Equal(DecisionKind.ReviewSuggestion, decision.Kind));
    }

    [Fact]
    public async Task Batch_is_idempotent_and_publishes_only_when_all_jobs_finish()
    {
        var left = await comparisons.ImportNetlistAsync(new ImportNetlistRequest(null, TestData.Json(TestData.Simple("a"))));
        var right = await comparisons.ImportNetlistAsync(new ImportNetlistRequest(null, TestData.Json(TestData.Simple("b"))));
        var pairs = new List<BatchPairInput> { new("pair", left.Id, right.Id, null) };
        var request = new BatchSubmitRequest(null, "stable-key", pairs);

        var first = await batches.SubmitAsync(request);
        var second = await batches.SubmitAsync(request);
        Assert.Equal(first.Id, second.Id);

        var runner = new BackgroundBatchRunner(store, TimeSpan.FromMilliseconds(1));
        await runner.ProcessOnceAsync();

        var published = await batches.GetAsync(first.Id);
        Assert.Equal(BatchStatus.Published, published.Status);
        Assert.NotNull(published.PublishedAt);
        Assert.Single(published.JobIds);
    }

    [Fact]
    public async Task Batch_validation_failure_does_not_leave_partial_records()
    {
        var left = await comparisons.ImportNetlistAsync(new ImportNetlistRequest(null, TestData.Json(TestData.Simple("a"))));
        var before = await File.ReadAllTextAsync(dataPath);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => batches.SubmitAsync(
            new BatchSubmitRequest(null, "invalid", [new BatchPairInput(null, left.Id, "missing", null)])));

        var after = await File.ReadAllTextAsync(dataPath);
        Assert.Equal(before, after);
        var inputs = await comparisons.ListInputsAsync();
        Assert.DoesNotContain(inputs.Netlists, record => record.Name == "invalid-batch");
    }

    [Fact]
    public async Task Certificate_verifies_against_fingerprints_and_mapping()
    {
        var left = await comparisons.ImportNetlistAsync(new ImportNetlistRequest(null, TestData.Json(TestData.Simple("a"))));
        var right = await comparisons.ImportNetlistAsync(new ImportNetlistRequest(null, TestData.Json(TestData.Simple("b", true))));
        var session = await comparisons.CreateSessionAsync(new CreateSessionRequest(null, left.Id, right.Id, null));

        var certificate = await comparisons.GetCertificateAsync(session.Session.Id);

        Assert.NotNull(certificate);
        Assert.True(ComparisonService.VerifyCertificate(certificate!));
        Assert.Equal(session.Session.LeftFingerprint, certificate!.LeftFingerprint);
        Assert.Contains(session.Events, ev => ev.Type == "session-created");
        Assert.Contains(session.Events, ev => ev.Type == "search-completed");
        Assert.Equal(session.Events.Select(ev => ev.Sequence).OrderBy(sequence => sequence).ToList(),
            session.Events.Select(ev => ev.Sequence).ToList());
    }

    public Task InitializeAsync()
    {
        store = new JsonDocumentStore(dataPath);
        comparisons = new ComparisonService(store);
        batches = new BatchService(store);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (File.Exists(dataPath))
        {
            File.Delete(dataPath);
        }

        return Task.CompletedTask;
    }
}
