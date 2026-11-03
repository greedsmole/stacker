using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
namespace Stacker.Desktop;

public partial class MainWindow
{
    private void RelocateRepositoryClick(object? sender,RoutedEventArgs e)=>AsyncHandler.Run(async()=>
    {
        var repositories=await Vm.KnownRepositoriesAsync();
        if(repositories.Count==0){Vm.Error="No previous repository identity is available.";return;}
        var original=new ComboBox{ItemsSource=repositories.Select(r=>r.Root).ToArray(),SelectedIndex=0,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Stretch};
        var choose=new Button{Content="Locate new folder…"};
        var dialog=new Window{Title="Locate moved repository",Width=620,Height=200,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        dialog.Content=new StackPanel{Margin=new(20),Spacing=14,Children={new TextBlock{Text="Select its previous location to keep history and review progress."},original,choose}};
        choose.Click+=(_,_)=>AsyncHandler.Run(async()=>
        {
            var folders=await dialog.StorageProvider.OpenFolderPickerAsync(new(){Title="Locate this repository",AllowMultiple=false});
            if(folders.FirstOrDefault()?.TryGetLocalPath() is not { } root)return;
            var selected=repositories[original.SelectedIndex];
            await Vm.RelocateRepositoryAsync(selected.Repository,root,selected.Worktree);dialog.Close();
        },message=>AsyncHandler.Report(this,message));
        await dialog.ShowDialog(this);
    },message=>AsyncHandler.Report(this,message));
}
