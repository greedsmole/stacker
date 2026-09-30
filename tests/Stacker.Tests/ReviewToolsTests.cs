using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;

namespace Stacker.Tests;

public sealed class ReviewToolsTests
{
    private sealed class RecordingCache(string root) : IGitObjectCache
    {
        public List<long[]> Requests { get; } = [];
        public Task ClearAsync(GitHubRepositoryContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RepositorySnapshot> PrepareAsync(GitHubRepositoryContext context, IReadOnlyList<PullRequest> prs, CancellationToken ct = default)
        {
            Requests.Add(prs.Select(pr => pr.Number).ToArray());
            return Task.FromResult(new RepositorySnapshot(root,
                prs.SelectMany(pr => new[] { new BranchRef(GitObjectCache.HeadRef(pr), pr.HeadSha), new BranchRef(GitObjectCache.BaseRef(pr), pr.BaseSha) }).ToArray(), 0));
        }
    }
    private static MainViewModel Create(GitFixture f, FakeGitHub hub, RecordingCache cache) =>
        new(f.Reader, new YamlStackStore(), new JsonSettingsStore(Path.Combine(f.Root,"settings")), new(), new(f.Reader), new(f.Reader),
            hub, hub, cache, new ApplicationStore(Path.Combine(f.Root,"app")));

    [AvaloniaFact] public async Task Selecting_a_standalone_PR_opens_its_reviewable_changes()
    {
        await using var f = new GitFixture(); await f.Linear();
        var hub = new FakeGitHub { Current = DiscoveryTests.Pr(42,"main","A") with { BaseSha = await f.Git("rev-parse","main"), HeadSha = await f.Git("rev-parse","A") } };
        var cache = new RecordingCache(f.Root); using var vm = Create(f,hub,cache);
        await vm.OpenRepositoryAsync(f.Root);
        var group = Assert.Single(vm.OtherGroups); Assert.True(group.IsSinglePr);
        await vm.SelectGroupAsync(group);
        Assert.Equal(DiffMode.Layer,vm.Mode); Assert.True(Assert.Single(vm.Sections).IsPrSnapshot);
        Assert.Equal([42L],Assert.Single(cache.Requests)); Assert.True(vm.HasPrDiscussion);
        Assert.StartsWith("Pull request",vm.ComparisonTitle);
        await vm.FlushReviewsAsync();
    }

    [AvaloniaFact] public async Task Stack_views_prepare_only_the_PRs_needed_for_the_comparison()
    {
        await using var f = new GitFixture(); await f.Linear();
        var main=await f.Git("rev-parse","main"); var a=await f.Git("rev-parse","A"); var b=await f.Git("rev-parse","B"); var c=await f.Git("rev-parse","C");
        var hub = new FakeGitHub { Current = DiscoveryTests.Pr(1,"main","A") with { BaseSha=main,HeadSha=a } };
        hub.Extra.Add(DiscoveryTests.Pr(2,"A","B") with { BaseSha=a,HeadSha=b });
        hub.Extra.Add(DiscoveryTests.Pr(3,"B","C") with { BaseSha=b,HeadSha=c });
        var cache = new RecordingCache(f.Root); using var vm = Create(f,hub,cache);
        await vm.OpenRepositoryAsync(f.Root); var group=Assert.Single(vm.RemoteGroups);
        await vm.SelectOverviewAsync(group); Assert.Equal([1L,3L],cache.Requests[^1]);
        await vm.SelectLayerAsync(group.Layers[1],false); Assert.Equal([2L],cache.Requests[^1]); Assert.True(vm.Sections[0].IsPrSnapshot);
        await vm.SelectLayerAsync(group.Layers[0],false); await vm.SelectLayerAsync(group.Layers[2],true);
        Assert.Equal([1L,2L,3L],cache.Requests[^1]); Assert.Equal(2,vm.Sections.Count);
        await vm.FlushReviewsAsync();
    }

