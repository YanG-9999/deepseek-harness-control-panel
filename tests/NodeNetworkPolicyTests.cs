using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

/// <summary>
/// Covers the transport-selection policy and executes the embedded Node fetch
/// helper against a loopback server, so a broken helper script cannot pass.
/// </summary>
public static class NodeNetworkPolicyTests
{
    public static int Main()
    {
        Run();
        return 0;
    }

    public static void Run()
    {
        VerifyTlsStackFailureDetection();
        VerifyArgumentQuoting();
        VerifyJsonEscaping();
        VerifyRequestJson();
        VerifyReportParsing();
        VerifyHelperAgainstLoopbackServer();
        // Printed here, not in Main: the aggregate entry point calls Run() directly,
        // so a message only in Main would never reach the suite output.
        Console.WriteLine("Node network policy tests passed.");
    }

    private static void VerifyTlsStackFailureDetection()
    {
        // The exact text HttpClient surfaces on a Clash TUN/fake-IP host: the
        // deepest SCHANNEL message is rewritten into a generic channel error.
        if (!NodeNetworkPolicy.IsTlsStackFailure("请求被中止: 未能创建 SSL/TLS 安全通道。"))
            throw new InvalidOperationException("The observed HttpClient TLS failure must select the Node transport.");
        // A raw HttpWebRequest exposes the deepest Win32 text instead.
        if (!NodeNetworkPolicy.IsTlsStackFailure("安全包中没有可用的凭证"))
            throw new InvalidOperationException("The observed SCHANNEL failure must select the Node transport.");
        if (!NodeNetworkPolicy.IsTlsStackFailure("No credentials are available in the security package"))
            throw new InvalidOperationException("The English SCHANNEL failure must select the Node transport.");
        if (!NodeNetworkPolicy.IsTlsStackFailure("基础连接已经关闭: 接收时发生错误。"))
            throw new InvalidOperationException("A closed underlying connection must select the Node transport.");

        // The real observed chain: HttpRequestException wrapping WebException.
        var observed = new System.Net.Http.HttpRequestException(
            "发送请求时出错。",
            new WebException("请求被中止: 未能创建 SSL/TLS 安全通道。"));
        if (!NodeNetworkPolicy.IsTlsStackFailure(observed))
            throw new InvalidOperationException("The observed HttpClient exception chain must select the Node transport.");

        // A WebException anywhere in the chain is a transport-layer failure even
        // when its message carries no recognizable text.
        var structural = new InvalidOperationException("outer", new WebException("something else entirely"));
        if (!NodeNetworkPolicy.IsTlsStackFailure(structural))
            throw new InvalidOperationException("A WebException in the chain must select the Node transport.");

        // A wrapper must still reach the inner SCHANNEL message.
        var wrapped = new InvalidOperationException(
            "outer",
            new System.Net.Http.HttpRequestException(
                "inner",
                new WebException("基础连接已经关闭: 接收时发生错误。")));
        if (!NodeNetworkPolicy.IsTlsStackFailure(wrapped))
            throw new InvalidOperationException("The exception chain must be walked to find a TLS stack fault.");

        // A genuine server rejection must NOT be retried over another transport.
        if (NodeNetworkPolicy.IsTlsStackFailure("下载失败，服务器返回 404 Not Found。"))
            throw new InvalidOperationException("An HTTP status rejection is not a transport fault.");
        if (NodeNetworkPolicy.IsTlsStackFailure("无法读取官方仓库默认分支。"))
            throw new InvalidOperationException("A parse failure is not a transport fault.");
        if (NodeNetworkPolicy.IsTlsStackFailure("") || NodeNetworkPolicy.IsTlsStackFailure((string)null))
            throw new InvalidOperationException("An empty message is not a transport fault.");
    }

    private static void VerifyArgumentQuoting()
    {
        if (NodeNetworkPolicy.QuoteArgument(@"C:\Program Files\node.exe") != @"""C:\Program Files\node.exe""")
            throw new InvalidOperationException("A path with spaces must be quoted.");

        // A crafted path must be refused, never escaped into the argument stream.
        AssertThrows("quote injection", delegate { NodeNetworkPolicy.QuoteArgument("a\"b"); });
        AssertThrows("newline injection", delegate { NodeNetworkPolicy.QuoteArgument("a\r\nb"); });
    }

    private static void VerifyJsonEscaping()
    {
        if (NodeNetworkPolicy.EscapeJson("plain") != "plain")
            throw new InvalidOperationException("Plain text must pass through unchanged.");
        if (NodeNetworkPolicy.EscapeJson("a\"b") != "a\\\"b")
            throw new InvalidOperationException("A double quote must be escaped.");
        if (NodeNetworkPolicy.EscapeJson(@"C:\temp\x") != @"C:\\temp\\x")
            throw new InvalidOperationException("A backslash must be escaped.");
        if (NodeNetworkPolicy.EscapeJson("a\nb") != "a\\nb")
            throw new InvalidOperationException("A newline must be escaped.");
        if (NodeNetworkPolicy.EscapeJson("") != "" || NodeNetworkPolicy.EscapeJson(null) != "")
            throw new InvalidOperationException("Null and empty text must serialize to an empty string.");
    }

