using System.Text.Json;
using CodeMaster.Application.Dtos.Auth;
using CodeMaster.Application.Services.Auth;
using CodeMaster.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeMaster.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AccountController : Controller
{
    private readonly IPublicAccountService _accountService;
    private readonly IConfiguration _configuration;

    public AccountController(IPublicAccountService accountService, IConfiguration configuration)
    {
        _accountService = accountService;
        _configuration = configuration;
    }

    [AllowAnonymous]
    [HttpPost("send-register-code")]
    public async Task<IActionResult> SendRegisterCode([FromBody] SendEmailCodeDto dto)
    {
        try
        {
            await _accountService.SendRegisterCodeAsync(dto);
            return Ok(ApiResponse.Success("Verification code sent."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterWithEmailDto dto)
    {
        try
        {
            var result = await _accountService.RegisterWithEmailAsync(dto);
            // 注册即视为登录：写入与服务端渲染页面共用的 Cookie，
            // 否则 /community 等门户页仍按未登录渲染。
            WriteAuthCookie(result.AccessToken, result.ExpiresIn);
            return Ok(ApiResponse<LoginResultDto>.Success(result, "Registration successful."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<LoginResultDto>.Fail(ex.Message));
        }
    }

    [AllowAnonymous]
    [HttpGet("github/authorize")]
    public IActionResult GithubAuthorize()
    {
        try
        {
            var result = _accountService.GetGithubAuthorizeUrl();
            return Ok(ApiResponse<GithubAuthorizeUrlDto>.Success(result, "GitHub authorization URL created."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<GithubAuthorizeUrlDto>.Fail(ex.Message));
        }
    }

    [AllowAnonymous]
    [HttpGet("github/login")]
    public IActionResult GithubLogin()
    {
        try
        {
            return Redirect(_accountService.GetGithubAuthorizeUrl().AuthorizeUrl);
        }
        catch (Exception ex)
        {
            var safeMessage = global::System.Net.WebUtility.HtmlEncode(ex.Message);
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Content($"""
                <!doctype html>
                <html lang="zh-CN">
                <head><meta charset="utf-8"><title>GitHub OAuth not configured - CodeMaster</title></head>
                <body>
                  <p>GitHub OAuth 未配置：{safeMessage}</p>
                  <p>请配置 Authentication:GitHub:ClientId 和 Authentication:GitHub:ClientSecret 后重试。</p>
                  <p><a href="/register">返回注册页</a></p>
                </body>
                </html>
                """, "text/html; charset=utf-8");
        }
    }

    [AllowAnonymous]
    [HttpGet("github/callback")]
    public async Task<IActionResult> GithubCallback([FromQuery] GithubCallbackDto dto)
    {
        LoginResultDto result;
        try
        {
            result = await _accountService.SignInWithGithubAsync(dto);
        }
        catch (Exception ex)
        {
            var safeMessage = global::System.Net.WebUtility.HtmlEncode(ex.Message);
            return Content($"""
                <!doctype html>
                <html lang="zh-CN">
                <head><meta charset="utf-8"><title>GitHub login failed - CodeMaster</title></head>
                <body>
                  <p>GitHub login failed: {safeMessage}</p>
                  <p><a href="/register">Back to registration</a></p>
                </body>
                </html>
                """, "text/html; charset=utf-8");
        }

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // GitHub 登录同样要建立门户会话，否则 /community 仍显示未登录
        WriteAuthCookie(result.AccessToken, result.ExpiresIn);

        return Content($"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <title>GitHub login - CodeMaster</title>
            </head>
            <body>
              <script>
                const result = {json};
                localStorage.setItem('Admin-Token', result.accessToken);
                localStorage.setItem('access_token', result.accessToken);
                localStorage.setItem('token', result.accessToken);
                window.location.replace('{GetAdminLoginUrl("github=success")}');
              </script>
              <p>GitHub login succeeded. Redirecting...</p>
            </body>
            </html>
            """, "text/html; charset=utf-8");
    }

    private string GetAdminLoginUrl(string query)
    {
        var configuredUrl = _configuration["Portal:AdminLoginUrl"];
        var url = string.IsNullOrWhiteSpace(configuredUrl)
            ? "/index.html#/login"
            : configuredUrl.Trim();

        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}{query}";
    }

    /// <summary>
    /// 写入服务端渲染页面共用的认证 Cookie（与 AuthController 保持一致）。
    /// </summary>
    private void WriteAuthCookie(string token, int? expiresIn)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        Response.Cookies.Append("cm_token", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            Path = "/",
            Expires = expiresIn.HasValue && expiresIn.Value > 0
                ? DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value)
                : DateTimeOffset.UtcNow.AddHours(2)
        });
    }
}
