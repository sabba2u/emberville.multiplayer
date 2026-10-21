using System.Linq;
using BetterNPCsAndQuests.Core;
using Xunit;

public class TweeTests
{
    const string Template = ":: StoryTitle\nEllie\n(keep these two)\n\n:: StoryData\n(never type two colons)\n\n-------- start your edit below --------\n\n:: DialogStart [NPC:Ellie]\nHello!\n[[Bye|End]]\n\n:: End [NPC:Ellie]\nSee you.\n";

    [Fact]
    public void Normalize_AddsMissingHeaderPassages()
    {
        var n = Twee.Normalize(":: DialogStart NPC:Ellie\nHi\n[[Bye|End]]\n:: End\nBye\n");
        var r = Twee.Inspect(n);
        Assert.Contains("DialogStart", r.Passages);
        Assert.Contains("End", r.Passages);
        Assert.Empty(r.Problems);
    }

    [Fact] public void Normalize_KeepsTemplateHeaders() => Assert.Equal(Template, Twee.Normalize(Template.Replace("\n", "\r\n")));

    [Fact]
    public void Normalize_DropsLeadingText()
    {
        var n = Twee.Normalize("some notes\n:: StoryTitle\nx\n:: StoryData\ny\n:: DialogStart\nHi\n");
        Assert.StartsWith(":: StoryTitle", n);
        Assert.Contains("DialogStart", Twee.Inspect(n).Passages);
    }

    [Fact]
    public void Normalize_OnlyStoryTitle_AddsStoryData()
    {
        var n = Twee.Normalize(":: StoryTitle\nx\n:: DialogStart\nHi\n");
        var r = Twee.Inspect(n);
        Assert.Contains("DialogStart", r.Passages);
        Assert.DoesNotContain("StoryTitle", r.Passages);
    }

    [Fact]
    public void Normalize_BracketsLooseTags()
    {
        var n = Twee.Normalize(":: DialogStart NPC:Ellie VO:hi\nHi\n");
        Assert.Contains(":: DialogStart [NPC:Ellie VO:hi]", n);
        Assert.Contains("DialogStart", Twee.Inspect(n).Passages);
    }

    [Fact]
    public void Inspect_FindsMissingTarget()
    {
        var r = Twee.Inspect(Twee.Normalize(":: DialogStart\nHi\n[[Go|Nowhere]]\n"));
        Assert.Contains(r.Problems, p => p.Contains("Nowhere"));
    }

    [Fact]
    public void Inspect_EndTarget_IsFine()
    {
        var r = Twee.Inspect(Twee.Normalize(":: DialogStart\nHi\n[[Let me look...|END]]\n"));
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Inspect_NoDialogStart_IsProblem()
    {
        var r = Twee.Inspect(Twee.Normalize(":: Hello\nHi\n"));
        Assert.Contains(r.Problems, p => p.Contains("DialogStart"));
    }

    [Fact]
    public void Inspect_CollectsFlags()
    {
        var r = Twee.Inspect(Twee.Normalize(":: DialogStart\nHi\n(set: $b to true)\n(if: $a and not $c)[[Go|DialogStart]]\n"));
        Assert.Contains("a", r.FlagsRead);
        Assert.Contains("c", r.FlagsRead);
        Assert.Contains("b", r.FlagsSet);
        Assert.DoesNotContain("b", r.FlagsRead);
    }

    [Fact]
    public void Inspect_CollectsTriggers()
    {
        var r = Twee.Inspect(Twee.Normalize(":: DialogStart [TRIGGER:wave]\nHi {TRIGGER:nod}\n(trigger: \"start ellie_errands\")\n"));
        Assert.Contains("start ellie_errands", r.Triggers);
        Assert.Contains("wave", r.Triggers);
        Assert.Contains("nod", r.Triggers);
    }

    [Fact]
    public void Inspect_TooManyChoices_IsProblem()
    {
        string choices = string.Concat(Enumerable.Range(0, 7).Select(i => $"[[c{i}|DialogStart]]\n"));
        var r = Twee.Inspect(Twee.Normalize(":: DialogStart\nHi\n" + choices));
        Assert.Contains(r.Problems, p => p.Contains("more than 6"));
    }

    [Fact]
    public void Inspect_Router_ManyChoices_IsFine()
    {
        string routes = string.Concat(Enumerable.Range(0, 8).Select(i => $"(if: $f{i})[[x|DialogStart]]\n"));
        var r = Twee.Inspect(Twee.Normalize(":: DialogStart\n" + routes));
        Assert.DoesNotContain(r.Problems, p => p.Contains("more than 6"));
    }

    [Fact]
    public void Inspect_Crlf_SameAsLf()
    {
        var a = Twee.Inspect(Twee.Normalize(Template));
        var b = Twee.Inspect(Twee.Normalize(Template.Replace("\n", "\r\n")));
        Assert.Equal(a.Passages.OrderBy(x => x), b.Passages.OrderBy(x => x));
        Assert.Empty(b.Problems);
    }
}
