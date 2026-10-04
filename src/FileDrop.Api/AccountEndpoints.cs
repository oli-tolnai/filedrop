using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed record CurrentAccount(Guid Id, string DisplayName, bool IsAdmin, string DeviceName);

public sealed class AccountSessionService(IPasswordHasher<AppUser> passwordHasher)
{
    private const string CookieName = "filedrop_session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(3650);

    public async Task<CurrentAccount?> GetCurrentAsync(
        HttpContext context,
        FileDropDbContext db,
        CancellationToken cancellationToken)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var tokenHash = HashToken(token);
        var now = DateTime.UtcNow;
        return await (from session in db.UserSessions.AsNoTracking()
                      join user in db.Users.AsNoTracking() on session.UserId equals user.Id
                      where session.TokenHash == tokenHash && session.ExpiresAtUtc > now
                      select new CurrentAccount(user.Id, user.DisplayName, user.IsAdmin, session.DeviceName))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public PasswordVerificationResult VerifyPassword(AppUser user, string password) =>
        passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);

    public string HashPassword(AppUser user, string password) => passwordHasher.HashPassword(user, password);

    public async Task<CurrentAccount> StartSessionAsync(
        HttpContext context,
        FileDropDbContext db,
        AppUser user,
        string deviceName,
        CancellationToken cancellationToken)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        db.UserSessions.Add(new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = HashToken(token),
            DeviceName = deviceName,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(SessionLifetime),
        });
        await db.SaveChangesAsync(cancellationToken);
        context.Response.Cookies.Append(CookieName, token, CookieOptions(context, now.Add(SessionLifetime)));
        return new CurrentAccount(user.Id, user.DisplayName, user.IsAdmin, deviceName);
    }

    public async Task EndSessionAsync(
        HttpContext context,
        FileDropDbContext db,
        CancellationToken cancellationToken)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var token) && !string.IsNullOrWhiteSpace(token))
        {
            var tokenHash = HashToken(token);
            await db.UserSessions.Where(item => item.TokenHash == tokenHash).ExecuteDeleteAsync(cancellationToken);
        }

        context.Response.Cookies.Delete(CookieName, CookieOptions(context, DateTime.UtcNow.AddDays(-1)));
    }

    public static string NormalizeName(string name) => name.Trim().ToUpperInvariant();

    public static bool SecureEquals(string supplied, string configured)
    {
        var left = Encoding.UTF8.GetBytes(supplied);
        var right = Encoding.UTF8.GetBytes(configured);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static CookieOptions CookieOptions(HttpContext context, DateTime expires) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        Expires = expires,
        Path = "/",
    };
}

