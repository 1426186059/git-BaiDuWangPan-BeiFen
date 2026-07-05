using System.Text.Json.Serialization;

namespace BaiduBackup.Models;

/// <summary>
/// AOT 所需的 JSON 源生成上下文，覆盖项目中所有手动序列化/反序列化的类型。
/// 注意：Minimal API 返回值（ApiResponse&lt;T&gt;）必须全部注册，否则 AOT 裁剪后序列化失败。
/// </summary>
[JsonSerializable(typeof(AuthRequest))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(SingleBackupRequest))]
[JsonSerializable(typeof(BatchBackupRequest))]
[JsonSerializable(typeof(BackupRepoItem))]
[JsonSerializable(typeof(List<BackupRepoItem>))]
[JsonSerializable(typeof(BackupResult))]
[JsonSerializable(typeof(BatchBackupResult))]
[JsonSerializable(typeof(BackupRecordFile))]
[JsonSerializable(typeof(BackupRecordView))]
[JsonSerializable(typeof(SetTotalRequest))]
[JsonSerializable(typeof(BackupProgressInfo))]
[JsonSerializable(typeof(GitHubRepoInfo))]
[JsonSerializable(typeof(List<GitHubRepoInfo>))]
[JsonSerializable(typeof(Dictionary<string, DateTime>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(RenameItem[]))]
[JsonSerializable(typeof(DiagnosticsResult))]
[JsonSerializable(typeof(DiagnosticsTargetResult))]
[JsonSerializable(typeof(TempDirInfo))]
// Minimal API 返回值类型（AOT 必需）
[JsonSerializable(typeof(ApiResponse<TokenResponse>))]
[JsonSerializable(typeof(ApiResponse<BackupResult>))]
[JsonSerializable(typeof(ApiResponse<BatchBackupResult>))]
[JsonSerializable(typeof(ApiResponse<List<GitHubRepoInfo>>))]
[JsonSerializable(typeof(ApiResponse<BackupRecordView>))]
[JsonSerializable(typeof(ApiResponse<bool>))]
[JsonSerializable(typeof(ApiResponse<BackupProgressInfo>))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
