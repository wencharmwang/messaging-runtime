using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MessagingRuntime.Mcp;

public static class MessagingTransports
{
    public static void RunStdio(MessagingMcpServer server, TextReader input, TextWriter output)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = line.Length > 1_048_576
                ? "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32600,\"message\":\"Request too large\"}}"
                : server.Handle(line);
            if (response is null) continue;
            output.WriteLine(response);
            output.Flush();
        }
    }

    /// <summary>Stateless Streamable HTTP with JSON responses, no optional SSE stream.</summary>
    public static WebApplication CreateHttp(MessagingMcpServer server, string token, int port = 0,
        MessagingMcpServer? workerServer = null, string? workerToken = null)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
            throw new ArgumentException("HTTP requires a local bearer token of at least 32 characters.", nameof(token));
        if (workerServer is not null && (string.IsNullOrWhiteSpace(workerToken) || workerToken.Length < 32 || workerToken == token))
            throw new ArgumentException("The automatic reply worker requires a distinct private token.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders(); // No request bodies, credentials, or message content in logs.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 1_048_576;
        });
        var app = builder.Build();
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token));
        var workerExpected = workerServer is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + workerToken));
        app.Run(async context =>
        {
            if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && origin != $"http://127.0.0.1:{context.Connection.LocalPort}")
            { context.Response.StatusCode = 403; return; }
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            var selected = workerExpected is not null && CryptographicOperations.FixedTimeEquals(workerExpected, actual) ? workerServer :
                CryptographicOperations.FixedTimeEquals(expected, actual) ? server : null;
            if (selected is null)
            { context.Response.StatusCode = 401; return; }
            if (context.Request.Path != "/mcp") { context.Response.StatusCode = 404; return; }
            if (context.Request.Method != "POST")
            { context.Response.StatusCode = 405; context.Response.Headers.Allow = "POST"; return; }
            if (context.Request.ContentType?.Split(';')[0].Trim() != "application/json")
            { context.Response.StatusCode = 415; return; }
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                var response = selected.Handle(body);
                context.Response.StatusCode = response is null ? 202 : 200;
                if (response is not null)
                {
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync(response, context.RequestAborted);
                }
            }
            catch (BadHttpRequestException) { context.Response.StatusCode = 413; }
        });
        return app;
    }
}
