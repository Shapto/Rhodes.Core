using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Rhodes.Core.Security;

public sealed record LoginRequest(string Token);

/// <summary>
/// Keeps the core's pages and API to Shapto: every request needs the access token, either as the cookie the login page sets,
/// or as "Authorization: Bearer &lt;token&gt;" for scripts and bots. Requests from the laptop itself can be let through
/// (Security:AllowLocalhost). The token comes from Security:AccessToken, or is generated once into Data\access-token.txt.
/// </summary>
public sealed class AccessGate
{
    public const string CookieName = "rhodes_access";
    private static readonly HashSet<string> OpenPaths = new(StringComparer.OrdinalIgnoreCase) { "/login.html", "/auth/login", "/rhodes.css", "/favicon.ico" };

    private readonly byte[] tokenBytes;
    private readonly bool allowLocalhost;

    public AccessGate(string token, bool allowLocalhost)
    {
        tokenBytes = Encoding.UTF8.GetBytes(token);
        this.allowLocalhost = allowLocalhost;
    }

    /// <summary>
    /// The configured token, or the one saved in the data folder, or a new one written there on first start.
    /// </summary>
    public static string LoadOrCreateToken(string? configuredToken, string dataFolder, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            return configuredToken.Trim();
        }

        var tokenPath = Path.Combine(dataFolder, "access-token.txt");
        if (File.Exists(tokenPath) && File.ReadAllText(tokenPath).Trim() is { Length: >= 16 } savedToken)
        {
            return savedToken;
        }

        var newToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        File.WriteAllText(tokenPath, newToken);
        logger.LogWarning("Created an access token for the Rhodes pages in {TokenPath}. Enter it once on each device at /login.html.", tokenPath);
        return newToken;
    }

    public bool IsCorrect(string? candidateToken)
    {
        return candidateToken is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidateToken), tokenBytes);
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path.Value ?? "/";
        if (OpenPaths.Contains(path) || IsAuthorized(context))
        {
            await next(context);
            return;
        }

        // Pages go to the login page and come back afterwards; API calls get a plain 401 the pages can show.
        var isPageRequest = path == "/" || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        if (isPageRequest && HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.Redirect("/login.html?next=" + Uri.EscapeDataString(path + context.Request.QueryString));
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync("Not logged in. Open /login.html and enter the access token.");
    }

    private bool IsAuthorized(HttpContext context)
    {
        if (allowLocalhost && context.Connection.RemoteIpAddress is { } remoteAddress && IPAddress.IsLoopback(remoteAddress))
        {
            return true;
        }

        if (IsCorrect(context.Request.Cookies[CookieName]))
        {
            return true;
        }

        var authorizationHeader = context.Request.Headers.Authorization.ToString();
        return authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && IsCorrect(authorizationHeader["Bearer ".Length..].Trim());
    }
}
