using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using BaiduBackup.Models;
using BaiduBackup.Services;

namespace BaiduBackup.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BackupController : ControllerBase
{
    private readonly GitHubService _gitHubService;
    private readonly BaiduNetdiskService _baiduService;
    private readonly ILogger<BackupController> _logger;
    private static readonly string _recordFile = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "backup-records.json");
    /// <summary>临时文件目录（应用自身目录下，重启不丢失）</summary>
    private static readonly string _tempDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "temp");
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>进度追踪器: key="owner/repo/branch" → 进度信息</summary>
    private static readonly ConcurrentDictionary<string, BackupProgressInfo> _progress =
        new(StringComparer.OrdinalIgnoreCase);

    public BackupController(
        GitHubService gitHubService,
        BaiduNetdiskService baiduService,
        ILogger<BackupController> logger)
    {
        _gitHubService = gitHubService;
        _baiduService = baiduService;
        _logger = logger;
        Directory.CreateDirectory(_tempDir); // 确保临时目录存在
    }

    // ==================== 备份记录管理 ====================

    /// <summary>读取备份记录文件</summary>
    private static BackupRecordFile LoadRecords()
    {
        try
        {
            if (System.IO.File.Exists(_recordFile))
            {
                var json = System.IO.File.ReadAllText(_recordFile);
                return JsonSerializer.Deserialize<BackupRecordFile>(json, _jsonOpts) ?? new();
            }
        }
        catch { }
        return new BackupRecordFile();
    }

    /// <summary>保存备份记录文件</summary>
    private static void SaveRecords(BackupRecordFile records)
    {
        try
        {
            var dir = Path.GetDirectoryName(_recordFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(records, _jsonOpts);
            System.IO.File.WriteAllText(_recordFile, json);
        }
        catch (Exception ex)
        {
            // 记录日志但不抛出，避免影响备份流程
            System.Diagnostics.Debug.WriteLine($"保存备份记录失败: {ex.Message}");
        }
    }

    /// <summary>标记单个仓库为已备份（含文件大小用于完整性校验）</summary>
    private void MarkRepoBackedUp(string owner, string repo, string branch, long fileSize)
    {
        var records = LoadRecords();
        var key = $"{owner}/{repo}/{branch}";
        records.BackedUp[key] = DateTime.UtcNow;
        records.FileSizes[key] = fileSize;
        SaveRecords(records);
    }

    /// <summary>检查是否全部备份完成</summary>
    private void CheckAllComplete()
    {
        var records = LoadRecords();
        if (records.AllComplete) return; // 已经是完成状态

        if (records.TotalRepoCount > 0 && records.BackedUp.Count >= records.TotalRepoCount)
        {
            records.AllComplete = true;
            records.AllCompleteTime = DateTime.UtcNow;
            SaveRecords(records);
            _logger.LogInformation("🎉 全部仓库备份完成！共 {Count} 个", records.TotalRepoCount);
        }
    }

    /// <summary>获取备份记录</summary>
    [HttpGet("records")]
    public IActionResult GetRecords()
    {
        var records = LoadRecords();
        return Ok(ApiResponse<BackupRecordView>.Ok(new BackupRecordView
        {
            BackedUp = records.BackedUp,
            FileSizes = records.FileSizes,
            AllComplete = records.AllComplete,
            AllCompleteTime = records.AllCompleteTime,
            TotalRepoCount = records.TotalRepoCount
        }));
    }

    /// <summary>设置仓库总数（在获取仓库列表后调用）</summary>
    [HttpPost("records/settotal")]
    public IActionResult SetTotalRepoCount([FromBody] SetTotalRequest req)
    {
        var records = LoadRecords();
        records.TotalRepoCount = req.TotalCount;
        SaveRecords(records);
        CheckAllComplete();
        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>重置备份记录</summary>
    [HttpPost("records/reset")]
    public IActionResult ResetRecords()
    {
        SaveRecords(new BackupRecordFile());
        _logger.LogInformation("备份记录已重置");
        return Ok(ApiResponse<bool>.Ok(true));
    }

    // ==================== 进度查询 ====================

    /// <summary>获取指定仓库的备份进度（前端轮询）</summary>
    [HttpGet("progress")]
    public IActionResult GetProgress([FromQuery] string owner, [FromQuery] string repo, [FromQuery] string branch = "main")
    {
        var key = $"{owner}/{repo}/{branch}";
        if (_progress.TryGetValue(key, out var info))
        {
            return Ok(ApiResponse<BackupProgressInfo>.Ok(info));
        }
        return Ok(ApiResponse<BackupProgressInfo>.Ok(new BackupProgressInfo
        {
            FileName = $"{repo}.zip",
            Stage = "idle",
            Message = "等待开始..."
        }));
    }

    // ==================== 备份 API ====================

    [HttpGet("tempdir")]
    public IActionResult GetTempDirectory()
    {
        return Ok(new { tempDir = _tempDir });
    }

    [HttpPost("single")]
    public async Task<ActionResult<ApiResponse<BackupResult>>> BackupSingle(
        [FromBody] SingleBackupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Owner) ||
            string.IsNullOrWhiteSpace(request.Repo) ||
            string.IsNullOrWhiteSpace(request.AccessToken))
        {
            return BadRequest(ApiResponse<BackupResult>.Fail("缺少必填参数"));
        }

        var key = $"{request.Owner}/{request.Repo}/{request.Branch}";
        var zipFileName = $"{request.Repo}.zip";
        var tempFile = Path.Combine(_tempDir, $"{request.Repo}.zip");

        // 初始化进度
        var progress = new BackupProgressInfo
        {
            Stage = "downloading",
            FileName = zipFileName,
            Message = "正在从 GitHub 下载 ZIP..."
        };
        _progress[key] = progress;

        bool success = false;
        try
        {
            _logger.LogInformation("=== 开始备份: {Owner}/{Repo} ({Branch}) ===",
                request.Owner, request.Repo, request.Branch);

            // 1. 检查网盘上是否已有该文件，有则跳过
            var targetPath = request.UploadPath.TrimEnd('/') + "/" + zipFileName;
            if (await _baiduService.FileExistsAsync(request.AccessToken, targetPath))
            {
                success = true;
                progress.Stage = "done";
                progress.IsCompleted = true;
                progress.Success = true;
                progress.UploadPercent = 100;
                progress.Message = $"✅ 已存在，跳过: {zipFileName}";
                _logger.LogInformation("网盘文件已存在，跳过备份: {Path}", targetPath);
                MarkRepoBackedUp(request.Owner, request.Repo, request.Branch, 0);
                CheckAllComplete();
                return Ok(ApiResponse<BackupResult>.Ok(new BackupResult
                {
                    Owner = request.Owner,
                    Repo = request.Repo,
                    Branch = request.Branch,
                    Success = true,
                    Message = "文件已存在，已跳过",
                    FilePath = targetPath
                }));
            }

            // 2. 从 GitHub 下载到临时文件（本地已存在则复用，跳过下载）
            if (!System.IO.File.Exists(tempFile))
            {
                // 先通过 GitHub API 获取仓库元数据，拿到大小预估
                long estimatedSize = 0;
                try
                {
                    var (sizeKb, _) = await _gitHubService.GetRepoInfoAsync(
                        request.Owner, request.Repo, request.GitHubToken);
                    estimatedSize = sizeKb * 1024L; // KB → 字节（粗略估算）
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "获取仓库元数据失败（不影响下载），使用 0 作为预估值");
                }

                progress.Message = $"📥 下载中... {zipFileName} (获取文件信息...)";

                var lastReport = DateTime.UtcNow;
                long actualFileSize;
                await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
                {
                    actualFileSize = await _gitHubService.DownloadRepositoryAsync(
                        request.Owner, request.Repo, request.Branch, request.GitHubToken,
                        fs, estimatedSize,
                        (downloaded, total) =>
                        {
                            progress.TotalBytes = total;
                            progress.DownloadedBytes = downloaded;
                            progress.DownloadPercent = total > 0
                                ? (int)(downloaded * 100 / total) : 0;

                            var now = DateTime.UtcNow;
                            if ((now - lastReport).TotalMilliseconds >= 200)
                            {
                                progress.Message = $"📥 下载中... {zipFileName} ({FormatBytes(downloaded)}" +
                                    (total > 0 ? $" / {FormatBytes(total)})" : ")");
                                lastReport = now;
                            }
                        });
                }

                // ✅ 完整性校验：文件不能为空
                if (actualFileSize == 0)
                {
                    throw new InvalidOperationException(
                        $"下载失败: 文件大小为 0 ({zipFileName})。可能仓库内容为空或下载链接失效。");
                }
                _logger.LogInformation("下载完整性校验通过: {File} = {Size}MB", zipFileName, actualFileSize / 1024.0 / 1024.0);
            }
            else
            {
                _logger.LogInformation("本地文件已存在，复用: {Temp}", tempFile);
                progress.Message = $"📥 复用本地文件: {zipFileName}";
            }

            var fileInfo = new FileInfo(tempFile);
            progress.DownloadedBytes = fileInfo.Length;
            progress.DownloadPercent = 100;
            progress.TotalBytes = fileInfo.Length;
            progress.Message = $"📥 下载完成: {zipFileName} ({FormatBytes(fileInfo.Length)})";
            _logger.LogInformation("下载完成, 临时文件: {Temp}, 大小: {Size}MB",
                tempFile, fileInfo.Length / 1024.0 / 1024.0);

            // 2. 上传到百度网盘（带进度）
            progress.Stage = "uploading";
            progress.Message = $"📤 上传中... {zipFileName} (0%)";
            await using var readStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read);
            var resultPath = await _baiduService.UploadFileAsync(
                request.AccessToken, readStream, request.UploadPath, zipFileName,
                (chunksDone, chunksTotal) =>
                {
                    progress.UploadChunksDone = chunksDone;
                    progress.UploadChunksTotal = chunksTotal;
                    progress.UploadPercent = chunksTotal > 0
                        ? (int)(chunksDone * 100L / chunksTotal) : 0;
                    progress.Message = $"📤 上传中... {zipFileName} ({progress.UploadPercent}%, 分片 {chunksDone}/{chunksTotal})";
                });

            // 完成
            success = true;
            progress.Stage = "done";
            progress.IsCompleted = true;
            progress.Success = true;
            progress.UploadPercent = 100;
            progress.Message = $"✅ 完成: {zipFileName} → {resultPath}";
            _logger.LogInformation("=== 备份完成: {Path} ===", resultPath);

            // 记录已备份（含文件大小，用于完整性校验）
            MarkRepoBackedUp(request.Owner, request.Repo, request.Branch, fileInfo.Length);
            CheckAllComplete();

            return Ok(ApiResponse<BackupResult>.Ok(new BackupResult
            {
                Owner = request.Owner,
                Repo = request.Repo,
                Branch = request.Branch,
                Success = true,
                Message = "备份成功",
                FilePath = resultPath,
                FileSize = fileInfo.Length
            }));
        }
        catch (Exception ex)
        {
            // 提取完整错误信息（包含 InnerException）
            var fullError = ex.Message;
            var inner = ex.InnerException;
            while (inner != null)
            {
                fullError += $" → {inner.Message}";
                inner = inner.InnerException;
            }
            _logger.LogError(ex, "备份失败: {Owner}/{Repo} | 完整错误: {FullError}",
                request.Owner, request.Repo, fullError);

            progress.Stage = "failed";
            progress.IsCompleted = true;
            progress.Success = false;
            progress.ErrorMessage = fullError;
            progress.Message = $"❌ 失败: {fullError}";

            return Ok(ApiResponse<BackupResult>.Ok(new BackupResult
            {
                Owner = request.Owner,
                Repo = request.Repo,
                Branch = request.Branch,
                Success = false,
                Message = fullError
            }));
        }
        finally
        {
            if (success)
            {
                // 上传成功才删除临时文件
                try { if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile); }
                catch (Exception ex) { _logger.LogWarning(ex, "清理临时文件失败: {Temp}", tempFile); }
            }
            else
            {
                _logger.LogInformation("上传失败，保留临时文件以便重试: {Temp}", tempFile);
            }
        }
    }

    /// <summary>带重试的单仓库备份</summary>
    private async Task<(BackupResult Result, RepoBackupStatus Status)> BackupSingleRepoWithRetryAsync(
        BackupRepoItem repo, BatchBackupRequest request, int maxRetries, int retryDelayMs)
    {
        var tempFile = Path.Combine(_tempDir, $"{repo.Repo}.zip");

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            bool repoSuccess = false;
            try
            {
                // 1. 从 GitHub 下载到临时文件（如果文件已存在则跳过下载）
                if (!System.IO.File.Exists(tempFile))
                {
                    _logger.LogInformation("第{Attempt}次尝试 - 下载: {Owner}/{Repo}", attempt, repo.Owner, repo.Repo);
                    long actualDownloadSize;
                    await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
                    {
                        actualDownloadSize = await _gitHubService.DownloadRepositoryAsync(
                            repo.Owner, repo.Repo, repo.Branch, request.GitHubToken, fs);
                    }

                    // 完整性校验：文件不能为空，且不能明显小于实际写入量
                    var dlFileInfo = new FileInfo(tempFile);
                    if (dlFileInfo.Length == 0)
                    {
                        throw new InvalidOperationException(
                            $"下载失败: 文件大小为 0 ({tempFile})。可能仓库内容为空或下载链接失效。");
                    }
                    if (actualDownloadSize > 0 && dlFileInfo.Length < actualDownloadSize)
                    {
                        throw new InvalidOperationException(
                            $"文件写入不完整: 期望 {actualDownloadSize} 字节，磁盘仅有 {dlFileInfo.Length} 字节。");
                    }
                    _logger.LogInformation("下载完整性校验通过: {File} = {Size}MB",
                        tempFile, dlFileInfo.Length / 1024.0 / 1024.0);
                }
                else
                {
                    _logger.LogInformation("第{Attempt}次尝试 - 复用已有文件: {Temp}", attempt, tempFile);
                }

                var fileInfo = new FileInfo(tempFile);
                _logger.LogInformation("下载完成, 临时文件: {Temp}, 大小: {Size}MB",
                    tempFile, fileInfo.Length / 1024.0 / 1024.0);

                // 2. 上传到百度网盘
                var zipFileName = $"{repo.Repo}.zip";
                await using var readStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read);
                var resultPath = await _baiduService.UploadFileAsync(
                    request.AccessToken, readStream, request.UploadPath, zipFileName);

                repoSuccess = true;
                var successResult = new BackupResult
                {
                    Owner = repo.Owner,
                    Repo = repo.Repo,
                    Branch = repo.Branch,
                    Success = true,
                    Message = attempt > 1 ? $"重试第{attempt}次成功" : "备份成功",
                    FilePath = resultPath,
                    FileSize = fileInfo.Length
                };
                return (successResult, RepoBackupStatus.Success);
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                // 可跳过的情况（空仓库/无内容/无访问权限）不重试
                var isSkip = msg.Contains("无可用内容") || msg.Contains("仓库或分支不存在") || msg.Contains("无权访问");
                if (isSkip)
                {
                    _logger.LogWarning("仓库备份跳过: {Owner}/{Repo}", repo.Owner, repo.Repo);
                    return (new BackupResult
                    {
                        Owner = repo.Owner,
                        Repo = repo.Repo,
                        Branch = repo.Branch,
                        Success = false,
                        Message = msg
                    }, RepoBackupStatus.Skipped);
                }

                _logger.LogWarning(ex, "第{Attempt}/{Max}次尝试失败: {Owner}/{Repo}",
                    attempt, maxRetries, repo.Owner, repo.Repo);

                if (attempt < maxRetries)
                {
                    // 失败后等待几秒再重试
                    _logger.LogInformation("等待 {Delay}秒 后重试...", retryDelayMs / 1000);
                    await Task.Delay(retryDelayMs);
                }
            }
            finally
            {
                if (repoSuccess)
                {
                    try { if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile); }
                    catch (Exception ex2) { _logger.LogWarning(ex2, "清理临时文件失败: {Temp}", tempFile); }
                }
            }
        }

        // 所有重试都失败
        _logger.LogError("重试{Max}次全部失败: {Owner}/{Repo}，保留文件: {Temp}",
            maxRetries, repo.Owner, repo.Repo, tempFile);
        return (new BackupResult
        {
            Owner = repo.Owner,
            Repo = repo.Repo,
            Branch = repo.Branch,
            Success = false,
            Message = $"重试{maxRetries}次均失败，已保留下载文件"
        }, RepoBackupStatus.Failed);
    }

    // 格式化字节数
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    [HttpPost("batch")]
    public async Task<ActionResult<ApiResponse<BatchBackupResult>>> BackupBatch(
        [FromBody] BatchBackupRequest request)
    {
        if (request.Repos == null || request.Repos.Count == 0)
            return BadRequest(ApiResponse<BatchBackupResult>.Fail("仓库列表不能为空"));
        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return BadRequest(ApiResponse<BatchBackupResult>.Fail("缺少百度网盘 access_token"));

        var result = new BatchBackupResult();
        var total = request.Repos.Count;

        const int MaxRetries = 3;
        const int RetryDelayMs = 30000; // 30秒，给百度服务端充足的同步时间

        _logger.LogInformation("========== 开始批量备份: {Count} 个仓库 ==========", total);

        for (int i = 0; i < total; i++)
        {
            var repo = request.Repos[i];
            _logger.LogInformation("[{Index}/{Total}] {Owner}/{Repo} ({Branch})",
                i + 1, total, repo.Owner, repo.Repo, repo.Branch);

            var backupResult = await BackupSingleRepoWithRetryAsync(
                repo, request, MaxRetries, RetryDelayMs);

            switch (backupResult.Status)
            {
                case RepoBackupStatus.Success:
                    result.SuccessCount++;
                    break;
                case RepoBackupStatus.Skipped:
                    result.SkippedCount++;
                    break;
                case RepoBackupStatus.Failed:
                    result.FailCount++;
                    break;
            }
            result.Results.Add(backupResult.Result);

            // 磁盘空间有限，仅真正失败（可能有残留临时文件）时终止。
            // 跳过（空仓库/无权限/无分支）不消耗磁盘，可以继续。
            if (backupResult.Status == RepoBackupStatus.Failed)
            {
                _logger.LogWarning("仓库 {Owner}/{Repo} 备份失败（临时文件可能已保留），终止后续 {Remaining} 个仓库以节省磁盘",
                    repo.Owner, repo.Repo, total - i - 1);
                break;
            }
        }

        _logger.LogInformation("========== 批量备份完成: 成功 {Ok} / 失败 {Fail} / 跳过 {Skip} ==========",
            result.SuccessCount, result.FailCount, result.SkippedCount);

        return Ok(ApiResponse<BatchBackupResult>.Ok(result));
    }
}