public static class AccountEndpoints
{
    private static readonly SemaphoreSlim SetupGate = new(1, 1);

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/account", StatusAsync);
        endpoints.MapPost("/api/account/setup", SetupAsync).RequireRateLimiting("account");
        endpoints.MapPost("/api/account/login", LoginAsync).RequireRateLimiting("account");
        endpoints.MapPost("/api/account/logout", LogoutAsync);
        endpoints.MapGet("/api/admin/users", ListUsersAsync);
        endpoints.MapPost("/api/admin/users", CreateUserAsync).RequireRateLimiting("account");
        return endpoints;
    }

    private static async Task<IResult> StatusAsync(
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        CancellationToken cancellationToken)
    {
        var account = await sessions.GetCurrentAsync(context, db, cancellationToken);
        return Results.Ok(new AccountStatusDto(
            SetupRequired: !await db.Users.AnyAsync(cancellationToken),
            Account: account));
    }

    private static async Task<IResult> SetupAsync(
        SetupAccountRequest request,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        IOptions<FileDropOptions> options,
        CancellationToken cancellationToken)
    {
        await SetupGate.WaitAsync(cancellationToken);
        try
        {
            if (await db.Users.AnyAsync(cancellationToken))
            {
                return Results.Conflict(new ApiError("A tulajdonosi fiók már létrejött."));
            }

            if (string.IsNullOrWhiteSpace(options.Value.SetupToken))
            {
                return Results.Problem("A kezdeti beállítási kód nincs konfigurálva.", statusCode: 503);
            }

            if (!AccountSessionService.SecureEquals(request.SetupToken ?? "", options.Value.SetupToken))
            {
                return Results.Unauthorized();
            }

            var validation = ValidateCredentials(request.DisplayName, request.Password, request.DeviceName);
            if (validation is not null) return Results.BadRequest(new ApiError(validation));

            var user = NewUser(request.DisplayName!, isAdmin: true);
            user.PasswordHash = sessions.HashPassword(user, request.Password!);
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
            var account = await sessions.StartSessionAsync(context, db, user, request.DeviceName!.Trim(), cancellationToken);
            return Results.Ok(account);
        }
        finally
        {
            SetupGate.Release();
        }
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        CancellationToken cancellationToken)
    {
        var validation = ValidateCredentials(request.DisplayName, request.Password, request.DeviceName);
        if (validation is not null) return Results.BadRequest(new ApiError(validation));

        var normalized = AccountSessionService.NormalizeName(request.DisplayName!);
        var user = await db.Users.FirstOrDefaultAsync(item => item.NormalizedName == normalized, cancellationToken);
        if (user is null || sessions.VerifyPassword(user, request.Password!) == PasswordVerificationResult.Failed)
        {
            return Results.Unauthorized();
        }

        var account = await sessions.StartSessionAsync(context, db, user, request.DeviceName!.Trim(), cancellationToken);
        return Results.Ok(account);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        CancellationToken cancellationToken)
    {
        await sessions.EndSessionAsync(context, db, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest request,
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        CancellationToken cancellationToken)
    {
        var current = await sessions.GetCurrentAsync(context, db, cancellationToken);
        if (current is null) return Results.Unauthorized();
        if (!current.IsAdmin) return Results.Forbid();

        var validation = ValidateCredentials(request.DisplayName, request.Password, "family-device");
        if (validation is not null) return Results.BadRequest(new ApiError(validation));

        var normalized = AccountSessionService.NormalizeName(request.DisplayName!);
        if (await db.Users.AnyAsync(item => item.NormalizedName == normalized, cancellationToken))
        {
            return Results.Conflict(new ApiError("Már van ilyen nevű fiók."));
        }

        var user = NewUser(request.DisplayName!, isAdmin: false);
        user.PasswordHash = sessions.HashPassword(user, request.Password!);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/admin/users/{user.Id}", new { user.Id, user.DisplayName });
    }

    private static async Task<IResult> ListUsersAsync(
        HttpContext context,
        FileDropDbContext db,
        AccountSessionService sessions,
        CancellationToken cancellationToken)
    {
        var current = await sessions.GetCurrentAsync(context, db, cancellationToken);
        if (current is null) return Results.Unauthorized();
        if (!current.IsAdmin) return Results.Forbid();

        var users = await db.Users
            .AsNoTracking()
            .OrderBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var sessionCounts = await db.UserSessions
            .AsNoTracking()
            .GroupBy(item => item.UserId)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.UserId, item => item.Count, cancellationToken);

        return Results.Ok(users.Select(user => new AdminUserDto(
            user.Id,
            user.DisplayName,
            user.IsAdmin,
            user.CreatedAtUtc,
            sessionCounts.GetValueOrDefault(user.Id))));
    }

    private static AppUser NewUser(string displayName, bool isAdmin) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = displayName.Trim(),
        NormalizedName = AccountSessionService.NormalizeName(displayName),
        PasswordHash = "",
        IsAdmin = isAdmin,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static string? ValidateCredentials(string? displayName, string? password, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length is < 2 or > 60)
            return "A név 2–60 karakter lehet.";
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 4 or > 128 || password.Any(character => character is < '0' or > '9'))
            return "A jelszó legalább 4 számjegyből állhat.";
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Trim().Length is < 2 or > 80)
            return "Az eszköznév 2–80 karakter lehet.";
        return null;
    }
}

public sealed record AccountStatusDto(bool SetupRequired, CurrentAccount? Account);
public sealed record SetupAccountRequest(string? DisplayName, string? Password, string? DeviceName, string? SetupToken);
public sealed record LoginRequest(string? DisplayName, string? Password, string? DeviceName);
public sealed record CreateUserRequest(string? DisplayName, string? Password);
public sealed record AdminUserDto(Guid Id, string DisplayName, bool IsAdmin, DateTime CreatedAtUtc, int ActiveSessionCount);
