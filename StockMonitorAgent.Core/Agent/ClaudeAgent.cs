using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockMonitorAgent.Core.Models;
using StockMonitorAgent.Core.Services;

namespace StockMonitorAgent.Core.Agent;

/// <summary>
/// Claude-powered agent for managing NSE stock price alerts.
///
/// Responsibilities (Core only — zero Console I/O):
///   • Maintain conversation history.
///   • Run the Anthropic tool-use agentic loop.
///   • Execute tool calls: fetch prices, add/remove/list alerts.
///   • Raise <see cref="ToolCalled"/> so the UI layer can show debug info.
///   • Return plain strings; the caller decides how to display them.
/// </summary>
public sealed class ClaudeAgent : IDisposable
{
    // ── Anthropic API config ─────────────────────────────────────────────────
    private const string ApiUrl    = "https://api.anthropic.com/v1/messages";
    private const string ModelId   = "claude-haiku-4-5";
    private const int    MaxTokens = 4096;

    // ── Dependencies ─────────────────────────────────────────────────────────
    private readonly HttpClient      _http;
    private readonly NseStockService _stocks;
    private readonly AlertStore      _alerts;

    // ── Conversation state (belongs in Core, not UI) ─────────────────────────
    private readonly List<JsonObject> _history = [];

    // ── Event: raised when Claude calls a tool (UI can display it) ──────────
    public event EventHandler<ToolCallEventArgs>? ToolCalled;

    // ── Tool schema sent to the Anthropic API ────────────────────────────────
    private static readonly JsonArray ToolDefinitions = JsonNode.Parse("""
    [
      {
        "name": "get_nse_price",
        "description": "Get the current live price AND 52-week/6-month/3-month high-low history for an NSE-listed stock. Use the historical data to tell the user whether the current price or their alert target is near a 52-week high/low or breaking out of a multi-month range. Call this before adding an alert so you can tell the user how far the target is and whether it is historically significant.",
        "input_schema": {
          "type": "object",
          "properties": {
            "symbol": { "type": "string", "description": "NSE ticker, e.g. RELIANCE, TCS, INFY, ITC" }
          },
          "required": ["symbol"]
        }
      },
      {
        "name": "add_price_alert",
        "description": "Register a price alert. Always fetch the current price first.",
        "input_schema": {
          "type": "object",
          "properties": {
            "symbol":       { "type": "string" },
            "target_price": { "type": "number", "description": "Target in INR" },
            "condition":    { "type": "string", "enum": ["above","below"] }
          },
          "required": ["symbol","target_price","condition"]
        }
      },
      {
        "name": "remove_price_alert",
        "description": "Remove an active price alert for a stock.",
        "input_schema": {
          "type": "object",
          "properties": {
            "symbol": { "type": "string" }
          },
          "required": ["symbol"]
        }
      },
      {
        "name": "list_alerts",
        "description": "List all active and triggered price alerts with live current prices, today's % change, and gap-to-target so you can tell the user how close each alert is to firing.",
        "input_schema": { "type": "object", "properties": {}, "required": [] }
      }
    ]
    """)!.AsArray();

    // ── System prompt ────────────────────────────────────────────────────────
    private const string SystemPrompt = """
    You are an intelligent NSE stock market monitoring assistant powered by Claude.
    Help the user set up and manage real-time price alerts for NSE-listed stocks
    using live prices from Yahoo Finance.

    Guidelines:
    - Always fetch the current price before adding an alert; tell the user the gap
      (e.g., "RELIANCE is at ₹2,850. Your alert fires when it crosses ₹3,000 — 5.3% away.").
    - Use ₹ and Indian number formatting (e.g., ₹1,23,456.78).
    - If a symbol is not found, tell the user and suggest the correct NSE ticker.
    - Be concise and market-aware.
    - Common NSE symbols: RELIANCE, TCS, INFY, HDFCBANK, WIPRO, ITC,
      BAJFINANCE, SBIN, ICICIBANK, ADANIENT, TATAMOTORS, HINDUNILVR.
    """;

