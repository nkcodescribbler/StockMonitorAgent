# Changelog

All notable changes to this project will be documented in this file.

---

## [1.0.0] — 2026-03-03

### Added

#### Core architecture
- `StockMonitorAgent.Core` class library — zero Console I/O; all business logic lives here
- `StockMonitorAgent` console app — pure I/O layer; subscribes to Core events for display
- Both projects bundled in a single `StockMonitorAgent.sln`

#### Real-time event-driven monitoring
- `NseStreamService` — connects to `wss://streamer.finance.yahoo.com/` and pushes a `PriceUpdated` event on every NSE price tick; no polling interval
- Manual Protobuf decoder (yaticker schema) — zero NuGet dependency; decodes base64-encoded binary frames from Yahoo Finance
- Auto-reconnect loop with 5-second back-off on WebSocket disconnect
- Dynamic symbol subscription: when the user adds a new alert via Claude, the symbol is immediately subscribed on the live stream via `AlertStore.AlertAdded` event

#### Conversational agent (Claude Haiku)
- `ClaudeAgent` — drives the Anthropic tool-use loop via direct REST calls to `/v1/messages`
- Four tools: `get_nse_price`, `add_price_alert`, `remove_price_alert`, `list_alerts`
- `get_nse_price` returns intraday analytics (`day_range_position_pct`, `momentum` label) **and** 52-week / 6-month / 3-month historical high/low — all in one tool response, fetched in parallel
- `list_alerts` fetches live prices for all alert symbols concurrently so Claude can comment on proximity gaps
- `ToolCalled` event raised on each tool invocation — UI layer displays in dark grey

#### Historical price context
- `PriceHistorySummary` model — 52-week, 6-month and 3-month high/low windows with helper methods (`PctFromWeek52High`, `IsNear52WeekHigh`, etc.)
- `NseStockService.GetHistoricalSummaryAsync` — fetches 1-year daily OHLC from Yahoo Finance; computes all three windows in a single pass
- Historical fetch runs in parallel with the live quote fetch — no added latency

#### Analytical LLM notifications
- `GenerateAlertMessageAsync` — 4-sentence analytical notification: confirms trigger, places price in 52-week/6-month/3-month context, interprets intraday momentum, suggests a specific next step
- `GenerateProximityWarningAsync` — 3-sentence heads-up when price enters the 2% proximity zone; includes whether the target itself sits at a historically significant level (52-week high/low, 3-month breakout)
- Both methods are one-shot (no history pollution) and run historical fetch in parallel

#### Proximity warnings
- `MonitoringService.ProximityThresholdPercent` (default 2%) — configurable proximity zone
- `ProximityWarning` event fires once per alert when the price enters the zone (`HasProximityWarning` flag prevents re-fire)
- `ProximityWarningEventArgs` carries `Alert`, `Quote`, `GapPercent`, and `WarningMessage`

#### Resilience
- `SendAsync` in `ClaudeAgent` retries on HTTP 429, 502, 503, 529 with exponential back-off: 2 s → 5 s → 10 s (up to 3 retries)
- Respects `Retry-After` response header when Anthropic sends one
- Synthetic `retry` tool event raised during back-off so the console shows progress in amber

#### NSE data via Yahoo Finance (no API key required)
- `NseStockService` — live REST quote with intraday OHLC; normalises `NSE:SYMBOL`, `SYMBOL.NS`, and plain `SYMBOL` formats
- `NseStreamService` — WebSocket stream; same normalisation applied to all subscription messages

#### Console display
- Yellow bordered box on alert trigger (`OnAlertTriggered`)
- Magenta bordered box on proximity warning (`OnProximityWarning`)
- Dark grey tool-call trace (`OnToolCalled`)
- Amber retry notification (`OnToolCalled` with `ToolName == "retry"`)
