namespace BaiduBackup.Models;

/// <summary>百度网盘重命名操作的 payload 项</summary>
internal class RenameItem
{
    public string path { get; set; } = "";
    public string newname { get; set; } = "";
}
