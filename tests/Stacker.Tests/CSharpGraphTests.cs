using Stacker.Core;
using Stacker.Core.Framework;
using Stacker.Infrastructure;

namespace Stacker.Tests;

public sealed partial class CSharpGraphTests
{
    [Fact]
    public async Task Indexes_three_commits_and_detects_rebound_calls_with_a_dirty_checkout()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        await using var fixture = new GitFixture();
        await fixture.Init();
        const string entry = "Stack.slnx";
        await fixture.Write(entry, """
            <Solution>
              <Project Path="Library/Library.csproj"/>
              <Project Path="Client/Client.csproj"/>
            </Solution>
            """);
        await fixture.Write("Library/Library.csproj", Project(""));
        await fixture.Write("Client/Client.csproj", Project(
            """<ItemGroup><ProjectReference Include="../Library/Library.csproj"/></ItemGroup>"""));
        await fixture.Write("NuGet.Config", """
            <configuration><packageSources><clear /></packageSources></configuration>
            """);
        await fixture.Write("Library/Calculator.cs", """
            namespace Library;
            public interface IRule { int Value { get; } }
            public class Rule { }
            public partial class Calculator : Rule, IRule
            {
                public int Value => 1;
                public static int Sum(long x) => 1;
                public static int Obsolete() => 0;
            }
            public class Marker { }
            """);
        await fixture.Write("Library/State.cs", """
            namespace Library;
            public partial class Calculator { public int Field = 2; }
            """);
        await fixture.Write("Client/Call.cs", """
            namespace Library;
            public class Marker { }
            public class Caller
            {
                public int Result() { var result = Calculator.Sum(1); return result; }
                public static int Initial = Calculator.Sum(1);
                public int Property { get { var result = Calculator.Sum(1); return result; } }
                public int Lambda() { System.Func<int> action = () => Calculator.Sum(1); return action(); }
                public Calculator Make() => new Calculator();
            }
            """);
        await fixture.Commit("C# stack base");
        var basis = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("checkout", "-b", "layer-one");
        await fixture.Write("Library/State.cs", """
            namespace Library;
            public partial class Calculator
            {
                public int Field = 3;
                public static int Sum(int x) => 2;
            }
            """);
        await fixture.Commit("change field initializer and add overload");
        var layer = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("checkout", "-b", "layer-two");
        await fixture.Write("Library/Calculator.cs", """
            namespace Library;
            public interface IRule { int Value { get; } }
            public class Rule { }
            public partial class Calculator : Rule, IRule
            {
                public int Value => 2;
                public static int Sum(long x) => 1;
            }
            public class Marker { }
            """);
        await fixture.Commit("remove a member and modify property body");
        var head = await fixture.Git("rev-parse", "HEAD");
        await fixture.Write("Client/Call.cs", "dirty checkout must stay intact");

        var beforeWorktrees = await fixture.Git("worktree", "list", "--porcelain");
        var reader = new RoslynGraphReader(fixture.Reader, new RepositoryGit(fixture.Runner, new()), fixture.Runner);
        var entries = (await reader.EntriesAsync(fixture.Identity, head, ct)).OrThrow();
        Assert.Equal(entry, entries[0]);
        var snapshots = new List<CSharpGraph>();
        foreach (var sha in new[] { basis, layer, head })
        {
            var snapshot = (await reader.ReadAsync(new(fixture.Identity, sha, entry), ct)).OrThrow();
            Assert.True(snapshot.IsComplete, string.Join('\n', snapshot.Diagnostics));
            snapshots.Add(snapshot);
        }
        var (first, middle, last) = (snapshots[0], snapshots[1], snapshots[2]);
        Assert.Same(middle, (await reader.ReadAsync(new(fixture.Identity, layer, entry), ct)).OrThrow());
        Assert.Equal(2, last.Types.Count(type => type.Name.EndsWith("Marker", StringComparison.Ordinal)));
        var calculator = Assert.Single(last.Types, type => type.Name.EndsWith("Calculator", StringComparison.Ordinal));
        Assert.Equal(["Library/Calculator.cs", "Library/State.cs"], calculator.Locations.Select(item => item.Path));
        Assert.Contains(last.Relationships, edge => edge.SourceType == calculator.Id && edge.Kind == CSharpRelationshipKind.Inherits);
        Assert.Contains(last.Relationships, edge => edge.SourceType == calculator.Id && edge.Kind == CSharpRelationshipKind.Implements);
        Assert.Contains(last.Relationships, edge => edge.TargetType == calculator.Id && edge.Kind == CSharpRelationshipKind.UsesType);
        Assert.Contains(last.Relationships, edge => edge.TargetType == calculator.Id && edge.Kind == CSharpRelationshipKind.Creates);

