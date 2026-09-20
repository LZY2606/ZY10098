using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NetCompare.Core;

namespace NetCompare.Tests;

public sealed class WebApiTests : IDisposable
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "netcompare-web-" + Guid.NewGuid());
    private readonly WebApplicationFactory<Program> factory;

    public WebApiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("NetCompare:DataDirectory", dataDirectory));
    }

    [Fact]
    public async Task ServesPageAndComparesImportedNetlistsThroughHttp()
    {
        using var client = factory.CreateClient();
        var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Pair-wise EDA", await page.Content.ReadAsStringAsync());

        var rules = await Post<RuleRevision>("/api/rules", new SaveRulesInput("r", "rules", new RuleSet
        {
            Id = "r",
            Version = "http-1",
            SearchTimeoutMs = 1000,
            SwappablePins = [new SwappablePinRule { DeviceType = "resistor", Pins = ["A", "B"] }]
        }));
        var left = await Post<NetlistRevision>("/api/netlists", new SaveNetlistInput("l", "left",
            NetJson("l", "N1", "N2")));
        var right = await Post<NetlistRevision>("/api/netlists", new SaveNetlistInput("rr", "right",
            NetJson("rr", "X1", "X2")));
        var session = await Post<ComparisonSession>("/api/sessions", new CreateSessionInput(
            left.NetlistId, right.NetlistId, rules.RulesId));

        var completed = await WaitFor(session.Id);
        Assert.Equal(AnalysisStatus.Equivalent, completed.Result!.Status);
        Assert.NotNull(completed.Result.Certificate);

        var verified = await Post<CertificateVerification>("/api/certificate/verify", completed.Result.Certificate!);
        Assert.True(verified.Valid);
    }

    private async Task<T> Post<T>(string path, object body)
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonSerializer.Deserialize<T>(text, Hashing.JsonOptions)!;
    }

    private async Task<ComparisonSession> WaitFor(string sessionId)
    {
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(25);
            using var client = factory.CreateClient();
            var state = await client.GetFromJsonAsync<PersistedState>("/api/state", Hashing.JsonOptions);
            var session = state!.Sessions[sessionId];
            if (session.Result is not null) return session;
        }
        throw new TimeoutException("background analysis did not finish");
    }

    private static string NetJson(string id, string netA, string netB) =>
        JsonSerializer.Serialize(new
        {
            id,
            name = id,
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
                            pins = new Dictionary<string, string> { ["A"] = netA, ["B"] = netB },
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
        factory.Dispose();
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
    }

    private sealed record CertificateVerification(bool Valid);
}
