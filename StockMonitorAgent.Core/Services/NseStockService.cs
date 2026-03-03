using System.Text.Json.Nodes;
using StockMonitorAgent.Core.Models;

namespace StockMonitorAgent.Core.Services;

/// <summary>
/// Fetches live NSE stock prices via Yahoo Finance (free, no API key).
/// NSE symbols on Yahoo Finance carry a ".NS" suffix (e.g., "INFY.NS").
/// </summary>
public sealed class NseStockService : IDisposable
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://query1.finance.yahoo.com/v8/finance/chart/";

    public NseStockService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        // Yahoo Finance blocks bare HttpClient calls without a browser User-Agent.
        _http.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// Returns a live quote for the given NSE symbol, or <c>null</c> if not found.
    /// Accepts plain symbol ("RELIANCE"), Yahoo suffix ("RELIANCE.NS"),
    /// or old Finnhub prefix ("NSE:RELIANCE") — all normalised internally.
    /// </summary>
    public async Task<StockQuote?> GetQuoteAsync(string symbol)
    {
        var yahooSymbol = NormalizeToYahoo(symbol);
        var url = $"{BaseUrl}{Uri.EscapeDataString(yahooSymbol)}?interval=1m&range=1d";

        try
        {
            var json  = await _http.GetStringAsync(url);
            var meta  = JsonNode.Parse(json)?["chart"]?["result"]?[0]?["meta"];

            if (meta is null)
                return null;    // Yahoo returns result:null for unknown symbols

            var price = meta["regularMarketPrice"]?.GetValue<decimal>() ?? 0;
            if (price == 0)
                return null;    // Treat zero as invalid (closed/delisted/invalid)

            return new StockQuote
            {
                Symbol        = StripSuffix(yahooSymbol),
                CompanyName   = meta["shortName"]?.GetValue<string>() ?? symbol.ToUpper(),
                CurrentPrice  = price,
                PreviousClose = meta["chartPreviousClose"]?.GetValue<decimal>()
                              ?? meta["previousClose"]?.GetValue<decimal>() ?? 0,
                High          = meta["regularMarketDayHigh"]?.GetValue<decimal>() ?? 0,
                Low           = meta["regularMarketDayLow"]?.GetValue<decimal>() ?? 0,
                FetchedAt     = DateTime.Now
            };
        }
        catch
        {
            return null;    // Caller decides how to surface errors
        }
    }

    /// <summary>
    /// Fetches a 1-year daily chart and computes 52-week, 6-month and 3-month
    /// high/low levels.  Returns <c>null</c> if the symbol is unknown or the
    /// request fails.
    ///
    /// These historical levels let Claude give contextual alert messages such as
    /// "RELIANCE is testing its 52-week high" or "the target sits well within
    /// the 3-month trading range — watch for resistance at ₹3,100."
    /// </summary>
    public async Task<PriceHistorySummary?> GetHistoricalSummaryAsync(string symbol)
    {
        var yahooSymbol = NormalizeToYahoo(symbol);
        // 1-year daily bars — compact and sufficient for all three windows
        var url = $"{BaseUrl}{Uri.EscapeDataString(yahooSymbol)}?interval=1d&range=1y";

        try
        {
            var json   = await _http.GetStringAsync(url);
            var result = JsonNode.Parse(json)?["chart"]?["result"]?[0];
            if (result is null) return null;

            var timestamps = result["timestamp"]?.AsArray();
            var quoteData  = result["indicators"]?["quote"]?[0];
            var highs      = quoteData?["high"]?.AsArray();
            var lows       = quoteData?["low"]?.AsArray();

            if (timestamps is null || highs is null || lows is null) return null;

            var now      = DateTime.UtcNow;
            var cutoff6m = now.AddMonths(-6);
            var cutoff3m = now.AddMonths(-3);

            decimal w52Hi = 0,            w52Lo = decimal.MaxValue;
            decimal mo6Hi = 0,            mo6Lo = decimal.MaxValue;
            decimal mo3Hi = 0,            mo3Lo = decimal.MaxValue;

            for (int i = 0; i < timestamps.Count; i++)
            {
                // Some bars are null when the market was closed that day
                var hi = highs[i]?.GetValue<double?>() ?? 0;
                var lo = lows[i]?.GetValue<double?>()  ?? 0;
                if (hi == 0 || lo == 0) continue;

                var hiD  = (decimal)hi;
                var loD  = (decimal)lo;
                var ts   = timestamps[i]?.GetValue<long>() ?? 0;
                var date = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;

                // 52-week (full 1-year range)
                if (hiD > w52Hi) w52Hi = hiD;
                if (loD < w52Lo) w52Lo = loD;

                // 6-month window
                if (date >= cutoff6m)
                {
                    if (hiD > mo6Hi) mo6Hi = hiD;
                    if (loD < mo6Lo) mo6Lo = loD;
                }

                // 3-month window
                if (date >= cutoff3m)
                {
                    if (hiD > mo3Hi) mo3Hi = hiD;
                    if (loD < mo3Lo) mo3Lo = loD;
                }
            }

            return new PriceHistorySummary
            {
                Symbol     = StripSuffix(yahooSymbol),
                Week52High = Math.Round(w52Hi, 2),
                Week52Low  = Math.Round(w52Lo == decimal.MaxValue ? 0 : w52Lo, 2),
                Month6High = Math.Round(mo6Hi, 2),
                Month6Low  = Math.Round(mo6Lo == decimal.MaxValue ? 0 : mo6Lo, 2),
                Month3High = Math.Round(mo3Hi, 2),
                Month3Low  = Math.Round(mo3Lo == decimal.MaxValue ? 0 : mo3Lo, 2),
                FetchedAt  = DateTime.Now
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns true if the symbol resolves to a live NSE quote.</summary>
    public async Task<bool> IsValidAsync(string symbol)
        => await GetQuoteAsync(symbol) is not null;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NormalizeToYahoo(string symbol)
    {
        symbol = symbol.Trim().ToUpper();
        if (symbol.StartsWith("NSE:"))       symbol = symbol["NSE:".Length..];
        if (!symbol.EndsWith(".NS"))         symbol += ".NS";
        return symbol;
    }

    private static string StripSuffix(string yahooSymbol)
        => yahooSymbol.Replace(".NS", "").ToUpper();

    public void Dispose() => _http.Dispose();
}
