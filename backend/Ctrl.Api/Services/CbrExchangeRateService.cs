using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ctrl.Api.Contracts;

namespace Ctrl.Api.Services;

public sealed class CbrExchangeRateService(HttpClient httpClient) : IExchangeRateService
{
    private static readonly (string Code, string Name)[] Currencies =
    [
        ("USD", "Доллар США"),
        ("EUR", "Евро")
    ];

    public async Task<ExchangeRatesResponse> GetLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var feed = await httpClient.GetFromJsonAsync<CbrRateFeed>("daily_json.js", cancellationToken);
            if (feed?.Valute is null || feed.Date == default)
            {
                throw new InvalidDataException("The CBR response did not contain currency data.");
            }

            var rates = Currencies.Select(currency =>
            {
                if (!feed.Valute.TryGetValue(currency.Code, out var item) ||
                    item.Nominal <= 0 ||
                    item.Value <= 0)
                {
                    throw new InvalidDataException($"The CBR response did not contain a valid {currency.Code} rate.");
                }

                return new CurrencyRateResponse(currency.Code, currency.Name, item.Value / item.Nominal);
            }).ToArray();

            return new ExchangeRatesResponse(feed.Date, "RUB", rates);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExchangeRatesUnavailableException("The CBR request timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ExchangeRatesUnavailableException("The CBR service could not be reached.", exception);
        }
        catch (JsonException exception)
        {
            throw new ExchangeRatesUnavailableException("The CBR response could not be parsed.", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new ExchangeRatesUnavailableException("The CBR response did not contain usable rates.", exception);
        }
    }

    private sealed record CbrRateFeed(
        [property: JsonPropertyName("Date")] DateTimeOffset Date,
        [property: JsonPropertyName("Valute")] Dictionary<string, CbrRate>? Valute);

    private sealed record CbrRate(
        [property: JsonPropertyName("Nominal")] int Nominal,
        [property: JsonPropertyName("Value")] decimal Value);
}
