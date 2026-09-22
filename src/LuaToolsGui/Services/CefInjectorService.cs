using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

public class CefInjectorService : IHostedService
{
    private readonly SteamService _steam;
    private readonly ILogger<CefInjectorService> _log;
    private CancellationTokenSource? _cts;
    private string _luatoolsJs = "";
    private string _polyfillJs = "";
    private string _directAddJs = "";
    private string _combinedScript = "";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Persistent CDP WebSocket per tab (keyed by tab id), reused across calls so the fast RPC-drain
    // loop doesn't pay a connect handshake every tick. Opened lazily, evicted (and reopened next call)
    // on any socket error. Touched only from the single InjectionLoop task (+ StopAsync on shutdown),
    // so no locking is needed. _cdpId hands out unique CDP command ids for response correlation.
    private readonly Dictionary<string, ClientWebSocket> _sockets = new();
    private int _cdpId;

    // CDP is opened by the `.cef-enable-remote-debugging` junction PluginInstallerService manages, not by
    // the loader DLL anymore, and Steam's own internal logic hardcodes this port. Confirmed this can't be
    // changed (file content has no effect on it), so unlike the old hook-based design (which deliberately
    // picked the uncommon 42067 to dodge collisions), this is stuck with 8080. A commonly-occupied port
    // (dev servers, Docker, etc.). PluginInstallerService.IsPort8080BusyAsync() surfaces a best-effort
    // warning for that case (probing /json to rule out Steam's own CDP server before warning. A bare bind
    // test can't tell "someone else has it" from "Steam has it and it's working correctly"); there is no
    // fallback port to switch to.
    private const string CefDebugUrl = "http://127.0.0.1:8080/json";
    private const string BaseUrl = "http://127.0.0.1:6767";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly CurrentSteamUserService _currentUser;
    private readonly SettingsService _settings;

    public CefInjectorService(
        SteamService steam,
        ILogger<CefInjectorService> logger,
        CurrentSteamUserService currentUser,
        SettingsService settings)
    {
        _steam = steam;
        _log = logger;
        _currentUser = currentUser;
        _settings = settings;
    }

    private bool IsAccountAllowed()
    {
        string allowed = _settings.AllowedSteamId;
        if (string.IsNullOrWhiteSpace(allowed)) return false;
        return _currentUser.IsCurrentUser(allowed);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await ReloadPluginFilesAsync();

        _ = Task.Run(() => InjectionLoop(_cts.Token), _cts.Token);
        _log.LogInformation("CEF injector started");
    }

    /// <summary>(Re)reads luatools.js + the polyfill from disk into memory. Called once at startup, and
    /// again by PluginInstallerService right after install/uninstall. The file-finding paths get checked
    /// exactly once otherwise, so a plugin installed/updated/removed after launch would silently never take
    /// effect until the app was restarted (the app is commonly launched by the loader BEFORE anything is
    /// installed, so this isn't just a theoretical edge case). _luatoolsJs/_polyfillJs are plain strings.
    /// Reassignment is atomic, so this is safe to call while InjectionLoop is concurrently reading them; the
    /// next poll cycle (~1s) just picks up the new content, no page reload needed.</summary>
    public async Task ReloadPluginFilesAsync()
    {
        if (!IsAccountAllowed())
        {
            _luatoolsJs = "";
            _polyfillJs = "";
            _directAddJs = "";
            _log.LogInformation("CEF injector disabled: active Steam account is not allowed");
            return;
        }

        var ct = _cts?.Token ?? CancellationToken.None;

        PluginInstallerService.ApplyBrandingToFrontend();

        var jsPath = FindLuaToolsJs();
        if (jsPath is not null && File.Exists(jsPath))
        {
            _luatoolsJs = await File.ReadAllTextAsync(jsPath, ct);
            _luatoolsJs = BrandTransformScript(_luatoolsJs);
            _log.LogInformation("Loaded luatools.js ({Length} bytes)", _luatoolsJs.Length);
        }
        else
        {
            _luatoolsJs = "";
            _log.LogWarning("luatools.js not found");
        }

        var polyfillPath = FindPolyfillJs();
        if (polyfillPath is not null && File.Exists(polyfillPath))
        {
            _polyfillJs = await File.ReadAllTextAsync(polyfillPath, ct);
        }
        else
        {
            _polyfillJs = BuildInlinePolyfill();
        }

        _directAddJs = BuildDirectAddJs();
        _combinedScript = _polyfillJs + "\n" + _luatoolsJs + "\n" + _directAddJs;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        foreach (var ws in _sockets.Values)
            try { ws.Dispose(); } catch { /* best effort on shutdown */ }
        _sockets.Clear();
        return Task.CompletedTask;
    }

