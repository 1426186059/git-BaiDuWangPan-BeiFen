using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BaiduBackup.Models;

namespace BaiduBackup.Services;

public class GitHubService
{
    private readonly HttpClient _http;

    public GitHubService(IHttpClientFactory httpClientFactory)
    {
        _http = httpClientFactory.CreateClient("GitHubApi");
    }

    public async Task<List<GitHubRepoInfo>> GetRepositoriesAsync(string username, string? githubToken)
    {
        var allRepos = new List<GitHubRepoInfo>();
        using var request = new HttpRequestMessage();
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
            using var response = await _http.SendAsync(request);

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

            // AOT 安全：用 JsonDocument 替代 ReadFromJsonAsync（避免反射）
            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var repos = doc.RootElement.EnumerateArray().ToList();
            if (repos.Count == 0) break;

            foreach (var repo in repos)
            {
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

                if (string.IsNullOrEmpty(ownerLogin) || string.IsNullOrEmpty(repoName))
                    continue;

                if (string.IsNullOrEmpty(defBranch))
                    continue;

                allRepos.Add(new GitHubRepoInfo
                {
                    Owner = ownerLogin,
                    Name = repoName,
                    FullName = fullName ?? $"{ownerLogin}/{repoName}",
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

        return allRepos;
    }

    /// <summary>
    /// 下载仓库 ZIP 并写入目标流，支持进度回调和 HTTP Range 断点续传。
    /// 内建流中断重试（默认最多 5 次额外重试），内部重试自动续传。
    /// </summary>
    public async Task<long> DownloadRepositoryAsync(
        string owner, string repo, string branch, string? githubToken,
        Stream destination, long estimatedSize = 0,
        Action<long, long, int, int>? progressCallback = null,
        int maxStreamRetries = 5,
        long resumeFromBytes = 0)
    {
        var failures = new List<string>();

        if (resumeFromBytes > 0 && destination.CanSeek)
        {
            destination.Position = resumeFromBytes;
        }

        async Task<HttpResponseMessage?> GetDownloadResponseAsync(long rangeStart)
        {
            failures.Clear();
            HttpResponseMessage? response = null;

            var archiveUrl = $"https://github.com/{owner}/{repo}/archive/refs/heads/{branch}.zip";
            var (resp1, failReason1) = await TryDownloadWithReasonAsync(archiveUrl, githubToken, rangeStart);
            if (resp1 != null)
            {
                response = resp1;
            }
            else
            {
                failures.Add($"[github archive] {failReason1}");
            }

            if (response == null)
            {
                var codeloadUrl = $"https://codeload.github.com/{owner}/{repo}/zip/refs/heads/{branch}";
                var (resp2, failReason2) = await TryDownloadWithReasonAsync(codeloadUrl, githubToken, rangeStart);
                if (resp2 != null)
                {
                    response = resp2;
                }
                else
                {
                    failures.Add($"[codeload] {failReason2}");
                }
            }

            if (response == null)
            {
                var apiArchiveUrl = $"https://api.github.com/repos/{owner}/{repo}/zipball/{branch}";
                var (resp3, failReason3) = await TryDownloadWithReasonAsync(apiArchiveUrl, githubToken, rangeStart);
                if (resp3 != null)
                {
                    response = resp3;
                }
                else
                {
                    failures.Add($"[API archive] {failReason3}");
                }
            }

            return response;
        }

        long alreadyDownloaded = destination.CanSeek ? destination.Position : 0;
        long currentRangeStart = alreadyDownloaded;

        int totalAttempts = maxStreamRetries + 1;
        for (int attempt = 0; attempt < totalAttempts; attempt++)
        {
            if (attempt > 0 && destination.CanSeek)
            {
                currentRangeStart = destination.Position;
                alreadyDownloaded = currentRangeStart;
            }

            var response = await GetDownloadResponseAsync(currentRangeStart);

            if (response == null)
            {
                var detail = string.Join("; ", failures);
                throw new InvalidOperationException(
                    $"仓库无可用内容: {owner}/{repo} ({branch})。" +
                    $"详情: {detail}。" +
                    "请确认: 1) Token 有该仓库的 repo 权限 2) 仓库非空 3) 分支名正确。");
            }

            bool isRangeResponse = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            long totalFileSize;
            long remainingToDownload;

            if (isRangeResponse)
            {
                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.HasRange == true && contentRange.From == currentRangeStart)
                {
                    totalFileSize = contentRange.Length ?? (currentRangeStart + (response.Content.Headers.ContentLength ?? 0));
                    remainingToDownload = response.Content.Headers.ContentLength ?? 0;
                }
                else
                {
                    response.Dispose();
                    goto fallbackFullDownload;
                }
            }
            else
            {
                if (currentRangeStart > 0)
                {
                    response.Dispose();

                    response = await GetDownloadResponseAsync(0);
                    if (response == null)
                    {
                        var detail = string.Join("; ", failures);
                        throw new InvalidOperationException(
                            $"断点续传失败: 服务器不支持 HTTP Range，且转为完整下载后三个下载源也全部失败。" +
                            $"详情: {detail}");
                    }

                    if (destination.CanSeek)
                    {
                        destination.SetLength(0);
                        destination.Position = 0;
                    }
                    alreadyDownloaded = 0;
                    currentRangeStart = 0;
                }
                totalFileSize = response.Content.Headers.ContentLength ?? 0;
                remainingToDownload = totalFileSize;
            }

            progressCallback?.Invoke(alreadyDownloaded, totalFileSize, attempt, maxStreamRetries);

            try
            {
                using var netStream = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[81920];
                long newlyDownloaded = 0;
                int bytesRead;

                while ((bytesRead = await netStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await destination.WriteAsync(buffer, 0, bytesRead);
                    newlyDownloaded += bytesRead;
                    progressCallback?.Invoke(alreadyDownloaded + newlyDownloaded, totalFileSize, 0, maxStreamRetries);
                }

                await destination.FlushAsync();

                long totalWritten = alreadyDownloaded + newlyDownloaded;

                if (totalFileSize > 0 && totalWritten < totalFileSize)
                {
                    throw new IOException(
                        $"下载不完整: 预期 {ByteFormatter.Format(totalFileSize)}，" +
                        $"实际仅 {ByteFormatter.Format(totalWritten)} ({totalWritten * 100L / totalFileSize}%)。" +
                        "连接可能提前断开。");
                }
                if (remainingToDownload > 0 && newlyDownloaded < remainingToDownload)
                {
                    throw new IOException(
                        $"本轮下载不完整: 预期 {ByteFormatter.Format(remainingToDownload)}，" +
                        $"实际仅 {ByteFormatter.Format(newlyDownloaded)}。");
                }

                return totalWritten;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException
                                         && attempt < totalAttempts - 1)
            {
                var delayMs = (attempt + 1) * 3000;
                await Task.Delay(delayMs);
            }
            finally
            {
                try { response?.Dispose(); } catch { }
            }
        }

    fallbackFullDownload:
        throw new InvalidOperationException(
            $"下载失败：GitHub 返回 206 Partial Content 但 Content-Range 头与请求不匹配。" +
            $"请重试备份。");
    }

    public async Task<(int SizeKb, string DefaultBranch)> GetRepoInfoAsync(
        string owner, string repo, string? githubToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repo}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BaiduBackup", "1.0"));
        if (!string.IsNullOrEmpty(githubToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);

        using var resp = await _http.SendAsync(request);
        resp.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = doc.RootElement;

        int size = 0;
        if (root.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number)
            size = sz.GetInt32();

        string defaultBranch = "main";
        if (root.TryGetProperty("default_branch", out var db) && db.ValueKind == JsonValueKind.String)
            defaultBranch = db.GetString() ?? "main";

        return (size, defaultBranch);
    }

    private async Task<(HttpResponseMessage? Response, string? FailReason)> TryDownloadWithReasonAsync(
        string url, string? githubToken, long rangeStart = 0, int redirectDepth = 0)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BaiduBackup", "1.0"));

        if (rangeStart > 0)
        {
            request.Headers.Range = new RangeHeaderValue(rangeStart, null);
        }

        if (!string.IsNullOrEmpty(githubToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        if (response.IsSuccessStatusCode)
            return (response, null);

        if (response.StatusCode == System.Net.HttpStatusCode.Redirect ||
            response.StatusCode == System.Net.HttpStatusCode.Found)
        {
            var redirectUrl = response.Headers.Location?.ToString();
            response.Dispose();
            if (!string.IsNullOrEmpty(redirectUrl))
            {
                const int maxRedirectDepth = 5;
                if (redirectDepth >= maxRedirectDepth)
                    return (null, $"重定向次数过多 (>{maxRedirectDepth})，可能是循环重定向");
                return await TryDownloadWithReasonAsync(redirectUrl, githubToken, rangeStart, redirectDepth + 1);
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
        return (null, failReason);
    }
}
