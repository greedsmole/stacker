using Stacker.Core;
using Stacker.Infrastructure;

namespace Stacker.Tests;

public sealed partial class CSharpGraphTests
{
    [Fact]
    public async Task Compile_path_casing_preserves_tracked_declarations_when_the_filesystem_resolves_it()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        await using var fixture = new GitFixture();
        await fixture.Init();
        await fixture.Write("Code.csproj", Project(""));
        await fixture.Write("NuGet.Config", "<configuration><packageSources><clear /></packageSources></configuration>");
        await fixture.Write("Sources/Code.cs", "public class Example { public int Field; }");
        await fixture.Commit("default compile paths");
        var basis = await fixture.Git("rev-parse", "HEAD");
        await fixture.Write("Code.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup><Compile Include="sources/code.cs" /></ItemGroup>
            </Project>
            """);
        await fixture.Commit("explicit compile path casing");
        var head = await fixture.Git("rev-parse", "HEAD");
        var reader = new RoslynGraphReader(fixture.Reader, new RepositoryGit(fixture.Runner, new()), fixture.Runner);
        var before = (await reader.ReadAsync(new(fixture.Identity, basis, "Code.csproj"), ct)).OrThrow();
        var after = (await reader.ReadAsync(new(fixture.Identity, head, "Code.csproj"), ct)).OrThrow();
        Assert.True(before.IsComplete, string.Join('\n', before.Diagnostics));
        var comparison = CSharpGraphComparison.Compare(before, after);

        if (!File.Exists(Path.Combine(fixture.Root, "sources/code.cs")))
        {
            Assert.False(after.IsComplete);
            Assert.DoesNotContain(comparison.Types, type => type.Change == CSharpChange.Removed);
            return;
        }

        Assert.True(after.IsComplete, string.Join('\n', after.Diagnostics));
        var type = Assert.Single(after.Types);
        Assert.Equal("Sources/Code.cs", Assert.Single(type.Locations).Path);
        Assert.All(type.Members.SelectMany(member => member.Locations), location => Assert.Equal("Sources/Code.cs", location.Path));
        Assert.All(comparison.Types, type => Assert.Equal(CSharpChange.Unchanged, type.Change));
        var location = Assert.Single(type.Locations);
        var blob = await fixture.Reader.ReadBlobAsync(fixture.Root, head, location.Path, ct);
        Assert.NotNull(blob);
        Assert.Contains("class Example", blob.Text);
    }
}
