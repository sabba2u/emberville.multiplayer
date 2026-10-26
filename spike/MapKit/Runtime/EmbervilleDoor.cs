// Emberville Map Kit: copy this file into your Unity project at Assets/MapKit/EmbervilleDoor.cs (NOT in an Editor folder:
// it's a component you add to a door marker). Add Component > Emberville > Door.
// Tick boxes for a door: its name, starting open, locked and the key. They're kept in the object's name, which is what the
// game reads ("Door: Vault (open, locked: Rusty Key)"); renaming the object by hand updates the boxes. The component itself
// never goes into the bundle (Build Bundles takes it out; the game hasn't got this script).
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("Emberville/Door")]
public class EmbervilleDoor : MonoBehaviour
{
    [Tooltip("What its levers and plates call it: \"Lever: -> Vault\"")]
    public string doorName = "Door";

    [Tooltip("Open when you arrive (a lever or plate can still close it)")]
    public bool startsOpen;

    [Tooltip("Needs the key to open")]
    public bool locked;

    [Tooltip("The item that opens it: the game's item name, e.g. Rusty Key")]
    public string key = "";

    /// <summary>The name the game reads.</summary>
    public string Compose()
    {
        var opts = new List<string>();
        if (startsOpen) opts.Add("open");
        if (locked) opts.Add("locked: " + (string.IsNullOrWhiteSpace(key) ? "Key" : key.Trim()));
        string name = string.IsNullOrWhiteSpace(doorName) ? "Door" : doorName.Trim();
        return "Door: " + name + (opts.Count > 0 ? " (" + string.Join(", ", opts) + ")" : "");
    }

    /// <summary>The boxes from the object's name (when it was renamed by hand). False if it isn't a door's name.</summary>
    public bool ReadName()
    {
        var m = Regex.Match(gameObject.name, @"^\s*Door\s*:\s*([^(]+?)\s*(\((.*)\))?\s*$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        doorName = m.Groups[1].Value.Trim();
        startsOpen = false; locked = false;
        foreach (var raw in m.Groups[3].Value.Split(','))
        {
            var opt = raw.Trim();
            var lm = Regex.Match(opt, @"^locked\s*:\s*(.+)$", RegexOptions.IgnoreCase);
            if (lm.Success) { locked = true; key = lm.Groups[1].Value.Trim(); }
            else if (opt.Equals("open", System.StringComparison.OrdinalIgnoreCase)) startsOpen = true;
        }
        return true;
    }

    void Reset()
    {
        if (!ReadName()) doorName = string.IsNullOrWhiteSpace(gameObject.name) ? "Door" : gameObject.name;
        Sync();
    }

    void OnValidate() => Sync();

    void Sync()
    {
#if UNITY_EDITOR
        string want = Compose();
        if (gameObject.name == want) return;
        gameObject.name = want;
        UnityEditor.EditorUtility.SetDirty(gameObject);
#endif
    }
}
