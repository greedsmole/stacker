using System.Text.Json;
using System.Text.RegularExpressions;
using Stacker.Core;

namespace Stacker.Infrastructure;

public sealed class GhExecutable
{
    public bool UseSavedCredentials { get; set; }
    public IReadOnlyList<string> UnsetEnvironment => UseSavedCredentials ? ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"] : [];
    public string? Override { get; set; }
    public string Resolve() => !string.IsNullOrWhiteSpace(Override) ? Override :
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => Path.Combine(p, OperatingSystem.IsWindows() ? "gh.exe" : "gh"))
        .Concat(new[] { "/opt/homebrew/bin/gh", "/usr/local/bin/gh" }).FirstOrDefault(File.Exists) ?? "gh";
}

public sealed class GitHubCli(IProcessRunner runner, GitExecutable git, GhExecutable gh) : IGitHubReader, IGitHubWriter
{
    public static (string Host, string Owner, string Name) ParseOrigin(string origin)
    {
        string host, path;
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh")
        { if (uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new StackerException("Origin must be a repository URL without query or fragment."); host = uri.Host; path = uri.AbsolutePath.Trim('/'); }
        else
        {
            var match = Regex.Match(origin, @"^(?:[^@/:]+@)?(?<host>[^/:]+):(?<path>[^\s]+)$");
            if (!match.Success) throw new StackerException("Origin is not a GitHub HTTPS/SSH URL. Set host/owner/repo in GitHub settings.");
            host = match.Groups["host"].Value; path = match.Groups["path"].Value;
        }
        return ParseRepository($"{host}/{path}");
    }
    public static (string Host, string Owner, string Name) ParseRepository(string value)
    {
        var parts = value.Trim().TrimEnd('/').Split('/');
        if (parts.Length != 3 || !Regex.IsMatch(parts[0], @"\A[a-zA-Z0-9][a-zA-Z0-9.-]*\z") || parts.Skip(1).Any(p => !Regex.IsMatch(p, @"\A[a-zA-Z0-9_.-]+\z")))
            throw new StackerException("Use host/owner/repo, for example github.com/team/project.");
        var name = parts[2].EndsWith(".git", StringComparison.Ordinal) ? parts[2][..^4] : parts[2];
        if (name.Length == 0) throw new StackerException("Repository name is empty.");
        return (parts[0].ToLowerInvariant(), parts[1], name);
    }
    private async Task<JsonElement> RunJsonAsync(string[] args, CancellationToken ct, string? body = null)
    {
        var response = await runner.RunAsync(new(gh.Resolve(), args, Timeout: TimeSpan.FromSeconds(60), MaxOutputBytes: 32 * 1024 * 1024,
            Environment: new Dictionary<string, string> { ["GH_PROMPT_DISABLED"] = "1", ["GH_PAGER"] = "cat", ["GH_DEBUG"] = "" }, StandardInput: body, UnsetEnvironment: gh.UnsetEnvironment), ct);
        if (response.ExitCode != 0) throw new StackerException($"GitHub request failed ({response.ExitCode}): {response.StdErr.Trim()}");
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(response.StdOut) ? "{}" : response.StdOut).RootElement.Clone(); }
        catch (JsonException) { throw new StackerException("gh returned invalid JSON. Check the GitHub host and CLI version."); }
    }
    private Task<JsonElement> Api(string host, string endpoint, CancellationToken ct, object? body = null, bool paginate = false) =>
        RunJsonAsync(["api", "--hostname", host, "--method", body is null ? "GET" : "POST", endpoint,
            .. (paginate ? new[] { "--paginate", "--slurp" } : []), .. (body is null ? [] : new[] { "--input", "-" })], ct, body is null ? null : JsonSerializer.Serialize(body));
    private async Task<string> ActiveUser(string host, CancellationToken ct)
    {
        var status = await RunJsonAsync(["auth", "status", "--active", "--hostname", host, "--json", "hosts"], ct);
        if (status.TryGetProperty("hosts", out var hosts) && hosts.TryGetProperty(host, out var accounts))
            foreach (var entry in accounts.EnumerateArray())
            {
                if (Bool(entry, "active") && Str(entry, "state") != "success" && Str(entry, "tokenSource") is "GH_TOKEN" or "GITHUB_TOKEN" or "GH_ENTERPRISE_TOKEN" or "GITHUB_ENTERPRISE_TOKEN")
                    throw new StackerException($"{host}: {Str(entry, "tokenSource")} is invalid and overrides saved gh accounts. Enable Settings → Use saved gh credentials, then Refresh GitHub; or fix the environment token and restart Stacker.");
                if (Bool(entry, "active") && Str(entry, "state") == "success" && !string.IsNullOrEmpty(Str(entry, "login"))) return Str(entry, "login");
            }
        throw new StackerException($"Not authenticated for {host}. Run: gh auth login --hostname {host}, then retry.");
    }
    public async Task<GitHubRepositoryContext> ConnectAsync(string root, string? repositoryOverride, CancellationToken ct = default)
    {
        (string Host, string Owner, string Name) parsed;
        if (!string.IsNullOrWhiteSpace(repositoryOverride)) parsed = ParseRepository(repositoryOverride);
        else
        {
            var remote = await runner.RunAsync(new(git.Resolve(), ["remote", "get-url", "origin"], root), ct);
            if (remote.ExitCode != 0) throw new StackerException("No origin remote. Local stacks remain available.");
            parsed = ParseOrigin(remote.StdOut.Trim());
        }
        var user = await ActiveUser(parsed.Host, ct);
        var repo = await Api(parsed.Host, $"repos/{parsed.Owner}/{parsed.Name}", ct);
        return new(parsed.Host, repo.GetProperty("id").GetInt64(), Str(repo.GetProperty("owner"), "login"), Str(repo, "name"), Str(repo, "clone_url"), user, Str(repo, "default_branch"));
    }
    public async Task VerifyAccessAsync(GitHubRepositoryContext context, CancellationToken ct = default)
    {
        var user = await ActiveUser(context.Host, ct);
        if (!string.Equals(user, context.User, StringComparison.OrdinalIgnoreCase)) throw new StackerException("The active GitHub account changed. Refresh GitHub before publishing.");
        var repository = await Api(context.Host, Repo(context), ct);
        if (repository.GetProperty("id").GetInt64() != context.RepositoryId) throw new StackerException("Repository identity changed. Reconnect before publishing.");
    }
    public async Task<GitHubSnapshot> SnapshotAsync(GitHubRepositoryContext context, CancellationToken ct = default)
    {
        var json = await Api(context.Host, Repo(context) + "/pulls?state=open&per_page=100", ct, paginate: true);
        return new(context, Pages(json).Select(ParsePr).ToArray(), DateTimeOffset.UtcNow);
    }
    public async Task<PullRequest> PullRequestAsync(GitHubRepositoryContext context, long number, CancellationToken ct = default)
    {
        var value = await Api(context.Host, $"{Repo(context)}/pulls/{number}", ct);
        if (Str(value, "state") != "open") throw new StackerException($"PR #{number} is no longer open. Refresh GitHub.");
        return ParsePr(value);
    }
    private static PullRequest ParsePr(JsonElement p)
    {
        var b = p.GetProperty("base"); var h = p.GetProperty("head");
        return new(p.GetProperty("number").GetInt64(), Str(p, "title"), RepoId(b), Str(b, "ref"), Str(b, "sha"), RepoId(h), Str(h, "ref"), Str(h, "sha"), Bool(p, "draft"), Str(p, "html_url"));
    }
    private static long RepoId(JsonElement p) => p.TryGetProperty("repo", out var repo) && repo.ValueKind == JsonValueKind.Object ? repo.GetProperty("id").GetInt64() : -1;
    public async Task<ReviewDiscussion> DiscussionAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default)
    {
        var comments = Pages(await Api(context.Host, $"{Repo(context)}/issues/{pr.Number}/comments?per_page=100", ct, paginate: true))
            .Select(c => new DiscussionComment(c.GetProperty("id").ToString(), Str(c.GetProperty("user"), "login"), Str(c, "body"), Str(c, "html_url"), Date(c, "created_at"))).ToArray();
        var reviews = Pages(await Api(context.Host, $"{Repo(context)}/pulls/{pr.Number}/reviews?per_page=100", ct, paginate: true))
            .Select(c => new ReviewSummary(c.GetProperty("id").ToString(), Str(c.GetProperty("user"), "login"), Str(c, "body"), Str(c, "state"), Date(c, "submitted_at"))).ToArray();
        var threads = new List<ReviewThread>(); string? cursor = null;
        const string fields = "id isResolved isOutdated viewerCanResolve viewerCanUnresolve subjectType path line startLine diffSide startDiffSide comments(first:100){nodes{id databaseId body url createdAt author{login} commit{oid}} pageInfo{hasNextPage endCursor}}";
        do
        {
            var data = await Graph(context.Host, "query($owner:String!,$name:String!,$number:Int!,$cursor:String){repository(owner:$owner,name:$name){pullRequest(number:$number){reviewThreads(first:100,after:$cursor){nodes{" + fields + "} pageInfo{hasNextPage endCursor}}}}}",
                new { owner = context.Owner, name = context.Name, number = pr.Number, cursor }, ct);
            var connection = data.GetProperty("repository").GetProperty("pullRequest").GetProperty("reviewThreads");
            foreach (var thread in connection.GetProperty("nodes").EnumerateArray())
            {
                var allComments = new List<JsonElement>(); var connectionComments = thread.GetProperty("comments");
                allComments.AddRange(connectionComments.GetProperty("nodes").EnumerateArray().Select(n => n.Clone()));
                while (Bool(connectionComments.GetProperty("pageInfo"), "hasNextPage"))
                {
                    var more = await Graph(context.Host, "query($id:ID!,$cursor:String){node(id:$id){... on PullRequestReviewThread{comments(first:100,after:$cursor){nodes{id databaseId body url createdAt author{login} commit{oid}} pageInfo{hasNextPage endCursor}}}}}",
                        new { id = Str(thread, "id"), cursor = Str(connectionComments.GetProperty("pageInfo"), "endCursor") }, ct);
                    connectionComments = more.GetProperty("node").GetProperty("comments"); allComments.AddRange(connectionComments.GetProperty("nodes").EnumerateArray().Select(n => n.Clone()));
                }
                var line = Int(thread, "line"); var first = allComments.FirstOrDefault();
                var isFile = Str(thread, "subjectType") == "FILE";
                var anchor = isFile || line is null ? null : new ReviewAnchor(pr.Number,
                    first.ValueKind == JsonValueKind.Object && first.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object ? Str(commit, "oid") : pr.HeadSha,
                    pr.BaseSha, Str(thread, "path"), Str(thread, "diffSide"), line.Value, Int(thread, "startLine"), NullableStr(thread, "startDiffSide"));
                threads.Add(new(Str(thread, "id"), Bool(thread, "isResolved"), Bool(thread, "isOutdated"), Bool(thread, "isResolved") ? Bool(thread, "viewerCanUnresolve") : Bool(thread, "viewerCanResolve"), anchor,
                    allComments.Select(c => new DiscussionComment(c.GetProperty("databaseId").ToString(), c.GetProperty("author").ValueKind == JsonValueKind.Object ? Str(c.GetProperty("author"), "login") : "deleted", Str(c, "body"), Str(c, "url"), Date(c, "createdAt"))).ToArray(), Str(thread, "path"), isFile));
            }
            var page = connection.GetProperty("pageInfo"); cursor = Bool(page, "hasNextPage") ? Str(page, "endCursor") : null;
        } while (cursor is not null);
        return new(comments, reviews, threads);
    }
    public async Task<IReadOnlyList<string>> ChangedFilePathsAsync(GitHubRepositoryContext context, PullRequest pr, CancellationToken ct = default)
    {
        var files = Pages(await Api(context.Host, $"{Repo(context)}/pulls/{pr.Number}/files?per_page=100", ct, paginate: true));
        return files.Select(f => Str(f, "filename")).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToArray();
    }
    public async Task ValidateFileAsync(GitHubRepositoryContext context, PullRequest pr, string path, CancellationToken ct = default)
    {
        var files = await ChangedFilePathsAsync(context, pr, ct);
        if (string.IsNullOrWhiteSpace(path) || !files.Contains(path, StringComparer.Ordinal))
            throw new StackerException("This file is not in the current PR diff. Refresh GitHub and reopen PR changes.");
    }
    public async Task ValidateAnchorsAsync(GitHubRepositoryContext context, PullRequest pr, IReadOnlyList<ReviewAnchor> anchors, CancellationToken ct = default)
    {
        if (anchors.Count == 0) return;
        var files = Pages(await Api(context.Host, $"{Repo(context)}/pulls/{pr.Number}/files?per_page=100", ct, paginate: true)).ToArray();
        foreach (var anchor in anchors)
        {
            if (anchor.HeadSha != pr.HeadSha || anchor.BaseSha != pr.BaseSha || anchor.PullRequest != pr.Number) throw new StackerException("The PR changed. Recheck the draft anchors.");
            var file = files.FirstOrDefault(f => Str(f, "filename") == anchor.Path);
            if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("patch", out var patch) || patch.ValueKind != JsonValueKind.String)
                throw new StackerException("GitHub did not provide a reviewable patch for this file. Open it on GitHub.");
            var parsed = PatchParser.Parse(new(anchor.Path, anchor.Path, FileChangeKind.Modified, 0, 0, "100644", "100644"), patch.GetString()!);
            var valid = parsed.Lines.Where(l => anchor.Side == "LEFT" ? l.Kind == DiffLineKind.Removed : l.Kind is DiffLineKind.Added or DiffLineKind.Context)
                .Select(l => anchor.Side == "LEFT" ? l.OldLineNumber : l.NewLineNumber).Where(n => n.HasValue).Select(n => n!.Value).ToHashSet();
            var start = anchor.StartLine ?? anchor.Line;
            if (anchor.Side is not ("LEFT" or "RIGHT") || start <= 0 || anchor.Line < start || anchor.Line - start > 50_000 || Enumerable.Range(start, anchor.Line - start + 1).Any(n => !valid.Contains(n)))
                throw new StackerException("The selected range is not present on this side of the GitHub diff.");
        }
    }
    private async Task<JsonElement> Graph(string host, string query, object variables, CancellationToken ct)
    {
        var json = await Api(host, "graphql", ct, new { query, variables });
        if (json.TryGetProperty("errors", out var errors)) throw new StackerException("GitHub GraphQL: " + errors.ToString());
        return json.GetProperty("data").Clone();
    }
    public async Task CommentAsync(GitHubRepositoryContext context, long number, string body, CancellationToken ct = default) =>
        _ = await Api(context.Host, $"{Repo(context)}/issues/{number}/comments", ct, new { body });
    public async Task FileCommentAsync(GitHubRepositoryContext context, PullRequest pr, string path, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(body)) throw new StackerException("Choose a file and enter a comment.");
        _ = await Api(context.Host, $"{Repo(context)}/pulls/{pr.Number}/comments", ct,
            new { body, commit_id = pr.HeadSha, path, subject_type = "file" });
    }
    public async Task InlineCommentAsync(GitHubRepositoryContext context, DraftComment comment, CancellationToken ct = default)
    {
        var body = AnchorBody(comment); body["commit_id"] = comment.Anchor.HeadSha;
        _ = await Api(context.Host, $"{Repo(context)}/pulls/{comment.Anchor.PullRequest}/comments", ct, body);
    }
    public async Task ReplyAsync(GitHubRepositoryContext context, long number, string commentId, string body, CancellationToken ct = default)
    {
        if (!long.TryParse(commentId, out var id) || id <= 0) throw new StackerException("Invalid comment ID.");
        _ = await Api(context.Host, $"{Repo(context)}/pulls/{number}/comments/{id}/replies", ct, new { body });
    }
    public async Task ResolveAsync(GitHubRepositoryContext context, string threadId, bool resolved, CancellationToken ct = default) =>
        _ = await Graph(context.Host, "mutation($id:ID!){" + (resolved ? "resolveReviewThread" : "unresolveReviewThread") + "(input:{threadId:$id}){thread{id isResolved}}}", new { id = threadId }, ct);
    public async Task SubmitReviewAsync(GitHubRepositoryContext context, ReviewDraft draft, CancellationToken ct = default)
    {
        if (draft.Decision is not ("COMMENT" or "APPROVE" or "REQUEST_CHANGES")) throw new StackerException("Invalid review decision.");
        _ = await Api(context.Host, $"{Repo(context)}/pulls/{draft.PullRequest}/reviews", ct,
            new { commit_id = draft.HeadSha, body = draft.Summary, @event = draft.Decision, comments = draft.Comments.Select(AnchorBody).ToArray() });
    }
    private static Dictionary<string, object> AnchorBody(DraftComment comment)
    {
        var a = comment.Anchor;
        if (a.Line <= 0 || a.Side is not ("LEFT" or "RIGHT") || a.StartLine is <= 0 || a.StartLine > a.Line) throw new StackerException("Invalid review line range.");
        var body = new Dictionary<string, object> { ["body"] = comment.Body, ["path"] = a.Path, ["line"] = a.Line, ["side"] = a.Side };
        if (a.StartLine is not null && a.StartLine != a.Line) { body["start_line"] = a.StartLine; body["start_side"] = a.StartSide ?? a.Side; }
        return body;
    }
    private static string Repo(GitHubRepositoryContext c) => $"repos/{c.Owner}/{c.Name}";
    private static IEnumerable<JsonElement> Pages(JsonElement json) => json.EnumerateArray().SelectMany(p => p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().ToArray() : [p]);
    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static string? NullableStr(JsonElement e, string name) => string.IsNullOrEmpty(Str(e, name)) ? null : Str(e, name);
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.TryGetInt32Safe(out var result) ? result : null;
    private static DateTimeOffset Date(JsonElement e, string name) => DateTimeOffset.TryParse(Str(e, name), out var result) ? result : DateTimeOffset.MinValue;
}
internal static class JsonHelpers
{
    internal static bool TryGetInt32Safe(this JsonElement value, out int result) { result = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result); }
}
