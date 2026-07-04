using Microsoft.AspNetCore.Mvc;
using BaiduBackup.Models;
using BaiduBackup.Services;

namespace BaiduBackup.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GitHubController : ControllerBase
{
    private readonly GitHubService _gitHubService;

    public GitHubController(GitHubService gitHubService)
    {
        _gitHubService = gitHubService;
    }

    [HttpGet("repos/{username}")]
    public async Task<ActionResult<ApiResponse<List<GitHubRepoInfo>>>> GetRepositories(
        string username, [FromQuery] string? token)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return BadRequest(ApiResponse<List<GitHubRepoInfo>>.Fail("用户名不能为空"));
        }

        try
        {
            var repos = await _gitHubService.GetRepositoriesAsync(username, token);
            return Ok(ApiResponse<List<GitHubRepoInfo>>.Ok(repos));
        }
        catch (Exception ex)
        {
            var fullError = ex.Message;
            var inner = ex.InnerException;
            while (inner != null)
            {
                fullError += $" → {inner.Message}";
                inner = inner.InnerException;
            }
            return BadRequest(ApiResponse<List<GitHubRepoInfo>>.Fail(fullError));
        }
    }
}
