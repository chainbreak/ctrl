namespace Ctrl.Api.Services;

public sealed class ExchangeRatesUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);
