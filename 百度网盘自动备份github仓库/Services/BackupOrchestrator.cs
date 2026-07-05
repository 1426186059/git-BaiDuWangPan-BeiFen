using System.Collections.Concurrent;
using System.Text.Json;
using BaiduBackup.Models;

namespace BaiduBackup.Services;

/// <summary>
/// 备份编排服务（单例），包含原 BackupController 的全部业务逻辑。
/// </summary>
public class BackupOrchestrator
{
    private readonly GitHubService _gitHubService;
    private readonly BaiduNetdiskService _baiduService;

    private static readonly string _recordFile = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "backup-records.json");
    private static readonly string _tempDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "temp");

    /// <summary>进度追踪器: key="owner/repo/branch" → 进度信息</summary>
    private static readonly ConcurrentDictionary<string, BackupProgressInfo> _progress =
        new(StringComparer.OrdinalIgnoreCase);

    public BackupOrchestrator(GitHubService gitHubService, BaiduNetdiskService baiduService)
    {
        _gitHubService = gitHubService;
        _baiduService = baiduService;
        Directory.CreateDirectory(_tempDir);
    }

    // ==================== 备份记录管理 ====================

    private static BackupRecordFile LoadRecords()
    {
        try
        {
            if (File.Exists(_recordFile))
            {
                var json = File.ReadAllText(_recordFile);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.BackupRecordFile) ?? new();
            }
        }
        catch { }
        return new BackupRecordFile();
    }

    private static void SaveRecords(BackupRecordFile records)
    {
        try
        {
            var dir = Path.GetDirectoryName(_recordFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(records, AppJsonContext.Default.BackupRecordFile);
            File.WriteAllText(_recordFile, json);
        }
        catch { }
    }

    private void MarkRepoBackedUp(string owner, string repo, string branch, long fileSize)
    {
        var records = LoadRecords();
        var key = $"{owner}/{repo}/{branch}";
        records.BackedUp[key] = DateTime.UtcNow;
        records.FileSizes[key] = fileSize;
        SaveRecords(records);
    }

    private void ClearAllComplete()
    {
        var records = LoadRecords();
        if (records.AllComplete)
        {
            records.AllComplete = false;
            records.AllCompleteTime = null;
            SaveRecords(records);
        }
    }

    private void CheckAllComplete()
    {
        var records = LoadRecords();
        // 不再短路：每次都会重新评估，允许「再次备份」后重新判定
        if (records.TotalRepoCount > 0 && records.BackedUp.Count >= records.TotalRepoCount && !records.AllComplete)
        {
            records.AllComplete = true;
            records.AllCompleteTime = DateTime.UtcNow;
            SaveRecords(records);
        }
    }

    public BackupRecordView GetRecords()
    {
        var records = LoadRecords();
        return new BackupRecordView
        {
            BackedUp = records.BackedUp,
            FileSizes = records.FileSizes,
            AllComplete = records.AllComplete,
            AllCompleteTime = records.AllCompleteTime,
            TotalRepoCount = records.TotalRepoCount
        };
    }

    public void SetTotalRepoCount(int totalCount)
    {
        var records = LoadRecords();
        // 总仓库数变更时（如切换 GitHub 账号），清除全部完成标记，让后续 CheckAllComplete 基于新数据重新判定
        records.TotalRepoCount = totalCount;
        records.AllComplete = false;
        records.AllCompleteTime = null;
        SaveRecords(records);
        CheckAllComplete();
    }

    public void ResetRecords()
    {
        SaveRecords(new BackupRecordFile());
    }

    // ==================== 进度查询 ====================

    public BackupProgressInfo GetProgress(string owner, string repo, string branch = "main")
    {
        var key = $"{owner}/{repo}/{branch}";
        if (_progress.TryGetValue(key, out var info))
            return info;

        return new BackupProgressInfo
        {
            FileName = $"{repo}.zip",
            Stage = "idle",
            Message = "等待开始..."
        };
    }

    // ==================== 单个备份 ====================

    public async Task<BackupResult> BackupSingleAsync(SingleBackupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Owner) ||
            string.IsNullOrWhiteSpace(request.Repo) ||
            string.IsNullOrWhiteSpace(request.AccessToken))
        {
            return new BackupResult
            {
                Owner = request.Owner, Repo = request.Repo, Branch = request.Branch,
                Success = false, Message = "缺少必填参数"
            };
        }

        var key = $"{request.Owner}/{request.Repo}/{request.Branch}";
        var zipFileName = $"{request.Repo}.zip";

        // 新备份开始 → 清除"全部完成"标记，后续 CheckAllComplete 会基于最新数据重新判定
        ClearAllComplete();

        var progress = new BackupProgressInfo
        {
            Stage = "downloading",
            FileName = zipFileName,
            Message = "正在从 GitHub 下载 ZIP..."
        };
        _progress[key] = progress;

        string? tempFile = null;
        bool success = false;
        try
        {
            // 1. 检查网盘上是否已有该文件
            var targetPath = request.UploadPath.TrimEnd('/') + "/" + zipFileName;
            if (await _baiduService.FileExistsAsync(request.AccessToken, targetPath))
            {
                success = true;
                progress.Stage = "done";
                progress.IsCompleted = true;
                progress.Success = true;
                progress.UploadPercent = 100;
                progress.Message = $"✅ 已存在，跳过: {zipFileName}";
                MarkRepoBackedUp(request.Owner, request.Repo, request.Branch, 0);
                CheckAllComplete();
                return new BackupResult
                {
                    Owner = request.Owner, Repo = request.Repo, Branch = request.Branch,
                    Success = true, Message = "文件已存在，已跳过", FilePath = targetPath
                };
            }

            // 2. 下载
            progress.Message = $"📥 下载中... {zipFileName}";
            var lastReport = DateTime.UtcNow;
            var (downloadedFile, actualFileSize) = await DownloadRepoWithRetryAsync(
                request.Owner, request.Repo, request.Branch, request.GitHubToken,
                progressCallback: (downloaded, total, round, attempt, maxAttempts, streamAttempt, streamTotal, maxRestarts) =>
                {
                    progress.TotalBytes = total;
                    progress.DownloadedBytes = downloaded;
                    progress.DownloadPercent = total > 0
                        ? (int)(downloaded * 100 / total) : 0;
                    progress.DownloadRestartRound = round - 1;
                    progress.DownloadRestartMaxRounds = maxRestarts;
                    progress.DownloadAttempt = attempt - 1;
                    progress.DownloadMaxAttempts = maxAttempts;
                    progress.DownloadStreamAttempt = streamAttempt;
                    progress.DownloadStreamMaxAttempts = streamTotal;

                    var now = DateTime.UtcNow;
                    if ((now - lastReport).TotalMilliseconds >= 200)
                    {
                        var info = $"重新下载次数: {round - 1}/{maxRestarts}，流重试次数：{streamAttempt}/{streamTotal}，Range续传：{attempt - 1}/{maxAttempts}";
                        progress.Message = $"📥 下载中: {zipFileName} ({ByteFormatter.Format(downloaded)}" +
                            (total > 0 ? $" / {ByteFormatter.Format(total)})" : ")") + "\n" + info;
                        lastReport = now;
                    }
                });
            tempFile = downloadedFile;

            progress.DownloadPercent = 100;
            progress.DownloadedBytes = actualFileSize;
            progress.TotalBytes = actualFileSize;
            progress.Message = $"📥 下载完成: {zipFileName} ({ByteFormatter.Format(actualFileSize)})";

            // 3. 上传
            progress.Stage = "uploading";
            progress.Message = $"📤 上传中... {zipFileName} (0%)";
            await using var readStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read);
            var resultPath = await _baiduService.UploadFileAsync(
                request.AccessToken, readStream, request.UploadPath, zipFileName,
                (chunksDone, chunksTotal, retryCount, maxRetries) =>
                {
                    progress.UploadChunksDone = chunksDone;
                    progress.UploadChunksTotal = chunksTotal;
                    progress.UploadPercent = chunksTotal > 0
                        ? (int)(chunksDone * 100L / chunksTotal) : 0;
                    progress.RetryCount = retryCount;
                    progress.MaxRetries = maxRetries;
                    var upRetryHint = retryCount > 0 ? $"\n上传重试次数: {retryCount}/{maxRetries}" : "";
                    progress.Message = $"📤 上传中: {zipFileName} ({progress.UploadPercent}%, 分片 {chunksDone}/{chunksTotal}){upRetryHint}";
                });

            success = true;
            progress.Stage = "done";
            progress.IsCompleted = true;
            progress.Success = true;
            progress.UploadPercent = 100;
            progress.Message = $"✅ 完成: {zipFileName} → {resultPath}";

            MarkRepoBackedUp(request.Owner, request.Repo, request.Branch, actualFileSize);
            CheckAllComplete();

            return new BackupResult
            {
                Owner = request.Owner, Repo = request.Repo, Branch = request.Branch,
                Success = true, Message = "备份成功", FilePath = resultPath, FileSize = actualFileSize
            };
        }
        catch (Exception ex)
        {
            var fullError = ex.Message;
            var inner = ex.InnerException;
            while (inner != null) { fullError += $" → {inner.Message}"; inner = inner.InnerException; }

            progress.Stage = "failed";
            progress.IsCompleted = true;
            progress.Success = false;
            progress.ErrorMessage = fullError;
            progress.Message = $"❌ 失败: {fullError}";

            return new BackupResult
            {
                Owner = request.Owner, Repo = request.Repo, Branch = request.Branch,
                Success = false, Message = fullError
            };
        }
        finally
        {
            if (success && tempFile != null)
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                catch { }
            }
        }
    }

    // ==================== 下载重试逻辑 ====================

    private async Task<(string TempFile, long FileSize)> DownloadRepoWithRetryAsync(
        string owner, string repo, string branch, string? gitHubToken,
        Action<long, long, int, int, int, int, int, int>? progressCallback = null)
    {
        var tempFile = Path.Combine(_tempDir, $"{repo}.zip");
        const int MaxRestartCycles = 3;
        const int AttemptsPerCycle = 5;
        const int ContinueDelayMs = 5000;

        long estimatedSize = 0;
        try
        {
            var (sizeKb, _) = await _gitHubService.GetRepoInfoAsync(owner, repo, gitHubToken);
            estimatedSize = sizeKb * 1024L;
        }
        catch { }

        var progKey = $"{owner}/{repo}/{branch}";
        for (int restartCycle = 1; restartCycle <= MaxRestartCycles; restartCycle++)
        {
            for (int attempt = 1; attempt <= AttemptsPerCycle; attempt++)
            {
                if (_progress.TryGetValue(progKey, out var retryProg))
                {
                    retryProg.RetryCount = attempt;
                    retryProg.MaxRetries = AttemptsPerCycle;
                }

                try
                {
                    bool isFullRestart = (attempt == AttemptsPerCycle);
                    long resumeFromBytes = 0;
                    FileMode fileMode;

                    if (isFullRestart)
                    {
                        try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                        catch { }
                        fileMode = FileMode.Create;
                    }
                    else if (File.Exists(tempFile))
                    {
                        var existingInfo = new FileInfo(tempFile);
                        if (existingInfo.Length > 0)
                        {
                            resumeFromBytes = existingInfo.Length;
                            fileMode = FileMode.Open;
                        }
                        else
                        {
                            fileMode = FileMode.Create;
                        }
                    }
                    else
                    {
                        fileMode = FileMode.Create;
                    }

                    long actualDownloadSize;
                    await using (var fs = new FileStream(tempFile, fileMode, FileAccess.Write))
                    {
                        actualDownloadSize = await _gitHubService.DownloadRepositoryAsync(
                            owner, repo, branch, gitHubToken, fs, estimatedSize,
                            progressCallback != null
                                ? (downloaded, total, streamAttempt, streamTotal) =>
                                {
                                    // streamAttempt=-1 → 流恢复，Range续传和流重试均置 0
                                    int outAttempt = streamAttempt < 0 ? 1 : attempt;
                                    int outStream   = streamAttempt < 0 ? 0 : streamAttempt;
                                    progressCallback(downloaded, total,
                                        restartCycle, outAttempt, AttemptsPerCycle - 1,
                                        outStream, streamTotal, MaxRestartCycles);
                                }
                                : null,
                            resumeFromBytes: resumeFromBytes);
                    }

                    var dlFileInfo = new FileInfo(tempFile);
                    if (dlFileInfo.Length == 0)
                        throw new InvalidOperationException(
                            $"下载失败: 文件大小为 0 ({repo}.zip)。可能仓库内容为空或下载链接失效。");
                    if (actualDownloadSize > 0 && dlFileInfo.Length < actualDownloadSize)
                        throw new InvalidOperationException(
                            $"文件写入不完整: 期望 {actualDownloadSize} 字节，磁盘仅有 {dlFileInfo.Length} 字节。");

                    return (tempFile, dlFileInfo.Length);
                }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    if (msg.Contains("无可用内容") || msg.Contains("仓库或分支不存在") || msg.Contains("无权访问"))
                    {
                        try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                        catch { }
                        throw;
                    }

                    if (attempt < AttemptsPerCycle)
                        await Task.Delay(ContinueDelayMs);
                    else
                    {
                        try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                        catch { }
                        if (restartCycle < MaxRestartCycles)
                            await Task.Delay(ContinueDelayMs);
                    }
                }
            }
        }

        throw new InvalidOperationException(
            $"{owner}/{repo} 已重新开始 {MaxRestartCycles} 轮（共 {MaxRestartCycles * AttemptsPerCycle} 次）全部失败，已跳过");
    }

    // ==================== 批量备份 ====================

    public async Task<BatchBackupResult> BackupBatchAsync(BatchBackupRequest request)
    {
        if (request.Repos == null || request.Repos.Count == 0)
            throw new ArgumentException("仓库列表不能为空");
        if (string.IsNullOrWhiteSpace(request.AccessToken))
            throw new ArgumentException("缺少百度网盘 access_token");

        var result = new BatchBackupResult();
        var total = request.Repos.Count;

        for (int i = 0; i < total; i++)
        {
            var repo = request.Repos[i];
            var singleRequest = new SingleBackupRequest
            {
                Owner = repo.Owner, Repo = repo.Repo, Branch = repo.Branch,
                AccessToken = request.AccessToken, GitHubToken = request.GitHubToken,
                UploadPath = request.UploadPath
            };

            var backupResult = await BackupSingleAsync(singleRequest);

            if (backupResult.Success)
                result.SuccessCount++;
            else
                result.FailCount++;
            result.Results.Add(backupResult);

            if (!backupResult.Success) break;
        }

        return result;
    }
}
