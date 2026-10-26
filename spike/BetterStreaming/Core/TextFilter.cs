using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterStreaming.Core;

/// <summary>
/// Viewer text before it's shown: cheermotes out ("Cheer100"), spaces tidied, listed words masked whole (case-blind;
/// "darnell" isn't "darn"), cut to a length. The built-in list is short and English; the streamer adds their own.
/// </summary>
public sealed class TextFilter
{
    static readonly string[] BuiltIn =
    {
        "fuck", "fucking", "fucker", "motherfucker", "shit", "bitch", "cunt", "cock", "dick", "pussy", "whore", "slut",
        "bastard", "asshole", "nigger", "nigga", "faggot", "fag", "retard", "retarded", "tranny", "kike", "spic",
        "chink", "wetback", "rape", "rapist", "nazi", "kys",
    };

    static readonly Regex Cheermote = new Regex(@"^[A-Za-z]+\d+$", RegexOptions.Compiled);
    readonly HashSet<string> words;

    public TextFilter(IEnumerable<string> extraWords)
    {
        words = new HashSet<string>(BuiltIn, StringComparer.OrdinalIgnoreCase);
        if (extraWords != null) foreach (var w in extraWords) if (!string.IsNullOrWhiteSpace(w)) words.Add(w.Trim());
    }

    public string Clean(string text, int max = 60)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var kept = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Where(t => !Cheermote.IsMatch(t));
        string s = string.Join(" ", kept);
        s = Regex.Replace(s, @"[\p{L}\p{N}']+", m => words.Contains(m.Value) ? new string('*', m.Value.Length) : m.Value);
        if (max > 0 && s.Length > max) s = s.Substring(0, max - 1) + "…";
        return s;
    }

    /// <summary>The name to show for a viewer.</summary>
    public static string Viewer(string displayName, bool anonymous) =>
        anonymous ? "an anonymous cheerer" : string.IsNullOrWhiteSpace(displayName) ? "someone" : displayName.Trim();
}
