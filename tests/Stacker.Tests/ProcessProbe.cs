namespace Stacker.Tests;
public static class ProcessProbe
{
    public static async Task Main(string[] args)
    {
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        switch (args.FirstOrDefault())
        {
            case "delay": await Task.Delay(TimeSpan.FromMinutes(2)); break;
            case "flood":
                for (var i = 0; i < 2000; i++) { Console.Out.Write(new string('o', 1024)); Console.Error.Write(new string('e', 1024)); }
                break;
            case "args": foreach (var arg in args.Skip(1)) Console.WriteLine(arg); break;
            case "exit": Console.Error.Write("intentional error"); Environment.ExitCode = 7; break;
            case "env": Console.Write(Environment.GetEnvironmentVariable(args[1]) ?? "absent"); break;
            case "stdin": Console.Write(await Console.In.ReadToEndAsync()); break;
        }
    }
}
