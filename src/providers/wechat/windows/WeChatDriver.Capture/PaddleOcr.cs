using System.Diagnostics;
using WeChatDriver.Windows;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace WeChatDriver.Capture;

/// <summary>
/// RapidOCR（PaddleOCR onnxruntime）本地服务客户端。
/// 启动 Python 子进程跑 tools/ocr_server.py（常驻 HTTP），模型只加载一次。
/// </summary>
public sealed class PaddleOcr : IOcrEngine, IDisposable
{
    private readonly Process? _proc;
    private readonly HttpClient _http;
    private readonly int _port;

    public bool IsAvailable { get; }
    public string Language => "zh-Hans (RapidOCR/PaddleOCR)";

    public PaddleOcr(string scriptPath, string? python = null, int port = 0)
    {
        _port = port > 0 ? port : FindFreePort();
        var pythonExe = python ?? PaddleOcrLocator.FindPython() ?? "python";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{scriptPath}\" {_port}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _proc = Process.Start(psi);
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            IsAvailable = WaitReady();
        }
        catch
        {
            IsAvailable = false;
            _http = new HttpClient();
        }
    }

    private bool WaitReady()
    {
        // 首次启动需要加载 onnx 模型，最多等 90s。
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _ = _http.GetAsync($"http://127.0.0.1:{_port}/").GetAwaiter().GetResult();
                // 能拿到 HTTP 响应（任何状态码）即说明服务已监听。
                return true;
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
        return false;
    }

    public IReadOnlyList<OcrLine> RecognizePng(byte[] pngBytes)
    {
        var raw = RecognizeRaw(pngBytes);
        return raw.Select(r => new OcrLine(r.Text, r.Score)).ToList();
    }

    public IReadOnlyList<OcrTextRect> RecognizePngWithRects(byte[] pngBytes)
    {
        var raw = RecognizeRaw(pngBytes);
        return raw.Select(r => new OcrTextRect(r.Text, r.X, r.Y, r.W, r.H)).ToList();
    }

    private sealed record RawLine(string Text, double Score, int X, int Y, int W, int H);

    private IReadOnlyList<RawLine> RecognizeRaw(byte[] pngBytes)
    {
        if (!IsAvailable) throw new WindowAutomationException("ocr_unavailable","The local OCR engine is unavailable.");
        try
        {
            using var content = new ByteArrayContent(pngBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            var resp = _http.PostAsync($"http://127.0.0.1:{_port}/ocr", content).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var lines = new List<RawLine>();
            foreach (var item in doc.RootElement.GetProperty("lines").EnumerateArray())
            {
                var text = item.GetProperty("text").GetString() ?? "";
                var score = item.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0.0;
                var x = item.GetProperty("x").GetInt32();
                var y = item.GetProperty("y").GetInt32();
                var w = item.GetProperty("w").GetInt32();
                var h = item.GetProperty("h").GetInt32();
                lines.Add(new RawLine(text, score, x, y, w, h));
            }
            return lines;
        }
        catch
        {
            throw new WindowAutomationException("ocr_failed","The local OCR service did not return a valid observation.");
        }
    }

    private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.Dispose();
            }
        }
        catch
        {
            // 忽略。
        }
        _http.Dispose();
    }
}
