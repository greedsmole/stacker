using Stacker.Core;
namespace Stacker.Tests;
public sealed class DiscoveryTests
{
    internal static PullRequest Pr(long n, string b, string h, long baseRepo = 1, long headRepo = 1) => new(n, "PR " + n, baseRepo, b, new('a', 40), headRepo, h, new('b', 40), false, "https://example.invalid/pr/" + n);
    [Fact] public void Independent_stacks_and_shared_prefixes_are_not_collapsed()
    {
        var result = StackDiscovery.Discover([Pr(1,"main","a"),Pr(2,"a","b"),Pr(3,"a","c"),Pr(4,"main","d"),Pr(5,"d","e"),Pr(6,"main","single")], ["main"]);
        Assert.Equal(3,result.Stacks.Count); Assert.Equal(2,result.Stacks.Count(s=>s.SharedPrefix)); Assert.Equal(6,Assert.Single(result.Other).Number);
        Assert.Contains(result.Stacks,s=>s.Layers.Select(p=>p.Number).SequenceEqual([1L,2L]));
        Assert.Contains(result.Stacks,s=>s.Layers.Select(p=>p.Number).SequenceEqual([4L,5L]));
    }
    [Fact] public void Boundaries_prevent_service_branches_from_becoming_layers()
    {
        var prs = new[] { Pr(1,"master","test"),Pr(2,"test","feature"),Pr(3,"feature","next") };
        var result = StackDiscovery.Discover(prs,["master","test"]);
        Assert.Equal(new long[]{2,3},Assert.Single(result.Stacks).Layers.Select(p=>p.Number)); Assert.Equal(1,Assert.Single(result.Other).Number);
        Assert.Equal(3,Assert.Single(StackDiscovery.Discover(prs,["master"]).Stacks).Layers.Count);
    }
    [Fact] public void Fork_identity_is_part_of_the_link()
    {
        var result = StackDiscovery.Discover([Pr(1,"main","feature",1,2),Pr(2,"feature","child",1,1)],["main"]);
        Assert.Empty(result.Stacks); Assert.Equal(2,result.Other.Count);
    }
    [Fact] public void Ambiguity_and_cycles_are_explicit_and_terminate()
    {
        var result = StackDiscovery.Discover([Pr(1,"main","same"),Pr(2,"main","same"),Pr(3,"same","child"),Pr(4,"cycle-b","cycle-a"),Pr(5,"cycle-a","cycle-b")],["main"]);
        Assert.Empty(result.Stacks); Assert.Equal(5,result.Other.Count); Assert.Contains(result.Warnings,w=>w.Contains("multiple")); Assert.Contains(result.Warnings,w=>w.Contains("cyclic"));
    }
    [Fact] public void Closed_parent_removed_from_snapshot_breaks_the_old_chain()
    {
        Assert.Single(StackDiscovery.Discover([Pr(1,"main","a"),Pr(2,"a","b")],["main"]).Stacks);
        var after=StackDiscovery.Discover([Pr(2,"main","b")],["main"]); Assert.Empty(after.Stacks); Assert.Single(after.Other);
    }
}
