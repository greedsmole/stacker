using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Desktop;
using Stacker.Desktop.ViewModels;
using Stacker.Infrastructure;
namespace Stacker.Tests;

public sealed class FoundationWorkflowTests
{
    [AvaloniaTheory]
    [InlineData("HTTP 422",true)]
    [InlineData("HTTP 401",true)]
    [InlineData("HTTP 503",false)]
    public async Task Publication_distinguishes_a_server_rejection_from_an_unknown_result(string response,bool retryAllowed)
    {
        await using var f=new GitFixture();var reader=new FakeGitHub();
        var app=new ApplicationStore(f.Root);var database=new SqliteHistoryStore(f.Root);
        var state=new ReviewApplicationState(app,database);var journal=f.CreateJournal();
        var bus=new CommandBus(new(journal));
        var writer=new CommandGitHubWriter(new(new ScriptedRunner(_=>new(1,"{}",response)),new(),new()),bus);
        using var section=new DiffSectionViewModel(f.Reader,f.Root,new("feature","main",reader.Current.BaseSha,reader.Current.HeadSha,null,[])){IsPrSnapshot=true};
        using(var review=new ReviewViewModel(reader,writer,state,f.Reader,FakeGitHub.Context,reader.Current,section,false))
        {
            await review.InitializeAsync();review.Composer="Preserve this text";
            await review.PostCommentCommand.ExecuteAsync(null);
            if(retryAllowed){await review.PostCommentCommand.ExecuteAsync(null);Assert.Equal(2,journal.Recent("github:"+FakeGitHub.Context.Identity).OrThrow().Count);}
            await review.FlushAsync();
            Assert.Equal(retryAllowed,review.CanPublish);Assert.Equal("Preserve this text",review.Composer);
            Assert.DoesNotContain("Invalid publication transition",review.Message);
        }
        using var reopened=new ReviewViewModel(reader,writer,state,f.Reader,FakeGitHub.Context,reader.Current,section,false);
        await reopened.InitializeAsync();Assert.Equal(retryAllowed,reopened.CanPublish);Assert.Equal("Preserve this text",reopened.Composer);
        await reopened.FlushAsync();
    }

    [AvaloniaFact] public async Task Embedded_history_shares_layer_selection_and_restores_navigation()
    {
        await using var f=new GitFixture();await f.Linear();var root=Directory.CreateTempSubdirectory("foundation-ui-").FullName;
        try
        {
            await new YamlStackStore().SaveAsync(f.Root,[f.Stack],null);
            using var services=Composition.Build(root);var vm=services.GetRequiredService<MainViewModel>();
            await vm.OpenRepositoryAsync(f.Root);await vm.ShowHistoryAsync();var history=vm.ActiveHistory!;
            Assert.True(vm.IsHistoryOpen);await history.ShowCurrentCommand.ExecuteAsync(null);
            Assert.Equal("A.txt",Assert.Single(history.DocumentSections).SelectedFile!.NewPath);
            history.SelectedNode=history.Nodes[1];await history.LoadLayerCommand.ExecuteAsync(null);await history.ShowCurrentCommand.ExecuteAsync(null);
            Assert.Equal("B.txt",Assert.Single(vm.Sections).SelectedFile!.NewPath);
            Assert.Equal(history.SelectedNode.Id,vm.Session.Selection.ChangeId);
            await history.BackCommand.ExecuteAsync(null);
            Assert.Equal(history.Nodes[0],history.SelectedNode);Assert.Equal("A.txt",Assert.Single(history.DocumentSections).SelectedFile!.NewPath);
            Assert.Equal("A.txt",Assert.Single(vm.Sections).SelectedFile!.NewPath);
            await history.ForwardCommand.ExecuteAsync(null);Assert.Equal(history.Nodes[1],history.SelectedNode);
            Assert.All(history.DocumentSections.SelectMany(s=>s.Lines),line=>Assert.False(line.IsLineCommentVisible));
            await vm.ShutdownAsync();
        }
        finally{Directory.Delete(root,true);}
    }

    [AvaloniaFact] public async Task Close_failure_can_return_to_editing_and_then_retry()
    {
        await using var f=new GitFixture();await f.Linear();var root=Directory.CreateTempSubdirectory("foundation-close-").FullName;
        try
        {
            using var services=Composition.Build(root);var vm=services.GetRequiredService<MainViewModel>();
            var window=new MainWindow{DataContext=vm};window.Show();await vm.OpenRepositoryAsync(f.Root);
            var blocker=Path.Combine(root,"workspace");if(Directory.Exists(blocker))Directory.Delete(blocker,true);await File.WriteAllTextAsync(blocker,"blocked");
            var asked=new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.CloseFailureHandler=errors=>{asked.TrySetResult(errors);return Task.FromResult(UnsavedCloseChoice.ContinueEditing);};
            window.Close();var problems=await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.Contains(problems,e=>e.StartsWith("Workspace:"));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{});Assert.True(window.IsVisible);
            File.Delete(blocker);window.CloseFailureHandler=_=>Task.FromResult(UnsavedCloseChoice.Discard);window.Close();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(window.IsVisible)await Task.Delay(10,timeout.Token);
        }
        finally{Directory.Delete(root,true);}
    }
}