    // Loop cadence: tick fast (drain queued RPCs every tick so the page's calls resolve in ~150ms rather
    // than the old ~1s), but only refresh the tab list + re-inject on every Nth tick (~1s). Injection
    // doesn't need to be fast, and the liveness/re-inject logic is unchanged, just decoupled from the drain.
    private const int TickMs = 150;
    private const int InjectEveryTicks = 7; // 7 * 150ms ≈ 1s

    private async Task InjectionLoop(CancellationToken ct)
    {
        // The store tabs to drain each fast tick (id + ws url), refreshed on the ~1s injection cadence.
        List<(string Id, string Ws)> storeTabs = new();
        int tick = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IsAccountAllowed())
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                if (!SteamService.IsSteamRunning())
                {
                    if (storeTabs.Count > 0)
                    {
                        foreach (var ws in _sockets.Values) try { ws.Dispose(); } catch { }
                        _sockets.Clear();
                        storeTabs.Clear();
                    }
                    await Task.Delay(2000, ct);
                    continue;
                }

                // ── Slow cadence (~1s): discover store tabs, ensure luatools.js is injected ──
                if (tick % InjectEveryTicks == 0)
                {
                    // Millennium may also be present and running its own plugin loader on this same page.
                    // That's fine now. The polyfill (BuildInlinePolyfill) only takes over "luatools" calls and
                    // passes everything else through to Millennium's real window.Millennium if one exists, so
                    // the two no longer need to be mutually exclusive.
                    var tabsJson = await _http.GetStringAsync(CefDebugUrl, ct);
                    if (!string.IsNullOrWhiteSpace(tabsJson))
                    {
                        var tabs = JsonSerializer.Deserialize<List<CefTabInfo>>(tabsJson, JsonOpts) ?? new();

                        // Inject luatools.js into every store-page tab whose JS context isn't already alive.
                        // Steam reuses the same CDP tab ID across SPA-style navigation (Home <-> store pages),
                        // but navigating to a different page wipes the JS context entirely, so "already
                        // injected by tab ID" is the wrong signal. Check liveness in the CURRENT context every
                        // cycle instead (window.__LuaToolsReady, set as the last statement of luatools.js's
                        // main IIFE) and re-inject whenever it's gone.
                        var script = string.IsNullOrEmpty(_combinedScript)
                            ? (_polyfillJs + "\n" + _luatoolsJs + "\n" + _directAddJs)
                            : _combinedScript;
                        var live = new List<(string, string)>();
                        var seen = new HashSet<string>();
                        foreach (var tab in tabs)
                        {
                            // Inject into ALL store pages, not just /app/ game pages: the store-wide header
                            // icon (and the "games added since last restart" popup, which only fires on the
                            // store home) need luatools.js present on the home page too. Steam boots straight
                            // to the home page, which has no "/app/" in its URL, so the old "/app/"-only filter
                            // meant the icon/popup never appeared there. luatools.js itself gates the per-game
                            // "Add via LuaTools" button to /app/ pages internally, so injecting store-wide is safe.
                            if (tab.Url?.Contains("store.steampowered.com", StringComparison.OrdinalIgnoreCase) == true
                                && !string.IsNullOrEmpty(tab.WebSocketDebuggerUrl)
                                && !string.IsNullOrEmpty(tab.Id))
                            {
                                seen.Add(tab.Id!);
                                // EvaluateReturnAsync's value.GetString() throws (silently, falling through to
                                // the raw response text) for a JSON boolean, so stringify explicitly rather
                                // than returning a bare boolean expression.
                                var alive = await EvaluateReturnAsync(tab.Id!, tab.WebSocketDebuggerUrl!, "String(window.__LuaToolsReady === true)", ct);
                                if (alive != "true")
                                    await EvaluateAsync(tab.Id!, tab.WebSocketDebuggerUrl!, script, ct);

                                live.Add((tab.Id!, tab.WebSocketDebuggerUrl!));
                            }
                        }

                        storeTabs = live;
                        // Drop persistent sockets for tabs that have gone away.
                        foreach (var stale in _sockets.Keys.Where(k => !seen.Contains(k)).ToList())
                            EvictSocket(stale);
                    }
                }

