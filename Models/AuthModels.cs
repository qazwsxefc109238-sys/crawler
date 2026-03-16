namespace Crawler_project.Models;

public sealed record RegisterRequest(string Email, string Password, string? DisplayName);

public sealed record LoginRequest(string Email, string Password);

public sealed record CloseSessionRequest(string? SessionToken);

public sealed record ConfirmEmailRequest(string Email, string Token);

public sealed record ResendConfirmationRequest(string Email);

public sealed record RegisterResponse(
    Guid UserId,
    string Email,
    bool RequiresEmailConfirmation,
    DateTimeOffset ConfirmationExpiresAt);

public sealed record AuthUserDto(Guid Id, string Email, string? DisplayName, string Role);

public sealed record AuthResponse(string SessionToken, AuthUserDto User, DateTimeOffset ExpiresAt);