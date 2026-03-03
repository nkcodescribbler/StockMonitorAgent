using StockMonitorAgent.Core.Agent;
using StockMonitorAgent.Core.Models;
using StockMonitorAgent.Core.Monitoring;
using StockMonitorAgent.Core.Services;

// ─── Banner ───────────────────────────────────────────────────────────────────
PrintBanner();

// ─── Guard: require ANTHROPIC_API_KEY ────────────────────────────────────────
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("ERROR: ANTHROPIC_API_KEY environment variable is not set.");
    Console.WriteLine("  Get your key : https://console.anthropic.com");
    Console.WriteLine("  Windows      : setx ANTHROPIC_API_KEY \"your-key-here\"");
    Console.ResetColor();
    return;
}

// ─── Wire up Core services (all business logic lives there) ───────────────────
var alertStore  = new AlertStore();
var stockSvc    = new NseStockService();   // REST — used by ClaudeAgent tool calls
var stream      = new NseStreamService();  // WebSocket — used by MonitoringService
var agent       = new ClaudeAgent(stockSvc, alertStore);
var monitor     = new MonitoringService(stream, alertStore, agent);

// ─── Subscribe to Core events for display ────────────────────────────────────
agent.ToolCalled         += OnToolCalled;
monitor.AlertTriggered   += OnAlertTriggered;
monitor.ProximityWarning += OnProximityWarning;

// ─── Start event-driven monitoring ───────────────────────────────────────────
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// stream.StartAsync connects to wss://streamer.finance.yahoo.com/ and
// pushes a PriceUpdated event on every price change — no polling needed.
_ = stream.StartAsync(cts.Token);
await monitor.StartAsync(cts.Token);  // seeds subscriptions + wires event handlers

// ─── Hint prompts ─────────────────────────────────────────────────────────────
Console.WriteLine("Talk to Claude to set up your NSE stock price alerts.");
Console.WriteLine("Monitoring is event-driven — alerts fire the instant the price is hit.");
Console.WriteLine("Examples:");
Console.WriteLine("  • Monitor RELIANCE and alert me when it goes above 3000");
Console.WriteLine("  • What is ITC trading at right now?");
Console.WriteLine("  • Add an alert for TCS below 3500");
Console.WriteLine("  • Show all my alerts  |  Remove the INFY alert");
Console.WriteLine("\nType 'quit' to exit.\n");

// ─── Conversation loop (I/O only) ────────────────────────────────────────────
while (!cts.Token.IsCancellationRequested)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("You: ");
    Console.ResetColor();

    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input)) continue;
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

    try
    {
        var reply = await agent.ProcessAsync(input, cts.Token);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("\nClaude: ");
        Console.ResetColor();
        Console.WriteLine(reply + "\n");
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n[Error] {ex.Message}\n");
        Console.ResetColor();
    }
}

// ─── Cleanup ──────────────────────────────────────────────────────────────────
cts.Cancel();
stockSvc.Dispose();
stream.Dispose();
agent.Dispose();
Console.WriteLine("\nGoodbye!");

// ═════════════════════════════════════════════════════════════════════════════
// Display helpers — the ONLY place Console is used
// ═════════════════════════════════════════════════════════════════════════════

static void PrintBanner()
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("""
    ╔══════════════════════════════════════════════════════╗
    ║        NSE Stock Price Alert Agent                   ║
    ║        Powered by nkCodeScribbler                    ║
    ╚══════════════════════════════════════════════════════╝
    """);
    Console.ResetColor();
}

static void OnToolCalled(object? _, ToolCallEventArgs e)
{
    // "retry" is a synthetic event emitted by ClaudeAgent when it backs off
    if (e.ToolName == "retry")
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"  [retry] Anthropic API busy — waiting before retry…  {e.InputJson}");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  [tool] {e.ToolName}  {e.InputJson}");
        Console.ResetColor();
    }
}

static void OnAlertTriggered(object? _, AlertTriggeredEventArgs e)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("╔══════════════════════════════════════════════════════╗");
    Console.WriteLine($"║  🔔  ALERT — {e.Alert.Symbol,-40}║");
    Console.WriteLine($"║  Price ₹{e.Quote.CurrentPrice,10:F2}  |  Target: " +
                      $"{e.Alert.Condition} ₹{e.Alert.TargetPrice,-12:F2}║");
    Console.WriteLine("╚══════════════════════════════════════════════════════╝");
    Console.ResetColor();

    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine($"\n{e.NotificationMessage}\n");
    Console.ResetColor();
}

static void OnProximityWarning(object? _, ProximityWarningEventArgs e)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine("┌──────────────────────────────────────────────────────┐");
    Console.WriteLine($"│  ⚠  PROXIMITY — {e.Alert.Symbol,-38}│");
    Console.WriteLine($"│  ₹{e.Quote.CurrentPrice:F2}  →  {e.Alert.Condition} ₹{e.Alert.TargetPrice:F2}" +
                      $"   ({e.GapPercent:F2}% away){new string(' ', Math.Max(0, 14 - e.Alert.Symbol.Length))}│");
    Console.WriteLine("└──────────────────────────────────────────────────────┘");
    Console.ResetColor();

    Console.ForegroundColor = ConsoleColor.DarkMagenta;
    Console.WriteLine($"\n{e.WarningMessage}\n");
    Console.ResetColor();
}
