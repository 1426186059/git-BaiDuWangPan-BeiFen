namespace BaiduBackup.Models;

public class GitHubRepoInfo
{
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public bool PrivateRepo { get; set; }
    public string Description { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";
    public int Size { get; set; }
    public string? UpdatedAt { get; set; }
}
