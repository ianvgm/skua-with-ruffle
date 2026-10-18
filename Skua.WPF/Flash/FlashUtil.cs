using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;

namespace Skua.WPF.Flash;

/// <summary>
/// IFlashUtil backed by Ruffle (web build) inside WebView2, instead of the Flash ActiveX control.
/// Wire format with skua.swf is unchanged: same XML as ExternalInterface, so Skua.Core is untouched.
/// </summary>
public class RuffleFlashUtil : IFlashUtil
{
    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptManager> _lazyManager;
    private WebView2? _view;
    private TcpWebSocketProxy? _proxy;
    private bool _ready;

    public event FlashCallHandler? FlashCall;
    /// <summary>Raised on the UI thread with the control to place in the window.</summary>
    public WebView2? View => _view;

    public RuffleFlashUtil(IMessenger messenger, Lazy<IScriptManager> manager)
    {
        _messenger = messenger;
        _lazyManager = manager;
    }

    public void InitializeFlash()
    {
        _view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
        _messenger.Send(new RuffleViewChangedMessage(_view));
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            string dir = AppContext.BaseDirectory;
            // Skua's post-build step moves most files into Assemblies\, so look in both places.
            string host = new[] { Path.Combine(dir, "ruffle-host"), Path.Combine(dir, "Assemblies", "ruffle-host") }
                .FirstOrDefault(Directory.Exists)
                ?? throw new DirectoryNotFoundException("ruffle-host folder not found next to Skua.exe");
            File.Copy(Path.Combine(dir, "skua.swf"), Path.Combine(host, "skua.swf"), true);

            // --disable-web-security: lets Ruffle fetch game.aq.com assets without CORS headers (dev shortcut).
            var env = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skua", "WebView2"),
                new CoreWebView2EnvironmentOptions("--disable-web-security --autoplay-policy=no-user-gesture-required --disable-features=IsolateOrigins,site-per-process"));
            await _view!.EnsureCoreWebView2Async(env);
            var cw = _view.CoreWebView2;
            cw.Settings.AreDefaultContextMenusEnabled = false;
            cw.Settings.AreDevToolsEnabled = true; // F12 for debugging
            cw.SetVirtualHostNameToFolderMapping("skua.local", host, CoreWebView2HostResourceAccessKind.Allow);
            cw.WebMessageReceived += OnWebMessage;

            _proxy = new TcpWebSocketProxy(); _proxy.Start();
            cw.Navigate("https://skua.local/index.html");
        }
        catch (Exception e)
        {
            MessageBox.Show("Failed to start Ruffle/WebView2 (is the WebView2 Runtime installed?)\r\n" + e.Message, "Skua + Ruffle");
            Environment.Exit(0);
        }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg = e.TryGetWebMessageAsString();
        if (msg == "<ready/>") { await StartGameAsync(); return; }
        try
        {
            XElement el = XElement.Parse(msg);
            string function = el.Attribute("name")!.Value;
            object[] args = el.Elements().Select(x => FromFlashXml(x)).ToArray();
            FlashCall?.Invoke(function, args);
        }
        catch { /* ignore malformed */ }
    }

    private async Task StartGameAsync()
    {
        var proxies = new JArray();
        try
        {
            using var http = new HttpClient();
            var servers = JArray.Parse(await http.GetStringAsync("http://content.aq.com/game/api/data/servers"));
            foreach (var s in servers)   // field names (sIP / iPort) must be verified against the live response
            {
                string ip = (string)s["sIP"]!; int port = (int)s["iPort"]!;
                proxies.Add(new JObject
                {
                    ["host"] = ip, ["port"] = port,
                    ["proxyUrl"] = $"ws://127.0.0.1:{_proxy!.Port}/?h={ip}&p={port}"
                });
            }
        }
        catch { /* servers list unavailable: sockets will fail until fixed */ }

        var cfg = new JObject { ["socketProxy"] = proxies, ["parameters"] = new JObject() };
        await _view!.CoreWebView2.ExecuteScriptAsync($"window.__skuaStart({cfg.ToString(Formatting.None)})");
        _ready = true;
    }

    // ---------- host -> SWF ----------
    public string? Call(string function, params object[] args) => Call<string>(function, args);

    public T? Call<T>(string function, params object[] args)
    {
        try
        {
            object? o = Call(function, typeof(T), args);
            return o is not null ? (T)o : (T)DefaultProvider.GetDefault<T>(typeof(T));
        }
        catch { return (T)DefaultProvider.GetDefault<T>(typeof(T)); }
    }

    public object? Call(string function, Type type, params object[] args)
    {
        if (_lazyManager.Value.ShouldExit && Thread.CurrentThread.Name == "Script Thread")
            _lazyManager.Value.ScriptCts?.Token.ThrowIfCancellationRequested();
        if (!_ready || _view?.CoreWebView2 is null) return default;
        try
        {
            string argsJson = JsonConvert.SerializeObject(args.Select(a => a is ExpandoObject ? (object)new Dictionary<string, object>((IDictionary<string, object>)a) : a));
            string js = $"window.__skuaCall({JsonConvert.SerializeObject(function)}, {argsJson})";
            string? raw = RunOnUi(js);
            string result = JsonConvert.DeserializeObject<string>(raw ?? "null") ?? "<null/>";
            XElement el = XElement.Parse(result);
            return el.FirstNode is null ? default : Convert.ChangeType(el.FirstNode.ToString(), type);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _messenger.Send<FlashErrorMessage>(new(e, function, args));
            return default;
        }
    }

    private string? RunOnUi(string js)
    {
        Dispatcher d = Application.Current.Dispatcher;
        if (!d.CheckAccess())
            return d.Invoke(() => _view!.CoreWebView2.ExecuteScriptAsync(js)).GetAwaiter().GetResult();

        // Called from the UI thread: pump messages while waiting to avoid a deadlock.
        Task<string> t = _view!.CoreWebView2.ExecuteScriptAsync(js);
        var frame = new DispatcherFrame();
        t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        return t.GetAwaiter().GetResult();
    }

    public object FromFlashXml(XElement el)
    {
        switch (el.Name.ToString())
        {
            case "number": return int.TryParse(el.Value, out int i) ? i : float.TryParse(el.Value, out float f) ? f : 0;
            case "true": return true;
            case "false": return false;
            case "null": return null!;
            case "array": return el.Elements().Select(e => FromFlashXml(e)).ToArray();
            case "object":
                dynamic d = new ExpandoObject();
                foreach (var e in el.Elements()) ((IDictionary<string, object>)d)[e.Attribute("id")!.Value] = FromFlashXml(e.Elements().First());
                return d;
            default: return el.Value;
        }
    }

    public IFlashObject<T> CreateFlashObject<T>(string path) => new FlashObject<T>(Call<int>("lnkCreate", path), this);

    public void Dispose()
    {
        _ready = false;
        try { _proxy?.Dispose(); } catch { }
        try { _view?.Dispose(); } catch { }
    }
}

public sealed record RuffleViewChangedMessage(WebView2 View);
