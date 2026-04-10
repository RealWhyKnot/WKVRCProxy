using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
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

    public event Func<ResolvePayload, Task<string?>>? OnResolveRequested;

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _logger.Trace("Initializing WebSocketIpcServer...");
        // Non-blocking start
        Task.Run(() => Start(AppDomain.CurrentDomain.BaseDirectory));
        return Task.CompletedTask;
    }

    private void Start(string baseDir)
    {
        try 
        {
            _logger?.Trace("Starting IPC server thread...");
            Port = FindFreePort(22361, 22370);
            
            _logger?.Trace("Selected Port: " + Port);
            _logger?.Info("Starting WebSocket IPC Server on port: " + Port);

            _logger?.Trace("Creating HttpListener...");
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            
            _logger?.Trace("Starting HttpListener...");
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
            var targets = new List<string> { Path.Combine(baseDir, "ipc_port.dat") };
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy");
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
            targets.Add(Path.Combine(appData, "ipc_port.dat"));

            foreach (var path in targets)
            {
                File.WriteAllText(path, Port.ToString());
                _logger?.Debug("IPC port exported: " + path);
            }
        } catch (Exception ex) {
            _logger?.Warning("Failed to export IPC port file — Redirector may not connect: " + ex.Message, ex);
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
        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            webSocket = wsContext.WebSocket;
            _logger?.Trace("Redirector linked via WebSocket.");

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
                _logger?.Trace("Payload deserialized, invoking resolution handler.");
                string? resolved = await OnResolveRequested.Invoke(payload);
                string response = string.IsNullOrEmpty(resolved) ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(resolved));

                var responseBytes = Encoding.UTF8.GetBytes(response);
                await webSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, _cts.Token);
            }
            else
            {
                _logger?.Warning("WebSocket request had null payload or no resolution handler registered — sending empty response.");
                var emptyBytes = Encoding.UTF8.GetBytes("");
                await webSocket.SendAsync(new ArraySegment<byte>(emptyBytes), WebSocketMessageType.Text, true, _cts.Token);
            }

            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", closeCts.Token); }
                catch (Exception ex) { _logger?.Trace("WebSocket close handshake failed (expected on abrupt disconnect): " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            if (!(ex is WebSocketException || ex is OperationCanceledException))
                _logger?.Error("WebSocket Session Error: " + ex.Message, ex);
        }
        finally
        {
            webSocket?.Dispose();
        }
    }

    private int FindFreePort(int start, int end)
    {
        _logger?.Trace("Searching for free port in range " + start + "-" + end + "...");
        for (int port = start; port <= end; port++)
        {
            try {
                using var client = new System.Net.Sockets.TcpClient();
                // Simple check: can we bind to it?
                var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                _logger?.Trace("Port " + port + " is available for binding.");
                return port;
            } catch { 
                _logger?.Trace("Port " + port + " is busy or restricted.");
            }
        }
        return start;
    }

    public void Shutdown()
    {
        _cts.Cancel();
        try {
            _listener?.Stop();
            _listener?.Close();
        } catch { }

        // Cleanup port files
        try {
            string localPort = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ipc_port.dat");
            if (File.Exists(localPort)) File.Delete(localPort);
            
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy", "ipc_port.dat");
            if (File.Exists(appData)) File.Delete(appData);
        } catch { }
    }

    public void Dispose()
    {
        Shutdown();
        _cts.Dispose();
    }
}
