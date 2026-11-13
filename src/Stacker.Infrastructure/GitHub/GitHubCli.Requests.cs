using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Infrastructure;

public sealed partial class GitHubCli
{
    private readonly ReadScheduler _metadataReads = new();

    // Each call pins the host. Review text travels only through stdin.
    private Task<JsonElement> Api(string host, string endpoint, CancellationToken ct, object? body = null,
        bool paginate = false, string? method = null, Func<JsonElement, JsonElement>? read = null) =>
        RunJsonAsync(["api", "--hostname", host, "--method", method ?? (body is null ? "GET" : "POST"), endpoint,
            .. (paginate ? new[] { "--paginate", "--slurp" } : []), .. (body is null ? [] : new[] { "--input", "-" })],
            ct, body is null ? null : JsonSerializer.Serialize(body), read);

    private Task<JsonElement> RunJsonAsync(string[] args, CancellationToken ct, string? body = null, Func<JsonElement, JsonElement>? read = null) =>
        body is null ? _metadataReads.RunAsync(ReadWork.Metadata, token => RunJsonCoreAsync(args, token, body, read), ct)
            : RunJsonCoreAsync(args, ct, body, read);

    private async Task<JsonElement> RunJsonCoreAsync(string[] args, CancellationToken ct, string? body, Func<JsonElement, JsonElement>? read)
    {
        var watch = Stopwatch.StartNew();
        var entry = commandLog?.BeginGitHub(args, DateTimeOffset.UtcNow);
        int? exitCode = null;
        var detail = "";
        var failed = false;
        try
        {
            var response = (await runner.RunAsync(new(gh.Resolve(), args, Timeout: TimeSpan.FromSeconds(60), MaxOutputBytes: 32 * 1024 * 1024,
                Environment: new Dictionary<string, string> { ["GH_PROMPT_DISABLED"] = "1", ["GH_PAGER"] = "cat", ["GH_DEBUG"] = "" },
                StandardInput: body, UnsetEnvironment: gh.UnsetEnvironment), ct)).OrThrow();
            exitCode = response.ExitCode;
            var parsed = TryParse(response.StdOut);
            // gh auth status can report an invalid account in JSON even when the process exits successfully.
            if (exitCode != 0 && (args[0] != "auth" || parsed is null))
                throw RequestFailure(response, parsed, body);
            var json = parsed ?? throw new StackerException("gh returned invalid JSON. Check the GitHub host and CLI version.");
            if (!string.IsNullOrWhiteSpace(response.StdErr))
                detail = SafeDetail(response.StdErr.Trim(), body);
            return read is null ? json : read(json);
        }
        catch (OperationCanceledException) { detail = "Cancelled."; throw; }
        catch (Exception ex)
        {
            failed = true;
            detail = SafeDetail(ex.Message, body);
            throw;
        }
        finally { commandLog?.Finish(entry, watch.Elapsed, exitCode, detail, failed); }
    }

    private static StackerException RequestFailure(ProcessResult response, JsonElement? json, string? body)
    {
        var match = Regex.Match(response.StdErr, @"\bHTTP\s+(?<status>[1-5]\d{2})\b", RegexOptions.IgnoreCase);
        int? status = match.Success ? int.Parse(match.Groups["status"].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        if (status is null && json is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("status", out var value) &&
            int.TryParse(value.ToString(), out var number) && number is >= 100 and <= 599)
            status = number;

        var details = json is { } payload ? DescribeErrors(payload) : "";
        if (details.Length == 0)
            details = response.StdErr.Trim();
        var label = status is int http ? $"GitHub HTTP {http}." : $"GitHub request failed (gh exit {response.ExitCode}).";
        var problem = Problem.Of(status == 401 ? ProblemCodes.GitHubAuth : ProblemCodes.GitHubRequest, SafeDetail(label + " " + details, body).Trim())
            with
        {
            Certainty = status is 400 or 401 or 403 or 404 or 405 or 409 or 410 or 415 or 422 or 429
                ? EffectCertainty.Rejected : EffectCertainty.Unknown
        };
        return status == 401 ? new GitHubAuthenticationException(problem) : new StackerException(problem);
    }

    private static string DescribeErrors(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            return "";
        var messages = new List<string> { Str(json, "message") };
        if (json.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray().Take(8))
            {
                if (error.ValueKind == JsonValueKind.String)
                    messages.Add(error.GetString()!);
                else if (error.ValueKind == JsonValueKind.Object)
                {
                    var fields = string.Join(" · ", new[] { Str(error, "resource"), Str(error, "field"), Str(error, "code") }
                        .Where(field => field.Length > 0));
                    var message = Str(error, "message");
                    messages.Add(string.Join(". ", new[] { fields, message }.Where(text => text.Length > 0)));
                }
            }
        }
        return string.Join("\n", messages.Where(message => !string.IsNullOrWhiteSpace(message)).Distinct(StringComparer.Ordinal));
    }

    private static string SafeDetail(string detail, string? body)
    {
        if (body is not null && TryParse(body) is { } input)
        {
            detail = detail.Replace(body, "[request payload]", StringComparison.Ordinal);
            foreach (var text in RequestTexts(input).Where(text => text.Length >= 3))
            {
                detail = detail.Replace(text, "[request text]", StringComparison.Ordinal);
                var marker = text.LastIndexOf("<!-- stacker:", StringComparison.Ordinal);
                if (marker > 0 && text[..marker].TrimEnd() is { Length: >= 3 } visible)
                    detail = detail.Replace(visible, "[request text]", StringComparison.Ordinal);
            }
        }
        detail = GitCommandLog.Redact(detail);
        return detail.Length > 2048 ? detail[..2048] + "…" : detail;
    }

    private static IEnumerable<string> RequestTexts(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Object)
            foreach (var property in input.EnumerateObject())
            {
                if (property.Name is "body" or "title" && property.Value.ValueKind == JsonValueKind.String)
                    yield return property.Value.GetString()!;
                else
                    foreach (var text in RequestTexts(property.Value))
                        yield return text;
            }
        else if (input.ValueKind == JsonValueKind.Array)
            foreach (var item in input.EnumerateArray())
                foreach (var text in RequestTexts(item))
                    yield return text;
    }

    private static JsonElement? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }
}
