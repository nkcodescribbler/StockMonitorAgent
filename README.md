# NSE Stock Price Alert Agent

A real-time NSE stock price monitoring agent powered by **Claude AI** (Haiku model) and **Yahoo Finance**.

## Features

- 💬 **Conversational alerts** — tell Claude in plain English to monitor any NSE stock
- ⚡ **Event-driven** — uses Yahoo Finance WebSocket stream; alerts fire the instant the price is hit, not on a polling timer
- 📊 **Historical context** — on alert trigger, Claude shows 52-week / 6-month / 3-month highs & lows so you know if the move is significant
- ⚠️ **Proximity warnings** — get a heads-up when price enters 2% of your target
- 🤖 **Analytical notifications** — Claude doesn't just say "price hit target"; it interprets momentum, historical position, and suggests a next step
- 🔁 **Auto-retry** — handles Anthropic API 529 overload errors with exponential backoff

## Architecture

```
StockMonitorAgent.sln
├── StockMonitorAgent.Core      ← Business logic (zero Console I/O)
│   ├── Models/StockModels.cs   — StockAlert, StockQuote, PriceHistorySummary, EventArgs
│   ├── Services/
│   │   ├── NseStockService.cs  — Yahoo Finance REST (live quotes + 1-year history)
│   │   └── NseStreamService.cs — Yahoo Finance WebSocket (real-time tick stream)
│   ├── Agent/ClaudeAgent.cs    — Anthropic tool-use loop, alert/proximity prompts
│   └── Monitoring/
│       └── MonitoringService.cs — Event-driven alert checker (PriceUpdated → check → fire)
└── StockMonitorAgent           ← Console app (I/O only)
    └── Program.cs
```

## Prerequisites

| Requirement | Notes |
|---|---|
| .NET 8 SDK | https://dotnet.microsoft.com/download |
| Anthropic API key | https://console.anthropic.com |

No other API keys are needed. Yahoo Finance is used via its free public endpoint.

## Setup

```bash
# 1. Clone
git clone https://github.com/nkcodescribbler/StockMonitorAgent.git
cd StockMonitorAgent

# 2. Set your Anthropic API key
# Windows (PowerShell)
$env:ANTHROPIC_API_KEY = "sk-ant-..."

# Windows (persistent)
setx ANTHROPIC_API_KEY "sk-ant-..."

# macOS / Linux
export ANTHROPIC_API_KEY="sk-ant-..."

# 3. Run
dotnet run --project StockMonitorAgent
```

## Usage

```
You: Monitor RELIANCE and alert me when it goes above 3000
You: What is TCS trading at right now?
You: Add an alert for INFY below 1400
You: Show all my alerts
You: Remove the RELIANCE alert
```

### Example alert output

```
╔══════════════════════════════════════════════════════╗
║  🔔  ALERT — RELIANCE                                ║
║  Price ₹   3,001.45  |  Target: above ₹3,000.00     ║
╚══════════════════════════════════════════════════════╝

RELIANCE crossed ₹3,001.45, triggering your alert. Price is at 97% of
today's range (₹2,940–₹3,010), suggesting the breakout has conviction.
This level is within 1.5% of the 52-week high of ₹3,050 — watch that
as key resistance. Consider setting a trailing stop at ₹2,970 to protect
gains while letting the move extend.
```

## How it works

1. User messages are sent to **Claude Haiku** via the Anthropic Messages API
2. Claude uses tool calls (`get_nse_price`, `add_price_alert`, `list_alerts`, `remove_price_alert`) to fetch live data and manage alerts
3. `NseStreamService` holds a persistent WebSocket connection to `wss://streamer.finance.yahoo.com/` and pushes a `PriceUpdated` event on every price tick
4. `MonitoringService` subscribes to those events and checks alert conditions immediately — no polling interval
5. When an alert fires, Claude generates a 4-sentence analytical notification enriched with 52-week / 6-month / 3-month high-low history

## NSE market hours

The WebSocket stream only pushes ticks during NSE trading hours:
**Monday – Friday, 09:15 – 15:30 IST**

Outside market hours the stream stays connected but silent. On-demand price queries via the agent still work at any time via the REST endpoint.

## License

MIT
