namespace Sts2.SeedFinder.Core.Install;

/// <summary>
/// This tool's own version, read from <c>VERSION.txt</c> beside the executable.
///
/// A file rather than a constant for the same reason <see cref="VerifiedBuild"/> is one: cutting
/// a release means editing one obvious thing at the repo root, and nothing has to be recompiled
/// for the number on screen to be right.
///
/// The file is deliberately loose. The first non-empty line is the version and everything after
/// it is for humans, so a release note can be appended without breaking the parse.
/// </summary>
/// <param name="Version">
/// The version as written, with any leading "v" kept. Compared with <see cref="Compare"/>, which
/// ignores that prefix, so both spellings work.
/// </param>
/// <param name="ReleaseDate">
/// The <c>Release Date:</c> line when it holds a real date. The placeholder that ships in an
/// unreleased checkout ("x/xx/xxxx") reads as null rather than being shown as though it were one.
/// </param>
public sealed record AppVersion(string Version, string? ReleaseDate)
{
    public const string FileName = "VERSION.txt";

    /// <summary>
    /// What a checkout missing the file reports. "unknown" is deliberately unparseable, so the
    /// update check declines to compare rather than guessing a direction and telling somebody
    /// they are out of date when nothing is known either way.
    /// </summary>
    public static readonly AppVersion Unknown = new("unknown", null);

    public static AppVersion Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName);
        try
        {
            string? version = null, date = null;

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (version is null) { version = line; continue; }

                if (line.StartsWith("Release Date:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line["Release Date:".Length..].Trim();
                    // The placeholder is x/xx/xxxx, so anything without a digit is not a date yet.
                    if (value.Length > 0 && value.Any(char.IsDigit)) date = value;
                }
            }

            return version is null ? Unknown : new AppVersion(version, date);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Unknown;
        }
    }

    /// <summary>
    /// Orders two version strings, returning null when either cannot be read as one.
    ///
    /// Null is a real answer here and not a failure to handle: the update check has to be able to
    /// say "there is a newer release, I cannot tell you whether yours predates it" instead of
    /// picking a side. Both a missing VERSION.txt and a release tagged something like "hotfix"
    /// land here.
    /// </summary>
    /// <returns>Negative when <paramref name="a"/> is older, 0 when equal, positive when newer.</returns>
    public static int? Compare(string? a, string? b)
    {
        var left = Parse(a);
        var right = Parse(b);
        if (left is null || right is null) return null;

        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            // A missing component is zero, so 0.2 and 0.2.0 are the same release rather than
            // the shorter one being treated as older every time it is checked.
            int x = i < left.Length ? left[i] : 0;
            int y = i < right.Length ? right[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    /// <summary>
    /// Pulls the leading dotted number out of a tag. "v0.110.1" and "0.110.1" both give
    /// [0, 110, 1]; a trailing suffix such as "-beta" is dropped rather than rejected, because
    /// it does not change which release came first among the ones we publish.
    /// </summary>
    private static int[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var s = text.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];

        int end = 0;
        while (end < s.Length && (char.IsAsciiDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end].Trim('.');
        if (s.Length == 0) return null;

        var parts = s.Split('.');
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out numbers[i])) return null;

        return numbers;
    }
}
