using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StockMonitorAgent.Core.Models;

namespace StockMonitorAgent.Core.Services;

/// <summary>
/// Connects to the Yahoo Finance WebSocket stream and raises
/// <see cref="PriceUpdated"/> for every live price tick received —
/// replacing the 30-second REST polling loop with a true push model.
///
/// Endpoint : wss://streamer.finance.yahoo.com/
/// Protocol :
///   → Send  {"subscribe":["RELIANCE.NS","TCS.NS"]}
///   ← Recv  base64-encoded Protobuf binary (yaticker schema)
///
/// Protobuf fields decoded (no NuGet required — manual decoder):
///   Field  1 (string) id            — Yahoo ticker  e.g. "RELIANCE.NS"
///   Field  2 (float)  price         — current price
///   Field 10 (float)  dayHigh
///   Field 11 (float)  dayLow
///   Field 13 (string) shortName     — company display name
///   Field 16 (float)  previousClose
///
/// Note: Yahoo Finance only pushes ticks during NSE market hours
/// (Mon–Fri 09:15–15:30 IST).  Outside those hours the stream stays
/// connected but silent; the REST-based ClaudeAgent tools remain
/// fully functional for on-demand price queries at any time.
/// </summary>
public sealed class NseStreamService : IDisposable
{
    private const string WsUrl = "wss://streamer.finance.yahoo.com/";

    // Symbols currently subscribed (normalised, no ".NS")
    private readonly HashSet<string> _symbols    = new(StringComparer.OrdinalIgnoreCase);
    private readonly object          _symbolLock = new();

    private ClientWebSocket? _ws;
    private bool             _disposed;

    /// <summary>
    /// Raised on a thread-pool thread for every price tick from Yahoo Finance.
    /// The <see cref="StockQuote"/> carries symbol, price, high, low and prev-close.
    /// </summary>
    public event EventHandler<StockQuote>? PriceUpdated;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the WebSocket connection loop.  Automatically reconnects on
    /// disconnection.  Runs until <paramref name="ct"/> is cancelled.
    /// Call as: <c>_ = stream.StartAsync(cts.Token)</c> to run in background.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            _ws = CreateSocket();
            try
            {
                await _ws.ConnectAsync(new Uri(WsUrl), ct);

                // Re-subscribe to all known symbols after each (re)connect
                string[] current;
                lock (_symbolLock) current = [.. _symbols.Select(ToYahoo)];
                if (current.Length > 0)
                    await SendJsonAsync(new { subscribe = current }, ct);

                await ReceiveLoopAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* connection error — will retry after delay */ }
            finally
            {
                try { _ws.Dispose(); } catch { /* ignore */ }
                _ws = null;
            }

            // Pause before reconnect attempt (skip delay on cancellation)
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Subscribes to live price ticks for the given NSE symbols
    /// (e.g. "RELIANCE", "TCS", "INFY.NS" — any format is normalised).
    /// Safe to call before <see cref="StartAsync"/>; subscriptions are
    /// replayed automatically after each reconnect.
    /// </summary>
    public async Task SubscribeAsync(IEnumerable<string> symbols,
                                     CancellationToken   ct = default)
    {
        var toAdd = new List<string>();
        lock (_symbolLock)
        {
            foreach (var s in symbols)
            {
                var norm = Normalise(s);
                if (_symbols.Add(norm))
                    toAdd.Add(ToYahoo(norm));
            }
        }

        if (toAdd.Count > 0 && _ws?.State == WebSocketState.Open)
            await SendJsonAsync(new { subscribe = toAdd }, ct);
    }