                // ── Fast cadence (every tick): drain pending CDP bridge requests ──
                foreach (var (id, ws) in storeTabs)
                    await ProcessSingleTab(id, ws, ct);

                tick++;
                int delay = storeTabs.Count > 0 ? TickMs : 500;
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogDebug("CEF cycle: {Message}", ex.Message);
                try { await Task.Delay(1000, ct); } catch { break; }
            }
        }
    }

    private async Task ProcessSingleTab(string tabId, string wsUrl, CancellationToken ct)
    {
        try
        {
            var pendingJson = await EvaluateReturnAsync(tabId, wsUrl,
                "JSON.stringify(Object.values(window.Millennium._pending||{}).map(function(r){return{m:r.method,a:JSON.stringify(r.args)}}))", ct);

            if (string.IsNullOrWhiteSpace(pendingJson) || pendingJson == "[]" || pendingJson == "null")
                return;

            var pending = JsonSerializer.Deserialize<List<PendingRequest>>(pendingJson, JsonOpts);
            if (pending is null or { Count: 0 }) return;

            var responses = new List<object>();
            foreach (var req in pending)
            {
                try
                {
                    var result = await CallBackendMethod(req.m ?? "", req.a ?? "{}");
                    responses.Add(new { v = JsonSerializer.Deserialize<object>(result) ?? result });
                }
                catch (Exception ex)
                {
                    responses.Add(new { e = ex.Message });
                }
            }

            var respArray = JsonSerializer.Serialize(responses);
            await EvaluateReturnAsync(tabId, wsUrl,
                "(function(r){var ks=Object.keys(window.Millennium._pending);for(var i=0;i<ks.length&&i<r.length;i++){var id=ks[i];window.Millennium._readyResponses[id]=r[i];delete window.Millennium._pending[id];}})(" + respArray + ");", ct);
        }
        catch { }
    }

    private async Task<string> CallBackendMethod(string method, string argsRaw)
    {
        var methodMap = new Dictionary<string, (string HttpMethod, string Path)>
        {
            ["HasLuaToolsForApp"] = ("GET", "/has/{appid}"),
            ["CheckApisForApp"] = ("POST", "/check-sources/{appid}"),
            ["StartAddViaLuaToolsFromUrl"] = ("POST", "/download/{appid}"),
            ["GetAddViaLuaToolsStatus"] = ("GET", "/download-status/{appid}"),
            ["CancelAddViaLuaTools"] = ("POST", "/cancel/{appid}"),
            ["DeleteLuaToolsForApp"] = ("POST", "/remove/{appid}"),
            // Store-page popup's self-contained add pipeline (PluginAddService-backed).
            // Raw fetch() to these from the page context is blocked as mixed content
            // (HTTPS store page -> HTTP localhost); route through this CDP bridge instead.
            ["StartLuaToolsAdd"] = ("POST", "/add/{appid}"),
            ["GetLuaToolsAddStatus"] = ("GET", "/add-status/{appid}"),
            ["PickLuaToolsAddSource"] = ("POST", "/add-source/{appid}"),
            // Menu actions (Settings, Fixes, Restart Steam). Same mixed-content problem, same fix.
            ["OpenSettings"] = ("POST", "/open/settings"),
            ["OpenFix"] = ("POST", "/open/fix/{appid}"),
            ["RestartSteam"] = ("POST", "/restart-steam"),
            // Open an external URL (Discord link, etc.), and check-for-updates. Also route
            // through the bridge (previously used a dead direct-fetch to a proxy port).
            ["OpenExternalUrl"] = ("POST", "/open-url"),
            ["CheckForUpdatesNow"] = ("POST", "/check-updates"),
            // "Games added since last Steam restart" popup: read the list, then dismiss it.
            ["ReadLoadedApps"] = ("GET", "/loaded-apps"),
            ["DismissLoadedApps"] = ("POST", "/loaded-apps"),
            ["GetApiList"] = ("GET", "/api-list"),
            ["GetIconDataUrl"] = ("GET", "/icon"),
            ["GetGamesDatabase"] = ("GET", "/games-database"),
            ["Logger"] = ("POST", "/log"),
        };

        if (!methodMap.TryGetValue(method, out var mapping))
            return """{"success":true}""";

        var path = mapping.Path;
        if (argsRaw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsRaw);
                var root = doc.RootElement;
                foreach (var prop in root.EnumerateObject())
                {
                    path = path.Replace($"{{{prop.Name}}}", Uri.EscapeDataString(prop.Value.ToString()));
                }
            }
            catch { }
        }

        var url = BaseUrl + path;
        if (mapping.HttpMethod == "GET")
        {
            return await _http.GetStringAsync(url);
        }
        else
        {
            var content = new StringContent(argsRaw ?? "{}", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content);
            return await resp.Content.ReadAsStringAsync();
        }
    }

    /// <summary>Return the reused (or freshly opened) CDP socket for a tab. Throws if the connect fails.
    /// The caller evicts on any exception.</summary>
    private async Task<ClientWebSocket> GetSocketAsync(string tabId, string wsUrl, CancellationToken ct)
    {
        if (_sockets.TryGetValue(tabId, out var existing))
        {
            if (existing.State == WebSocketState.Open) return existing;
            EvictSocket(tabId); // stale (closed/aborted). Drop and reopen below
        }

        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), ct);
        _sockets[tabId] = ws;
        return ws;
    }

    private void EvictSocket(string tabId)
    {
        if (_sockets.Remove(tabId, out var ws))
            try { ws.Dispose(); } catch { /* best effort */ }
    }

    /// <summary>Send a Runtime.evaluate over the tab's persistent socket and return the evaluated string
    /// value (or the raw response text if there's no plain value). Uses a unique CDP id per call and reads
    /// frames until that id comes back, skipping any interleaved event frames. On any socket error the
    /// connection is evicted so the next call transparently reopens a fresh one (same net behavior as the
    /// old open-per-call code, minus the handshake cost on the happy path).</summary>
    private async Task<string> EvaluateReturnAsync(string tabId, string wsUrl, string expression, CancellationToken ct)
    {
        try
        {
            var ws = await GetSocketAsync(tabId, wsUrl, ct);
            int id = ++_cdpId;
            var cmd = new { id, method = "Runtime.evaluate", @params = new { expression, returnByValue = true } };
            var json = JsonSerializer.Serialize(cmd);
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, ct);

            var buffer = new byte[65536];
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // Each CDP message is one WS message (possibly multi-frame). Read whole messages until we get
            // the one carrying our id. We never enable any CDP domain, so in practice the only traffic is
            // our own responses. The id check is just defensive against any stray event frames.
            while (!linked.Token.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
                    if (result.MessageType == WebSocketMessageType.Close) { EvictSocket(tabId); return ""; }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var responseText = sb.ToString();
                JsonElement root;
                try { using var doc = JsonDocument.Parse(responseText); root = doc.RootElement.Clone(); }
                catch { continue; } // unparseable frame. Keep reading

                if (!(root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var rid) && rid == id))
                    continue; // an event or a different id, not our response yet

                if (root.TryGetProperty("result", out var cdpResult) &&
                    cdpResult.TryGetProperty("result", out var evalResult) &&
                    evalResult.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    // Only a string value maps cleanly; GetString() throws on e.g. a JSON boolean, so
                    // callers that want a bool stringify it in JS (String(...)). Anything non-string
                    // falls through to the raw response text (same as the old code) rather than throwing.
                    return value.GetString() ?? responseText;
                }
                return responseText; // our response, but no plain string value (e.g. an injection eval)
            }
            return "";
        }
        catch (Exception ex)
        {
            _log.LogDebug("EvaluateReturnAsync: CDP call failed: {Message}", ex.Message);
            EvictSocket(tabId);
            return "";
        }
    }

    /// <summary>Fire-and-forget evaluate (used for injecting the polyfill + luatools.js). Shares the same
    /// persistent socket + id-correlated read path; the return value is ignored.</summary>
    private async Task EvaluateAsync(string tabId, string wsUrl, string script, CancellationToken ct)
    {
        await EvaluateReturnAsync(tabId, wsUrl, script, ct);
    }

    private string? FindLuaToolsJs()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // The frontend is no longer bundled with the app. PluginInstallerService downloads it from
        // GitHub releases into %AppData%\LuaToolsGui\plugin. If it isn't installed yet, nothing injects.
        string[] candidates =
        {
            Path.Combine(appData, "LuaToolsGui", "plugin", "public", "luatools.js"),
            Path.Combine(appData, "LuaToolsGui", "plugin", "luatools.js"),
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        return null;
    }

    private string? FindPolyfillJs()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "public", "millennium-polyfill.js"),
            Path.Combine(AppContext.BaseDirectory, "millennium-polyfill.js"),
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        return null;
    }

    /// <summary>Real Millennium (if present on this page) already defines window.Millennium.callServerMethod
    /// as one shared object used by every Millennium plugin, routed server-side by the pluginName argument.
    /// It is NOT per-plugin namespaced. Blindly overwriting it (the old behavior) broke every other
    /// Millennium plugin on the page whenever both injectors were active, not just LuaTools. This only takes
    /// over "luatools" calls and passes every other pluginName through to the real callServerMethod
    /// unchanged, so this app's CDP injection can coexist with Millennium instead of requiring it be absent.
    /// _pending/_readyResponses are attached directly onto whichever object ends up as window.Millennium
    /// (fake or real) under their original names. ProcessSingleTab's polling reads/writes those regardless
    /// of which case this is.</summary>
    private string BuildInlinePolyfill()
    {
        return @"
(function(){
var real=window.Millennium;
var pending={},ready={},reqId=0;
function ltCall(p,m,a){
  var i='_ltr_'+(++reqId);
  pending[i]={method:m,args:a,ts:Date.now()};
  return new Promise(function(rv,rj){
    var mx=100;
    function ck(){
      var r=ready[i];
      if(r!==undefined){delete ready[i];if(r.e){rj(new Error(r.e))}else{rv(r.v)}return}
      if(--mx>0){setTimeout(ck,50)}else{delete pending[i];rv({success:true})}
    }
    ck();
  });
}
if(real&&typeof real.callServerMethod==='function'){
  var realCall=real.callServerMethod.bind(real);
  real.callServerMethod=function(p,m,a){return (p==='luatools'||p==='sweettools'||p==='steam tools')?ltCall(p,m,a):realCall(p,m,a)};
  real._pending=pending;
  real._readyResponses=ready;
  window.Millennium=real;
}else{
  window.Millennium={_pending:pending,_readyResponses:ready,callServerMethod:function(p,m,a){return ltCall(p,m,a)}};
}
})();
";
    }

    /// <summary>
    /// Generates the direct add injection script. Injects a native Steam-styled purchase block
    /// directly on top of the "Buy <Game>" area (#game_area_purchase) on Steam game store pages,
    /// and an inline "+ Add via LuaTools" button next to "Add to Cart".
    /// </summary>
    private string BuildDirectAddJs()
    {
        return @"
(function () {
  if (window.__LuaToolsDirectAddInjected) return;
  window.__LuaToolsDirectAddInjected = true;

  // Hide any legacy/sidebar 'Add via LuaTools' buttons so only 'Add to Library' appears
  if (!document.getElementById('luatools-clean-styles')) {
    var cleanStyle = document.createElement('style');
    cleanStyle.id = 'luatools-clean-styles';
    cleanStyle.textContent = '.apphub_OtherSiteInfo .luatools-button, .steamdb-buttons .luatools-button, [data-steamdb-buttons] .luatools-button { display: none !important; }';
    document.head.appendChild(cleanStyle);
  }

  var lastCheckTime = 0;
  function initDirectAdd() {
    var now = Date.now();
    if (now - lastCheckTime < 250) return;
    lastCheckTime = now;

    var url = window.location.href;
    var match = url.match(/https:\/\/store\.steampowered\.com\/app\/(\d+)/i);
    if (!match) return;

    var appid = parseInt(match[1], 10);
    if (isNaN(appid)) return;

    var purchaseArea = document.querySelector('#game_area_purchase') || document.querySelector('.game_area_purchase');
    if (!purchaseArea) return;

    var existing = document.getElementById('luatools-direct-purchase-block');
    if (existing && existing.getAttribute('data-appid') === String(appid)) {
      return;
    }
    if (existing) {
      existing.remove();
    }

    var gameName = '';
    var nameEl = document.querySelector('.apphub_AppName, #appHubAppName');
    if (nameEl && nameEl.textContent) {
      gameName = nameEl.textContent.trim();
    }
    if (!gameName) {
      gameName = (document.title || '').replace(/\s+on Steam\s*$/i, '').trim();
    }
    if (!gameName) gameName = 'this game';

    var wrapper = document.createElement('div');
    wrapper.id = 'luatools-direct-purchase-block';
    wrapper.className = 'game_area_purchase_game_wrapper luatools-direct-wrapper';
    wrapper.setAttribute('data-appid', String(appid));
    wrapper.style.marginBottom = '16px';

    var block = document.createElement('div');
    block.className = 'game_area_purchase_game';
    block.style.position = 'relative';
    block.style.minHeight = '48px';
    block.style.padding = '16px 200px 16px 16px';
    block.style.background = 'linear-gradient(135deg, rgba(24, 40, 56, 0.95) 0%, rgba(16, 26, 38, 0.95) 100%)';
    block.style.border = '1px solid rgba(102, 192, 244, 0.35)';
    block.style.borderRadius = '4px';
    block.style.boxShadow = '0 4px 16px rgba(0, 0, 0, 0.5)';

    var platform = document.createElement('div');
    platform.className = 'game_area_purchase_platform';
    platform.style.marginBottom = '4px';
    platform.innerHTML = '<span class=""platform_img win""></span>';
    block.appendChild(platform);

    var h1 = document.createElement('h1');
    h1.style.display = 'flex';
    h1.style.alignItems = 'center';
    h1.style.gap = '8px';
    h1.style.fontSize = '21px';
    h1.style.color = '#ffffff';
    h1.style.fontWeight = 'normal';
    h1.style.margin = '0';
    h1.style.lineHeight = '28px';

    var titleText = document.createElement('span');
    titleText.textContent = 'Add ' + gameName + ' via SweetTools';
    h1.appendChild(titleText);
    block.appendChild(h1);

    var action = document.createElement('div');
    action.className = 'game_purchase_action';
    action.style.position = 'absolute';
    action.style.right = '16px';
    action.style.top = '50%';
    action.style.transform = 'translateY(-50%)';
    action.style.zIndex = '5';
    action.style.margin = '0';

    var actionBg = document.createElement('div');
    actionBg.className = 'game_purchase_action_bg';
    actionBg.style.background = '#000000';
    actionBg.style.borderRadius = '2px';
    actionBg.style.padding = '0';
    actionBg.style.display = 'flex';
    actionBg.style.alignItems = 'center';

    var btnContainer = document.createElement('div');
    btnContainer.className = 'btn_addtocart';
    btnContainer.style.margin = '0';

    var addBtn = document.createElement('a');
    addBtn.href = '#';
    addBtn.className = 'btn_green_steamui btn_medium luatools-button luatools-direct-add-btn Focusable';
    addBtn.title = 'Add via SweetTools';
    addBtn.style.padding = '0 18px';
    addBtn.style.lineHeight = '32px';
    addBtn.style.height = '32px';
    addBtn.style.fontSize = '15px';
    addBtn.style.cursor = 'pointer';
    addBtn.style.display = 'inline-block';
    addBtn.style.textDecoration = 'none';

    var btnSpan = document.createElement('span');
    btnSpan.textContent = 'Add via SweetTools';
    addBtn.appendChild(btnSpan);
    btnContainer.appendChild(addBtn);
    actionBg.appendChild(btnContainer);
    action.appendChild(actionBg);
    block.appendChild(action);

    wrapper.appendChild(block);

    // Insert right at the top of the purchase area (on top of Buy <Game>)
    purchaseArea.prepend(wrapper);

    function updateInstalledState() {
      btnSpan.textContent = 'In Library';
      addBtn.className = 'btn_blue_steamui btn_medium Focusable';
      addBtn.title = 'In Library (Click to Restart Steam)';
      addBtn.onclick = function (e) {
        e.preventDefault();
        if (window.Millennium && typeof window.Millennium.callServerMethod === 'function') {
          window.Millennium.callServerMethod('luatools', 'RestartSteam', {});
        }
      };
    }

    addBtn.addEventListener('click', function (e) {
      e.preventDefault();
      if (typeof window.startLuaToolsAdd === 'function') {
        window.startLuaToolsAdd(appid, addBtn);
      } else if (window.Millennium && typeof window.Millennium.callServerMethod === 'function') {
        btnSpan.textContent = 'Adding...';
        window.Millennium.callServerMethod('luatools', 'StartLuaToolsAdd', {
          appid: appid,
          name: gameName
        }).catch(function () {});
      }
      var pollCount = 0;
      var pollInt = setInterval(function () {
        pollCount++;
        if (window.Millennium && typeof window.Millennium.callServerMethod === 'function') {
          window.Millennium.callServerMethod('luatools', 'HasLuaToolsForApp', { appid: appid })
            .then(function (res) {
              var p = typeof res === 'string' ? JSON.parse(res) : res;
              if (p && p.success && p.exists === true) {
                clearInterval(pollInt);
                updateInstalledState();
              }
            }).catch(function () {});
        }
        if (pollCount > 60) clearInterval(pollInt);
      }, 1000);
    });

    // Check if already in library
    if (window.Millennium && typeof window.Millennium.callServerMethod === 'function') {
      window.Millennium.callServerMethod('luatools', 'HasLuaToolsForApp', { appid: appid })
        .then(function (res) {
          var payload = typeof res === 'string' ? JSON.parse(res) : res;
          if (payload && payload.success && payload.exists === true) {
            updateInstalledState();
          }
        }).catch(function () {});
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initDirectAdd);
  } else {
    initDirectAdd();
  }

  try {
    var observer = new MutationObserver(function () {
      initDirectAdd();
    });
    observer.observe(document.body, { childList: true, subtree: true });
  } catch (_) {}

  var curUrl = window.location.href;
  setInterval(function () {
    if (window.location.href !== curUrl) {
      curUrl = window.location.href;
      initDirectAdd();
    }
  }, 1000);
})();
";
    }

    /// <summary>
    /// Transforms the injected store-page script to use SweetTools branding, the new icon,
    /// and replaces "LuaTools" references with "Steam Tools" and "Add via SweetTools".
    /// </summary>
    public static string BrandTransformScript(string js)
    {
        if (string.IsNullOrEmpty(js)) return js;

        // 1. Direct SweetTools PNG icon injection:
        // Replace relative icon paths ("LuaTools/luatools-icon.png") with embedded SweetTools PNG data URL
        // so images load synchronously without making a 404-failing HTTP request in Steam CEF.
        js = js.Replace("\"LuaTools/luatools-icon.png\"", "\"" + HttpServerService.SweetToolsIconPngDataUrl + "\"");
        js = js.Replace("'LuaTools/luatools-icon.png'", "\"" + HttpServerService.SweetToolsIconPngDataUrl + "\"");

        // Neutralize cogwheel/star fallback that replaced headerBtn.innerHTML on network error
        js = Regex.Replace(js, @"img\.onerror\s*=\s*function\s*\(\)\s*\{[\s\S]*?headerBtn\.innerHTML\s*=[\s\S]*?<\/svg>';?\s*\};?", "img.onerror = null;");
        js = Regex.Replace(js, @"titleIcon\.onerror\s*=\s*function\s*\(\)\s*\{[\s\S]*?\};?", "titleIcon.onerror = null;");

        // Replace old purple icon dataUrl in GetIconDataUrl shim with new SweetTools PNG dataUrl
        js = Regex.Replace(js, @"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0[A-Za-z0-9+/=]+", HttpServerService.SweetToolsIconPngDataUrl);

        // 2. Replace button and action labels (unescaped and JSON-escaped quotes)
        js = Regex.Replace(js, @"(?<=\\?[""'])Add via LuaTools(?=\\?[""'])", "Add via SweetTools");
        js = Regex.Replace(js, @"(?<=\\?[""'])Remove via LuaTools(?=\\?[""'])", "Remove via SweetTools");
        js = Regex.Replace(js, @"(?<=\\?[""'])Games via LuaTools(?=\\?[""'])", "Games via SweetTools");

        // 3. Replace translation titles in JSON dictionaries (covers all locales with escaped quotes)
        js = Regex.Replace(js, @"(?i)(menu\.title\\?"":\\?"")(?:LuaTools|LooaToolz)\s*[\u00B7\u2022•·A\s-]*", "$1Steam Tools • ");
        js = Regex.Replace(js, @"(?i)(settings\.title\\?"":\\?"")(?:LuaTools|LooaToolz)\s*[\u00B7\u2022•·A\s-]*", "$1Steam Tools • ");
        js = Regex.Replace(js, @"(?i)(menu\.removeLuaTools\\?"":\\?"")(?:Remove via LuaTools|Remove LuaTools)", "$1Remove via SweetTools");
        js = Regex.Replace(js, @"(?i)(\\?""Installed LuaTools\\?"")", "\\\"Installed Steam Tools\\\"");
        js = Regex.Replace(js, @"common\.appName\\?"":\\?""LuaTools\\?""", "common.appName\\\":\\\"Sweet Tools\\\"");

        // 4. Replace standalone and title strings in JS code
        js = js.Replace("LuaTools •", "Steam Tools •");
        js = js.Replace("LuaTools \\u2022", "Steam Tools \\u2022");
        js = js.Replace("LuaTools ·", "Steam Tools •");
        js = js.Replace("LuaTools \u00B7", "Steam Tools •");
        js = js.Replace("LuaTools \u2022", "Steam Tools •");
        js = js.Replace("LuaTools A", "Steam Tools •");
        js = js.Replace("LuaTools Menu", "Steam Tools Menu");
        js = js.Replace("LuaTools Settings", "Steam Tools Settings");

        js = Regex.Replace(js, @"t\(\s*""menu\.title""\s*,\s*""[^""]+""\s*\)", "t(\"menu.title\", \"Steam Tools • Menu\")");
        js = Regex.Replace(js, @"t\(\s*""settings\.title""\s*,\s*""[^""]+""\s*\)", "t(\"settings.title\", \"Steam Tools Settings\")");
        js = Regex.Replace(js, @"LuaTools\s*[\u00B7\u2022\u00A0\uFFFD•·A\s-]+\s*Menu\b", "Steam Tools • Menu");
        js = Regex.Replace(js, @"LuaTools\s*[\u00B7\u2022\u00A0\uFFFD•·A\s-]+\s*Settings\b", "Steam Tools Settings");
        js = Regex.Replace(js, @"LuaTools\s*[\u00B7\u2022\u00A0\uFFFD•·A\s-]+\s*Fixes Menu\b", "Steam Tools • Fixes Menu");
        js = Regex.Replace(js, @"LuaTools\s*[\u00B7\u2022\u00A0\uFFFD•·A\s-]+\s*AIO Fixes Menu\b", "Steam Tools • AIO Fixes Menu");
        js = Regex.Replace(js, @"LuaTools\s*[\u00B7\u2022\u00A0\uFFFD•·A\s-]+\s*Added Games\b", "Steam Tools • Added Games");

        // 5. Replace alert and confirm dialog titles
        js = js.Replace("ShowLuaToolsAlert(\"LuaTools\",", "ShowLuaToolsAlert(\"Sweet Tools\",");
        js = js.Replace("showLuaToolsConfirm(\n      \"LuaTools\",", "showLuaToolsConfirm(\n      \"Sweet Tools\",");
        js = js.Replace("showLuaToolsConfirm(\"LuaTools\",", "showLuaToolsConfirm(\"Sweet Tools\",");
        js = js.Replace("ShowLuaToolsAlert('LuaTools',", "ShowLuaToolsAlert('Sweet Tools',");
        js = js.Replace("showLuaToolsConfirm('LuaTools',", "showLuaToolsConfirm('Sweet Tools',");

        // 6. Tooltips, attributes, and labels
        js = js.Replace("aria-label=\"LuaTools\"", "aria-label=\"Steam Tools\"");
        js = js.Replace("alt=\"LuaTools\"", "alt=\"Steam Tools\"");
        js = js.Replace("alt = \"LuaTools\"", "alt = \"Steam Tools\"");
        js = js.Replace("\"LuaTools Settings\"", "\"Steam Tools Settings\"");
        js = js.Replace("'LuaTools Settings'", "'Steam Tools Settings'");

        return js;
    }
}

internal class CefTabInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string? Title { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string? Url { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("webSocketDebuggerUrl")]
    public string? WebSocketDebuggerUrl { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal class PendingRequest
{
    public string? m { get; set; }
    public string? a { get; set; }
}
