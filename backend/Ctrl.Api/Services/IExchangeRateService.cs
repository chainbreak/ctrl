using Ctrl.Api.Contracts;

namespace Ctrl.Api.Services;

public interface IExchangeRateService
{
    Task<ExchangeRatesResponse> GetLatestAsync(CancellationToken cancellationToken);
}
