using Crawler_project.Models;
using Npgsql;
using NpgsqlTypes;
using System.Net;
using System.Security.Cryptography;
using System.Net;
using NpgsqlTypes;
using System.Text;

namespace Crawler_project.Services;

public sealed class UserAuthService
{
    private const int Pbkdf2Iterations = 120_000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);
    private readonly string _connectionString;

    public UserAuthService(DatabaseOptions options)
    {
        _connectionString = options.ConnectionString;
    }

    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new ArgumentException("Email and password are required.");
        }

        var passwordHash = HashPassword(request.Password);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var tx = await connection.BeginTransactionAsync(ct);

        // 1. Проверяем, есть ли уже пользователь
        await using var lookup = new NpgsqlCommand(@"
SELECT id, email_confirmed
FROM public.users
WHERE email_normalized = @emailNormalized
LIMIT 1;", connection, tx);

        lookup.Parameters.AddWithValue("emailNormalized", email);

        Guid userId;
        bool emailConfirmed;

        await using (var reader = await lookup.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                userId = reader.GetGuid(0);
                emailConfirmed = reader.GetBoolean(1);
            }
            else
            {
                userId = Guid.Empty;
                emailConfirmed = false;
            }
        }

        if (userId == Guid.Empty)
        {
            // 2. Пользователя нет 
            await using var insertUser = new NpgsqlCommand(@"
INSERT INTO public.users (email, email_normalized, password_hash, display_name, email_confirmed)
VALUES (@email, @emailNormalized, @passwordHash, @displayName, false)
RETURNING id;", connection, tx);

            insertUser.Parameters.AddWithValue("email", request.Email.Trim());
            insertUser.Parameters.AddWithValue("emailNormalized", email);
            insertUser.Parameters.AddWithValue("passwordHash", passwordHash);
            insertUser.Parameters.AddWithValue("displayName", (object?)request.DisplayName?.Trim() ?? DBNull.Value);

            userId = (Guid)(await insertUser.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("Failed to create user."));
        }
        else if (emailConfirmed)
        {
            // 3. Пользователь уже полностью зарегистрирован
            throw new InvalidOperationException("User with this email already exists.");
        }
        else
        {
            // 4. Пользователь есть, но email не подтверждён
            
            await using var updateUser = new NpgsqlCommand(@"
UPDATE public.users
SET password_hash = @passwordHash,
    display_name = @displayName,
    updated_at = now()
WHERE id = @userId;", connection, tx);

            updateUser.Parameters.AddWithValue("userId", userId);
            updateUser.Parameters.AddWithValue("passwordHash", passwordHash);
            updateUser.Parameters.AddWithValue("displayName", (object?)request.DisplayName?.Trim() ?? DBNull.Value);

            await updateUser.ExecuteNonQueryAsync(ct);

            
            await using var invalidateTokens = new NpgsqlCommand(@"
UPDATE public.user_email_confirmations
SET used_at = now()
WHERE user_id = @userId
  AND used_at IS NULL;", connection, tx);

            invalidateTokens.Parameters.AddWithValue("userId", userId);
            await invalidateTokens.ExecuteNonQueryAsync(ct);
        }

        var confirmation = await CreateEmailConfirmationAsync(connection, tx, userId, ct);

        await tx.CommitAsync(ct);

        return new RegisterResponse(
            userId,
            request.Email.Trim(),
            true,
            confirmation.ExpiresAt);
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest request, string? userAgent, string? ipAddress, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var lookup = new NpgsqlCommand(@"
SELECT id, email, display_name, role, password_hash
FROM public.users
WHERE email_normalized = @emailNormalized
  AND is_active = true
  AND email_confirmed = true
LIMIT 1;", connection);

        lookup.Parameters.AddWithValue("emailNormalized", email);

        await using var reader = await lookup.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var userId = reader.GetGuid(0);
        var rawEmail = reader.GetString(1);
        var displayName = reader.IsDBNull(2) ? null : reader.GetString(2);
        var role = reader.GetString(3);
        var passwordHash = reader.GetString(4);
        await reader.DisposeAsync();

        if (!VerifyPassword(request.Password, passwordHash))
        {
            return null;
        }

        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var touchLogin = new NpgsqlCommand("UPDATE public.users SET last_login_at = now(), last_seen_at = now(), updated_at = now() WHERE id = @userId;", connection, tx);
        touchLogin.Parameters.AddWithValue("userId", userId);
        await touchLogin.ExecuteNonQueryAsync(ct);

        var session = await CreateSessionAsync(connection, tx, userId, userAgent, ipAddress, ct);
        await tx.CommitAsync(ct);

        return new AuthResponse(session.PlainToken, new AuthUserDto(userId, rawEmail, displayName, role), session.ExpiresAt);
    }

    public async Task<AuthUserDto?> GetUserBySessionAsync(string sessionToken, CancellationToken ct)
    {
        var sessionTokenHash = HashSessionToken(sessionToken);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
SELECT u.id, u.email, u.display_name, u.role
FROM public.user_sessions s
JOIN public.users u ON u.id = s.user_id
WHERE s.session_token_hash = @hash
  AND s.revoked_at IS NULL
  AND s.expires_at > now()
  AND u.is_active = true
LIMIT 1;", connection);

        cmd.Parameters.AddWithValue("hash", sessionTokenHash);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var user = new AuthUserDto(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3));

        await reader.DisposeAsync();

        await using var touch = new NpgsqlCommand(@"
UPDATE public.user_sessions
SET last_used_at = now()
WHERE session_token_hash = @hash;", connection);
        touch.Parameters.AddWithValue("hash", sessionTokenHash);
        await touch.ExecuteNonQueryAsync(ct);

        return user;
    }

    public async Task<bool> CloseSessionAsync(string sessionToken, CancellationToken ct)
    {
        var sessionTokenHash = HashSessionToken(sessionToken);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
UPDATE public.user_sessions
SET revoked_at = COALESCE(revoked_at, now()),
    last_used_at = now()
WHERE session_token_hash = @hash
  AND revoked_at IS NULL;", connection);

        cmd.Parameters.AddWithValue("hash", sessionTokenHash);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    private static async Task<(string PlainToken, DateTimeOffset ExpiresAt)> CreateSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid userId,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct)
    {
        var plainToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashSessionToken(plainToken);
        var expiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime);

        await using var cmd = new NpgsqlCommand(@"
INSERT INTO public.user_sessions (user_id, session_token_hash, user_agent, ip_address, expires_at, last_used_at)
VALUES (@userId, @tokenHash, @userAgent, @ipAddress, @expiresAt, now());", connection, tx);

        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("tokenHash", tokenHash);
        cmd.Parameters.AddWithValue("userAgent", (object?)userAgent ?? DBNull.Value);
        if (!string.IsNullOrWhiteSpace(ipAddress) && IPAddress.TryParse(ipAddress, out var parsedIp))
        {
            cmd.Parameters.Add("ipAddress", NpgsqlDbType.Inet).Value = parsedIp;
        }
        else
        {
            cmd.Parameters.Add("ipAddress", NpgsqlDbType.Inet).Value = DBNull.Value;
        }
        cmd.Parameters.AddWithValue("expiresAt", expiresAt);

        await cmd.ExecuteNonQueryAsync(ct);
        return (plainToken, expiresAt);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], "pbkdf2-sha256", StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string HashSessionToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private static async Task<(string Token, DateTimeOffset ExpiresAt)> CreateEmailConfirmationAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction tx,
    Guid userId,
    CancellationToken ct)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashSessionToken(token);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(24);

        await using var cmd = new NpgsqlCommand(@"
INSERT INTO public.user_email_confirmations (user_id, token_hash, expires_at)
VALUES (@userId, @tokenHash, @expiresAt);", connection, tx);

        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("tokenHash", tokenHash);
        cmd.Parameters.AddWithValue("expiresAt", expiresAt);

        await cmd.ExecuteNonQueryAsync(ct);
        return (token, expiresAt);
    }

    public async Task<(string Email, string Token, DateTimeOffset ExpiresAt)?> ResendConfirmationAsync(string email, CancellationToken ct)
    {
        var normalizedEmail = NormalizeEmail(email);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var lookup = new NpgsqlCommand(@"
SELECT id, email, email_confirmed
FROM public.users
WHERE email_normalized = @emailNormalized
LIMIT 1;", connection);

        lookup.Parameters.AddWithValue("emailNormalized", normalizedEmail);

        await using var reader = await lookup.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var userId = reader.GetGuid(0);
        var rawEmail = reader.GetString(1);
        var emailConfirmed = reader.GetBoolean(2);
        await reader.DisposeAsync();

        if (emailConfirmed)
        {
            return null;
        }

        await using var tx = await connection.BeginTransactionAsync(ct);
        var confirmation = await CreateEmailConfirmationAsync(connection, tx, userId, ct);
        await tx.CommitAsync(ct);

        return (rawEmail, confirmation.Token, confirmation.ExpiresAt);
    }
    public async Task<bool> ConfirmEmailAsync(string email, string token, CancellationToken ct)
    {
        var normalizedEmail = NormalizeEmail(email);
        var tokenHash = HashSessionToken(token);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
SELECT c.user_id
FROM public.user_email_confirmations c
JOIN public.users u ON u.id = c.user_id
WHERE u.email_normalized = @emailNormalized
  AND c.token_hash = @tokenHash
  AND c.used_at IS NULL
  AND c.expires_at > now()
LIMIT 1;", connection, tx);

        cmd.Parameters.AddWithValue("emailNormalized", normalizedEmail);
        cmd.Parameters.AddWithValue("tokenHash", tokenHash);

        var userIdObj = await cmd.ExecuteScalarAsync(ct);
        if (userIdObj is null || userIdObj is DBNull)
        {
            return false;
        }

        var userId = (Guid)userIdObj;

        await using var updateUser = new NpgsqlCommand(@"
UPDATE public.users
SET email_confirmed = true,
    email_confirmed_at = now(),
    updated_at = now()
WHERE id = @userId;", connection, tx);

        updateUser.Parameters.AddWithValue("userId", userId);
        await updateUser.ExecuteNonQueryAsync(ct);

        await using var updateToken = new NpgsqlCommand(@"
UPDATE public.user_email_confirmations
SET used_at = now()
WHERE user_id = @userId
  AND token_hash = @tokenHash
  AND used_at IS NULL;", connection, tx);

        updateToken.Parameters.AddWithValue("userId", userId);
        updateToken.Parameters.AddWithValue("tokenHash", tokenHash);
        await updateToken.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        return true;
    }

}