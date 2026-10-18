using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Skua.WPF.Flash;

/// <summary>
/// Ruffle (web) cannot open raw TCP sockets, and AQW's SmartFox connection is raw TCP.
/// This tiny proxy accepts a WebSocket at ws://127.0.0.1:PORT/?h=HOST&amp;p=PORT and pipes bytes to HOST:PORT.
/// </summary>
public sealed class TcpWebSocketProxy : IDisposable
{
    private readonly HttpListener _http = new();
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; }

    public TcpWebSocketProxy()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public void Start()
    {
        _http.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _http.GetContextAsync();
                    if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
                    _ = Task.Run(() => Handle(ctx));
                }
                catch { if (_cts.IsCancellationRequested) break; }
            }
        });
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var q = HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
            string host = q["h"]!; int port = int.Parse(q["p"]!);
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port);
            var ns = tcp.GetStream();

            var up = Task.Run(async () =>      // Ruffle -> server
            {
                var buf = new byte[16384];
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    await ns.WriteAsync(buf.AsMemory(0, r.Count), _cts.Token);
                }
            });
            var down = Task.Run(async () =>    // server -> Ruffle
            {
                var buf = new byte[16384];
                int n;
                while ((n = await ns.ReadAsync(buf, _cts.Token)) > 0)
                    await ws.SendAsync(buf.AsMemory(0, n), WebSocketMessageType.Binary, true, _cts.Token);
            });
            await Task.WhenAny(up, down);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    public void Dispose() { _cts.Cancel(); try { _http.Close(); } catch { } }
}
