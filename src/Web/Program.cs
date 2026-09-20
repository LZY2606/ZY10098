using NetCompare.Core;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IStateStore>(_ =>
    new FileStateStore(builder.Configuration.GetValue<string>("NetCompare:DataDirectory")));
builder.Services.AddSingleton<AnalysisQueue>();
builder.Services.AddSingleton<ComparisonService>();
builder.Services.AddHostedService<AnalysisWorker>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (NotFoundException exception)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { message = exception.Message });
    }
    catch (ConflictException exception)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { message = exception.Message });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");
api.MapGet("/state", async (ComparisonService service) => Results.Ok(await service.Snapshot()));
api.MapPost("/netlists", async (SaveNetlistInput input, ComparisonService service) =>
    Results.Ok(await service.SaveNetlist(input)));
api.MapPost("/rules", async (SaveRulesInput input, ComparisonService service) =>
    Results.Ok(await service.SaveRules(input)));
api.MapPost("/sessions", async (CreateSessionInput input, ComparisonService service) =>
    Results.Ok(await service.CreateSession(input)));
api.MapPost("/sessions/{id}/decisions", async (
    string id,
    DecisionInput input,
    ComparisonService service) => Results.Ok(await service.AddDecision(id, input)));
api.MapPost("/sessions/{id}/suggestions/{decisionId}/accept", async (
    string id,
    string decisionId,
    ComparisonService service) => Results.Ok(await service.ReviewSuggestion(id, decisionId, true)));
api.MapPost("/sessions/{id}/suggestions/{decisionId}/reject", async (
    string id,
    string decisionId,
    ComparisonService service) => Results.Ok(await service.ReviewSuggestion(id, decisionId, false)));
api.MapPost("/sessions/{id}/retry", async (string id, ComparisonService service) =>
    Results.Ok(await service.RequeueSession(id)));
api.MapDelete("/sessions/{id}/decisions/{decisionId}", async (
    string id,
    string decisionId,
    ComparisonService service) => Results.Ok(await service.DeleteDecision(id, decisionId)));
api.MapPost("/batches", async (CreateBatchInput input, ComparisonService service) =>
    Results.Ok(await service.CreateBatch(input)));
api.MapPost("/certificate/verify", (MappingCertificate certificate) =>
    Results.Ok(new { valid = CertificateHasher.Verify(certificate) }));
api.MapGet("/netlists/{id}/flat", async (string id, IStateStore store) =>
{
    var netlist = await store.Read(state => state.Netlists.TryGetValue(id, out var value) ? value : null);
    if (netlist is null) return Results.NotFound();
 var rules = await store.Read(state => state.Rules.Values
        .OrderByDescending(r => r.CreatedAt)
        .FirstOrDefault());
    if (rules is null) return Results.BadRequest(new { message = "no rules exist" });
    return Results.Ok(NetlistExpander.Expand(netlist, rules.Rules));
});

app.Run();

public partial class Program;
