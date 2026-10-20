using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// One clock for everyone: the host's. The game has a single clock (TimeManager: dayCount + dayTime 0-24 h, a day =
/// 5 real minutes, no weather, seasons or sleep; the day just rolls over at 24:00). Everything follows it through its
/// per-frame DayTimeEvent (crop growth, lighting, the HUD clock) and DayStateEvent (town lamps at 0 / 6 / 12 / 18 h).
///
/// The host sends (day, time) every second. Clients keep running their own clock between messages (smooth) and snap
/// to the host's when they're off by more than a couple of game minutes (their pauses, cutscenes setting the time,
/// loading). A jump to a later day does what the game's rollover does (new day schedule, NewDayEvent); the day phase
/// is set straight away so lamps and lighting don't wait for the next scheduled change.
/// </summary>
internal static class TimeSync
{
    public static Plugin Plugin;

    const float SendInterval = 1f;
    const float SnapHours = 0.04f;   // ~2.4 game minutes (= ~0.5 s real)
    static float timer;

    /// <summary>Host, every frame.</summary>
    public static void Tick()
    {
        if (Plugin == null || !Plugin.IsHost || !Singleton<TimeManager>.HasMain) return;
        timer += Time.unscaledDeltaTime;
        if (timer < SendInterval) return;
        timer = 0f;
        var tm = Singleton<TimeManager>.main;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.TimeSync);
        w.Put(tm.dayCount);
        w.Put(tm.dayTime);
        Plugin.SendToAllClients(w, DeliveryMethod.Sequenced);
    }

    // ---- everyone goes to bed (todo #7) ----
    //
    // The demo has no bed and no night skip, so: everyone types /sleep in chat. When every player is ready, the host
    // skips its clock to 6:00 the next morning, the way the game's own rollover starts a new day; the clock message
    // takes everyone along (crops grow by the hours skipped, like any other time passing).

    const float WakeHour = 6f;
    static readonly System.Collections.Generic.HashSet<int> sleepy = new System.Collections.Generic.HashSet<int>();   // host
    static bool meSleepy;

    /// <summary>"/sleep": ready (or not any more) to skip to the morning.</summary>
    public static void ToggleSleep()
    {
        if (Plugin == null || !Plugin.Connected || Plugin.MyId < 0) { Chat.System("Not connected: there's nobody to wait for, so nothing to skip."); return; }
        meSleepy = !meSleepy;
        if (Plugin.IsHost) { SetSleepy(0, meSleepy); return; }
        var w = new NetDataWriter();
        w.Put((byte)MsgType.SleepReady);
        w.Put(meSleepy);
        Plugin.SendTo(0, w);
    }

    /// <summary>Host: a player is (no longer) ready to sleep.</summary>
    public static void OnSleepReady(NetDataReader r, int from) => SetSleepy(from, r.GetBool());

    /// <summary>Host: someone left; they don't hold the night up (the rest may all be ready now).</summary>
    public static void Forget(int id)
    {
        sleepy.Remove(id);
        if (sleepy.Count > 0) CheckSleep(-1, false);
    }

    /// <summary>A session ended.</summary>
    public static void Clear()
    {
        sleepy.Clear();
        meSleepy = false;
    }

    static void SetSleepy(int id, bool ready)
    {
        if (ready) sleepy.Add(id); else sleepy.Remove(id);
        CheckSleep(id, ready);
    }

    static void CheckSleep(int who, bool ready)
    {
        int total = Plugin.PlayerCount;
        bool all = sleepy.Count >= total && total > 0;
        if (all && Singleton<TimeManager>.HasMain)
        {
            var tm = Singleton<TimeManager>.main;
            if (tm.dayTime >= WakeHour) tm.dayCount++;
            tm.dayTime = WakeHour;
            tm.ScheduleStateEvents();
            Bus<NewDayEvent>.Invoke(new NewDayEvent { CurrentDay = tm.dayCount });
            if (tm.CurrentDayState != DayState.Morning) tm.CurrentDayState = DayState.Morning;
            sleepy.Clear();
            meSleepy = false;
            timer = SendInterval;   // the new time goes out right away
        }
        var w = new NetDataWriter();
        w.Put((byte)MsgType.SleepState);
        w.Put(who);
        w.Put(ready);
        w.Put(all ? 0 : sleepy.Count);
        w.Put(total);
        w.Put(all);
        Plugin.SendToAllClients(w, DeliveryMethod.ReliableOrdered);
        ShowSleep(who, ready, all ? 0 : sleepy.Count, total, all);
    }

    public static void OnSleepState(NetDataReader r)
    {
        int who = r.GetInt();
        bool ready = r.GetBool();
        int count = r.GetInt(), total = r.GetInt();
        bool all = r.GetBool();
        if (all) meSleepy = false;
        ShowSleep(who, ready, count, total, all);
    }

    static void ShowSleep(int who, bool ready, int count, int total, bool all)
    {
        if (all)
        {
            int day = Singleton<TimeManager>.HasMain ? Singleton<TimeManager>.main.dayCount : 0;
            Chat.System($"Everyone slept. Good morning: day {day}{(Plugin.IsHost ? "" : " (the host's clock is bringing you along)")}.");
            return;
        }
        if (who < 0) { Chat.System($"Ready to sleep: {count}/{total}."); return; }
        string name = who == Plugin.MyId ? "You" : Plugin.NameOf(who);
        Chat.System(ready
            ? $"{name} {(who == Plugin.MyId ? "are" : "is")} ready to sleep ({count}/{total}). Everyone type /sleep to skip to the morning."
            : $"{name} {(who == Plugin.MyId ? "are" : "is")} not sleepy any more ({count}/{total}).");
    }

    public static void OnTime(NetDataReader r)
    {
        int day = r.GetInt();
        float time = r.GetFloat();
        if (Plugin.IsHost || !Singleton<TimeManager>.HasMain) return;
        var tm = Singleton<TimeManager>.main;
        float diff = (day - tm.dayCount) * 24f + (time - tm.dayTime);
        if (Mathf.Abs(diff) < SnapHours) return;
        // A little ahead (we rolled over midnight first, or the host paused a moment): let the host catch up rather
        // than going back across midnight and starting the same day twice.
        if (diff < 0f && diff > -0.25f) return;

        bool newDay = day > tm.dayCount;
        tm.dayCount = day;
        tm.dayTime = time;
        if (newDay)
        {
            tm.ScheduleStateEvents();
            Bus<NewDayEvent>.Invoke(new NewDayEvent { CurrentDay = day });
            NetLog.Info($"Day {day} (the host's clock)");
        }
        var phase = time < 6f ? DayState.EarlyMorning : time < 12f ? DayState.Morning : time < 18f ? DayState.Afternoon : DayState.Night;
        if (tm.CurrentDayState != phase) tm.CurrentDayState = phase;   // fires DayStateEvent (lamps)
    }
}
