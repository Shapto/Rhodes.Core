using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rhodes.Core.Web;

/// <summary>
/// A downloaded page or file as text, with the address it was finally read from (after redirects).
/// </summary>
public sealed record WebPage(string Url, string Title, string Text);

/// <summary>
/// Thrown with a message meant for Closure: what went wrong and what she can do instead.
/// </summary>
public sealed class WebReadException(string message) : Exception(message);

/// <summary>
/// Downloads pages for her tools. Uses .NET's HTTP client and falls back to curl.exe when a site blocks it,
/// since curl gets through some bot checks that .NET doesn't. GitHub repository and file links go through
/// GitHub's API and raw file host, so she gets the file tree, README or the file itself instead of GitHub's page HTML.
/// </summary>
public sealed partial class WebReader
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const int MaximumTreeEntries = 300;
    private const int MaximumReadmeCharacters = 4000;
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private readonly HttpClient httpClient;
    private readonly string? gitHubToken;
    private readonly ILogger<WebReader> logger;

    public WebReader(string? gitHubToken, ILogger<WebReader> logger)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25),
            MaxResponseContentBufferSize = MaximumResponseBytes,
        };
        this.gitHubToken = string.IsNullOrWhiteSpace(gitHubToken) ? null : gitHubToken;
        this.logger = logger;
    }

    /// <summary>
    /// The raw response body (HTML for search pages). Falls back to curl on a block or a network error.
    /// </summary>
    public async Task<(string Body, string ContentType, string FinalUrl)> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.7");
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (statusCode is 403 or 429 or 503)
            {
                logger.LogInformation("{Url} answered {StatusCode}; trying curl", uri, statusCode);
                return await GetWithCurlAsync(uri, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new WebReadException($"The page answered HTTP {statusCode}. Check the address, or try another result.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            return (body, contentType, response.RequestMessage?.RequestUri?.ToString() ?? uri.ToString());
        }
        catch (HttpRequestException exception)
        {
            logger.LogInformation("{Url} failed with .NET ({Message}); trying curl", uri, exception.Message);
            return await GetWithCurlAsync(uri, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebReadException("The page took longer than 25 seconds to answer. Try another result.");
        }
    }

    /// <summary>
    /// A page or file as readable text: HTML is stripped down to its text; GitHub links are read through GitHub's API.
    /// </summary>
    public async Task<WebPage> ReadPageAsync(Uri uri, CancellationToken cancellationToken)
    {
        var gitHubMatch = GitHubLinkPattern().Match(uri.ToString());
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && gitHubMatch.Success)
        {
            var owner = gitHubMatch.Groups["owner"].Value;
            var repository = gitHubMatch.Groups["repository"].Value;
            var reference = gitHubMatch.Groups["reference"].Success ? gitHubMatch.Groups["reference"].Value : null;
            var path = gitHubMatch.Groups["path"].Success ? Uri.UnescapeDataString(gitHubMatch.Groups["path"].Value.TrimEnd('/')) : "";
            if (gitHubMatch.Groups["kind"].Value == "blob" && reference is not null)
            {
                var rawUri = new Uri($"https://raw.githubusercontent.com/{owner}/{repository}/{reference}/{path}");
                var (rawBody, _, _) = await GetAsync(rawUri, cancellationToken);
                return new WebPage(uri.ToString(), $"{owner}/{repository}: {path}", rawBody);
            }

            return await ReadGitHubListingAsync(owner, repository, reference, path, cancellationToken);
        }

        var (body, contentType, finalUrl) = await GetAsync(uri, cancellationToken);
        if (contentType.StartsWith("image/", StringComparison.Ordinal) || contentType is "application/pdf" or "application/octet-stream" or "application/zip")
        {
            throw new WebReadException($"That link is a {contentType} file, not a text page. You can't read it yet.");
        }

        var looksLikeHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || (contentType.Length == 0 && body.TrimStart().StartsWith('<'));
        return looksLikeHtml ? new WebPage(finalUrl, ExtractTitle(body), HtmlToText(body)) : new WebPage(finalUrl, Path.GetFileName(new Uri(finalUrl).AbsolutePath), body);
    }

    /// <summary>
    /// A repository or folder: description, default branch, the file tree (under the folder, if one was given), and the README at the root.
    /// </summary>
    private async Task<WebPage> ReadGitHubListingAsync(string owner, string repository, string? reference, string path, CancellationToken cancellationToken)
    {
        var repositoryInformation = await GetGitHubJsonAsync($"repos/{owner}/{repository}", cancellationToken);
        var branch = reference ?? repositoryInformation["default_branch"]?.GetValue<string>() ?? "main";
        var tree = await GetGitHubJsonAsync($"repos/{owner}/{repository}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1", cancellationToken);

        var pathPrefix = path.Length == 0 ? "" : path + "/";
        var entries = (tree["tree"] as JsonArray ?? new JsonArray())
            .Select(entry => (Path: entry?["path"]?.GetValue<string>() ?? "", Type: entry?["type"]?.GetValue<string>() ?? "", Size: entry?["size"]?.GetValue<long>()))
            .Where(entry => entry.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine($"GitHub repository {owner}/{repository}" + (path.Length > 0 ? $", folder {path}" : ""));
        var description = repositoryInformation["description"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(description))
        {
            builder.AppendLine("Description: " + description);
        }

        builder.AppendLine($"Branch: {branch} · language: {repositoryInformation["language"]?.GetValue<string>() ?? "unknown"} · stars: {repositoryInformation["stargazers_count"]?.GetValue<int>() ?? 0}");
        builder.AppendLine();
        builder.AppendLine($"Files ({Math.Min(entries.Count, MaximumTreeEntries)} of {entries.Count}{(tree["truncated"]?.GetValue<bool>() == true ? ", GitHub cut the list short" : "")}):");
        foreach (var entry in entries.Take(MaximumTreeEntries))
        {
            var shownPath = entry.Path[pathPrefix.Length..];
            builder.AppendLine(entry.Type == "tree" ? $"  {shownPath}/" : $"  {shownPath} ({FormatSize(entry.Size ?? 0)})");
        }

        if (path.Length == 0)
        {
            var readme = await TryGetGitHubReadmeAsync(owner, repository, cancellationToken);
            if (readme is not null)
            {
                builder.AppendLine();
                builder.AppendLine("README:");
                builder.AppendLine(readme.Length > MaximumReadmeCharacters ? readme[..MaximumReadmeCharacters] + "\n[README continues]" : readme);
            }
        }

        builder.AppendLine();
        builder.AppendLine($"To read a file, call web_fetch with https://github.com/{owner}/{repository}/blob/{branch}/<path>.");
        return new WebPage($"https://github.com/{owner}/{repository}", $"{owner}/{repository}", builder.ToString());
    }

    private async Task<JsonNode> GetGitHubJsonAsync(string apiPath, CancellationToken cancellationToken)
    {
        using var request = CreateGitHubRequest(apiPath, "application/vnd.github+json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new WebReadException("GitHub says that repository, branch or path doesn't exist, or it's private.");
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new WebReadException("GitHub's limit of 60 requests an hour is used up. Try again later; Shapto can add a GitHub token to raise it.");
        }

        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) ?? new JsonObject();
    }

    private async Task<string?> TryGetGitHubReadmeAsync(string owner, string repository, CancellationToken cancellationToken)
    {
        using var request = CreateGitHubRequest($"repos/{owner}/{repository}/readme", "application/vnd.github.raw+json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken) : null;
    }

    private HttpRequestMessage CreateGitHubRequest(string apiPath, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + apiPath);
        request.Headers.UserAgent.ParseAdd("Rhodes-Closure");
        request.Headers.Accept.ParseAdd(accept);
        if (gitHubToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gitHubToken);
        }

        return request;
    }

    /// <summary>
    /// curl.exe ships with Windows 10 and 11. Its status code is appended after a marker so it can be split from the body.
    /// </summary>
    private async Task<(string Body, string ContentType, string FinalUrl)> GetWithCurlAsync(Uri uri, CancellationToken cancellationToken)
    {
        const string statusMarker = "\n__RHODES_CURL__";
        var startInformation = new ProcessStartInfo(OperatingSystem.IsWindows() ? "curl.exe" : "curl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "--silent", "--location", "--compressed", "--max-time", "20", "--max-filesize", MaximumResponseBytes.ToString(), "--user-agent", BrowserUserAgent, "--header", "Accept-Language: en-US,en;q=0.9", "--write-out", statusMarker + "%{http_code} %{content_type} %{url_effective}", uri.ToString() })
        {
            startInformation.ArgumentList.Add(argument);
        }

        Process? startedProcess;
        try
        {
            startedProcess = Process.Start(startInformation);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new WebReadException("The site blocked the request, and curl isn't installed to try again. Try another result.");
        }

        using var process = startedProcess ?? throw new WebReadException("Couldn't start curl to fetch the page.");
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already finished.
            }
        });
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var markerIndex = output.LastIndexOf(statusMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new WebReadException("The page couldn't be reached (no answer). Try another result.");
        }

        var statusParts = output[(markerIndex + statusMarker.Length)..].Split(' ', 3);
        var statusCode = int.TryParse(statusParts[0], out var parsedStatusCode) ? parsedStatusCode : 0;
        if (statusCode is < 200 or >= 300)
        {
            throw new WebReadException(statusCode == 0 ? "The page couldn't be reached. Try another result." : $"The page answered HTTP {statusCode}, even through curl. It probably blocks bots; try another result.");
        }

        var contentType = statusParts.Length > 1 ? statusParts[1].Split(';')[0] : "";
        var finalUrl = statusParts.Length > 2 ? statusParts[2].Trim() : uri.ToString();
        return (output[..markerIndex], contentType, finalUrl);
    }

    public static string ExtractTitle(string html)
    {
        var titleMatch = TitlePattern().Match(html);
        return titleMatch.Success ? WebUtility.HtmlDecode(WhitespacePattern().Replace(titleMatch.Groups["title"].Value, " ")).Trim() : "";
    }

    /// <summary>
    /// Readable text from HTML: scripts, styles, navigation, headers, footers and sidebars removed, block elements become line breaks,
    /// tags stripped, entities decoded, blank runs collapsed.
    /// </summary>
    public static string HtmlToText(string html)
    {
        var bodyMatch = BodyPattern().Match(html);
        var text = bodyMatch.Success ? bodyMatch.Groups["body"].Value : html;
        text = HiddenBlockPattern().Replace(text, " ");
        text = PageChromePattern().Replace(text, " ");
        text = CommentPattern().Replace(text, " ");
        text = ListItemPattern().Replace(text, "\n- ");
        text = BlockBoundaryPattern().Replace(text, "\n");
        text = TagPattern().Replace(text, "");
        text = WebUtility.HtmlDecode(text);

        var builder = new StringBuilder();
        var previousLineWasBlank = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = WhitespacePattern().Replace(rawLine, " ").Trim();
            if (line.Length == 0)
            {
                if (!previousLineWasBlank && builder.Length > 0)
                {
                    builder.Append('\n');
                }

                previousLineWasBlank = true;
                continue;
            }

            builder.Append(line).Append('\n');
            previousLineWasBlank = false;
        }

        return builder.ToString().Trim();
    }

    private static string FormatSize(long byteCount)
    {
        return byteCount >= 1024 * 1024 ? $"{byteCount / 1024.0 / 1024.0:0.0} MB" : byteCount >= 1024 ? $"{byteCount / 1024.0:0.0} KB" : $"{byteCount} B";
    }

    [GeneratedRegex(@"^https?://(?:www\.)?github\.com/(?<owner>[\w.-]+)/(?<repository>[\w.-]+?)(?:\.git)?(?:/(?<kind>tree|blob)/(?<reference>[^/?#]+)(?:/(?<path>[^?#]*))?)?/?(?:[?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubLinkPattern();

    [GeneratedRegex(@"<title[^>]*>(?<title>[\s\S]*?)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"<body[^>]*>(?<body>[\s\S]*?)(?:</body>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BodyPattern();

    [GeneratedRegex(@"<(script|style|noscript|svg|template|iframe|canvas)\b[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenBlockPattern();

    [GeneratedRegex(@"<(nav|header|footer|aside|form)\b[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex PageChromePattern();

    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemPattern();

    [GeneratedRegex(@"<(?:br|hr)\b[^>]*>|</?(?:p|div|section|article|main|h[1-6]|tr|table|ul|ol|pre|blockquote|dd|dt|figure|figcaption)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBoundaryPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"[ \t\f\v ]+")]
    private static partial Regex WhitespacePattern();
}