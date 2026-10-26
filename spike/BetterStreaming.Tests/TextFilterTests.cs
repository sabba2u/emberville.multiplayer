using BetterStreaming.Core;
using Xunit;

public class TextFilterTests
{
    readonly TextFilter f = new TextFilter(new[] { "darn" });

    [Fact] public void StripsCheermotes() => Assert.Equal("hi", f.Clean("Cheer100 hi"));
    [Fact] public void CollapsesSpaces() => Assert.Equal("go go go", f.Clean("  go   go\tgo "));
    [Fact] public void MasksWholeWordsCaseBlind() => Assert.Equal("**** it", f.Clean("DARN it"));
    [Fact] public void LeavesLongerWords() => Assert.Equal("darnell says hi", f.Clean("darnell says hi"));
    [Fact] public void MasksNextToPunctuation() => Assert.Equal("oh ****!", f.Clean("oh darn!"));
    [Fact] public void Cuts()
    {
        string s = f.Clean(new string('a', 100), 60);
        Assert.Equal(60, s.Length);
        Assert.EndsWith("…", s);
    }
    [Fact] public void NullIsEmpty() => Assert.Equal("", f.Clean(null));
    [Fact] public void Anonymous() => Assert.Equal("an anonymous cheerer", TextFilter.Viewer("", true));
    [Fact] public void Named() => Assert.Equal("xxJoexx", TextFilter.Viewer("xxJoexx", false));
    [Fact] public void NoNameNotAnonymous() => Assert.Equal("someone", TextFilter.Viewer(" ", false));
}
