using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.IPC;

public class WebSocketIpcServer : IProxyModule, IDisposable
{
    public string Name => "IPCServer";
    private Logger? _logger;
    private HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    public int Port { get; private set; }

    // Directories that were requested before the port was bound; flushed in Start().
    private readonly List<string> _pendingExportDirs = new();
    // All ipc_port.dat paths written by this instance, for cleanup on shutdown.
    private readonly List<string> _exportedPaths = new();
    private readonly object _exportLock = new();

    public event Func<ResolvePayload, Task<string?>>? OnResolveRequested;

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        // Non-blocking start
        Task.Run(() => Start(AppDomain.CurrentDomain.BaseDirectory));
        return Task.CompletedTask;
    }

    private void Start(string baseDir)
    {
        try 
        {
            Port = FindFreePort(22361, 22370);

            _logger?.Info("Starting WebSocket IPC Server on port: " + Port);

            _listener = new HttpListener();
            _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");

            _listener.Start();

            _listenTask = Task.Run(ListenLoop);
            _logger?.Success("IPC Server listening.");
        }
        catch (Exception ex)
        {
            _logger?.Fatal("IPC Server Start Failed: " + ex.Message + "\n" + ex.StackTrace);
            return;
        }
        
        try {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy");
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);

            WritePortFile(baseDir);
            WritePortFile(appData);
        } catch (Exception ex) {
            _logger?.Warning("Failed to export IPC port file — Redirector may not connect: " + ex.Message, ex);
        }

        // Flush directories that were queued before the port was bound
        List<string> pending;
        lock (_exportLock)
        {
            pending = new List<string>(_pendingExportDirs);
            _pendingExportDirs.Clear();
        }
        foreach (var dir in pending) WritePortFile(dir);
    }

    /// <summary>
    /// Exports ipc_port.dat to an additional directory (e.g. VRChat Tools folder).
    /// Safe to call before the port is bound — the write will be deferred and flushed
    /// automatically once the server starts.
    /// </summary>
    public void ExportPortToDirectory(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _logger?.Warning($"IPC port export requested for non-existent directory: {dir}");
            return;
        }

        lock (_exportLock)
        {
            if (Port == 0)
            {
                if (!_pendingExportDirs.Contains(dir))
                    _pendingExportDirs.Add(dir);
                return;
            }
        }

        WritePortFile(dir);
    }

    private void WritePortFile(string dir)
    {
        try
        {
            string path = Path.Combine(dir, "ipc_port.dat");
            File.WriteAllText(path, Port.ToString());
            lock (_exportLock) { if (!_exportedPaths.Contains(path)) _exportedPaths.Add(path); }
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Failed to write ipc_port.dat to {dir}: {ex.Message}");
        }
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                if (context.Request.IsWebSocketRequest)
                {
                    _ = Task.Run(() => HandleConnectionAsync(context));
                }
                else
                {
                    _logger?.Debug("Rejected non-WebSocket request from: " + context.Request.RemoteEndPoint);
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
            catch (Exception ex) when (!(ex is ObjectDisposedException || ex is HttpListenerException))
            {
                _logger?.Error("WebSocket Server Error: " + ex.Message);
            }
        }
    }

    private async Task HandleConnectionAsync(HttpListenerContext context)
    {
        WebSocket? webSocket = null;
        string sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            webSocket = wsContext.WebSocket;
            _logger?.Debug("[" + sessionId + "] Redirector linked via WebSocket.");

            var buffer = new byte[1024 * 16]; 
            var ms = new MemoryStream();
            
            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            ms.Seek(0, SeekOrigin.Begin);
            string json;
            using (var reader = new StreamReader(ms, Encoding.UTF8))
            {
                json = await reader.ReadToEndAsync();
            }

            var payload = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ResolvePayload);

            if (payload != null && OnResolveRequested != null)
            {
                string? resolved = await OnResolveRequested.Invoke(payload);
                string response = string.IsNullOrEmpty(resolved) ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(resolved));

                var responseBytes = Encoding.UTF8.GetBytes(response);
                await webSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, _cts.Token);
            }
            else
            {
                _logger?.Warning("[" + sessionId + "] WebSocket request had null payload or no resolution handler registered — sending empty response.");
                var emptyBytes = Encoding.UTF8.GetBytes("");
                await webSocket.SendAsync(new ArraySegment<byte>(emptyBytes), WebSocketMessageType.Text, true, _cts.Token);
            }

            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", closeCts.Token); }
                catch { /* Close handshake failure is expected on abrupt disconnect */ }
            }
        }
        catch (Exception ex)
        {
            if (!(ex is WebSocketException || ex is OperationCanceledException))
                _logger?.Error("[" + sessionId + "] WebSocket Session Error: " + ex.Message, ex);
        }
        finally
        {
            webSocket?.Dispose();
        }
    }

    private int FindFreePort(int start, int end)
    {
        for (int port = start; port <= end; port++)
        {
            try {
                using var client = new System.Net.Sockets.TcpClient();
                // Simple check: can we bind to it?
                var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            } catch {
            }
        }
        return start;
    }

    public ModuleHealthReport GetHealthReport()
    {
        return new ModuleHealthReport
        {
            ModuleName = Name,
            Status = (_listener != null && _listener.IsListening && Port > 0)
                ? HealthStatus.Healthy
                : HealthStatus.Failed,
            Reason = (_listener == null || Port == 0)
                ? "IPC server failed to bind to any port in range 22361-22370"
                : "",
            LastChecked = DateTime.Now
        };
    }

    public void Shutdown()
    {
        _cts.Cancel();
        try {
            _listener?.Stop();
            _listener?.Close();
        } catch { }

        // Cleanup exported port files from the app directory and VRChat Tools.
        // Keep the %LOCALAPPDATA% copy so the Redirector can detect "proxy not running"
        // vs "port file missing" on the next session — a stale port fails fast with
        // connection refused, while a missing port file gives no diagnostic info.
        string appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WKVRCProxy", "ipc_port.dat");
        List<string> toDelete;
        lock (_exportLock) { toDelete = new List<string>(_exportedPaths); }
        foreach (var path in toDelete)
        {
            if (path.Equals(appDataPath, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    public void Dispose()
    {
        Shutdown();
        _cts.Dispose();
    }
}