    // ── Constructor ──────────────────────────────────────────────────────────
    public ClaudeAgent(NseStockService stocks, AlertStore alerts)
    {
        _stocks = stocks;
        _alerts = alerts;

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a user message, runs the full agentic tool-use loop,
    /// and returns Claude's final text response.
    /// Raises <see cref="ToolCalled"/> for each tool invocation.
    /// </summary>
    public async Task<string> ProcessAsync(string userMessage,
                                           CancellationToken ct = default)
    {
        _history.Add(UserMessage(userMessage));
        return await RunAgentLoopAsync(ct);
    }

    /// <summary>
    /// Generates a rich, analytical alert notification without polluting conversation history.
    /// Fetches 52-week / 6-month / 3-month history in parallel so Claude can tell the investor
    /// whether the triggered price is historically significant — e.g. "testing 52-week high".
    /// </summary>
    public async Task<string> GenerateAlertMessageAsync(StockAlert alert,
                                                        StockQuote quote)
    {
        // Kick off historical fetch in parallel — doesn't delay the alert
        var histTask = _stocks.GetHistoricalSummaryAsync(alert.Symbol);

        var dayRangePositionPct = quote.High == quote.Low ? 50m
            : Math.Round((quote.CurrentPrice - quote.Low) / (quote.High - quote.Low) * 100, 1);

        var hist = await histTask;

        var histBlock = hist is null
            ? "  (historical data unavailable)"
            : $"""
              52-week high : ₹{hist.Week52High:F2}  ({hist.PctFromWeek52High(quote.CurrentPrice):+0.00;-0.00}% from current price)
              52-week low  : ₹{hist.Week52Low:F2}   ({hist.PctAboveWeek52Low(quote.CurrentPrice):+0.00}% above the low)
              6-month high : ₹{hist.Month6High:F2}
              6-month low  : ₹{hist.Month6Low:F2}
              3-month high : ₹{hist.Month3High:F2}
              3-month low  : ₹{hist.Month3Low:F2}
              Near 52-wk high? {(hist.IsNear52WeekHigh(quote.CurrentPrice) ? "YES — approaching historic resistance" : "No")}
              Near 52-wk low?  {(hist.IsNear52WeekLow(quote.CurrentPrice)  ? "YES — near historic support"           : "No")}
            """;

        var prompt = $"""
        A price alert has just triggered. Write a 4-sentence notification that gives the
        investor genuine insight — not just a restatement of numbers.

        Intraday snapshot:
          Stock             : {quote.Symbol} ({quote.CompanyName})
          Alert condition   : {alert.Condition} ₹{alert.TargetPrice:F2}
          Current price     : ₹{quote.CurrentPrice:F2}
          Previous close    : ₹{quote.PreviousClose:F2}
          Day high / low    : ₹{quote.High:F2} / ₹{quote.Low:F2}
          Day change        : {quote.ChangePercent:+0.00;-0.00}%
          Day-range position: {dayRangePositionPct}% (0 = day low, 100 = day high)
          Alert created     : {alert.CreatedAt:dd-MMM HH:mm}

        Historical context (past 12 months of NSE daily data):
        {histBlock}

        Your 4 sentences must address:
          1. Confirm the alert fired and state the exact trigger price.
          2. Place it in historical context — is this near the 52-week high/low?
             Is it breaking out of the 3-month or 6-month range, or still mid-range?
          3. Assess intraday momentum — where is the price in today's range?
             Does the move look sustained or like a brief spike to the target?
          4. Offer one specific, actionable next step
             (e.g. set a trailing stop just below the 3-month low, watch the
              52-week high as key resistance, confirm with volume before acting).

        Use ₹ and Indian number formatting. Be direct and market-aware.
        """;

        var request  = BuildRequest([UserMessage(prompt)], includeTools: false);
        var response = await SendAsync(request);
        return ExtractText(response);
    }

    /// <summary>
    /// Generates a proximity warning when the price is within
    /// <paramref name="gapPercent"/>% of the alert target.
    /// Includes historical high/low context so Claude can assess whether the
    /// target price itself is at a historically important level.
    /// </summary>
    public async Task<string> GenerateProximityWarningAsync(StockAlert alert,
                                                             StockQuote quote,
                                                             decimal    gapPercent)
    {
        // Fetch history in parallel — lightweight since it runs in background
        var histTask  = _stocks.GetHistoricalSummaryAsync(alert.Symbol);

        var direction = alert.Condition == "above" ? "approaching from below"
                                                   : "approaching from above";
        var momentum  = quote.ChangePercent >= 0 ? "up on the day" : "down on the day";
        var hist      = await histTask;

        // Is the target itself near a historically significant level?
        string targetContext = "";
        if (hist is not null)
        {
            var pctFromW52Hi = Math.Abs(hist.PctFromWeek52High(alert.TargetPrice));
            var pctFromW52Lo = Math.Abs((alert.TargetPrice - hist.Week52Low) / hist.Week52Low * 100);

            if (pctFromW52Hi <= 2)
                targetContext = $"  ⚠ The target ₹{alert.TargetPrice:F2} is within 2% of the 52-week high (₹{hist.Week52High:F2}) — strong resistance expected there.";
            else if (pctFromW52Lo <= 2)
                targetContext = $"  ✔ The target ₹{alert.TargetPrice:F2} is near the 52-week low (₹{hist.Week52Low:F2}) — key support level.";
            else if (alert.TargetPrice >= hist.Month3High)
                targetContext = $"  ↑ The target ₹{alert.TargetPrice:F2} is above the 3-month high (₹{hist.Month3High:F2}) — a breakout if reached.";
        }

        var prompt = $"""
        A stock is closing in on a user-defined price alert. Give a concise 3-sentence
        heads-up that helps the investor decide whether to act now or wait.

        Current situation:
          Stock         : {quote.Symbol} ({quote.CompanyName})
          Current price : ₹{quote.CurrentPrice:F2}  ({quote.ChangePercent:+0.00;-0.00}% — {momentum})
          Alert target  : {alert.Condition} ₹{alert.TargetPrice:F2}
          Gap remaining : {gapPercent:F2}%  ({direction})
          Day high / low: ₹{quote.High:F2} / ₹{quote.Low:F2}
        {targetContext}

        Sentence 1: State how close the price is to the target and whether today's
                    momentum makes it likely to be reached.
        Sentence 2: Comment on whether the target price itself sits at a historically
                    important level (52-week high/low, 3-month range boundary) if relevant.
        Sentence 3: Suggest one specific action the investor could take right now
                    (e.g., prepare a limit order, tighten a stop, watch volume).

        Use ₹ and Indian number formatting. Be direct and actionable.
        """;

        var request  = BuildRequest([UserMessage(prompt)], includeTools: false);
        var response = await SendAsync(request);
        return ExtractText(response);
    }

    // ── Agentic loop ─────────────────────────────────────────────────────────

    private async Task<string> RunAgentLoopAsync(CancellationToken ct)
    {
        string finalText = "";

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var response = await SendAsync(BuildRequest(_history, includeTools: true), ct);
            var stopReason = response["stop_reason"]?.GetValue<string>();
            var blocks     = response["content"]?.AsArray() ?? [];

            // Record assistant turn
            var assistantContent = new JsonArray();
            foreach (var b in blocks) assistantContent.Add(b?.DeepClone());
            _history.Add(new JsonObject
            {
                ["role"]    = "assistant",
                ["content"] = assistantContent
            });

            // Capture final text
            foreach (var b in blocks)
                if (b?["type"]?.GetValue<string>() == "text")
                    finalText = b["text"]?.GetValue<string>() ?? "";

            if (stopReason != "tool_use")
                return finalText;

            // Execute every tool Claude requested
            var toolResults = new JsonArray();
            foreach (var b in blocks)
            {
                if (b?["type"]?.GetValue<string>() != "tool_use") continue;

                var id    = b["id"]?.GetValue<string>()   ?? "";
                var name  = b["name"]?.GetValue<string>() ?? "";
                var input = b["input"]?.AsObject()        ?? [];

                // Notify UI (fire-and-forget event)
                ToolCalled?.Invoke(this, new ToolCallEventArgs(name, input.ToJsonString()));

                var result = await RunToolAsync(name, input);
                toolResults.Add(new JsonObject
                {
                    ["type"]        = "tool_result",
                    ["tool_use_id"] = id,
                    ["content"]     = result
                });
            }

            _history.Add(new JsonObject
            {
                ["role"]    = "user",
                ["content"] = toolResults
            });
        }
    }

