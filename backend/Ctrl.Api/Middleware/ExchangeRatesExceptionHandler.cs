using Ctrl.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Ctrl.Api.Middleware;

internal sealed class ExchangeRatesExceptionHandler(ILogger<ExchangeRatesExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ExchangeRatesUnavailableException)
        {
            return false;
        }

        logger.LogWarning(exception, "The reference exchange-rate provider is unavailable.");
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Exchange rates temporarily unavailable",
            Detail = "The reference rates could not be retrieved. Please try again later.",
            Instance = httpContext.Request.Path
        }, cancellationToken);

        return true;
    }
}
