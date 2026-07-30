namespace Sts2.SeedFinder.Web.Assets;

/// <summary>
/// Decodes every relic icon out of a local game install into a plain folder of images.
///
/// This is what decouples a deployment from the game: run it once on a machine that has the
/// game, point <c>Assets:Provider=bundled</c> and <c>Assets:Directory</c> at the output, and
/// the server never needs the install again.
///
/// It does not change anything about who owns the art. Serving that folder from a public host
/// is redistribution of Mega Crit's assets, which their Content Policy does not grant — see
/// docs/web_app_specs.md section 4. Exporting for your own use is a different thing from publishing.
/// </summary>
public static class AssetExport
{
    public static async Task<int> RunAsync(string targetDirectory, string? gameDirectory)
    {
        var provider = LocalGameAssetProvider.TryCreate(gameDirectory);
        if (provider is null)
        {
            Console.Error.WriteLine("Could not find a Slay the Spire 2 install.");
            Console.Error.WriteLine(@"Pass the game directory: --export-assets <out> ""C:\...\Slay the Spire 2""");
            return 1;
        }

        Directory.CreateDirectory(targetDirectory);
        Console.WriteLine($"Source: {provider.Status}");
        Console.WriteLine($"Target: {Path.GetFullPath(targetDirectory)}");

        int written = 0, skipped = 0;
        foreach (var slug in provider.AvailableSlugs.OrderBy(s => s, StringComparer.Ordinal))
        {
            var img = await provider.TryGetAsync(slug, CancellationToken.None);
            if (img is null) { skipped++; continue; }

            // The bundled provider keys off the file name, so the slug has to be the stem.
            var ext = img.Value.ContentType == "image/webp" ? ".webp" : ".png";
            await File.WriteAllBytesAsync(Path.Combine(targetDirectory, slug + ext), img.Value.Bytes);
            written++;
        }

        Console.WriteLine($"Wrote {written} icon(s){(skipped > 0 ? $", skipped {skipped}" : "")}.");

        // Descriptions travel with the icons, or a bundled deployment loses its tooltips.
        var text = provider.AllText.ToDictionary(kv => kv.Key, kv => kv.Value.Description);
        if (text.Count > 0)
        {
            await File.WriteAllTextAsync(
                Path.Combine(targetDirectory, BundledAssetProvider.TextFileName),
                System.Text.Json.JsonSerializer.Serialize(text, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            Console.WriteLine($"Wrote {text.Count} description(s) to {BundledAssetProvider.TextFileName}.");
        }

        Console.WriteLine();
        Console.WriteLine("To serve these without the game installed, set:");
        Console.WriteLine("  STS2_ASSETS=bundled");
        Console.WriteLine($"  Assets__Directory={Path.GetFullPath(targetDirectory)}");
        Console.WriteLine();
        Console.WriteLine("These are Mega Crit's assets. Exporting them for your own use is not the");
        Console.WriteLine("same as publishing them — see docs/web_app_specs.md section 4 before hosting.");
        return 0;
    }
}