    // ── Tool implementations (pure logic, no I/O) ─────────────────────────────

    private async Task<string> RunToolAsync(string name, JsonObject input)
    {
        try
        {
            return name switch
            {
                "get_nse_price"      => await ToolGetPrice(input),
                "add_price_alert"    => await ToolAddAlert(input),
                "remove_price_alert" => ToolRemoveAlert(input),
                "list_alerts"        => await ToolListAlertsAsync(),
                _                    => $"Unknown tool: {name}"
            };
        }
        catch (Exception ex)
        {
            return $"Tool error: {ex.Message}";
        }
    }

    private async Task<string> ToolGetPrice(JsonObject input)
    {
        var symbol = input["symbol"]?.GetValue<string>() ?? "";

        // Fetch live quote AND 1-year history in parallel — same latency as one call
        var quoteTask = _stocks.GetQuoteAsync(symbol);
        var histTask  = _stocks.GetHistoricalSummaryAsync(symbol);

        var quote = await quoteTask;
        if (quote is null)
            return $"Symbol '{symbol}' not found on NSE. Check the ticker is correct.";

        var hist = await histTask;   // usually already completed

        // Intraday computed analytics
        var dayRange       = quote.High - quote.Low;
        var dayRangePosPct = dayRange == 0 ? 50m
            : Math.Round((quote.CurrentPrice - quote.Low) / dayRange * 100, 1);

        var momentum = quote.ChangePercent switch
        {
            >= 2m    => "strong_uptrend",
            >= 0.5m  => "mild_uptrend",
            <= -2m   => "strong_downtrend",
            <= -0.5m => "mild_downtrend",
            _        => "sideways"
        };

        // Historical context object (null-safe)
        object? historical = hist is null ? null : new
        {
            week52_high              = hist.Week52High,
            week52_low               = hist.Week52Low,
            pct_from_52wk_high       = hist.PctFromWeek52High(quote.CurrentPrice),
            pct_above_52wk_low       = hist.PctAboveWeek52Low(quote.CurrentPrice),
            month6_high              = hist.Month6High,
            month6_low               = hist.Month6Low,
            month3_high              = hist.Month3High,
            month3_low               = hist.Month3Low,
            // Narrative flags Claude can reference directly
            near_52wk_high           = hist.IsNear52WeekHigh(quote.CurrentPrice),
            near_52wk_low            = hist.IsNear52WeekLow(quote.CurrentPrice),
            above_3month_high        = quote.CurrentPrice > hist.Month3High,
            below_3month_low         = quote.CurrentPrice < hist.Month3Low
        };

        return JsonSerializer.Serialize(new
        {
            symbol                 = quote.Symbol,
            company                = quote.CompanyName,
            current_price          = quote.CurrentPrice,
            prev_close             = quote.PreviousClose,
            change                 = Math.Round(quote.Change, 2),
            change_pct             = Math.Round(quote.ChangePercent, 2),
            day_high               = quote.High,
            day_low                = quote.Low,
            day_range              = Math.Round(dayRange, 2),
            day_range_position_pct = dayRangePosPct,
            momentum,
            historical,            // 52-wk / 6-mo / 3-mo highs & lows
            as_of                  = quote.FetchedAt.ToString("HH:mm:ss")
        });
    }

