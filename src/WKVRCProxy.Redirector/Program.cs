using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core;
using WKVRCProxy.Core.IPC;

namespace WKVRCProxy.Redirector;

class Program
{
    static async Task<int> Main(string[] args)
    {
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp-wrapper.log");
        
        try
        {
            // First look for local link file (portable mode)
            string portFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ipc_port.dat");
            
            // If not found, look in the AppData folder where Core might have placed it
            if (!File.Exists(portFile))
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy");
                portFile = Path.Combine(appData, "ipc_port.dat");
            }

            if (!File.Exists(portFile)) throw new Exception("Link file missing.");

            string portStr = File.ReadAllText(portFile).Trim();
            if (!int.TryParse(portStr, out int port)) throw new Exception("Link data corrupted.");

            string relayPortFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy", "relay_port.dat");
            int relayPort = 0;
            if (File.Exists(relayPortFile)) {
                try {
                    string rpStr = File.ReadAllText(relayPortFile).Trim();
                    int.TryParse(rpStr, out relayPort);
                } catch { } // Don't crash redirector if missing or locked
            }

            var payload = new ResolvePayload { Args = args };
            var envVars = Environment.GetEnvironmentVariables();
            foreach (System.Collections.DictionaryEntry de in envVars)
            {
                payload.Env[de.Key.ToString() ?? ""] = de.Value?.ToString() ?? "";
            }

            string json = JsonSerializer.Serialize(payload, CoreJsonContext.Default.ResolvePayload);

            using var ws = new ClientWebSocket();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            
            await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/"), cts.Token);

            var sendBytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(sendBytes), WebSocketMessageType.Text, true, cts.Token);

            // Chunked read response
            var buffer = new byte[1024 * 16];
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            string responseBase64 = Encoding.UTF8.GetString(ms.ToArray());
            if (!string.IsNullOrEmpty(responseBase64))
            {
                string finalUrl = Encoding.UTF8.GetString(Convert.FromBase64String(responseBase64));
                WriteToStdout(finalUrl);
                return 0;
            }
            
            throw new Exception("Core returned no data.");
        }
        catch (Exception ex)
        {
            try {
                File.AppendAllText(logPath, "[" + DateTime.Now.ToString("s") + "] Link Failure: " + ex.Message + "\n");
            } catch { }

            string? url = args.FirstOrDefault(a => a.StartsWith("http"));
            if (url != null) WriteToStdout(url);
            return 0;
        }
    }

    private static void WriteToStdout(string result)
    {
        string output = result.Trim() + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(output);
        using (Stream stdout = Console.OpenStandardOutput())
        {
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
    }
}
