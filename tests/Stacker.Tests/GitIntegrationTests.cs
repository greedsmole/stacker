using Stacker.Core;
namespace Stacker.Tests;
public sealed class GitIntegrationTests
{
    [Fact]
    public async Task Linear_modes_use_exact_commit_boundaries_and_preserve_original_parent()
    {
        await using var f = new GitFixture(); await f.Linear();
        var repo = await f.Reader.OpenAsync(f.Root); var service = new DiffService(f.Reader);
        var b = Assert.Single(await service.CompareAsync(new(repo, f.Stack, DiffMode.Layer, [1])));
        Assert.Equal(await f.Git("rev-parse", "A"), b.BaseSha); Assert.Equal(await f.Git("rev-parse", "B"), b.HeadSha);
        Assert.Equal("B.txt", Assert.Single(b.Files).NewPath);
        var patch = await f.Reader.PatchAsync(f.Root, b.BaseSha, b.HeadSha, b.Files[0]);
        var added = Assert.Single(patch.Lines, l => l.Kind == DiffLineKind.Added);
        Assert.Equal(1, added.NewLineNumber); Assert.Null(added.OldLineNumber); Assert.Equal("+B", added.Text);
        var cumulative = Assert.Single(await service.CompareAsync(new(repo, f.Stack, DiffMode.Cumulative, [1])));
        Assert.Equal(2, cumulative.Files.Count);
        var full = Assert.Single(await service.CompareAsync(new(repo, f.Stack, DiffMode.FullStack, []))); Assert.Equal(3, full.Files.Count);
        var multi = await service.CompareAsync(new(repo, f.Stack, DiffMode.MultiLayer, [2, 0, 2]));
        Assert.Equal(new[] { "refs/heads/A", "refs/heads/C" }, multi.Select(m => m.Layer));
        Assert.Equal(await f.Git("rev-parse", "B"), multi[1].BaseSha); Assert.Equal("C.txt", Assert.Single(multi[1].Files).NewPath);
    }
    [Fact]
    public async Task Divergence_warns_and_uses_merge_base()
    {
        await using var f = new GitFixture(); await f.Linear();
        var originalA = await f.Git("rev-parse", "A");
        await f.Git("checkout", "A"); await f.Write("new-parent.txt", "later\n"); await f.Commit("parent advanced");
        var repo = await f.Reader.OpenAsync(f.Root);
        var layer = Assert.Single(await new DiffService(f.Reader).CompareAsync(new(repo, f.Stack, DiffMode.Layer, [1])));
        Assert.Equal(originalA, layer.BaseSha); Assert.NotNull(layer.Warning);
        var layers = await new StackService(f.Reader).SnapshotAsync(repo, f.Stack);
        Assert.Equal(1, layers[1].Ahead); Assert.Equal(1, layers[1].Behind); Assert.NotNull(layers[1].Warning);
    }
    [Fact]
    public async Task Missing_refs_and_unrelated_histories_are_explicit_errors()
    {
        await using var f = new GitFixture(); await f.Linear(); await f.Git("branch", "-D", "B");
        var repo = await f.Reader.OpenAsync(f.Root);
        var snapshot = await new StackService(f.Reader).SnapshotAsync(repo, f.Stack); Assert.Null(snapshot[1].HeadSha);
        Assert.Contains("Missing ref", (await Assert.ThrowsAsync<StackerException>(() => new DiffService(f.Reader).CompareAsync(new(repo, f.Stack, DiffMode.Layer, [1])))).Message);
        await f.Git("checkout", "--orphan", "unrelated"); await f.Git("commit", "--allow-empty", "-m", "unrelated");
        repo = await f.Reader.OpenAsync(f.Root);
        var stack = new StackDefinition("x", "x", "refs/heads/main", ["refs/heads/unrelated"]);
        Assert.Contains("no common ancestor", (await Assert.ThrowsAsync<StackerException>(() => new DiffService(f.Reader).CompareAsync(new(repo, stack, DiffMode.Layer, [0])))).Message);
    }
    [Fact]
    public async Task Criss_cross_history_rejects_multiple_merge_bases()
    {
        await using var f = new GitFixture(); await f.Init();
        var root = await f.Git("rev-parse", "HEAD"); var tree = await f.Git("rev-parse", "HEAD^{tree}");
        var a = await f.Git("commit-tree", tree, "-p", root, "-m", "A");
        var b = await f.Git("commit-tree", tree, "-p", root, "-m", "B");
        var left = await f.Git("commit-tree", tree, "-p", a, "-p", b, "-m", "left");
        var right = await f.Git("commit-tree", tree, "-p", b, "-p", a, "-m", "right");
        await f.Git("update-ref", "refs/heads/left", left); await f.Git("update-ref", "refs/heads/right", right);
        var repo = await f.Reader.OpenAsync(f.Root);
        Assert.Equal(2, (await f.Reader.MergeBasesAsync(f.Root, left, right)).Count);
        Assert.Contains("multiple merge-bases", (await Assert.ThrowsAsync<StackerException>(() => new DiffService(f.Reader).CompareAsync(new(repo, new("x", "x", "refs/heads/left", ["refs/heads/right"]), DiffMode.Layer, [0])))).Message);
    }
    [Fact]
    public async Task File_metadata_handles_rename_binary_mode_submodule_unicode_and_newlines()
    {
        await using var f = new GitFixture(); await f.Init();
        await f.Write("rename old ü.txt", string.Concat(Enumerable.Range(1, 20).Select(n => $"line {n}\n")));
        await f.Write("delete.txt", "delete\n"); await f.Write("mode.sh", "#!/bin/sh\n"); await f.Commit("files");
        var parent = await f.Git("rev-parse", "HEAD");
        await f.Git("mv", "rename old ü.txt", "rename new ü.txt"); File.Delete(Path.Combine(f.Root, "delete.txt"));
        await f.Write("no newline.txt", "last line"); await f.Write("crlf.txt", "first\r\nsecond\r\n");
        await File.WriteAllBytesAsync(Path.Combine(f.Root, "binary.bin"), [0, 1, 2, 3]);
        await f.Git("add", "--all"); await f.Git("update-index", "--chmod=+x", "mode.sh");
        await f.Git("update-index", "--add", "--cacheinfo", $"160000,{parent},module"); await f.Git("commit", "-m", "changes");
        var head = await f.Git("rev-parse", "HEAD");
        var files = await f.Reader.FilesAsync(f.Root, parent, head);
        var rename = Assert.Single(files, x => x.Kind == FileChangeKind.Renamed); Assert.Equal("rename old ü.txt", rename.OldPath); Assert.Equal("rename new ü.txt", rename.NewPath);
        Assert.True(Assert.Single(files, x => x.NewPath == "binary.bin").IsBinary);
        Assert.True(Assert.Single(files, x => x.NewPath == "module").IsSubmodule);
        Assert.True(Assert.Single(files, x => x.NewPath == "mode.sh").ModeChanged);
        Assert.Equal(FileChangeKind.Deleted, Assert.Single(files, x => x.NewPath == "delete.txt").Kind);
        var noNewline = files.Single(x => x.NewPath == "no newline.txt");
        var patch = await f.Reader.PatchAsync(f.Root, parent, head, noNewline); Assert.Contains(patch.Lines, l => l.Kind == DiffLineKind.Notice && l.Text.Contains("No newline"));
        var crlf = await f.Reader.PatchAsync(f.Root, parent, head, files.Single(x => x.NewPath == "crlf.txt")); Assert.Equal(2, crlf.Lines.Count(l => l.Kind == DiffLineKind.Added));
    }
    [Fact]
    public async Task Reader_supports_worktrees_refresh_and_does_not_mutate_git_state()
    {
        await using var f = new GitFixture(); await f.Linear();
        await f.Write("dirty.txt", "unstaged\n"); await f.Write("A.txt", "staged\n"); await f.Git("add", "A.txt");
        var before = await f.Git("status", "--porcelain=v1", "-z"); var head = await f.Git("rev-parse", "HEAD");
        var index = await File.ReadAllBytesAsync(Path.Combine(f.Root, ".git", "index")); var refs = await f.Git("show-ref");
        var repo = await f.Reader.OpenAsync(f.Root); Assert.Equal(2, repo.ChangedFiles);
        var results = await new DiffService(f.Reader).CompareAsync(new(repo, f.Stack, DiffMode.FullStack, []));
        foreach (var result in results) foreach (var file in result.Files) await f.Reader.PatchAsync(f.Root, result.BaseSha, result.HeadSha, file);
        Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(f.Root, ".git", "index")));
        Assert.Equal(head, await f.Git("rev-parse", "HEAD")); Assert.Equal(refs, await f.Git("show-ref")); Assert.Equal(before, await f.Git("status", "--porcelain=v1", "-z"));
        var worktree = await f.AddWorktree(); var work = await f.Reader.OpenAsync(worktree); Assert.Equal(await f.Git("-C", worktree, "rev-parse", "--show-toplevel"), work.Root);
        await f.Git("branch", "fresh"); Assert.Contains((await f.Reader.OpenAsync(f.Root)).Refs, r => r.Name == "refs/heads/fresh");
    }
    [Fact]
    public async Task Literal_paths_are_not_interpreted_as_pathspecs()
    {
        if (OperatingSystem.IsWindows()) return; // Colon is not a valid Windows filename.
        await using var f = new GitFixture(); await f.Init(); var parent = await f.Git("rev-parse", "HEAD");
        await f.Write(":(glob)*.txt", "literal\n"); await f.Write("other.txt", "other\n"); await f.Commit("paths"); var head = await f.Git("rev-parse", "HEAD");
        var file = (await f.Reader.FilesAsync(f.Root, parent, head)).Single(f => f.NewPath.StartsWith(':'));
        var patch = await f.Reader.PatchAsync(f.Root, parent, head, file); Assert.DoesNotContain("+other", patch.Patch); Assert.Contains("+literal", patch.Patch);
    }

    [Fact]
    public async Task Reused_path_and_rename_patches_respect_git_metadata_and_custom_prefix_settings()
    {
        await using var f = new GitFixture(); await f.Init();
        await f.Write("old ü name.txt", string.Concat(Enumerable.Range(1, 30).Select(i => $"original {i}\n"))); await f.Commit("original");
        var parent = await f.Git("rev-parse", "HEAD");
        await f.Git("mv", "old ü name.txt", "new ü name.txt");
        await f.Write("old ü name.txt", "unrelated replacement\n"); await f.Commit("rename and reuse");
        await f.Git("config", "diff.noprefix", "true");
        var head = await f.Git("rev-parse", "HEAD");
        var files = await f.Reader.FilesAsync(f.Root, parent, head);
        // Git represents a reused source as modification + addition unless rewrite breaking is requested.
        var reused = Assert.Single(files, file => file.NewPath == "old ü name.txt");
        var reusedPatch = await f.Reader.PatchAsync(f.Root, parent, head, reused);
        Assert.Contains("+unrelated replacement", reusedPatch.Patch);
        Assert.DoesNotContain("diff --git a/new", reusedPatch.Patch);
        await f.Git("mv", "new ü name.txt", "final ü name.txt"); await f.Commit("plain rename");
        var renamedHead = await f.Git("rev-parse", "HEAD");
        var rename = Assert.Single(await f.Reader.FilesAsync(f.Root, head, renamedHead), file => file.Kind == FileChangeKind.Renamed);
        var patch = await f.Reader.PatchAsync(f.Root, head, renamedHead, rename);
        Assert.Contains("rename from new ü name.txt", patch.Patch);
        Assert.DoesNotContain("unrelated replacement", patch.Patch);
    }
    [Fact]
    public async Task Quoted_control_characters_in_paths_are_decoded_and_matched()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var f = new GitFixture(); await f.Init(); var parent = await f.Git("rev-parse", "HEAD");
        var path = "tab\tline\nquote\".txt";
        await f.Write(path, "unusual path\n"); await f.Commit("odd path"); var head = await f.Git("rev-parse", "HEAD");
        var file = Assert.Single(await f.Reader.FilesAsync(f.Root, parent, head)); Assert.Equal(path, file.NewPath);
        var patch = await f.Reader.PatchAsync(f.Root, parent, head, file); Assert.Contains("+unusual path", patch.Patch);
    }

    [Fact]
    public async Task Large_patch_is_rejected_without_truncation()
    {
        await using var f = new GitFixture(); await f.Init(); var parent = await f.Git("rev-parse", "HEAD");
        await f.Write("large.txt", string.Concat(Enumerable.Repeat("line\n", 50_001))); await f.Commit("large"); var head = await f.Git("rev-parse", "HEAD");
        var file = Assert.Single(await f.Reader.FilesAsync(f.Root, parent, head));
        await Assert.ThrowsAsync<OutputLimitException>(() => f.Reader.PatchAsync(f.Root, parent, head, file));
    }
}
