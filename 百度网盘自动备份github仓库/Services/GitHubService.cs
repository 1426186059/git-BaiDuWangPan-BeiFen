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

        _logger.LogInformation("获取到 {Count} 个仓库 (用户: {User})", allRepos.Count, username);
        return allRepos;
    }

    /// <summary>
    /// 下载仓库 ZIP 并写入目标流，支持进度回调和 HTTP Range 断点续传。
    /// 内建流中断重试（最多额外 2 次），内部重试自动续传。
    /// </summary>
    /// <param name="estimatedSize">
    /// 预估文件总字节数（来自 GitHub API 的 repo.size 字段，单位 KB×1024）。
    /// 当 HTTP Content-Length 不可用时，用此值计算下载百分比。
    /// </param>
    /// <param name="maxStreamRetries">流读取中断时的额外重试次数（默认 5，即最多 6 次总尝试）</param>
    /// <param name="resumeFromBytes">
    /// 断点续传起始字节偏移量。0 表示全新下载。&gt;0 时方法会：
    /// ① 设置 destination.Position = resumeFromBytes
    /// ② 发送 HTTP Range: bytes={resumeFromBytes}- 请求头
    /// ③ 将新数据追加写入 destination（不覆盖已有数据）
    /// </param>
    public async Task<long> DownloadRepositoryAsync(
        string owner, string repo, string branch, string? githubToken,
        Stream destination, long estimatedSize = 0,
        Action<long, long, int, int>? progressCallback = null,
        int maxStreamRetries = 5,
        long resumeFromBytes = 0)
    {
        var failures = new List<string>();

        // 设置目标流的起始写入位置
        if (resumeFromBytes > 0 && destination.CanSeek)
        {
            destination.Position = resumeFromBytes;
            _logger.LogInformation("📌 断点续传: 从 {Offset}MB 处继续下载", resumeFromBytes / 1024.0 / 1024.0);
        }

        // 尝试 3 种下载 URL + Range 头的本地函数
        async Task<HttpResponseMessage?> GetDownloadResponseAsync(long rangeStart)
        {
            failures.Clear();
            HttpResponseMessage? response = null;

            // 方案1: github.com 直接 archive 下载
            var archiveUrl = $"https://github.com/{owner}/{repo}/archive/refs/heads/{branch}.zip";
            var (resp1, failReason1) = await TryDownloadWithReasonAsync(archiveUrl, githubToken, rangeStart);
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
                var (resp2, failReason2) = await TryDownloadWithReasonAsync(codeloadUrl, githubToken, rangeStart);
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
                var (resp3, failReason3) = await TryDownloadWithReasonAsync(apiArchiveUrl, githubToken, rangeStart);
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

            return response;
        }

        // 累计已下载量（含调用前就有的数据，用于进度报告）
        long alreadyDownloaded = destination.CanSeek ? destination.Position : 0;
        // 当前 Range 起始位置
        long currentRangeStart = alreadyDownloaded;

        int totalAttempts = maxStreamRetries + 1; // 首次 + retry 次数
        for (int attempt = 0; attempt < totalAttempts; attempt++)
        {
            // 内部重试：从已下载位置继续（不重置流！）
            if (attempt > 0 && destination.CanSeek)
            {
                currentRangeStart = destination.Position; // 当前流末尾 = 下次 Range 起点
                alreadyDownloaded = currentRangeStart;
                if (currentRangeStart > 0)
                    _logger.LogInformation("📌 流中断，续传从 {Offset}MB 处继续", currentRangeStart / 1024.0 / 1024.0);
                else
                    _logger.LogInformation("📌 流中断，文件尚为空，从头下载");
            }

            var response = await GetDownloadResponseAsync(currentRangeStart);

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

            // 判断响应类型：206 Partial Content（Range 成功）vs 200 OK（服务器不支持 Range 或从头传）
            bool isRangeResponse = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            long totalFileSize; // 整个文件的总大小
            long remainingToDownload; // 本次要下载的字节数

            if (isRangeResponse)
            {
                // 206: Content-Range: bytes X-Y/Z，Content-Length = 剩余字节
                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.HasRange == true && contentRange.From == currentRangeStart)
                {
                    totalFileSize = contentRange.Length ?? (currentRangeStart + (response.Content.Headers.ContentLength ?? 0));
                    remainingToDownload = response.Content.Headers.ContentLength ?? 0;
                    _logger.LogInformation("✅ 206 Range 响应: bytes {From}-{To}/{Total}",
                        contentRange.From, contentRange.To, totalFileSize);
                }
                else
                {
                    // Range 响应异常：可能服务器返回了不匹配的 Range，保险起见按 200 处理
                    _logger.LogWarning("⚠️ 206 响应 Range 不匹配，按全新下载处理");
                    response.Dispose();
                    goto fallbackFullDownload;
                }
            }
            else
            {
                // 200 OK：服务器不支持 Range 续传，或返回了完整文件
                if (currentRangeStart > 0)
                {
                    // 服务器不支持 Range：不丢弃已有的 GB 级数据，先尝试转为完整下载
                    _logger.LogWarning("⚠️ 请求 Range 续传但服务器返回 200（不支持 Range），转为完整下载（保留已有数据待确认）");
                    response.Dispose();

                    // 重新请求不带 Range 头的完整下载
                    response = await GetDownloadResponseAsync(0);
                    if (response == null)
                    {
                        // 完整下载也失败 → 保留已有部分数据，让外层重试继续处理
                        var detail = string.Join("; ", failures);
                        _logger.LogError("Range 续传失败（服务器不支持），转为完整下载也全部失败: {Detail}", detail);
                        throw new InvalidOperationException(
                            $"断点续传失败: 服务器不支持 HTTP Range，且转为完整下载后三个下载源也全部失败。" +
                            $"详情: {detail}");
                    }

                    // 完整下载请求成功 → 安全截断旧数据，从头写入
                    _logger.LogInformation("✅ 获取完整下载响应，截断旧数据重新写入");
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

            var displaySize = totalFileSize > 0 ? ByteFormatter.Format(totalFileSize) : "未知";
            if (attempt == 0)
            {
                var resumeNote = alreadyDownloaded > 0 ? $" (已续传 {ByteFormatter.Format(alreadyDownloaded)})" : "";
                _logger.LogInformation("正在下载: {Owner}/{Repo} ({Branch}), 总大小={Size}{Resume}",
                    owner, repo, branch, displaySize, resumeNote);
            }
            else
            {
                _logger.LogWarning("第 {Attempt}/{Max} 次重试: {Owner}/{Repo}, 总大小={Size}",
                    attempt + 1, totalAttempts, owner, repo, displaySize);
            }

            // 立即报告初始进度
            progressCallback?.Invoke(alreadyDownloaded, totalFileSize, attempt, totalAttempts);

            try
            {
                // 流式下载：直接从 HTTP 响应体分块读取。
                // HttpClientHandler 已配置 AutomaticDecompression=None + AllowAutoRedirect=true，
                // 重定向后的响应体为原始 ZIP 字节流，不触发 .NET 10 的 DeflateStream 兼容问题。
                using var netStream = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[81920]; // 80KB 分块
                long newlyDownloaded = 0; // 本轮新下载的字节
                int bytesRead;

                while ((bytesRead = await netStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await destination.WriteAsync(buffer, 0, bytesRead);
                    newlyDownloaded += bytesRead;
                    // 流重试后数据恢复传输 → 流重试计数器重置为 0
                    progressCallback?.Invoke(alreadyDownloaded + newlyDownloaded, totalFileSize, 0, totalAttempts);
                }

                await destination.FlushAsync();

                long totalWritten = alreadyDownloaded + newlyDownloaded;

                // 完整性校验
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

                _logger.LogInformation("下载完成，总大小: {Size}MB", totalWritten / 1024.0 / 1024.0);
                return totalWritten;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException
                                         && attempt < totalAttempts - 1)
            {
                var delayMs = (attempt + 1) * 3000; // 递增等待: 3s, 6s
                var partialBytes = destination.CanSeek ? destination.Position : 0;
                _logger.LogWarning(ex,
                    "下载流中断 [第{Attempt}次, 共{Max}次]: {Owner}/{Repo}, 已下载 {Partial}MB。" +
                    "等待 {Delay}s 后从断点续传...",
                    attempt + 1, totalAttempts, owner, repo,
                    partialBytes / 1024.0 / 1024.0, delayMs / 1000);
                await Task.Delay(delayMs);
            }
            finally
            {
                // 确保 response 在任何异常路径（包括最后一次不满足 when 条件的尝试）都能被释放
                try { response?.Dispose(); } catch { }
            }
        }

    fallbackFullDownload:
        // 仅由 goto 跳转到达（206 响应的 Range 头不匹配时，见上方"⚠️ 206 响应 Range 不匹配"分支）
        throw new InvalidOperationException(
            $"下载失败：GitHub 返回 206 Partial Content 但 Content-Range 头与请求不匹配。" +
            $"请重试备份。");
    }

    /// <summary>
    /// 通过 GitHub API 获取单个仓库元数据（含仓库大小，用于下载进度预估）。
    /// </summary>
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

        _logger.LogInformation("GitHub API 仓库信息: {Owner}/{Repo} size={Size}KB, branch={Branch}",
            owner, repo, size, defaultBranch);
        return (size, defaultBranch);
    }

    /// <summary>
    /// 尝试下载并返回失败原因。成功返回 (response, null)，失败返回 (null, 原因描述)。
    /// </summary>
    /// <param name="rangeStart">
    /// 断点续传起始字节。&gt;0 时发送 Range: bytes={rangeStart}- 请求头。
    /// 服务器支持则返回 206 Partial Content，不支持则返回 200 OK（完整文件）。
    /// </param>
    private async Task<(HttpResponseMessage? Response, string? FailReason)> TryDownloadWithReasonAsync(
        string url, string? githubToken, long rangeStart = 0, int redirectDepth = 0)
    {
        _logger.LogDebug("尝试下载: {Url} (rangeStart={RangeStart})", url, rangeStart);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BaiduBackup", "1.0"));

        if (rangeStart > 0)
        {
            // 断点续传：模仿浏览器的 Range 请求方式
            request.Headers.Range = new RangeHeaderValue(rangeStart, null);
        }

        if (!string.IsNullOrEmpty(githubToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        // 2xx 成功（200 完整 / 206 部分内容）
        if (response.IsSuccessStatusCode)
            return (response, null);

        // 302 重定向 → 跟随（递归时保持 rangeStart 不变）
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
                _logger.LogInformation("跟随重定向到: {Url} (range={Range}, depth={Depth})", redirectUrl, rangeStart, redirectDepth + 1);
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
        _logger.LogWarning("下载失败 [{Reason}]: {Url}", failReason, url);
        return (null, failReason);
    }

}
