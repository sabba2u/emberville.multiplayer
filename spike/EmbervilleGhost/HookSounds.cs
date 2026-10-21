using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The Hunter's Hook's sounds. Every throw: the Hollow Hunter's own hook sound (the game's Wwise event
/// Play_Acolyte_Tracker_HookGrab, in the Main bank). Now and then (about one throw in 20): your own shout, from
/// BepInEx/plugins/EmbervilleGhost/Sounds/getoverhere.wav, if you've put one there (not shipped with the mod).
/// The game runs its sound through Wwise with Unity's audio switched off (AudioManager.m_DisableAudio), so the shout
/// plays through Windows itself (winmm PlaySound, from memory, at the volume set in the config).
/// </summary>
internal static class HookSounds
{
    const string GameEvent = "Play_Acolyte_Tracker_HookGrab";
    static ConfigEntry<float> volume;
    static ConfigEntry<int> every;
    static byte[] shout;
    static bool looked;
    static int untilShout;

    public static void Init(ConfigFile config)
    {
        volume = config.Bind("HuntersHook", "ShoutVolume", 0.8f, "Volume of your own shout (Sounds/getoverhere.wav next to the mod), 0 - 1");
        every = config.Bind("HuntersHook", "ShoutEvery", 20, "About one throw in this many plays the shout (0 = never)");
    }

    /// <summary>Our throw: the hook sound, and now and then the shout. True = this one shouts (teammates hear it too).</summary>
    public static bool Thrown(GameObject from)
    {
        bool shout = false;
        if (every != null && every.Value > 0)
        {
            if (untilShout <= 0) untilShout = UnityEngine.Random.Range(Mathf.Max(1, every.Value * 3 / 4), every.Value * 5 / 4 + 1);
            shout = --untilShout == 0;
        }
        Play(from, shout);
        return shout;
    }

    /// <summary>A throw's sounds at that character (ours, or a teammate's stand-in).</summary>
    public static void Play(GameObject at, bool shout)
    {
        try { if (at) AkSoundEngine.PostEvent(GameEvent, at); } catch (Exception e) { NetLog.Warn($"Hook sound: {e.Message}", toast: false); }
        if (shout) Shout();
    }

    /// <summary>The shout now (also /hookshout, to try it).</summary>
    public static void Shout()
    {
        if (!looked)
        {
            looked = true;
            string path = Path.Combine(Path.GetDirectoryName(typeof(HookSounds).Assembly.Location) ?? ".", "Sounds", "getoverhere.wav");
            if (File.Exists(path)) shout = File.ReadAllBytes(path);
            else NetLog.Info($"Hook: no shout sound at {path} (put a .wav there)", toast: false);
        }
        if (shout == null) return;
        try
        {
            // Windows reads the sound from memory while it plays: a fixed copy (a .NET array could move under it)
            var bytes = Scaled(shout, Mathf.Clamp01(volume?.Value ?? 0.8f));
            if (!ReferenceEquals(bytes, inNative))
            {
                PlaySound(IntPtr.Zero, IntPtr.Zero, 0);   // stop the old one before its memory goes
                if (native != IntPtr.Zero) Marshal.FreeHGlobal(native);
                native = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, native, bytes.Length);
                inNative = bytes;
            }
            PlaySound(native, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
        catch (Exception e) { NetLog.Warn($"Hook shout: {e.Message}", toast: false); }
    }

    static IntPtr native;
    static byte[] inNative;
    static byte[] scaledFor; static float scaledAt = -1f; static byte[] scaled;

    /// <summary>A 16-bit PCM wave at a lower volume (anything else plays as it is).</summary>
    static byte[] Scaled(byte[] wav, float v)
    {
        if (v >= 0.999f) return wav;
        if (ReferenceEquals(scaledFor, wav) && Mathf.Approximately(scaledAt, v)) return scaled;
        var copy = (byte[])wav.Clone();
        try
        {
            int pos = 12, bits = 0, format = 0;
            while (pos + 8 <= copy.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(copy, pos, 4);
                int size = BitConverter.ToInt32(copy, pos + 4);
                if (id == "fmt ") { format = BitConverter.ToInt16(copy, pos + 8); bits = BitConverter.ToInt16(copy, pos + 22); }
                else if (id == "data" && format == 1 && bits == 16)
                {
                    int end = Math.Min(copy.Length - 1, pos + 8 + size);
                    for (int i = pos + 8; i + 1 < end; i += 2)
                    {
                        short sample = BitConverter.ToInt16(copy, i);
                        short s2 = (short)Mathf.Clamp(Mathf.RoundToInt(sample * v), short.MinValue, short.MaxValue);
                        copy[i] = (byte)(s2 & 0xFF); copy[i + 1] = (byte)((s2 >> 8) & 0xFF);
                    }
                    break;
                }
                pos += 8 + size + (size & 1);
            }
        }
        catch { return wav; }
        scaledFor = wav; scaledAt = v; scaled = copy;
        return copy;
    }

    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    [DllImport("winmm.dll", SetLastError = true)]
    static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
