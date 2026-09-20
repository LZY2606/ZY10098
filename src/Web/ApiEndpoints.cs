using PairwiseGsb.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace PairwiseGsb.Web;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { ok = true }));

        app.MapGet("/api/inputs", async (ComparisonService service) =>
        {
            var inputs = await service.ListInputsAsync();
            return Results.Ok(new { inputs.Netlists, inputs.Rules });
        });

        app.MapPost("/api/netlists", async (ImportNetlistRequest request, ComparisonService service) =>
        {
            if (string.IsNullOrWhiteSpace(request.RawText))
            {
                return Results.BadRequest(new { message = "rawText 不能为空。" });
            }

            try
            {
                return Results.Ok(await service.ImportNetlistAsync(request));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });

        app.MapPost("/api/rules", async (ImportRulesRequest request, ComparisonService service) =>
        {
            try
            {
                return Results.Ok(await service.ImportRulesAsync(request));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });

        app.MapPost("/api/sessions", async (CreateSessionRequest request, ComparisonService service) =>
        {
            try
            {
                return Results.Ok(await service.CreateSessionAsync(request));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });

        app.MapGet("/api/sessions", async (ComparisonService service) => Results.Ok(await service.ListSessionsAsync()));
        app.MapGet("/api/sessions/{id}", async (string id, ComparisonService service) =>
        {
            try
            {
                return Results.Ok(await service.GetSessionAsync(id));
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { message = exception.Message });
            }
        });

        app.MapPost("/api/sessions/{id}/search", async (string id, SearchRequest request, ComparisonService service) =>
            Results.Ok(await service.SearchAsync(id, request.TimeoutMs)));

        app.MapPost("/api/sessions/{id}/locks", async (string id, LockRequest request, ComparisonService service) =>
            Results.Ok(await service.LockAsync(id, request)));

        app.MapPost("/api/sessions/{id}/vetos", async (string id, LockRequest request, ComparisonService service) =>
            Results.Ok(await service.VetoAsync(id, request)));

        app.MapPost("/api/sessions/{id}/suggestions/{decisionId}", async (
            string id,
            string decisionId,
            SuggestionDecisionRequest request,
            ComparisonService service) =>
        {
            try
            {
                return Results.Ok(await service.ReviewSuggestionAsync(id, decisionId, request));
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { message = exception.Message });
            }
        });

        app.MapGet("/api/sessions/{id}/certificate", async (string id, ComparisonService service) =>
        {
            var certificate = await service.GetCertificateAsync(id);
            return certificate is null ? Results.NotFound(new { message = "该会话没有等价证书。" }) : Results.Ok(certificate);
        });

        app.MapPost("/api/certificates/verify", (MappingCertificate certificate, ComparisonService service) =>
            Results.Ok(new { valid = ComparisonService.VerifyCertificate(certificate) }));

        app.MapPost("/api/batches", async (BatchSubmitRequest request, BatchService service) =>
        {
            try
            {
                return Results.Ok(await service.SubmitAsync(request));
            }
            catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });

        app.MapGet("/api/batches/{id}", async (string id, BatchService service) =>
        {
            try
            {
                return Results.Ok(await service.GetAsync(id));
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { message = exception.Message });
            }
        });

        return app;
    }
}

public sealed record SearchRequest(int? TimeoutMs);
