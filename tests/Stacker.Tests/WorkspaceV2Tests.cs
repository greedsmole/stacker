using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Stacker.Core;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class CountingReader(IGitRepositoryReader inner) : IGitRepositoryReader
{
    public int Opens { get; private set; }
    public bool FailOpen { get; set; }
    public Task<RepositorySnapshot> OpenAsync(string path,CancellationToken ct=default) { Opens++; if(FailOpen) throw new StackerException("Test refresh failure"); return inner.OpenAsync(path,ct); }
    public Task<IReadOnlyList<string>> MergeBasesAsync(string root,string left,string right,CancellationToken ct=default)=>inner.MergeBasesAsync(root,left,right,ct);
    public Task<(int Ahead,int Behind)> CountsAsync(string root,string parent,string head,CancellationToken ct=default)=>inner.CountsAsync(root,parent,head,ct);
    public Task<IReadOnlyList<FileChange>> FilesAsync(string root,string @base,string head,CancellationToken ct=default)=>inner.FilesAsync(root,@base,head,ct);
    public Task<FileDiff> PatchAsync(string root,string @base,string head,FileChange file,CancellationToken ct=default)=>inner.PatchAsync(root,@base,head,file,ct);
}
public sealed class WorkspaceV2Tests
{
    [Fact] public async Task Demo_manifest_checks_real_changes_across_multiple_stacks()
    {
        await using var fixture=new GitFixture(); var store=new YamlStackStore(); var appStore=new ApplicationStore(fixture.Root);
        var root=await new DemoRepositoryGenerator(fixture.Runner,new(),store,appStore).CreateAsync();
        var repo=await fixture.Reader.OpenAsync(root); var stacks=await store.LoadAsync(root);
        Assert.Equal(5,stacks.Stacks.Count);
        var manifest=JsonSerializer.Deserialize<DemoExpectation[]>(await File.ReadAllTextAsync(Path.Combine(root,"demo-manifest.json")))!;
        foreach(var expected in manifest)
        {
            var result=Assert.Single(await new DiffService(fixture.Reader).CompareAsync(new(repo,stacks.Stacks.Single(s=>s.Id==expected.Stack),expected.Mode,expected.Positions)));
            Assert.Equal(expected.Files,result.Files.Count); Assert.Equal(expected.Additions,result.Additions); Assert.Equal(expected.Deletions,result.Deletions);
        }
        var snapshot=JsonSerializer.Deserialize<GitHubSnapshot>(await File.ReadAllTextAsync(Path.Combine(root,".stacker-demo.json")))!;
        var discovery=StackDiscovery.Discover(snapshot.PullRequests,["main","master","test"]);
        Assert.Equal(4,discovery.Stacks.Count); Assert.Equal(new long[]{130,131},discovery.Other.Select(p=>p.Number));
        var multi=await new DiffService(fixture.Reader).CompareAsync(new(repo,stacks.Stacks[0],DiffMode.MultiLayer,[0,2]));
        Assert.Equal(5,multi[0].Additions); Assert.Equal(1,multi[1].Additions); Assert.Equal(0,multi[1].Deletions);
    }
    [AvaloniaFact] public async Task Multiple_stacks_keep_state_refresh_reuses_sections_and_focus_does_not_reload()
    {
        await using var f=new GitFixture(); await f.Linear(); var store=new YamlStackStore();
        await store.SaveAsync(f.Root,[f.Stack,new("second","Second","refs/heads/main",["refs/heads/A","refs/heads/C"]),new("third","Third","refs/heads/main",["refs/heads/B"])],null);
        var reader=new CountingReader(f.Reader); using var vm=new MainViewModel(reader,store,new JsonSettingsStore(Path.Combine(f.Root,"preferences")),new(),new(reader),new(reader),applicationStore:new ApplicationStore(Path.Combine(f.Root,"appdata")));
        var window=new MainWindow{DataContext=vm}; window.Show(); await vm.OpenRepositoryAsync(f.Root);
        Assert.Equal(3,vm.LocalGroups.Count); Assert.Equal(DiffMode.FullStack,vm.Mode);
        var first=Assert.Single(vm.Sections); first.SelectedFile=first.Files.Single(f=>f.NewPath=="B.txt"); first.Filter=".txt"; first.ScrollY=41; first.IsExpanded=false;
        await vm.SelectGroupAsync(vm.LocalGroups[1]); Assert.NotSame(first,Assert.Single(vm.Sections));
        await vm.SelectGroupAsync(vm.LocalGroups[0]); Assert.Same(first,Assert.Single(vm.Sections)); Assert.Equal("B.txt",first.SelectedFile?.NewPath); Assert.Equal(41,first.ScrollY); Assert.False(first.IsExpanded);
        await vm.RefreshAsync(); Assert.Same(first,Assert.Single(vm.Sections));
        var calls=reader.Opens; var other=new Window(); other.Show(); other.Close(); window.Activate(); Assert.Equal(calls,reader.Opens);
        reader.FailOpen=true; await vm.RefreshAsync(); Assert.Same(first,Assert.Single(vm.Sections)); Assert.Contains("Previous data retained",vm.Error);
        reader.FailOpen=false;
        await vm.SelectLayerAsync(vm.LocalGroups[0].Layers[1],false); Assert.Equal(DiffMode.Layer,vm.Mode); Assert.Equal("B.txt",Assert.Single(vm.Sections[0].Files).NewPath);
        vm.Mode=DiffMode.Cumulative; await vm.CompareAsync(); Assert.Equal(2,vm.Sections[0].Files.Count);
        await vm.SelectLayerAsync(vm.LocalGroups[0].Layers[0],false); await vm.SelectLayerAsync(vm.LocalGroups[0].Layers[2],true); Assert.Equal(DiffMode.MultiLayer,vm.Mode); Assert.Equal(2,vm.Sections.Count);
        var multi = vm.Sections.ToArray();
        await vm.SelectGroupAsync(vm.LocalGroups[1]); await vm.SelectGroupAsync(vm.LocalGroups[0]);
        Assert.True(vm.HasSavedSelection); await vm.RestoreSelectionAsync();
        Assert.Equal(new[]{0,2},vm.SelectedPositions); Assert.Equal(multi,vm.Sections.ToArray());
        window.Close();
    }
    [AvaloniaFact] public async Task Editing_one_stack_preserves_others_and_same_ids_in_another_repo_do_not_reuse_views()
    {
        await using var f=new GitFixture(); await f.Linear(); await using var g=new GitFixture(); await g.Linear();
        var store=new YamlStackStore(); var second=f.Stack with{Id="second",Name="Second"};
        await store.SaveAsync(f.Root,[f.Stack,second],null); await store.SaveAsync(g.Root,[g.Stack],null);
        using var vm=new MainViewModel(f.Reader,store,new JsonSettingsStore(Path.Combine(f.Root,"preferences")),new(),new(f.Reader),new(f.Reader),applicationStore:new ApplicationStore(Path.Combine(f.Root,"appdata")));
        await vm.OpenRepositoryAsync(f.Root); var original=vm.Sections[0];
        Assert.True(await vm.SaveStackAsync(f.Stack with{Name="Renamed"})); Assert.Equal("Second",(await store.LoadAsync(f.Root)).Stacks[1].Name);
        await vm.OpenRepositoryAsync(g.Root); Assert.NotSame(original,vm.Sections[0]); Assert.Equal("Test",vm.LocalGroups[0].Name);
    }
}
