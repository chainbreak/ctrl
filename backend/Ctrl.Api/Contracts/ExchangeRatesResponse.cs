namespace Ctrl.Api.Contracts;

/// <summary>Reference exchange rates quoted in the specified currency.</summary>
public sealed record ExchangeRatesResponse(
    DateTimeOffset UpdatedAt,
    string QuoteCurrency,
    IReadOnlyList<CurrencyRateResponse> Rates);

/// <summary>A single reference exchange rate.</summary>
public sealed record CurrencyRateResponse(
    string CurrencyCode,
    string CurrencyName,
    decimal Rate);
