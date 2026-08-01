using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2.SeedFinder.Core.Install;

namespace Sts2.SeedFinder.Web;

/// <summary>
/// Asks GitHub what the newest published release is and compares it with the copy that is
/// running. On demand only: nothing here runs unless the user presses the button, so a tool that
/// is otherwise entirely local does not quietly phone anywhere on startup.
/// </summary>
public static class UpdateCheck
{
    /// <summary>
    /// Overridable so a fork checks its own releases instead of reporting this one's as an
    /// update forever. Config key <c>Updates:Repository</c>, as "owner/repo".
    /// </summary>
    public const string DefaultRepository = "AlejandroGA-GHUB/StS2-Multiplayer-Seed-Finder";

    /// <summary>
    /// Unauthenticated GitHub allows 60 requests an hour per address, and a button is easy to
    /// press twice. Answers are reused for this long so leaning on it cannot exhaust the budget
    /// and start reporting failures.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);

    // Timeout rather than the default 100 seconds: this sits behind a button someone is waiting
    // on, and "could not reach GitHub" is a better answer than a spinner that never resolves.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static UpdateDto? _cached;
    private static DateTimeOffset _cachedAt;

    public static async Task<UpdateDto> RunAsync(string? repository, CancellationToken ct)
    {
        var repo = string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository.Trim();
        var current = AppVersion.Load();

        await Gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheFor) return _cached;

            var result = await FetchAsync(repo, current, ct);

            // Only successes are cached. A failed check is usually a dropped connection, and
            // holding that for fifteen minutes would make the retry look broken too.
            if (result.Status != UpdateStatus.Unreachable) { _cached = result; _cachedAt = DateTimeOffset.UtcNow; }
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<UpdateDto> FetchAsync(string repo, AppVersion current, CancellationToken ct)
    {
        try
        {
            // Deliberately NOT /releases/latest. That endpoint excludes prereleases entirely, so
            // it answers 404 for a repository whose only releases are prereleases, and the check
            // would report a permanent failure rather than an answer. This list is newest first
            // and includes both kinds, which also means a prerelease published after a stable one
            // is what gets compared. That is the intent: it is the newest thing a user could
            // install, and the response carries LatestIsPrerelease so the UI can say which it is.
            var url = $"https://api.github.com/repos/{repo}/releases?per_page=10";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            // GitHub rejects an API request with no User-Agent outright.
            request.Headers.Add("User-Agent", "Sts2-SeedFinder-UpdateCheck");

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // GitHub answers 404 rather than 403 for a repository the caller cannot see, so
                // a private repo and a mistyped one are indistinguishable from here. Both are
                // named, because the fix differs and neither is the user's connection.
                var why = (int)response.StatusCode switch
                {
                    404 => $"{repo} has no releases visible. A private repository reads the same "
                           + "way as one that does not exist.",
                    403 or 429 => "GitHub is rate limiting this address. Try again in a few minutes.",
                    var code => $"GitHub answered {code}.",
                };
                return Unreachable(current, why);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                // Drafts are visible to the author's own token and to nobody else, so skipping
                // them keeps this consistent with what a user would actually be able to download.
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;

                var tag = Text(release, "tag_name");
                if (tag is null) continue;

                var name = Text(release, "name");
                var page = Text(release, "html_url") ?? $"https://github.com/{repo}/releases";
                var published = Text(release, "published_at");
                var pre = release.TryGetProperty("prerelease", out var p) && p.GetBoolean();

                var order = AppVersion.Compare(current.Version, tag);
                var status = order switch
                {
                    null => UpdateStatus.Unknown,
                    < 0 => UpdateStatus.Outdated,
                    0 => UpdateStatus.Current,
                    // A checkout ahead of the newest release is the author's, or somebody
                    // building from main. Worth distinguishing from up to date so it does not
                    // read as a comparison that failed.
                    _ => UpdateStatus.Ahead,
                };

                return new UpdateDto(
                    Status: status,
                    Current: current.Version,
                    CurrentReleaseDate: current.ReleaseDate,
                    Latest: tag,
                    LatestName: name,
                    LatestIsPrerelease: pre,
                    PublishedOn: published?.Length >= 10 ? published[..10] : published,
                    Url: page,
                    Message: status == UpdateStatus.Unknown
                        ? $"Could not compare {Show(current.Version)} with {tag}."
                        : null);
            }

            return Unreachable(current, "That repository has no published releases yet.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Unreachable(current, "Could not reach GitHub. Check your connection.");
        }
    }

    private static UpdateDto Unreachable(AppVersion current, string message) => new(
        UpdateStatus.Unreachable, current.Version, current.ReleaseDate,
        null, null, false, null, "https://github.com/" + DefaultRepository + "/releases", message);

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Show(string? v) => string.IsNullOrWhiteSpace(v) ? "this copy" : v;
}

public enum UpdateStatus
{
    /// <summary>Running the newest published release.</summary>
    Current,
    /// <summary>A newer release exists.</summary>
    Outdated,
    /// <summary>Newer than anything published, so built from source.</summary>
    Ahead,
    /// <summary>A release was found but the two versions could not be ordered.</summary>
    Unknown,
    /// <summary>GitHub could not be asked, or had nothing to say.</summary>
    Unreachable,
}

/// <remarks>
/// The status converter is on the property rather than on the shared serializer options, which
/// every other endpoint also uses: without it the enum ships as an integer and the UI ends up
/// switching on 0 through 4.
/// </remarks>
public sealed record UpdateDto(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] UpdateStatus Status,
    string Current, string? CurrentReleaseDate,
    string? Latest, string? LatestName, bool LatestIsPrerelease,
    string? PublishedOn, string Url, string? Message);
