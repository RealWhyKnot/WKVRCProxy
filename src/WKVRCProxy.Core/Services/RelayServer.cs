using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class RelayServer : IProxyModule, IDisposable
{
    public string Name => "RelayServer";

    private Logger? _logger;
    private IModuleContext? _context;
    private HttpListener? _listener;
    private CancellationTokenSource _cts = new();
    private RelayPortManager? _portManager;

    public Task InitializeAsync(IModuleContext context)
    {
        _context = context;
        _logger = context.Logger;
        _logger.Trace("Initializing RelayServer...");

        try
        {
            _portManager = context.GetModule<RelayPortManager>();
            int port = _portManager.CurrentPort;

            if (port == 0)
            {
                _logger.Error("RelayServer failed: Port is 0. Is RelayPortManager registered?");
                return Task.CompletedTask;
            }

            _listener = new HttpListener();
            string prefix = "http://127.0.0.1:" + port + "/";
            _listener.Prefixes.Add(prefix);
            _listener.Start();

            _logger.Success("Relay listening on port " + port);

            _ = Task.Run(ListenLoop);
        }
        catch (Exception ex)
        {
            _logger.Error("RelayServer Init Error: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    private async Task ListenLoop()
    {
        if (_listener == null) return;

        while (_listener.IsListening && !_cts.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (HttpListenerException) { /* Ignored on closing */ }
            catch (Exception ex)
            {
                _logger?.Error("Relay Listen Loop Error: " + ex.Message);
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (!context.Request.Url!.AbsolutePath.StartsWith("/play", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            string? targetBase64 = context.Request.QueryString["target"];
            if (string.IsNullOrEmpty(targetBase64))
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            string targetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(targetBase64));

            _logger?.Trace("Relaying request for: " + targetUrl);

            // Dummy response for Phase 2 verification
            byte[] responseBytes = Encoding.UTF8.GetBytes("Relay Active. Target was: " + targetUrl);
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
        }
        catch (Exception ex)
        {
            _logger?.Error("Relay Request Handling Error: " + ex.Message);
            context.Response.StatusCode = 500;
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    public void Shutdown()
    {
        _cts.Cancel();
        if (_listener != null && _listener.IsListening)
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        Shutdown();
        _cts.Dispose();
    }
}