    [AvaloniaFact] public async Task File_and_changed_line_search_open_matching_file_and_wrap_persists_in_view()
    {
        await using var f = new GitFixture(); await f.Linear(); await new YamlStackStore().SaveAsync(f.Root,[f.Stack],null);
        using var vm = new MainViewModel(f.Reader,new YamlStackStore(),new JsonSettingsStore(Path.Combine(f.Root,"settings")),new(),new(f.Reader),new(f.Reader));
        await vm.OpenRepositoryAsync(f.Root);
        vm.SearchScope="Files"; vm.SearchQuery="B.txt"; await vm.SearchAsync();
        var fileHit=Assert.Single(vm.SearchResults); Assert.Equal("B.txt",fileHit.Path); await vm.OpenSearchHitAsync(fileHit);
        Assert.Equal("B.txt",vm.Sections[0].SelectedFile?.NewPath);
        vm.SearchScope="Changed lines"; vm.SearchQuery="^B$"; vm.SearchRegex=true; await vm.SearchAsync();
        var lineHit=Assert.Single(vm.SearchResults); Assert.Equal(DiffLineKind.Added,lineHit.Line?.Kind);
        var firstSection=vm.Sections[0];
        await vm.SelectLayerAsync(vm.LocalGroups[0].Layers[0],false);
        await vm.SetWrapCodeAsync(true);
        Assert.True(firstSection.WrapCode);
        await vm.SelectOverviewAsync(vm.LocalGroups[0]);
        Assert.True(vm.Sections[0].WrapCode);
        Assert.All(vm.Sections[0].Lines,line=>Assert.Equal(TextWrapping.Wrap,line.TextWrapping));
        Assert.Equal(ScrollBarVisibility.Disabled,vm.Sections[0].CodeHorizontalScroll);
        vm.SearchQuery="["; await vm.SearchAsync(); Assert.Contains("Invalid regex",vm.SearchStatus);
    }

    [AvaloniaFact] public async Task Selected_diff_rows_open_a_single_multi_line_review_comment()
    {
        await using var f = new GitFixture(); await f.Init(); await f.Git("checkout","-b","feature");
        await f.Write("note.cs","first\nsecond\nthird\n"); await f.Commit("three lines");
        var hub = new FakeGitHub { Current = DiscoveryTests.Pr(42,"main","feature") with { BaseSha=await f.Git("rev-parse","main"), HeadSha=await f.Git("rev-parse","feature") } };
        using var vm=Create(f,hub,new RecordingCache(f.Root)); var window=new MainWindow { DataContext=vm }; window.Show();
        await vm.OpenRepositoryAsync(f.Root); await vm.SelectGroupAsync(Assert.Single(vm.OtherGroups));
        var section=Assert.Single(vm.Sections); await section.LoadAsync(section.SelectedFile); window.UpdateLayout();
        var lines=section.Lines.Where(line=>line.Kind==DiffLineKind.Added).ToArray(); Assert.Equal(3,lines.Length);
        var list=window.GetVisualDescendants().OfType<ListBox>().First(item=>ReferenceEquals(item.ItemsSource,section.Lines));
        list.SelectedItems!.Add(lines[0]); list.SelectedItems.Add(lines[1]);
        var button=window.GetVisualDescendants().OfType<Button>().First(item=>item.Content?.ToString()=="Comment on selected lines");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Yield(); window.UpdateLayout();
        Assert.Equal("note.cs · RIGHT · lines 1–2",vm.ActiveReview!.SelectionLabel);
        vm.ActiveReview.Composer="Review both lines"; await vm.ActiveReview.AddToDraftCommand.ExecuteAsync(null);
        var anchor=Assert.Single(vm.ActiveReview.DraftComments).Anchor; Assert.Equal(1,anchor.StartLine); Assert.Equal(2,anchor.Line);
        await vm.FlushReviewsAsync(); window.Close();
    }

    [Fact] public async Task Git_log_records_running_command_and_final_status()
    {
        await using var f=new GitFixture(); var log=new GitCommandLog(); var updates=new List<GitCommandEntry>(); log.Changed += entry=>{if(entry is not null)updates.Add(entry);};
        var result=await new ProcessRunner(log).RunAsync(new(new GitExecutable().Resolve(),["--version"],f.Root));
        Assert.Equal(0,result.ExitCode); Assert.Equal(2,updates.Count); Assert.True(updates[0].IsRunning);
        Assert.False(updates[1].IsRunning); Assert.Equal(0,updates[1].ExitCode); Assert.Contains("git --version",updates[1].Command);
        await new ProcessRunner(log).RunAsync(new(new GitExecutable().Resolve(),["-c","remote.origin.url=https://user:placeholder-secret@example.invalid/repo.git","--version"],f.Root));
        Assert.DoesNotContain("placeholder-secret",log.Snapshot()[1].Command);
        Assert.Contains("[credentials]",log.Snapshot()[1].Command);
        Assert.Equal(2,log.Snapshot().Count); log.Clear(); Assert.Empty(log.Snapshot());
    }
}
