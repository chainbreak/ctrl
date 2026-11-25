using Ctrl.Api.Contracts;
using Ctrl.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace Ctrl.Api.Controllers;

/// <summary>Provides public reference exchange rates.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/exchange-rates")]
[OutputCache(Duration = 300)]
public sealed class ExchangeRatesController(IExchangeRateService exchangeRateService) : ControllerBase
{
    /// <summary>Gets the latest USD and EUR reference rates quoted in RUB.</summary>
    [HttpGet]
    [EndpointSummary("Get USD and EUR reference rates in RUB")]
    [EndpointDescription("Returns cached Central Bank of Russia reference rates. These rates do not represent a TAP exchange rate.")]
    [ProducesResponseType<ExchangeRatesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ExchangeRatesResponse>> GetLatest(CancellationToken cancellationToken)
    {
        var rates = await exchangeRateService.GetLatestAsync(cancellationToken);
        return Ok(rates);
    }
}
