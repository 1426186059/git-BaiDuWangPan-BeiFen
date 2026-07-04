using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BaiduBackup.Models;

namespace BaiduBackup.Services;

public class GitHubService
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubService> _logger;

    public GitHubService(IHttpClientFactory httpClientFactory, ILogger<GitHubService> logger)
    {
        _http = httpClientFactory.CreateClient("GitHubApi");
        _logger = logger;
    }

    public async Task<List<GitHubRepoInfo>> GetRepositoriesAsync(string username, string? githubToken)
    {
        var allRepos = new List<GitHubRepoInfo>();
        var request = new HttpRequestMessage();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BaiduBackup", "1.0"));

        if (!string.IsNullOrEmpty(githubToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        }

        int page = 1;
        while (true)
        {
            var url = !string.IsNullOrEmpty(githubToken)
                ? $"https://api.github.com/user/repos?per_page=100&page={page}&sort=updated&type=all"
                : $"https://api.github.com/users/{username}/repos?per_page=100&page={page}&sort=updated";

            request.RequestUri = new Uri(url);
            var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var rateRemain = response.Headers.TryGetValues("X-RateLimit-Remaining", out var r) ? r.FirstOrDefault() : "?";
                    var rateReset = response.Headers.TryGetValues("X-RateLimit-Reset", out var rs) ? rs.FirstOrDefault() : null;
                    var resetTime = rateReset != null
                        ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(rateReset)).ToLocalTime().ToString("HH:mm:ss")
                        : "未知";

                    throw new InvalidOperationException(
                        $"GitHub API 403 访问被拒绝 [限流剩余={rateRemain}, 重置时间={resetTime}]。" +
                        (string.IsNullOrEmpty(githubToken)
                            ? " 你未提供 GitHub Token，未认证请求限流 60次/小时。请在前端填写 GitHub Personal Access Token。"
                            : " Token 可能已过期、权限不足（需勾选 repo 权限），或触发二次验证。请检查 Token 并重新生成。"));
                }
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    throw new InvalidOperationException($"用户 '{username}' 不存在");
                throw new InvalidOperationException($"GitHub API 错误: {response.StatusCode}");
            }

            var repos = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
            if (repos == null || repos.Count == 0) break;

            foreach (var repo in repos)
            {
                // 安全读取字段，防止 null 值导致 GetString() 抛异常
                string? SafeGetString(JsonElement el, string prop)
                {
                    if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
                    return null;
                }
                int SafeGetInt32(JsonElement el, string prop)
                {
                    if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
                        return v.GetInt32();
                    return 0;
                }

                var desc = SafeGetString(repo, "description") ?? "";
                var defBranch = SafeGetString(repo, "default_branch");
                var ownerLogin = SafeGetString(repo.GetProperty("owner"), "login");
                var repoName = SafeGetString(repo, "name");
                var fullName = SafeGetString(repo, "full_name");
                var updatedAt = SafeGetString(repo, "updated_at");

                // 跳过无法解析关键字段的仓库
                if (string.IsNullOrEmpty(ownerLogin) || string.IsNullOrEmpty(repoName))
                {
                    _logger.LogWarning("跳过无法解析的仓库: {FullName}", fullName ?? "未知");
                    continue;
                }

                // 空仓库（无默认分支）标记为特殊情况
                if (string.IsNullOrEmpty(defBranch))
                {
                    _logger.LogInformation("仓库 {Name} 无默认分支（可能是空仓库），跳过", fullName);
                    continue;
                }

                allRepos.Add(new GitHubRepoInfo
                {
                    Owner = ownerLogin,
                    Name = repoName,
                    FullName = fullName!,
                    PrivateRepo = repo.GetProperty("private").GetBoolean(),
                    Description = desc,
                    DefaultBranch = defBranch,
                    Size = SafeGetInt32(repo, "size"),
                    UpdatedAt = updatedAt
                });
            }

            if (repos.Count < 100) break;
            page++;
        }

        _logger.LogInformation("获取到 {Count} 个仓库 (用户: {User})", allRepos.Count, username);
        return allRepos;
    }

    /// <summary>
    /// 下载仓库 ZIP 并写入目标流，支持进度回调。
    /// </summary>
    /// <param name="estimatedSize">
    /// 预估文件总字节数（来自 GitHub API 的 repo.size 字段，单位 KB×1024）。
    /// 当 HTTP Content-Length 不可用时，用此值计算下载百分比。
    /// </param>
    public async Task<long> DownloadRepositoryAsync(
        string owner, string repo, string branch, string? githubToken,
        Stream destination, long estimatedSize = 0,
        Action<long, long>? progressCallback = null)
    {
        var failures = new List<string>();
        HttpResponseMessage? response = null;

        // 方案1: github.com 直接 archive 下载
        var archiveUrl = $"https://github.com/{owner}/{repo}/archive/refs/heads/{branch}.zip";
        var (resp1, failReason1) = await TryDownloadWithReasonAsync(archiveUrl, githubToken);
        if (resp1 != null)
        {
            response = resp1;
            _logger.LogInformation("✅ github archive 成功: {Url}", archiveUrl);
        }
        else
        {
            failures.Add($"[github archive] {failReason1}");
        }

        // 方案2: codeload.github.com
        if (response == null)
        {
            var codeloadUrl = $"https://codeload.github.com/{owner}/{repo}/zip/refs/heads/{branch}";
            var (resp2, failReason2) = await TryDownloadWithReasonAsync(codeloadUrl, githubToken);
            if (resp2 != null)
            {
                response = resp2;
                _logger.LogInformation("✅ codeload 成功: {Url}", codeloadUrl);
            }
            else
            {
                failures.Add($"[codeload] {failReason2}");
            }
        }

        // 方案3: API zipball
        if (response == null)
        {
            var apiArchiveUrl = $"https://api.github.com/repos/{owner}/{repo}/zipball/{branch}";
            var (resp3, failReason3) = await TryDownloadWithReasonAsync(apiArchiveUrl, githubToken);
            if (resp3 != null)
            {
                response = resp3;
                _logger.LogInformation("✅ API archive 成功: {Url}", apiArchiveUrl);
            }
            else
            {
                failures.Add($"[API archive] {failReason3}");
            }
        }

        // 三个方式都失败 → 输出详细原因
        if (response == null)
        {
            var detail = string.Join("; ", failures);
            _logger.LogWarning("三个下载方式全部失败: {Owner}/{Repo} ({Branch}) | 详情: {Detail}",
                owner, repo, branch, detail);
            throw new InvalidOperationException(
                $"仓库无可用内容: {owner}/{repo} ({branch})。" +
                $"详情: {detail}。" +
                "请确认: 1) Token 有该仓库的 repo 权限 2) 仓库非空 3) 分支名正确。");
        }

        // 优先用 HTTP Content-Length（精确），否则用 GitHub API 预估大小
        var contentLength = response.Content.Headers.ContentLength;
        var totalSize = contentLength ?? estimatedSize;

        _logger.LogInformation("正在下载: {Owner}/{Repo} ({Branch}), 大小: {Size}MB (Content-Length={CL}, 预估={Est}MB)",
            owner, repo, branch, totalSize / 1024.0 / 1024.0,
            contentLength, estimatedSize / 1024.0 / 1024.0);

        // 立即报告初始进度（让前端看到总量和 0%）
        progressCallback?.Invoke(0, totalSize);

        // 流式下载：直接从 HTTP 响应体分块读取。
        // HttpClientHandler 已配置 AutomaticDecompression=None + AllowAutoRedirect=true，
        // 重定向后的响应体为原始 ZIP 字节流，不触发 .NET 10 的 DeflateStream 兼容问题。
        using var netStream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[81920]; // 80KB 分块
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await netStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;
            progressCallback?.Invoke(totalRead, totalSize);
        }

        await destination.FlushAsync();

        // 完整性校验：对比实际下载字节数与 Content-Length
        if (contentLength.HasValue && totalRead < contentLength.Value)
        {
            throw new InvalidOperationException(
                $"下载不完整: 预期 {FormatBytes(contentLength.Value)}，" +
                $"实际仅下载 {FormatBytes(totalRead)} ({totalRead * 100L / contentLength.Value}%)。" +
                "请重试或检查网络连接。");
        }

        _logger.LogInformation("下载完成，实际大小: {Size}MB", totalRead / 1024.0 / 1024.0);
        return totalRead;

        static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }

    /// <summary>
    /// 通过 GitHub API 获取单个仓库元数据（含仓库大小，用于下载进度预估）。
    /// </summary>
    public async Task<(int SizeKb, string DefaultBranch)> GetRepoInfoAsync(
        string owner, string repo, string? githubToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repo}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BaiduBackup", "1.0"));
        if (!string.IsNullOrEmpty(githubToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);

        var resp = await _http.SendAsync(request);
        resp.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = doc.RootElement;

        int size = 0;
        if (root.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number)
            size = sz.GetInt32();

        string defaultBranch = "main";
        if (root.TryGetProperty("default_branch", out var db) && db.ValueKind == JsonValueKind.String)
            defaultBranch = db.GetString() ?? "main";

        _logger.LogInformation("GitHub API 仓库信息: {Owner}/{Repo} size={Size}KB, branch={Branch}",
            owner, repo, size, defaultBranch);
        return (size, defaultBranch);
    }

    /// <summary>尝试下载并返回失败原因。成功返回 (response, null)，失败返回 (null, 原因描述)</summary>
    private async Task<(HttpResponseMessage? Response, string? FailReason)> TryDownloadWithReasonAsync(
        string url, string? githubToken)
    {
        _logger.LogDebug("尝试下载: {Url}", url);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BaiduBackup", "1.0"));

        if (!string.IsNullOrEmpty(githubToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        if (response.IsSuccessStatusCode)
            return (response, null);

        // 302 重定向 → 跟随
        if (response.StatusCode == System.Net.HttpStatusCode.Redirect ||
            response.StatusCode == System.Net.HttpStatusCode.Found)
        {
            var redirectUrl = response.Headers.Location?.ToString();
            response.Dispose();
            if (!string.IsNullOrEmpty(redirectUrl))
            {
                _logger.LogInformation("跟随重定向到: {Url}", redirectUrl);
                return await TryDownloadWithReasonAsync(redirectUrl, githubToken);
            }
            return (null, "重定向但无 Location 头");
        }

        var statusCode = (int)response.StatusCode;
        string failReason;

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            failReason = $"HTTP 404 (分支/仓库不存在或为空仓库)";
        }
        else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var rateRemain = -1;
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var r);
            if (r != null) int.TryParse(r.FirstOrDefault(), out rateRemain);

            if (rateRemain == 0)
            {
                response.Dispose();
                throw new InvalidOperationException(
                    $"GitHub API 限流 [剩余配额=0]。请等待限流重置后再试。");
            }

            var hasToken = !string.IsNullOrEmpty(githubToken);
            failReason = hasToken
                ? $"HTTP 403 (Token 无该仓库访问权限, 剩余配额={rateRemain})"
                : $"HTTP 403 (私有仓库需要提供 GitHub Token)";
        }
        else
        {
            var body = await response.Content.ReadAsStringAsync();
            failReason = $"HTTP {statusCode} — {body[..Math.Min(body.Length, 200)]}";
        }

        response.Dispose();
        _logger.LogWarning("下载失败 [{Reason}]: {Url}", failReason, url);
        return (null, failReason);
    }

    /// <summary>尝试从指定 URL 下载（兼容旧接口）</summary>
    private async Task<HttpResponseMessage?> TryDownloadAsync(string url, string? githubToken)
    {
        var (response, _) = await TryDownloadWithReasonAsync(url, githubToken);
        return response;
    }
}
