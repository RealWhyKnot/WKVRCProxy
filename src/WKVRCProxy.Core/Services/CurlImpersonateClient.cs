using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class CurlImpersonateClient : IProxyModule
{
    public string Name => "CurlImpersonateClient";
    private Logger? _logger;
    private string _executablePath = "";

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;
        _executablePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "curl-impersonate-win.exe");
        
        if (!File.Exists(_executablePath))
        {
            _logger.Warning("curl-impersonate-win.exe not found at: " + _executablePath);
        }

        return Task.CompletedTask;
    }

    public Task<Stream> SendRequestAsync(string method, string url, Dictionary<string, string> headers)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("--impersonate");
        psi.ArgumentList.Add("chrome116");
        psi.ArgumentList.Add("-s"); // silent
        psi.ArgumentList.Add("-i"); // include headers in output
        psi.ArgumentList.Add("-X");
        psi.ArgumentList.Add(method);

        foreach (var header in headers)
        {
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add(header.Key + ": " + header.Value);
        }

        psi.ArgumentList.Add(url);

        var process = Process.Start(psi);
        if (process == null) throw new Exception("Failed to start curl-impersonate-win process.");

        // Pipe stderr to logger asynchronously
        _ = Task.Run(async () => {
            using var reader = process.StandardError;
            while (!reader.EndOfStream)
            {
                string? line = await reader.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                {
                    _logger?.Trace("[CURL-WARN] " + line);
                }
            }
        });

        // We return the raw output stream
        return Task.FromResult(process.StandardOutput.BaseStream);
    }

    public void Shutdown() { }
}
