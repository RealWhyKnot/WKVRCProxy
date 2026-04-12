using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.IPC;

public class HttpIpcServer : IDisposable
{
    private readonly Logger _logger;
    private HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    public int Port { get; private set; }

    public event Func<ResolvePayload, Task<string?>>? OnResolveRequested;

    public HttpIpcServer(Logger logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        Port = FindFreePort(22361, 22370);
        _logger.Info("Starting HTTP IPC Server on port: " + Port);

        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
        _listener.Start();

        _listenTask = Task.Run(ListenLoop);
        
        // Save port to a known file for the Redirector
        try {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy");
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
            string portFile = Path.Combine(appData, "ipc_port.dat");
            File.WriteAllText(portFile, Port.ToString());
            _logger.Debug("IPC port exported to: " + portFile);
        } catch (Exception ex) {
            _logger.Error("Failed to export IPC port: " + ex.Message);
        }
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (Exception ex) when (!(ex is ObjectDisposedException || ex is HttpListenerException))
            {
                _logger.Error("HttpIpcServer Loop Error: " + ex.Message);
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        using var response = context.Response;
        try
        {
            if (context.Request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            string json = await reader.ReadToEndAsync();
            
            _logger.Debug("IPC Received: " + json);

            var payload = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ResolvePayload);
            if (payload == null || OnResolveRequested == null)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            string? result = await OnResolveRequested.Invoke(payload);
            
            byte[] buffer;
            if (string.IsNullOrEmpty(result))
            {
                buffer = Array.Empty<byte>();
            }
            else
            {
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(result));
                buffer = Encoding.UTF8.GetBytes(b64);
            }

            response.ContentType = "text/plain";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            _logger.Debug("IPC Responded with " + buffer.Length + " bytes.");
        }
        catch (Exception ex)
        {
            _logger.Error("IPC Request Handler Error: " + ex.Message);
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
    }

    private int FindFreePort(int start, int end)
    {
        for (int port = start; port <= end; port++)
        {
            if (IsPortAvailable(port)) return port;
        }
        return start; // Fallback
    }

    private bool IsPortAvailable(int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            return !success;
        }
        catch { return true; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _cts.Dispose();
    }
}
