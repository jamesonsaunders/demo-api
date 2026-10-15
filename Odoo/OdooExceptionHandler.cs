using Microsoft.AspNetCore.Diagnostics;
using Polly;

namespace DemoApi.Odoo;

// Odoo failures are upstream problems, so they surface as 502/503 instead of a generic 500.
public sealed class OdooExceptionHandler(IProblemDetailsService problemDetails, ILogger<OdooExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        (int Status, string Title)? problem = exception switch
        {
            OdooException => (StatusCodes.Status502BadGateway, "Odoo request failed."),
            HttpRequestException or ExecutionRejectedException => (StatusCodes.Status503ServiceUnavailable, "Odoo is unavailable."),
            _ => null,
        };
        if (problem is not { } p) return false;

        logger.LogError(exception, "{Title}", p.Title);
        context.Response.StatusCode = p.Status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = p.Status, Title = p.Title },
        });
    }
}
