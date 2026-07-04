using Microsoft.AspNetCore.Mvc;
using BaiduBackup.Models;
using BaiduBackup.Services;

namespace BaiduBackup.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly BaiduNetdiskService _baiduService;

    public AuthController(BaiduNetdiskService baiduService)
    {
        _baiduService = baiduService;
    }

    [HttpPost("baidu")]
    public async Task<ActionResult<ApiResponse<TokenResponse>>> ExchangeBaiduToken(
        [FromBody] AuthRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) ||
            string.IsNullOrWhiteSpace(request.ClientSecret) ||
            string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(ApiResponse<TokenResponse>.Fail("缺少必填参数"));
        }

        try
        {
            var token = await _baiduService.ExchangeCodeForTokenAsync(
                request.ClientId, request.ClientSecret, request.Code, request.RedirectUri);
            return Ok(ApiResponse<TokenResponse>.Ok(token));
        }
        catch (Exception ex)
        {
            var fullError = ex.Message;
            var inner = ex.InnerException;
            while (inner != null) { fullError += $" → {inner.Message}"; inner = inner.InnerException; }
            return BadRequest(ApiResponse<TokenResponse>.Fail(fullError));
        }
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<TokenResponse>>> RefreshToken(
        [FromBody] RefreshRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken) ||
            string.IsNullOrWhiteSpace(request.ClientId) ||
            string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            return BadRequest(ApiResponse<TokenResponse>.Fail("缺少必填参数"));
        }

        try
        {
            var token = await _baiduService.RefreshTokenAsync(
                request.RefreshToken, request.ClientId, request.ClientSecret);
            return Ok(ApiResponse<TokenResponse>.Ok(token));
        }
        catch (Exception ex)
        {
            var fullError = ex.Message;
            var inner = ex.InnerException;
            while (inner != null) { fullError += $" → {inner.Message}"; inner = inner.InnerException; }
            return BadRequest(ApiResponse<TokenResponse>.Fail(fullError));
        }
    }
}