    private static void VerifyRequestJson()
    {
        string json = NodeNetworkPolicy.BuildRequestJson(
            "https://example.com/a?b=1",
            @"C:\temp\out.bin",
            120,
            "agent/1.0");
        if (json.IndexOf("\"url\":\"https://example.com/a?b=1\"", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The request JSON must carry the URL.");
        if (json.IndexOf(@"""outputPath"":""C:\\temp\\out.bin""", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The request JSON must carry an escaped output path.");
        if (json.IndexOf("\"timeoutSeconds\":120", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The request JSON must carry the timeout as a number.");
        if (json.IndexOf("\"userAgent\":\"agent/1.0\"", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("The request JSON must carry the user agent.");

        string fallback = NodeNetworkPolicy.BuildRequestJson("https://example.com", "out.bin", 5, "");
        if (fallback.IndexOf("\"userAgent\":\"" + NodeNetworkPolicy.UserAgent + "\"", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("A blank user agent must fall back to the panel default.");

        AssertThrows("blank url", delegate { NodeNetworkPolicy.BuildRequestJson("", "out.bin", 5, "a"); });
        AssertThrows("blank path", delegate { NodeNetworkPolicy.BuildRequestJson("https://x", "", 5, "a"); });
        AssertThrows("non-positive timeout", delegate { NodeNetworkPolicy.BuildRequestJson("https://x", "out.bin", 0, "a"); });
    }

    private static void VerifyReportParsing()
    {
        string success = "{\"ok\":true,\"status\":200,\"contentType\":\"application/json\",\"bytes\":42,\"finalUrl\":\"https://x/\"}";
        if (!NodeNetworkPolicy.IsSuccessReport(success, 200))
            throw new InvalidOperationException("A 200 report must be recognized as success.");
        if (NodeNetworkPolicy.IsSuccessReport(success, 404))
            throw new InvalidOperationException("The reported status must be compared, not assumed.");
        if (NodeNetworkPolicy.IsFailureReport(success))
            throw new InvalidOperationException("A success report is not a failure.");
        if (NodeNetworkPolicy.ReadReportedNumber(success, "bytes", -1) != 42)
            throw new InvalidOperationException("A reported number must be parsed.");

        string failure = "{\"ok\":false,\"aborted\":false,\"error\":\"TypeError: fetch failed\"}";
        if (!NodeNetworkPolicy.IsFailureReport(failure))
            throw new InvalidOperationException("A failure report must be recognized.");
        if (NodeNetworkPolicy.IsSuccessReport(failure, 200))
            throw new InvalidOperationException("A failure report is never a success.");

        if (!NodeNetworkPolicy.IsFailureReport("") || !NodeNetworkPolicy.IsFailureReport(null))
            throw new InvalidOperationException("An empty report must be treated as a failure, never a silent success.");
        if (NodeNetworkPolicy.ReadReportedNumber("", "bytes", 7) != 7)
            throw new InvalidOperationException("A missing key must return the fallback.");
        if (NodeNetworkPolicy.ReadReportedNumber("{\"bytes\":\"many\"}", "bytes", 7) != 7)
            throw new InvalidOperationException("A non-numeric value must return the fallback.");
    }

    /// <summary>
    /// Executes the embedded helper exactly as the panel does, against a loopback
    /// HTTP server, and asserts the body reached disk.
    /// </summary>
    private static void VerifyHelperAgainstLoopbackServer()
    {
        string node = FindNodeExecutable();
        if (String.IsNullOrEmpty(node))
        {
            Console.WriteLine("  (skipped helper execution: no node.exe on this machine)");
            return;
        }

        const string payload = "dsh-net-channel-ok";
        int port = 47831;
        var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:" + port + "/probe/");
        listener.Start();
        var server = new Thread(delegate()
        {
            try
            {
                HttpListenerContext context = listener.GetContext();
                byte[] body = Encoding.UTF8.GetBytes(payload);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = body.Length;
                context.Response.OutputStream.Write(body, 0, body.Length);
                context.Response.OutputStream.Close();
            }
            catch
            {
                // The assertion below reports a missing response.
            }
        });
        server.IsBackground = true;
        server.Start();

        string work = Path.Combine(Path.GetTempPath(), "dsh-helper-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string helper = Path.Combine(work, "fetch-helper.mjs");
        string request = Path.Combine(work, "request.json");
        string output = Path.Combine(work, "response.txt");
        try
        {
            File.WriteAllText(helper, NodeNetworkPolicy.HelperScript, new UTF8Encoding(false));
            File.WriteAllText(
                request,
                NodeNetworkPolicy.BuildRequestJson("http://127.0.0.1:" + port + "/probe/", output, 30, "dsh-test"),
                new UTF8Encoding(false));

            var psi = new ProcessStartInfo(node, NodeNetworkPolicy.QuoteArgument(helper) + " " + NodeNetworkPolicy.QuoteArgument(request));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;

            string report;
            string errors;
            using (var process = Process.Start(psi))
            {
                report = process.StandardOutput.ReadToEnd().Trim();
                errors = process.StandardError.ReadToEnd().Trim();
                if (!process.WaitForExit(60000))
                {
                    process.Kill();
                    throw new InvalidOperationException("The Node fetch helper did not exit within 60 seconds.");
                }
            }

            if (!NodeNetworkPolicy.IsSuccessReport(report, 200))
                throw new InvalidOperationException(
                    "The embedded Node helper failed against a loopback server. report=" + report + " stderr=" + errors);
            if (!File.Exists(output))
                throw new InvalidOperationException("The Node helper reported success but wrote no output file.");
            string written = File.ReadAllText(output);
            if (written != payload)
                throw new InvalidOperationException("The Node helper wrote the wrong body: " + written);
            if (NodeNetworkPolicy.ReadReportedNumber(report, "bytes", -1) != payload.Length)
                throw new InvalidOperationException("The reported byte count must match the written body.");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private static string FindNodeExecutable()
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (String.IsNullOrWhiteSpace(folder))
                continue;
            try
            {
                string candidate = Path.Combine(folder.Trim().Trim('"'), "node.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }
        return null;
    }

    private static void AssertThrows(string label, Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return;
        }
        throw new InvalidOperationException("Expected an exception for " + label + ".");
    }
}