    private async Task<string> ToolAddAlert(JsonObject input)
    {
        var symbol  = input["symbol"]?.GetValue<string>()       ?? "";
        var target  = input["target_price"]?.GetValue<decimal>() ?? 0;
        var cond    = input["condition"]?.GetValue<string>()     ?? "above";

        var quote = await _stocks.GetQuoteAsync(symbol);
        if (quote is null)
            return $"Cannot create alert: '{symbol}' not found on NSE.";

        _alerts.Add(new StockAlert
        {
            Symbol      = quote.Symbol,
            TargetPrice = target,
            Condition   = cond
        });

        return JsonSerializer.Serialize(new
        {
            status        = "alert_added",
            symbol        = quote.Symbol,
            target_price  = target,
            condition     = cond,
            current_price = quote.CurrentPrice,
            gap_pct       = Math.Round(
                Math.Abs((target - quote.CurrentPrice) / quote.CurrentPrice) * 100, 2)
        });
    }

    private string ToolRemoveAlert(JsonObject input)
    {
        var symbol = input["symbol"]?.GetValue<string>() ?? "";
        return _alerts.Remove(symbol)
            ? $"Alert for {symbol.ToUpper()} removed."
            : $"No active alert found for {symbol.ToUpper()}.";
    }

    private async Task<string> ToolListAlertsAsync()
    {
        var all = _alerts.GetAll();
        if (all.Count == 0) return "No alerts set up yet.";

        // Fetch live prices in parallel so Claude can comment on proximity
        var tasks = all.Select(a => _stocks.GetQuoteAsync(a.Symbol));
        var quotes = await Task.WhenAll(tasks);

        var result = all.Zip(quotes, (alert, quote) =>
        {
            decimal? gapPct = null;
            string?  gapDir = null;

            if (!alert.IsTriggered && quote is not null)
            {
                gapPct = Math.Round(
                    Math.Abs(alert.TargetPrice - quote.CurrentPrice)
                    / quote.CurrentPrice * 100, 2);
                gapDir = alert.Condition == "above"
                    ? (quote.CurrentPrice >= alert.TargetPrice ? "ABOVE_TARGET" : "below_target")
                    : (quote.CurrentPrice <= alert.TargetPrice ? "BELOW_TARGET" : "above_target");
            }

            return new
            {
                symbol          = alert.Symbol,
                target          = alert.TargetPrice,
                condition       = alert.Condition,
                status          = alert.IsTriggered ? "TRIGGERED" : "ACTIVE",
                current_price   = quote?.CurrentPrice,
                change_pct      = quote is not null
                                  ? Math.Round(quote.ChangePercent, 2)
                                  : (decimal?)null,
                gap_to_target_pct = gapPct,
                gap_direction   = gapDir,
                created         = alert.CreatedAt.ToString("HH:mm:ss"),
                triggered_at    = alert.TriggeredAt?.ToString("HH:mm:ss")
            };
        });

        return JsonSerializer.Serialize(result);
    }

