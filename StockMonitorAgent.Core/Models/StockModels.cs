namespace StockMonitorAgent.Core.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Domain models
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A user-defined price alert for an NSE stock.</summary>
public class StockAlert
{
    public string  Symbol      { get; set; } = "";
    public decimal TargetPrice { get; set; }

    /// <summary>"above" → alert when price ≥ target. "below" → price ≤ target.</summary>
    public string   Condition          { get; set; } = "above";
    public bool     IsTriggered        { get; set; } = false;
    public bool     HasProximityWarning { get; set; } = false;
    public DateTime CreatedAt          { get; set; } = DateTime.Now;
    public DateTime? TriggeredAt       { get; set; }
}

/// <summary>A live price snapshot for an NSE stock (from Yahoo Finance).</summary>
public class StockQuote
{
    public string  Symbol        { get; set; } = "";
    public string  CompanyName   { get; set; } = "";
    public decimal CurrentPrice  { get; set; }
    public decimal PreviousClose { get; set; }
    public decimal High          { get; set; }
    public decimal Low           { get; set; }
    public decimal Change        => CurrentPrice - PreviousClose;
    public decimal ChangePercent => PreviousClose == 0 ? 0
                                  : (Change / PreviousClose) * 100;
    public DateTime FetchedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Historical high/low summary computed from a 1-year daily chart.
/// Gives Claude the context to identify whether a triggered price is near
/// a 52-week high, deep in a long-term range, or breaking out of a 3-month base.
/// </summary>
public class PriceHistorySummary
{
    public string  Symbol     { get; set; } = "";

    // ── Computed from full 1-year daily OHLC ─────────────────────────────────
    public decimal Week52High { get; set; }
    public decimal Week52Low  { get; set; }

    // ── Subset windows ────────────────────────────────────────────────────────
    public decimal Month6High { get; set; }
    public decimal Month6Low  { get; set; }
    public decimal Month3High { get; set; }
    public decimal Month3Low  { get; set; }

    public DateTime FetchedAt { get; set; } = DateTime.Now;

    // ── Helpers for prompt building ───────────────────────────────────────────

    /// <summary>
    /// % distance of <paramref name="currentPrice"/> from the 52-week high.
    /// Negative = below the high (normal). Positive = above it (new high).
    /// </summary>
    public decimal PctFromWeek52High(decimal currentPrice) =>
        Week52High == 0 ? 0
        : Math.Round((currentPrice - Week52High) / Week52High * 100, 2);

    /// <summary>% distance of <paramref name="currentPrice"/> above the 52-week low.</summary>
    public decimal PctAboveWeek52Low(decimal currentPrice) =>
        Week52Low == 0 ? 0
        : Math.Round((currentPrice - Week52Low) / Week52Low * 100, 2);

    /// <summary>True when current price is within 1% of the 52-week high.</summary>
    public bool IsNear52WeekHigh(decimal currentPrice) =>
        Week52High > 0 && Math.Abs(PctFromWeek52High(currentPrice)) <= 1m;

    /// <summary>True when current price is within 1% of the 52-week low.</summary>
    public bool IsNear52WeekLow(decimal currentPrice) =>
        Week52Low > 0 && Math.Abs(PctAboveWeek52Low(currentPrice)) <= 1m;
}

// ─────────────────────────────────────────────────────────────────────────────
// Thread-safe alert store
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Thread-safe in-memory repository for price alerts.</summary>
public class AlertStore
{
    private readonly List<StockAlert> _alerts = [];
    private readonly object           _lock   = new();

    /// <summary>
    /// Raised (outside the lock) after a new alert is added.
    /// <see cref="MonitoringService"/> listens here to subscribe the new
    /// symbol to the live WebSocket stream.
    /// </summary>
    public event EventHandler<StockAlert>? AlertAdded;

    public void Add(StockAlert alert)
    {
        lock (_lock) _alerts.Add(alert);
        AlertAdded?.Invoke(this, alert);   // notify outside the lock
    }

    public bool Remove(string symbol)
    {
        lock (_lock)
            return _alerts.RemoveAll(a =>
                a.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                && !a.IsTriggered) > 0;
    }

    public IReadOnlyList<StockAlert> GetActive()
    {
        lock (_lock) return [.. _alerts.Where(a => !a.IsTriggered)];
    }

    public IReadOnlyList<StockAlert> GetAll()
    {
        lock (_lock) return [.. _alerts];
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// EventArgs — allow the Console layer to react without coupling to Core
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Raised by ClaudeAgent each time it calls a tool.</summary>
public class ToolCallEventArgs(string toolName, string inputJson) : EventArgs
{
    public string ToolName  { get; } = toolName;
    public string InputJson { get; } = inputJson;
}

/// <summary>Raised by MonitoringService when a price alert is triggered.</summary>
public class AlertTriggeredEventArgs(
    StockAlert alert,
    StockQuote quote,
    string     notificationMessage) : EventArgs
{
    public StockAlert Alert               { get; } = alert;
    public StockQuote Quote               { get; } = quote;
    public string     NotificationMessage { get; } = notificationMessage;
}

/// <summary>
/// Raised by MonitoringService when a stock price enters the proximity zone
/// (within a configurable % of the alert target) but has not yet triggered.
/// </summary>
public class ProximityWarningEventArgs(
    StockAlert alert,
    StockQuote quote,
    decimal    gapPercent,
    string     warningMessage) : EventArgs
{
    public StockAlert Alert          { get; } = alert;
    public StockQuote Quote          { get; } = quote;
    /// <summary>How far (%) the current price is from the target.</summary>
    public decimal    GapPercent     { get; } = gapPercent;
    public string     WarningMessage { get; } = warningMessage;
}
