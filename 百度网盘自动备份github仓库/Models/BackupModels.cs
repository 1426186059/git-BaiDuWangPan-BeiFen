namespace BaiduBackup.Models;

public class SingleBackupRequest
{
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string AccessToken { get; set; } = "";
    public string? GitHubToken { get; set; }
    public string UploadPath { get; set; } = "/apps/xuke/github仓库备份/";
}

public class BatchBackupRequest
{
    public List<BackupRepoItem> Repos { get; set; } = new();
    public string AccessToken { get; set; } = "";
    public string? GitHubToken { get; set; }
    public string UploadPath { get; set; } = "/apps/xuke/github仓库备份/";
}

public class BackupRepoItem
{
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "main";
}

/// <summary>单仓库备份状态</summary>
public enum RepoBackupStatus
{
    Success,
    Skipped,
    Failed
}

public class BackupResult
{
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? FilePath { get; set; }
    public long FileSize { get; set; }
}

public class BatchBackupResult
{
    public List<BackupResult> Results { get; set; } = new();
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    /// <summary>跳过的仓库数（无内容等非致命原因）</summary>
    public int SkippedCount { get; set; }
}

/// <summary>
/// 备份记录文件
/// </summary>
public class BackupRecordFile
{
    /// <summary> key: "owner/repo/branch" → 备份时间 </summary>
    public Dictionary<string, DateTime> BackedUp { get; set; } = new();
    /// <summary> key: "owner/repo/branch" → 下载文件大小(字节)，用于完整性校验 </summary>
    public Dictionary<string, long> FileSizes { get; set; } = new();
    /// <summary> 是否全部备份完成 </summary>
    public bool AllComplete { get; set; }
    /// <summary> 全部完成时间 </summary>
    public DateTime? AllCompleteTime { get; set; }
    /// <summary> 总仓库数（用于判断是否全部完成） </summary>
    public int TotalRepoCount { get; set; }
}

/// <summary>
/// 前端用的备份记录视图
/// </summary>
public class BackupRecordView
{
    public Dictionary<string, DateTime> BackedUp { get; set; } = new();
    /// <summary> 下载文件大小(字节)，用于前端展示完整性校验 </summary>
    public Dictionary<string, long> FileSizes { get; set; } = new();
    public bool AllComplete { get; set; }
    public DateTime? AllCompleteTime { get; set; }
    public int TotalRepoCount { get; set; }
}

/// <summary>
/// 设置仓库总数请求
/// </summary>
public class SetTotalRequest
{
    public int TotalCount { get; set; }
}

/// <summary>
/// 实时备份进度（通过 ConcurrentDictionary 在服务端维护，前端轮询获取）
/// </summary>
public class BackupProgressInfo
{
    /// <summary>当前阶段: idle|downloading|md5|uploading|completing|done|failed</summary>
    public string Stage { get; set; } = "idle";
    /// <summary>Zip 文件名</summary>
    public string FileName { get; set; } = "";
    /// <summary>文件总字节数（下载/上传共用）</summary>
    public long TotalBytes { get; set; }
    /// <summary>已下载字节数</summary>
    public long DownloadedBytes { get; set; }
    /// <summary>下载进度百分比</summary>
    public int DownloadPercent { get; set; }
    /// <summary>上传总分片数</summary>
    public int UploadChunksTotal { get; set; }
    /// <summary>已上传分片数</summary>
    public int UploadChunksDone { get; set; }
    /// <summary>上传进度百分比</summary>
    public int UploadPercent { get; set; }
    /// <summary>当前描述信息</summary>
    public string Message { get; set; } = "";
    /// <summary>是否完成</summary>
    public bool IsCompleted { get; set; }
    /// <summary>是否成功</summary>
    public bool Success { get; set; }
    /// <summary>错误信息（失败时）</summary>
    public string? ErrorMessage { get; set; }
}
