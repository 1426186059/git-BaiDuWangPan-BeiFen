using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using BaiduBackup.Models;
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

// Minimal API JSON 源生成上下文（AOT 兼容）
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

// 注册 HttpClient
builder.Services.AddHttpClient("BaiduApi", client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BaiduBackup/1.0");
}).ConfigurePrimaryHttpMessageHandler(() =>
{
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
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
    };
});

// 诊断用裸 HttpClient
builder.Services.AddHttpClient("Diagnostics", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(() => CreateSocketsHttpHandler());

static SocketsHttpHandler CreateSocketsHttpHandler()
{
    IWebProxy? proxy = null;
    var httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                  ?? Environment.GetEnvironmentVariable("https_proxy");
    var httpProxy  = Environment.GetEnvironmentVariable("HTTP_PROXY")
                  ?? Environment.GetEnvironmentVariable("http_proxy");

    if (!string.IsNullOrWhiteSpace(httpsProxy))
        proxy = new WebProxy(httpsProxy) { BypassProxyOnLocal = true };
    else if (!string.IsNullOrWhiteSpace(httpProxy))
        proxy = new WebProxy(httpProxy) { BypassProxyOnLocal = true };
    else
    {
        proxy = WebRequest.DefaultWebProxy;
        if (proxy != null)
            proxy.Credentials = CredentialCache.DefaultCredentials;
    }

    return new SocketsHttpHandler
    {
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
builder.Services.AddSingleton<BackupOrchestrator>();

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

app.UseStaticFiles();
app.UseRouting();
app.UseCors();

// ==================== Auth 端点 ====================

app.MapPost("/api/auth/baidu", async (AuthRequest request, BaiduNetdiskService baidu) =>
{
    if (string.IsNullOrWhiteSpace(request.ClientId) ||
        string.IsNullOrWhiteSpace(request.ClientSecret) ||
        string.IsNullOrWhiteSpace(request.Code))
    {
        return Results.BadRequest(ApiResponse<TokenResponse>.Fail("缺少必填参数"));
    }

    try
    {
        var token = await baidu.ExchangeCodeForTokenAsync(
            request.ClientId, request.ClientSecret, request.Code, request.RedirectUri);
        return Results.Ok(ApiResponse<TokenResponse>.Ok(token));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ApiResponse<TokenResponse>.Fail(GetFullError(ex)));
    }
});

app.MapPost("/api/auth/refresh", async (RefreshRequest request, BaiduNetdiskService baidu) =>
{
    if (string.IsNullOrWhiteSpace(request.RefreshToken) ||
        string.IsNullOrWhiteSpace(request.ClientId) ||
        string.IsNullOrWhiteSpace(request.ClientSecret))
    {
        return Results.BadRequest(ApiResponse<TokenResponse>.Fail("缺少必填参数"));
    }

    try
    {
        var token = await baidu.RefreshTokenAsync(
            request.RefreshToken, request.ClientId, request.ClientSecret);
        return Results.Ok(ApiResponse<TokenResponse>.Ok(token));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ApiResponse<TokenResponse>.Fail(GetFullError(ex)));
    }
});

// ==================== GitHub 端点 ====================

app.MapGet("/api/github/repos/{username}", async (string username, string? token, GitHubService github) =>
{
    if (string.IsNullOrWhiteSpace(username))
        return Results.BadRequest(ApiResponse<List<GitHubRepoInfo>>.Fail("用户名不能为空"));

    try
    {
        var repos = await github.GetRepositoriesAsync(username, token);
        return Results.Ok(ApiResponse<List<GitHubRepoInfo>>.Ok(repos));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ApiResponse<List<GitHubRepoInfo>>.Fail(GetFullError(ex)));
    }
});

// ==================== Backup 端点 ====================

app.MapGet("/api/backup/records", (BackupOrchestrator orch) =>
    Results.Ok(ApiResponse<BackupRecordView>.Ok(orch.GetRecords())));

app.MapPost("/api/backup/records/settotal", (SetTotalRequest req, BackupOrchestrator orch) =>
{
    orch.SetTotalRepoCount(req.TotalCount);
    return Results.Ok(ApiResponse<bool>.Ok(true));
});

app.MapPost("/api/backup/records/reset", (BackupOrchestrator orch) =>
{
    orch.ResetRecords();
    return Results.Ok(ApiResponse<bool>.Ok(true));
});

app.MapGet("/api/backup/progress", (string owner, string repo, string? branch, BackupOrchestrator orch) =>
    Results.Ok(ApiResponse<BackupProgressInfo>.Ok(orch.GetProgress(owner, repo, branch ?? "main"))));

app.MapPost("/api/backup/single", async (SingleBackupRequest request, BackupOrchestrator orch) =>
{
    var result = await orch.BackupSingleAsync(request);
    return Results.Ok(ApiResponse<BackupResult>.Ok(result));
});

app.MapPost("/api/backup/batch", async (BatchBackupRequest request, BackupOrchestrator orch) =>
{
    if (request.Repos == null || request.Repos.Count == 0)
        return Results.BadRequest(ApiResponse<BatchBackupResult>.Fail("仓库列表不能为空"));
    if (string.IsNullOrWhiteSpace(request.AccessToken))
        return Results.BadRequest(ApiResponse<BatchBackupResult>.Fail("缺少百度网盘 access_token"));

    try
    {
        var result = await orch.BackupBatchAsync(request);
        return Results.Ok(ApiResponse<BatchBackupResult>.Ok(result));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ApiResponse<BatchBackupResult>.Fail(GetFullError(ex)));
    }
});

// ==================== 工具端点 ====================

app.MapGet("/api/tempdir", () =>
    Results.Ok(new TempDirInfo { TempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp") }));

// ==================== 网络诊断端点 ====================

app.MapGet("/api/diagnostics/network", async (IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient("Diagnostics");
    var results = new List<DiagnosticsTargetResult>();

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
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            sw.Stop();
            results.Add(new DiagnosticsTargetResult
            {
                Target = name,
                Url = url,
                Ok = true,
                StatusCode = (int)response.StatusCode,
                LatencyMs = sw.ElapsedMilliseconds
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
            results.Add(new DiagnosticsTargetResult
            {
                Target = name,
                Url = url,
                Ok = false,
                Error = fullError,
                ErrorType = ex.GetType().Name,
                StackTrace = ex.StackTrace?.Split('\n', StringSplitOptions.TrimEntries).Take(3)
            });
        }
    }

    string? systemProxy = null;
    try
    {
        var proxy = WebRequest.GetSystemWebProxy();
        var proxyUri = proxy?.GetProxy(new Uri("https://github.com"));
        systemProxy = proxyUri?.ToString();
    }
    catch { }

    return Results.Ok(new DiagnosticsResult
    {
        SystemProxy = systemProxy,
        MachineName = Environment.MachineName,
        OsVersion = Environment.OSVersion.ToString(),
        Results = results
    });
});

app.MapFallbackToFile("index.html");

app.Run();

static string GetFullError(Exception ex)
{
    var msg = ex.Message;
    var inner = ex.InnerException;
    while (inner != null) { msg += $" → {inner.Message}"; inner = inner.InnerException; }
    return msg;
}
