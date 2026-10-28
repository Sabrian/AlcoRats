using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace AlcoRats.Bot.Jitsi;

/// <summary>
/// Saves page DOM snapshots under <c>doms/</c> for offline UI analysis (Gravatar, login, prejoin, …).
/// </summary>
static class DomSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ResolveDomsDirectory()
    {
        // Prefer project AlcoRats.Bot/doms when running from bin/.../netX.0
        var fromBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "doms"));
        if (IsProjectDomsCandidate(fromBin))
            return fromBin;

        var besideApp = Path.Combine(AppContext.BaseDirectory, "doms");
        return besideApp;
    }

    public static async Task SaveAsync(
        IPage page,
        string screenName,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = ResolveDomsDirectory();
            Directory.CreateDirectory(dir);

            var safe = Sanitize(screenName);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var baseName = $"{stamp}_{safe}";

            var payload = await page.EvaluateFunctionAsync<DomCapture>(
                """
                () => {
                    function collectInputs(root, out) {
                        if (!root) return;
                        var list = root.querySelectorAll ? root.querySelectorAll('input, button, select, textarea, img, [role="button"]') : [];
                        for (var i = 0; i < list.length; i++) {
                            var el = list[i];
                            out.push({
                                tag: (el.tagName || '').toLowerCase(),
                                type: el.type || '',
                                name: el.name || '',
                                id: el.id || '',
                                className: String(el.className || '').slice(0, 120),
                                placeholder: el.placeholder || '',
                                ariaLabel: el.getAttribute('aria-label') || '',
                                testId: el.getAttribute('data-testid') || '',
                                src: el.src || el.getAttribute('src') || '',
                                text: ((el.innerText || el.textContent || '') + '').replace(/\s+/g, ' ').trim().slice(0, 80)
                            });
                        }
                        var all = root.querySelectorAll ? root.querySelectorAll('*') : [];
                        for (var j = 0; j < all.length; j++) {
                            if (all[j].shadowRoot) collectInputs(all[j].shadowRoot, out);
                        }
                    }
                    var interactive = [];
                    collectInputs(document, interactive);
                    var html = '';
                    try { html = document.documentElement ? document.documentElement.outerHTML : ''; } catch (e) {}
                    return {
                        url: location.href,
                        title: document.title || '',
                        readyState: document.readyState || '',
                        html: html,
                        interactive: interactive,
                        bodyTextPreview: ((document.body && (document.body.innerText || '')) || '').slice(0, 2000)
                    };
                }
                """);

            if (payload == null)
                return;

            var htmlPath = Path.Combine(dir, baseName + ".html");
            var metaPath = Path.Combine(dir, baseName + ".json");

            var htmlDoc = new StringBuilder();
            htmlDoc.AppendLine("<!DOCTYPE html>");
            htmlDoc.AppendLine("<!-- screen: " + safe + " -->");
            htmlDoc.AppendLine("<!-- url: " + (payload.Url ?? "") + " -->");
            htmlDoc.AppendLine("<!-- captured: " + DateTime.Now.ToString("o") + " -->");
            htmlDoc.Append(payload.Html ?? "");

            await File.WriteAllTextAsync(htmlPath, htmlDoc.ToString(), Encoding.UTF8, cancellationToken);

            var meta = new
            {
                screen = screenName,
                capturedAt = DateTime.Now,
                url = payload.Url,
                title = payload.Title,
                readyState = payload.ReadyState,
                bodyTextPreview = payload.BodyTextPreview,
                interactive = payload.Interactive,
                htmlFile = Path.GetFileName(htmlPath),
            };
            await File.WriteAllTextAsync(
                metaPath,
                JsonSerializer.Serialize(meta, JsonOptions),
                Encoding.UTF8,
                cancellationToken);

            logger.LogInformation("DOM snapshot saved: {Html} (+ {Meta})", htmlPath, Path.GetFileName(metaPath));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save DOM snapshot for screen {Screen}", screenName);
        }
    }

    private static bool IsProjectDomsCandidate(string path)
    {
        try
        {
            var parent = Directory.GetParent(path)?.FullName;
            if (parent == null)
                return false;
            return File.Exists(Path.Combine(parent, "AlcoRats.Bot.csproj"))
                   || File.Exists(Path.Combine(parent, "Dockerfile"));
        }
        catch
        {
            return false;
        }
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "screen";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is '/' or '\\' or ':')
                sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(s) ? "screen" : s;
    }

    private sealed class DomCapture
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public string ReadyState { get; set; }
        public string Html { get; set; }
        public string BodyTextPreview { get; set; }
        public object Interactive { get; set; }
    }
}
