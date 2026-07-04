using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using BaiduBackup.Services;

// ⚠️ 全局 SSL 兜底：ServicePointManager 兼容旧式 API（WebRequest 等）
ServicePointManager.SecurityProtocol =
    SecurityProtocolType.Tls12 |
    SecurityProtocolType.Tls13 |
    SecurityProtocolType.Tls11 |
    SecurityProtocolType.Tls;
ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
ServicePointManager.Expect100Continue = true;
ServicePointManager.DefaultConnectionLimit = 100;

var builder = WebApplication.CreateBuilder(args);

// 添加控制器
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 注册 HttpClient
builder.Services.AddHttpClient("BaiduApi", client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BaiduBackup/1.0");
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    // BaiduApi 也不需要自动解压：ReadResponseStringAsync 已自行处理原始字节+UTF8解码。
    // HTTP 层解压遇到 charset=utf8 头或异常 Content-Encoding 会导致 DeflateStream 报错。
    var handler = CreateSocketsHttpHandler();
    handler.AutomaticDecompression = DecompressionMethods.None;
    return handler;
});

builder.Services.AddHttpClient("GitHubApi", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BaiduBackup/1.0");
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    // 使用 HttpClientHandler 而非 SocketsHttpHandler：
    // .NET 10 预览版下 SocketsHttpHandler 即使设 None 仍可能触发解压，抛
    // "The archive entry was compressed using an unsupported compression method"。
    // HttpClientHandler 更简单，DecompressionMethods.None 行为更可靠。
    IWebProxy? proxy = null;
    var httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                  ?? Environment.GetEnvironmentVariable("https_proxy");
    if (!string.IsNullOrWhiteSpace(httpsProxy))
        proxy = new WebProxy(httpsProxy) { BypassProxyOnLocal = true };
    else
    {
        proxy = WebRequest.DefaultWebProxy;
        if (proxy != null) proxy.Credentials = CredentialCache.DefaultCredentials;
    }

    return new HttpClientHandler
    {
        Proxy = proxy,
        UseProxy = proxy != null,
        AutomaticDecompression = DecompressionMethods.None, // 关键：禁止解压
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
    };
});

// 诊断用裸 HttpClient（不走工厂，独立验证连通性）
builder.Services.AddHttpClient("Diagnostics", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(() => CreateSocketsHttpHandler());

static SocketsHttpHandler CreateSocketsHttpHandler()
{
    // 检测系统代理（通过环境变量）
    IWebProxy? proxy = null;
    var httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                  ?? Environment.GetEnvironmentVariable("https_proxy");
    var httpProxy  = Environment.GetEnvironmentVariable("HTTP_PROXY")
                  ?? Environment.GetEnvironmentVariable("http_proxy");

    if (!string.IsNullOrWhiteSpace(httpsProxy))
    {
        proxy = new WebProxy(httpsProxy) { BypassProxyOnLocal = true };
    }
    else if (!string.IsNullOrWhiteSpace(httpProxy))
    {
        proxy = new WebProxy(httpProxy) { BypassProxyOnLocal = true };
    }
    else
    {
        // 兜底：使用 Windows 系统代理设置
        proxy = WebRequest.DefaultWebProxy;
        if (proxy != null)
            proxy.Credentials = CredentialCache.DefaultCredentials;
    }

    return new SocketsHttpHandler
    {
        // 🔑 关键：使用系统代理
        Proxy = proxy,
        UseProxy = proxy != null,
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        MaxConnectionsPerServer = 100,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        ResponseDrainTimeout = TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        },
    };
}

// 注册服务
builder.Services.AddSingleton<BaiduNetdiskService>();
builder.Services.AddSingleton<GitHubService>();

// CORS 配置
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.MapControllers();

app.MapGet("/api/tempdir", () => Results.Ok(new { tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp") }));

// ============ 网络诊断端点 ============
app.MapGet("/api/diagnostics/network", async (IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient("Diagnostics");
    var results = new List<object>();

    var targets = new[]
    {
        ("GitHub API", "https://api.github.com"),
        ("GitHub Download", "https://codeload.github.com"),
        ("百度 OAuth", "https://openapi.baidu.com"),
        ("百度网盘 API", "https://pan.baidu.com"),
        ("百度 PCS", "https://d.pcs.baidu.com"),
    };

    foreach (var (name, url) in targets)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            sw.Stop();
            results.Add(new
            {
                target = name,
                url,
                ok = true,
                statusCode = (int)response.StatusCode,
                latencyMs = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            var fullError = ex.Message;
            var inner = ex.InnerException;
            while (inner != null)
            {
                fullError += $" → {inner.Message}";
                inner = inner.InnerException;
            }
            results.Add(new
            {
                target = name,
                url,
                ok = false,
                error = fullError,
                errorType = ex.GetType().Name,
                stackTrace = ex.StackTrace?.Split('\n', StringSplitOptions.TrimEntries).Take(3)
            });
        }
    }

    // 也检测系统代理
    string? systemProxy = null;
    try
    {
        var proxy = WebRequest.GetSystemWebProxy();
        var proxyUri = proxy?.GetProxy(new Uri("https://github.com"));
        systemProxy = proxyUri?.ToString();
    }
    catch { }

    return Results.Ok(new
    {
        systemProxy,
        machineName = Environment.MachineName,
        osVersion = Environment.OSVersion.ToString(),
        results
    });
});

app.MapFallbackToFile("index.html");

app.Run();
