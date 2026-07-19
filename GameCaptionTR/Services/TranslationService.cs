using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace GameCaptionTR.Services;

/// <summary>
/// Çevrimiçi çeviri. API anahtarı istemez; oran limiti olabilir.
/// </summary>
public sealed class TranslationService : IDisposable
{
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private string? _lastSource;
    private string? _lastTranslation;

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text.Trim();
        if (text.Equals(_lastSource, StringComparison.Ordinal))
        {
            return _lastTranslation ?? text;
        }

        var cacheKey = $"{sourceLanguage}|{targetLanguage}|{text}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            _lastSource = text;
            _lastTranslation = cached;
            return cached;
        }

        // Aynı cümleye çok yakın OCR gürültüsünü engelle
        if (!string.IsNullOrEmpty(_lastSource) && Similarity(_lastSource, text) > 0.92)
        {
            return _lastTranslation ?? text;
        }

        var translated = await TranslateWithGoogleAsync(text, sourceLanguage, targetLanguage, cancellationToken);
        if (string.IsNullOrWhiteSpace(translated))
        {
            translated = await TranslateWithMyMemoryAsync(text, sourceLanguage, targetLanguage, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(translated))
        {
            translated = text;
        }

        if (_cache.Count > 200)
        {
            _cache.Clear();
        }

        _cache[cacheKey] = translated;
        _lastSource = text;
        _lastTranslation = translated;
        return translated;
    }

    public async Task<string> TranslateDocumentAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = new StringBuilder();
        foreach (var chunk in SplitIntoChunks(text, 1200))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var translated = await TranslateAsync(
                chunk,
                sourceLanguage,
                targetLanguage,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(translated))
            {
                continue;
            }

            if (result.Length > 0)
            {
                result.AppendLine();
                result.AppendLine();
            }

            result.Append(translated);
        }

        return result.ToString();
    }

    private async Task<string> TranslateWithGoogleAsync(string text, string source, string target, CancellationToken cancellationToken)
    {
        try
        {
            var url =
                "https://translate.googleapis.com/translate_a/single" +
                $"?client=gtx&sl={Uri.EscapeDataString(source)}&tl={Uri.EscapeDataString(target)}&dt=t&q={Uri.EscapeDataString(text)}";

            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var parts = doc.RootElement[0];
            if (parts.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0)
                {
                    builder.Append(part[0].GetString());
                }
            }

            return builder.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<string> TranslateWithMyMemoryAsync(string text, string source, string target, CancellationToken cancellationToken)
    {
        try
        {
            var url =
                "https://api.mymemory.translated.net/get" +
                $"?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(source)}|{Uri.EscapeDataString(target)}";

            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("responseData", out var data) &&
                data.TryGetProperty("translatedText", out var translated))
            {
                var value = WebUtility.HtmlDecode(translated.GetString() ?? string.Empty).Trim();
                if (value.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return value;
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var max = Math.Max(a.Length, b.Length);
        var distance = Levenshtein(a, b);
        return 1.0 - (distance / (double)max);
    }

    private static int Levenshtein(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    private static IEnumerable<string> SplitIntoChunks(string text, int maxLength)
    {
        var paragraphs = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var current = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            foreach (var word in paragraph.Split(
                         ' ',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (current.Length > 0 && current.Length + word.Length + 1 > maxLength)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }

            if (current.Length > 0 && current.Length + 2 <= maxLength)
            {
                current.AppendLine();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }

    public void Dispose() => _http.Dispose();
}
