namespace BaiduBackup.Models;

public class AuthRequest
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Code { get; set; } = "";
    public string RedirectUri { get; set; } = "";
}

public class RefreshRequest
{
    public string RefreshToken { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public class TokenResponse
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public long ExpiresIn { get; set; }
}