    // ── HTTP helpers ─────────────────────────────────────────────────────────

    private JsonObject BuildRequest(List<JsonObject> messages, bool includeTools)
    {
        var req = new JsonObject
        {
            ["model"]      = ModelId,
            ["max_tokens"] = MaxTokens,
            ["system"]     = SystemPrompt,
            ["messages"]   = JsonNode.Parse(JsonSerializer.Serialize(messages))!.AsArray()
        };
        if (includeTools)
            req["tools"] = ToolDefinitions.DeepClone().AsArray();
        return req;
    }

    // Retry delays for transient failures: 2 s → 5 s → 10 s
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>
    /// Sends one Anthropic API request with automatic retry on transient errors.
    ///
    /// Retried (up to 3 times with exponential back-off):
    ///   • 529 overloaded_error — server temporarily at capacity
    ///   • 429 rate_limit_error — too many requests; respects Retry-After header
    ///   • 503 / 502            — transient gateway errors
    ///
    /// Not retried (fail immediately):
    ///   • 401 — bad API key
    ///   • 400 — malformed request (bug in our code)
    ///   • 4xx other — permanent client errors
    /// </summary>
    private async Task<JsonObject> SendAsync(JsonObject request,
                                             CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            var content  = new StringContent(
                request.ToJsonString(), Encoding.UTF8, "application/json");
            var httpResp = await _http.PostAsync(ApiUrl, content, ct);
            var body     = await httpResp.Content.ReadAsStringAsync(ct);

            if (httpResp.IsSuccessStatusCode)
                return JsonNode.Parse(body)?.AsObject()
                    ?? throw new InvalidOperationException("Empty API response.");

            var status = (int)httpResp.StatusCode;

            // Retry on transient server-side errors only
            bool isTransient = status is 429 or 502 or 503 or 529;

            if (isTransient && attempt < RetryDelays.Length)
            {
                // Honour Retry-After header when the server sends one
                var delay = RetryDelays[attempt];
                if (httpResp.Headers.TryGetValues("Retry-After", out var vals)
                    && int.TryParse(vals.FirstOrDefault(), out var secs))
                    delay = TimeSpan.FromSeconds(Math.Max(secs, delay.TotalSeconds));

                // Inform caller something is happening (raised as fake tool event)
                ToolCalled?.Invoke(this, new ToolCallEventArgs(
                    "retry",
                    $"{{\"attempt\":{attempt + 1},\"status\":{status}," +
                    $"\"wait_sec\":{delay.TotalSeconds}}}"));

                await Task.Delay(delay, ct);
                continue;
            }

            // Permanent or exhausted-retries — translate to a clean message
            var friendly = status switch
            {
                401 => "Invalid API key. Verify your ANTHROPIC_API_KEY environment variable.",
                400 => "Bad request sent to Anthropic API — please report this as a bug.",
                429 => "Rate limit reached after retries. Wait a minute and try again.",
                529 => "Anthropic API is overloaded. Please try again in a few minutes.",
                503 or 502 => "Anthropic API is temporarily unavailable. Try again shortly.",
                _   => $"Anthropic API error {status}."
            };

            throw new HttpRequestException(friendly);
        }

        // Unreachable — satisfies the compiler
        throw new HttpRequestException("Anthropic API is unavailable. Please try again later.");
    }

    private static string ExtractText(JsonObject response)
    {
        foreach (var b in response["content"]?.AsArray() ?? [])
            if (b?["type"]?.GetValue<string>() == "text")
                return b["text"]?.GetValue<string>() ?? "";
        return "";
    }

    private static JsonObject UserMessage(string text)
        => new() { ["role"] = "user", ["content"] = text };

    public void Dispose() => _http.Dispose();
}
