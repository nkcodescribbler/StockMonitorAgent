using StockMonitorAgent.Core.Agent;
using StockMonitorAgent.Core.Models;
using StockMonitorAgent.Core.Services;

namespace StockMonitorAgent.Core.Monitoring;

/// <summary>
/// Event-driven monitoring service — replaces the 30-second polling loop
/// with real-time reaction to WebSocket price ticks.
///
/// How it works:
///   1. On <see cref="StartAsync"/>, subscribe every existing alert symbol
///      to <see cref="NseStreamService"/>.
///   2. When the user adds a new alert (<see cref="AlertStore.AlertAdded"/>),
///      dynamically subscribe the new symbol — no restart needed.
///   3. Every time Yahoo Finance pushes a price tick,
///      <see cref="NseStreamService.PriceUpdated"/> fires and
///      <see cref="CheckAlertAsync"/> runs immediately on the thread pool.
///   4. If a target is hit  → ask Claude for an analytical message
///                          → raise <see cref="AlertTriggered"/>.
///   5. If price enters the proximity zone (within
///      <see cref="ProximityThresholdPercent"/>%) → ask Claude for a
///      heads-up message (once per alert) → raise <see cref="ProximityWarning"/>.
/// </summary>
public sealed class MonitoringService
{
    private readonly NseStreamService _stream;
    private readonly AlertStore       _alerts;
    private readonly ClaudeAgent      _agent;

    /// <summary>
    /// How close (as % of current price) the price must be to the target
    /// before a proximity warning fires.  Default: 2 %.
    /// </summary>
    public decimal ProximityThresholdPercent { get; set; } = 2m;

    /// <summary>Raised immediately when a price target is hit.</summary>
    public event EventHandler<AlertTriggeredEventArgs>? AlertTriggered;

    /// <summary>
    /// Raised when the price enters the proximity zone.
    /// Fires only once per alert regardless of how many ticks arrive.
    /// </summary>
    public event EventHandler<ProximityWarningEventArgs>? ProximityWarning;

    public MonitoringService(NseStreamService stream,
                             AlertStore       alerts,
                             ClaudeAgent      agent)
    {
        _stream = stream;
        _alerts = alerts;
        _agent  = agent;
    }

    /// <summary>
    /// Registers event handlers and seeds subscriptions for any alerts
    /// that already exist.  Returns immediately — actual streaming is
    /// driven by <see cref="NseStreamService.StartAsync"/> running in
    /// the background.
    /// </summary>
    public Task StartAsync(CancellationToken ct)
    {
        // ── Seed: subscribe symbols that already have active alerts ───────────
        var existing = _alerts.GetActive()
                              .Select(a => a.Symbol)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();

        if (existing.Count > 0)
            _ = _stream.SubscribeAsync(existing, ct);

        // ── Dynamic: when user adds a new alert, subscribe its symbol ─────────
        _alerts.AlertAdded += (_, alert) =>
            _ = _stream.SubscribeAsync([alert.Symbol], ct);

        // ── Core: react to every price tick from the WebSocket ────────────────
        _stream.PriceUpdated += (_, quote) =>
            _ = Task.Run(() => CheckAlertAsync(quote, ct), ct);

        return Task.CompletedTask;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Called on a thread-pool thread for every price tick.
    /// Checks all active alerts for the incoming symbol and fires
    /// events where conditions are met.
    /// </summary>
    private async Task CheckAlertAsync(StockQuote quote, CancellationToken ct)
    {
        // Only consider alerts whose symbol matches this tick
        var matching = _alerts.GetActive()
            .Where(a => a.Symbol.Equals(quote.Symbol, StringComparison.OrdinalIgnoreCase));

        foreach (var alert in matching)
        {
            if (ct.IsCancellationRequested) break;

            // ── 1. Full trigger ───────────────────────────────────────────────
            bool triggered = alert.Condition == "above"
                ? quote.CurrentPrice >= alert.TargetPrice
                : quote.CurrentPrice <= alert.TargetPrice;

            if (triggered)
            {
                // Mark before the async call to prevent re-entry from the next tick
                alert.IsTriggered = true;
                alert.TriggeredAt = DateTime.Now;

                string message;
                try   { message = await _agent.GenerateAlertMessageAsync(alert, quote); }
                catch { message = $"{alert.Symbol} hit ₹{quote.CurrentPrice:F2} " +
                                  $"(target: {alert.Condition} ₹{alert.TargetPrice:F2})."; }

                AlertTriggered?.Invoke(this,
                    new AlertTriggeredEventArgs(alert, quote, message));
                continue;
            }

            // ── 2. Proximity warning (fires only once per alert) ──────────────
            if (alert.HasProximityWarning) continue;

            var gapPercent = Math.Abs(alert.TargetPrice - quote.CurrentPrice)
                             / quote.CurrentPrice * 100m;

            if (gapPercent > ProximityThresholdPercent) continue;

            // Set flag before the async Claude call to prevent duplicate warnings
            alert.HasProximityWarning = true;

            string warningMsg;
            try   { warningMsg = await _agent.GenerateProximityWarningAsync(
                                     alert, quote, gapPercent); }
            catch { warningMsg = $"{alert.Symbol} is ₹" +
                                 $"{Math.Abs(alert.TargetPrice - quote.CurrentPrice):F2} " +
                                 $"({gapPercent:F2}%) from your " +
                                 $"{alert.Condition} ₹{alert.TargetPrice:F2} target."; }

            ProximityWarning?.Invoke(this,
                new ProximityWarningEventArgs(alert, quote, gapPercent, warningMsg));
        }
    }
}
