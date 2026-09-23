using System.Diagnostics;
using System.Text;
using Stacker.Core;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
namespace Stacker.Infrastructure;

public sealed class TextMateSyntaxHighlighter : ISyntaxHighlighter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>>> _cache = [];
    private readonly Dictionary<string, (RegistryOptions Options, Registry Registry)> _registries = [];
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".json", ".yaml", ".yml", ".xml", ".axaml", ".csproj", ".js", ".jsx", ".ts", ".tsx", ".html", ".css", ".sql", ".sh", ".bash", ".md" };
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>>> HighlightAsync(string blobId, string path, string text, string theme, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(extension) || Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024 || text.Count(c => c == '\n') > 50_000) return new Dictionary<int, IReadOnlyList<SyntaxSpan>>();
        if (extension is ".axaml" or ".csproj") extension = ".xml";
        var key = blobId + "|" + extension + "|" + theme;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var result = await Task.Run<IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>>>(() =>
            {
                var watch = Stopwatch.StartNew();
                var output = new Dictionary<int, IReadOnlyList<SyntaxSpan>>();
                if (!_registries.TryGetValue(theme, out var registry))
                {
                    var options = new RegistryOptions(theme == "Light" ? ThemeName.LightPlus : ThemeName.DarkPlus);
                    registry = (options, new Registry(options)); _registries[theme] = registry;
                }
                var language = registry.Options.GetLanguageByExtension(extension);
                if (language is null) return output;
                var grammar = registry.Registry.LoadGrammar(registry.Options.GetScopeByLanguageId(language.Id));
                if (grammar is null) return output;
                var state = grammar.TokenizeLine("", null, TimeSpan.FromMilliseconds(100)).RuleStack;
                state = null;
                var lines = text.Split('\n');
                for (var index = 0; index < lines.Length; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (watch.Elapsed > TimeSpan.FromSeconds(2)) return new Dictionary<int, IReadOnlyList<SyntaxSpan>>();
                    var line = lines[index]; var lineWatch = Stopwatch.StartNew();
                    var tokenized = grammar.TokenizeLine(line, state, TimeSpan.FromMilliseconds(100)); state = tokenized.RuleStack;
                    if (lineWatch.Elapsed > TimeSpan.FromMilliseconds(150)) return new Dictionary<int, IReadOnlyList<SyntaxSpan>>();
                    var spans = new List<SyntaxSpan>(); var palette = registry.Registry.GetTheme();
                    foreach (var token in tokenized.Tokens)
                    {
                        var start = Math.Clamp(token.StartIndex, 0, line.Length); var end = Math.Clamp(token.EndIndex, start, line.Length);
                        foreach (var rule in palette.Match(token.Scopes))
                        {
                            var color = palette.GetColor(rule.foreground);
                            if (end > start && !string.IsNullOrEmpty(color)) { spans.Add(new(start, end - start, color)); break; }
                        }
                    }
                    output[index + 1] = spans;
                }
                return output;
            }, ct);
            if (_cache.Count >= 24) _cache.Remove(_cache.Keys.First()); _cache[key] = result; return result;
        }
        catch (OperationCanceledException) { throw; }
        catch { return new Dictionary<int, IReadOnlyList<SyntaxSpan>>(); }
        finally { _gate.Release(); }
    }
}
