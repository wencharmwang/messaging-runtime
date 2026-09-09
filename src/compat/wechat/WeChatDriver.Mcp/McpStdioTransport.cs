using System.Text;

namespace WeChatDriver.Mcp;

/// <summary>
/// MCP 的 stdio 传输：从标准输入逐行读取 JSON-RPC 请求，把响应逐行写回标准输出。
/// 日志只写到标准错误，绝不污染 stdout 协议流。可注入自定义流以便单元测试。
/// </summary>
public sealed class McpStdioTransport
{
    private readonly McpServer _server;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _log;

    public McpStdioTransport(McpServer server)
        : this(server,
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.OpenStandardError())
    { }

    public McpStdioTransport(McpServer server, Stream input, Stream output, Stream log)
        : this(server,
            new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true),
            new StreamWriter(output, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true },
            new StreamWriter(log, new UTF8Encoding(false)) { AutoFlush = true })
    { }

    public McpStdioTransport(McpServer server, TextReader input, TextWriter output, TextWriter log)
    {
        _server = server;
        _input = input;
        _output = output;
        _log = log;
    }

    /// <summary>阻塞运行，直到 stdin 关闭。返回进程退出码。</summary>
    public int Run()
    {
        string? line;
        while ((line = _input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var response = _server.Handle(line);
                if (response is not null)
                {
                    _output.Write(response);
                    _output.Write('\n');
                    _output.Flush();
                }
            }
            catch (Exception ex)
            {
                _log.WriteLine($"[mcp] 处理消息异常: {ex}");
            }
        }
        return 0;
    }
}
