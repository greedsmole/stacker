using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Infrastructure;

public sealed class DiagnosticSink(IApplicationState state):IDiagnosticSink
{
    private readonly object _sync=new();
    private string Root=>Path.Combine(state.Root,"diagnostics");
    public Result<Unit> Record(DiagnosticEvent entry)=>Boundary.Capture(()=>
    {
        lock(_sync)
        {
            Directory.CreateDirectory(Root);var file=Path.Combine(Root,"operations.jsonl");
            if(File.Exists(file)&&new FileInfo(file).Length>=4*1024*1024)
            {
                for(var i=4;i>=1;i--){var old=Path.Combine(Root,"operations."+i+".jsonl");var next=Path.Combine(Root,"operations."+(i+1)+".jsonl");if(i==4&&File.Exists(old))File.Delete(old);else if(File.Exists(old))File.Move(old,next,true);}
                File.Move(file,Path.Combine(Root,"operations.1.jsonl"),true);
            }
            File.AppendAllText(file,JsonSerializer.Serialize(entry)+"\n");
        }
    },ProblemCodes.StoreWrite);
    public Task<Result<string>> ExportAsync(string destination,CancellationToken ct=default)=>Boundary.CaptureAsync(async()=>
    {
        ct.ThrowIfCancellationRequested();Directory.CreateDirectory(destination);
        // The allowlist contains only IDs, durations, byte counts and catalogued problem codes.
        foreach(var file in Directory.Exists(Root)?Directory.GetFiles(Root,"operations*.jsonl"):[])
            await File.WriteAllBytesAsync(Path.Combine(destination,Path.GetFileName(file)),await File.ReadAllBytesAsync(file,ct),ct);
        return destination;
    },ProblemCodes.StoreWrite);
}