        var firstLayer = CSharpGraphComparison.Compare(first, middle);
        var caller = Assert.Single(firstLayer.Types, type => type.Type.Name.EndsWith("Caller", StringComparison.Ordinal));
        var result = Assert.Single(caller.Members, member => member.Member.Signature.Contains("Result", StringComparison.Ordinal));
        Assert.Equal(result.Before!.DeclarationHash, result.After!.DeclarationHash);
        Assert.Equal(CSharpChange.Modified, result.Change);
        foreach (var name in new[] { "Initial", "Property", "Lambda" })
        {
            var member = Assert.Single(caller.Members, member => member.Member.Signature.Contains(name, StringComparison.Ordinal));
            Assert.Equal(member.Before!.DeclarationHash, member.After!.DeclarationHash);
            Assert.Equal(CSharpChange.Modified, member.Change);
            Assert.Contains(middle.Relationships, edge => edge.SourceMember == member.Member.Id
                && edge.Kind == CSharpRelationshipKind.Calls && edge.TargetType == calculator.Id);
        }
        var rebound = firstLayer.Relationships.Where(edge => edge.Relationship.SourceMember == result.Member.Id && edge.Relationship.Kind == CSharpRelationshipKind.Calls).ToArray();
        Assert.Contains(rebound, edge => edge.Change == CSharpChange.Added && edge.Relationship.TargetType == calculator.Id);
        Assert.Contains(rebound, edge => edge.Change == CSharpChange.Removed && edge.Relationship.TargetType == calculator.Id);

        var adjacent = CSharpGraphComparison.Compare(middle, last);
        var cumulative = CSharpGraphComparison.Compare(first, last);
        var latest = Assert.Single(adjacent.Types, type => type.Type.Id == calculator.Id);
        Assert.Contains(latest.Members, member => member.Change == CSharpChange.Removed && member.Member.Signature.Contains("Obsolete"));
        Assert.Contains(latest.Members, member => member.Change == CSharpChange.Modified && member.Member.Signature.Contains("Value"));
        Assert.Contains(cumulative.Types.Single(type => type.Type.Id == calculator.Id).Members,
            member => member.Change == CSharpChange.Modified && member.Member.Signature.Contains("Field"));
        Assert.All(CSharpGraphComparison.Compare(null, last).Types, type => Assert.Equal(CSharpChange.Unchanged, type.Change));

        var missing = await reader.EntriesAsync(fixture.Identity, new string('0', 40), ct);
        Assert.Contains("unavailable", missing.Problem!.Message);
        Assert.Equal(head, await fixture.Git("rev-parse", "HEAD"));
        Assert.Equal("dirty checkout must stay intact", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "Client/Call.cs"), ct));
        Assert.Equal(beforeWorktrees, await fixture.Git("worktree", "list", "--porcelain"));
    }

    [Fact]
    public async Task Incomplete_compilation_preserves_available_types_without_claiming_removals()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        await using var fixture = new GitFixture();
        await fixture.Init();
        var beforeCSharp = await fixture.Git("rev-parse", "HEAD");
        await fixture.Write("Code.csproj", Project(""));
        await fixture.Write("NuGet.Config", "<configuration><packageSources><clear /></packageSources></configuration>");
        await fixture.Write("Code.cs", "public class Original {} public class Shared {}");
        await fixture.Commit("compiling source");
        var basis = await fixture.Git("rev-parse", "HEAD");
        await fixture.Write("Code.cs", "public class Shared { public Unresolved Value; }");
        await fixture.Commit("incomplete source");
        var head = await fixture.Git("rev-parse", "HEAD");
        var reader = new RoslynGraphReader(fixture.Reader, new RepositoryGit(fixture.Runner, new()), fixture.Runner);
        var before = (await reader.ReadAsync(new(fixture.Identity, basis, "Code.csproj"), ct)).OrThrow();
        var after = (await reader.ReadAsync(new(fixture.Identity, head, "Code.csproj"), ct)).OrThrow();
        Assert.True(before.IsComplete);
        var empty = (await reader.ReadAsync(new(fixture.Identity, beforeCSharp, "Code.csproj"), ct)).OrThrow();
        Assert.True(empty.IsComplete);
        Assert.All(CSharpGraphComparison.Compare(empty, before).Types, type => Assert.Equal(CSharpChange.Added, type.Change));
        Assert.False(after.IsComplete);
        Assert.Contains(after.Diagnostics, message => message.Contains("Unresolved"));
        Assert.Equal("Shared", Assert.Single(after.Types).Name);
        var comparison = CSharpGraphComparison.Compare(before, after);
        Assert.Equal(CSharpChange.Unknown, comparison.Types.Single(type => type.Type.Name == "Original").Change);
    }

    [Fact]
    public async Task Cancellation_during_restore_removes_the_temporary_worktree()
    {
        await using var fixture = new GitFixture();
        await fixture.Init();
        await fixture.Write("Code.csproj", Project(""));
        await fixture.Write("Code.cs", "public class Example {}");
        await fixture.Commit("C# project");
        var commit = await fixture.Git("rev-parse", "HEAD");
        var before = await fixture.Git("worktree", "list", "--porcelain");
        var restore = new BlockingRestore();
        var reader = new RoslynGraphReader(fixture.Reader, new RepositoryGit(fixture.Runner, new()), restore);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var request = reader.ReadAsync(new(fixture.Identity, commit, "Code.csproj"), deadline.Token);
        var temporaryPath = await restore.Entered.Task.WaitAsync(deadline.Token);
        Assert.True(Directory.Exists(temporaryPath));
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(25)));
        Assert.False(Directory.Exists(temporaryPath));
        Assert.Equal(before, await fixture.Git("worktree", "list", "--porcelain"));
    }

    private static string Project(string content) =>
        $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>{content}</Project>""";
}
