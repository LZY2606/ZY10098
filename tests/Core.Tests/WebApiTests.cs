using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PairwiseGsb.Core;
using PairwiseGsb.Core.Storage;
using PairwiseGsb.Web;

namespace Core.Tests;

public sealed class WebApiTests : IAsyncLifetime
{
    private TestServer server = null!;
    private HttpClient http = null!;
    private string dataPath = "";

    [Fact]
    public async Task Serves_page_and_completes_workflow_through_http()
    {
        var index = await http.GetStringAsync("/");
        Assert.Contains("Pair-wise GSB", index);

        var left = await Post<NetlistRecord>("/api/netlists", new ImportNetlistRequest(null, TestData.Json(TestData.Simple("a"))));
        var right = await Post<NetlistRecord>("/api/netlists", new ImportNetlistRequest(null, TestData.Json(TestData.Simple("b"))));
        var snapshot = await Post<SessionSnapshot>("/api/sessions",
            new CreateSessionRequest(null, left.Id, right.Id, null));

        Assert.Equal(SessionStatus.Equivalent, snapshot.Session.Status);
        var certificate = await http.GetFromJsonAsync<MappingCertificate>($"/api/sessions/{snapshot.Session.Id}/certificate");
        Assert.NotNull(certificate);

        var verify = await http.PostAsJsonAsync("/api/certificates/verify", certificate!);
        Assert.True(verify.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Invalid_json_returns_bad_request_not_non_equivalence()
    {
        var response = await http.PostAsJsonAsync("/api/netlists", new ImportNetlistRequest(null, "{"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<T> Post<T>(string path, object body)
    {
        var response = await http.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public Task InitializeAsync()
    {
        dataPath = Path.Combine(Path.GetTempPath(), $"pairwise-gsb-web-{Guid.NewGuid():N}.json");
#pragma warning disable ASPDEPR004
        var builder = new WebHostBuilder()
            .UseWebRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Web", "wwwroot")))
            .ConfigureServices(services =>
            {
                services.AddRouting();
                WebSetup.ConfigureServices(services, dataPath, false);
            })
            .Configure(app =>
            {
                app.UseDefaultFiles();
                app.UseStaticFiles();
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapApi());
            });
#pragma warning restore ASPDEPR004
        server = new TestServer(builder);
        http = server.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        server.Dispose();
        if (File.Exists(dataPath))
        {
            File.Delete(dataPath);
        }
    }
}
