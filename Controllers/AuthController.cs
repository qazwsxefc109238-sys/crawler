using Crawler_project.Models;
using Crawler_project.Services;
using RegisterRequestModel = Crawler_project.Models.RegisterRequest;
using LoginRequestModel = Crawler_project.Models.LoginRequest;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
namespace Crawler_project.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly UserAuthService _authService;
    private readonly IEmailSender _emailSender;
    private readonly EmailOptions _emailOptions;

    public AuthController(
        UserAuthService authService,
        IEmailSender emailSender,
        IOptions<EmailOptions> emailOptions)
    {
        _authService = authService;
        _emailSender = emailSender;
        _emailOptions = emailOptions.Value;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequestModel request, CancellationToken ct)
    {
        try
        {
            var response = await _authService.RegisterAsync(request, ct);

            var confirmation = await _authService.ResendConfirmationAsync(request.Email, ct);
            if (confirmation is not null)
            {
                var link =
                    $"{_emailOptions.ConfirmationBaseUrl}?email={Uri.EscapeDataString(confirmation.Value.Email)}&token={Uri.EscapeDataString(confirmation.Value.Token)}";

                await _emailSender.SendAsync(
                    confirmation.Value.Email,
                    "Подтверждение регистрации",
                    $"<p>Подтвердите почту:</p><p><a href=\"{link}\">{link}</a></p>",
                    ct);
            }

            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequestModel request, CancellationToken ct)
    {
        var response = await _authService.LoginAsync(
            request,
            Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct);

        if (response is null)
        {
            return Unauthorized(new { error = "Invalid email, password, or email is not confirmed." });
        }

        return Ok(response);
    }
    [HttpPost("confirm-email")]
    [AllowAnonymous]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest request, CancellationToken ct)
    {
        var ok = await _authService.ConfirmEmailAsync(request.Email, request.Token, ct);
        if (!ok)
        {
            return BadRequest(new { error = "Token is invalid or expired." });
        }

        return Ok(new { message = "Email confirmed." });
    }
    [HttpPost("resend-confirmation")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendConfirmationRequest request, CancellationToken ct)
    {
        var result = await _authService.ResendConfirmationAsync(request.Email, ct);
        if (result is null)
        {
            return BadRequest(new { error = "User not found or email already confirmed." });
        }

        var link =
            $"{_emailOptions.ConfirmationBaseUrl}?email={Uri.EscapeDataString(result.Value.Email)}&token={Uri.EscapeDataString(result.Value.Token)}";

        await _emailSender.SendAsync(
            result.Value.Email,
            "Подтверждение регистрации",
            $"<p>Подтвердите почту:</p><p><a href=\"{link}\">{link}</a></p>",
            ct);

        return Ok(new { message = "Confirmation email sent." });
    }

    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = SessionAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var sessionToken = ExtractSessionToken();
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return Unauthorized(new { error = "Missing session token." });
        }

        var user = await _authService.GetUserBySessionAsync(sessionToken, ct);
        if (user is null)
        {
            return Unauthorized(new { error = "Session is invalid or expired." });
        }

        return Ok(user);
    }

    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = SessionAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> Logout([FromBody] CloseSessionRequest? request, CancellationToken ct)
    {
        var sessionToken = request?.SessionToken;
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            sessionToken = ExtractSessionToken();
        }

        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return BadRequest(new { error = "Missing session token." });
        }

        await _authService.CloseSessionAsync(sessionToken, ct);
        return Ok(new { ok = true });
    }

    [HttpPost("session/close")]
    public async Task<IActionResult> CloseOnPageExit([FromBody] CloseSessionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SessionToken))
        {
            return BadRequest(new { error = "Missing session token." });
        }

        await _authService.CloseSessionAsync(request.SessionToken, ct);
        return Ok(new { ok = true });
    }

    private string? ExtractSessionToken()
    {
        var fromHeader = Request.Headers["X-Session-Token"].ToString();
        if (!string.IsNullOrWhiteSpace(fromHeader))
        {
            return fromHeader.Trim();
        }

        var authHeader = Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader[7..].Trim();
        }

        return null;
    }
}