    /// <summary>
    /// Removes symbols from the active subscription (e.g. after an alert fires
    /// and the symbol is no longer monitored).
    /// </summary>
    public async Task UnsubscribeAsync(IEnumerable<string> symbols,
                                       CancellationToken   ct = default)
    {
        var toRemove = new List<string>();
        lock (_symbolLock)
        {
            foreach (var s in symbols)
            {
                var norm = Normalise(s);
                if (_symbols.Remove(norm))
                    toRemove.Add(ToYahoo(norm));
            }
        }

        if (toRemove.Count > 0 && _ws?.State == WebSocketState.Open)
            await SendJsonAsync(new { unsubscribe = toRemove }, ct);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _ws?.Dispose(); } catch { /* ignore */ }
    }

    // ── Private: WebSocket I/O ────────────────────────────────────────────────

    /// <summary>Creates a socket with headers that Yahoo Finance expects.</summary>
    private static ClientWebSocket CreateSocket()
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://finance.yahoo.com");
        ws.Options.SetRequestHeader(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/122.0.0.0 Safari/537.36");
        return ws;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var chunk = new byte[8192];
        var accum = new List<byte>(8192);

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            accum.Clear();
            WebSocketReceiveResult result;

            // Read one complete WebSocket message (may span multiple frames)
            do
            {
                result = await _ws.ReceiveAsync(chunk, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                accum.AddRange(chunk.AsSpan(0, result.Count).ToArray());
            }
            while (!result.EndOfMessage);

            var raw = Encoding.UTF8.GetString(accum.ToArray()).Trim();
            if (string.IsNullOrEmpty(raw)) continue;

            // Yahoo Finance sends each tick as a base64-encoded Protobuf blob
            try
            {
                var bytes = Convert.FromBase64String(raw);
                var quote = DecodeProtobuf(bytes);
                if (quote is not null)
                    PriceUpdated?.Invoke(this, quote);
            }
            catch { /* Ignore malformed / heartbeat frames */ }
        }
    }

    private async Task SendJsonAsync<T>(T payload, CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    // ── Private: Protobuf decoder (yaticker schema, zero NuGet dependency) ────

    /// <summary>
    /// Manually decodes a yaticker Protobuf binary.
    /// Wire types handled: 0 (varint), 1 (64-bit), 2 (length-delimited), 5 (32-bit float).
    /// Returns <c>null</c> if the message is malformed or contains no valid price.
    /// </summary>
    private static StockQuote? DecodeProtobuf(byte[] data)
    {
        var quote = new StockQuote { FetchedAt = DateTime.Now };
        int pos   = 0;

        while (pos < data.Length)
        {
            var tag         = ReadVarint(data, ref pos);
            var fieldNumber = (int)(tag >> 3);
            var wireType    = (int)(tag & 0x7);

            switch (wireType)
            {
                case 0: // Varint  (int32, int64, sint64, bool, enum …)
                    ReadVarint(data, ref pos);
                    break;

                case 1: // 64-bit fixed (double, fixed64, sfixed64)
                    if (pos + 8 > data.Length) return null;
                    pos += 8;
                    break;

                case 2: // Length-delimited (string, bytes, embedded message)
                {
                    var len = (int)ReadVarint(data, ref pos);
                    if (pos + len > data.Length) return null;
                    var str = Encoding.UTF8.GetString(data, pos, len);
                    pos += len;
                    switch (fieldNumber)
                    {
                        case 1:  quote.Symbol      = Normalise(str); break;  // id
                        case 13: quote.CompanyName = str;            break;  // shortName
                    }
                    break;
                }

                case 5: // 32-bit fixed (float, fixed32, sfixed32)
                {
                    if (pos + 4 > data.Length) return null;
                    var f = BitConverter.ToSingle(data, pos);
                    pos += 4;
                    switch (fieldNumber)
                    {
                        case 2:  quote.CurrentPrice  = (decimal)f; break;  // price
                        case 10: quote.High           = (decimal)f; break;  // dayHigh
                        case 11: quote.Low            = (decimal)f; break;  // dayLow
                        case 16: quote.PreviousClose  = (decimal)f; break;  // previousClose
                    }
                    break;
                }

                default:
                    // Unknown wire type — cannot safely advance; discard message
                    return null;
            }
        }

        return quote.CurrentPrice > 0 && !string.IsNullOrEmpty(quote.Symbol)
            ? quote
            : null;
    }

    /// <summary>
    /// Reads a base-128 varint from <paramref name="data"/> starting at
    /// <paramref name="pos"/> and advances <paramref name="pos"/> past it.
    /// </summary>
    private static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong result = 0;
        int   shift  = 0;
        byte  b;
        do
        {
            if (pos >= data.Length)
                throw new InvalidOperationException("Unexpected end of varint.");
            b       = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            shift  += 7;
        }
        while ((b & 0x80) != 0);
        return result;
    }

    // ── Symbol helpers ────────────────────────────────────────────────────────

    /// <summary>Strips ".NS" / "NSE:" affixes and upper-cases the ticker.</summary>
    private static string Normalise(string symbol) =>
        symbol.Replace(".NS", "")
              .Replace("NSE:", "")
              .Trim()
              .ToUpperInvariant();

    /// <summary>Appends the ".NS" suffix Yahoo Finance requires for NSE stocks.</summary>
    private static string ToYahoo(string normalised) => normalised + ".NS";
}
