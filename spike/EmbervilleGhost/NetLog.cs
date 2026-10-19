using System;
using System.Collections.Generic;

namespace EmbervilleGhost;

public enum LogKind { Info, Success, Warning, Error }

/// <summary>Message log shared by the ImGui panel and toasts. Main thread only.</summary>
public static class NetLog
{
    public const int MaxEntries = 300;

    public struct Entry
    {
        public DateTime Time;
        public LogKind Kind;
        public string Text;
    }

    public static readonly List<Entry> Entries = new List<Entry>();

    /// <summary>Log a line. toast=true also pops it up on screen.</summary>
    public static void Add(LogKind kind, string text, bool toast = false)
    {
        Entries.Add(new Entry { Time = DateTime.Now, Kind = kind, Text = text });
        if (Entries.Count > MaxEntries) Entries.RemoveAt(0);

        switch (kind)
        {
            case LogKind.Warning: Plugin.Log.LogWarning(text); break;
            case LogKind.Error: Plugin.Log.LogError(text); break;
            default: Plugin.Log.LogInfo(text); break;
        }

        if (toast) Toasts.Show(kind, text);
    }

    public static void Info(string text, bool toast = false) => Add(LogKind.Info, text, toast);
    public static void Success(string text, bool toast = true) => Add(LogKind.Success, text, toast);
    public static void Warn(string text, bool toast = true) => Add(LogKind.Warning, text, toast);
    public static void Error(string text, bool toast = true) => Add(LogKind.Error, text, toast);
}
