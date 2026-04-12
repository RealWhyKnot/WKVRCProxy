using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.IPC;

public class PipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Logger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    public event Func<ResolvePayload, Task<string?>>? OnResolveRequested;

    public PipeServer(string pipeName, Logger logger)
    {
        _pipeName = pipeName;
        _logger = logger;
    }

    public void Start()
    {
        _logger.Info("Starting Named Pipe Server on: " + _pipeName);
        _serverTask = Task.Run(AcceptConnections);
    }

    private async Task AcceptConnections()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var stream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await stream.WaitForConnectionAsync(_cts.Token);
                
                _logger.Debug("Pipe client connected.");
                _ = Task.Run(() => HandleClientAsync(stream));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Error("PipeServer Accept Error: " + ex.Message);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream)
    {
        try
        {
            using (stream)
            using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
            using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.AutoFlush = true;
                
                string? json = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(json))
                {
                    _logger.Warning("Received empty payload from pipe client.");
                    return;
                }

                _logger.Debug("Received Payload: " + json);

                var payload = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ResolvePayload);
                if (payload == null || OnResolveRequested == null)
                {
                    await writer.WriteLineAsync("");
                    return;
                }

                string? result = await OnResolveRequested.Invoke(payload);
                if (string.IsNullOrEmpty(result))
                {
                    await writer.WriteLineAsync("");
                }
                else
                {
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(result));
                    await writer.WriteLineAsync(b64);
                    _logger.Debug("Sent Response: " + b64);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Pipe Client Handler Error: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _serverTask?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
