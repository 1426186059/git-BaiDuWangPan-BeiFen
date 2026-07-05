namespace BaiduBackup.Models;

/// <summary>网络诊断：单个目标检测结果</summary>
internal class DiagnosticsTargetResult
{
    public string Target { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Ok { get; set; }
    public int StatusCode { get; set; }
    public long LatencyMs { get; set; }
    public string? Error { get; set; }
    public string? ErrorType { get; set; }
    public IEnumerable<string>? StackTrace { get; set; }
}

/// <summary>网络诊断：总体结果</summary>
internal class DiagnosticsResult
{
    public string? SystemProxy { get; set; }
    public string MachineName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public List<DiagnosticsTargetResult> Results { get; set; } = new();
}
