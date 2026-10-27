using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BetterStreaming.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterStreaming;

/// <summary>
/// Pull the Slots: a cabinet with three reels; the lever comes down, the reels blur and stop left to right with a
/// clunk. Two matching on the first reels and the third spins on longer, slowing, for the tension. What the line shows
/// pays (Core.SlotMachine): three of a kind that effect doubled, three 7s the jackpot, three skulls instant death, two
/// of a kind that effect, no match the streamer's [Slots] NoMatch (nothing by default). The reels' symbols and how often
/// each comes up: [Slots] Reel.
/// </summary>
internal sealed class Slots : IEffect
{
    public string Id => "slots";

    static ConfigEntry<string> reel, noMatch;

    public static void Bind(ConfigFile config)
    {
        reel = config.Bind("Slots", "Reel", SlotMachine.Default, "The reels' symbols: effect:how often, comma separated (jackpot = the 7: heal + chain lightning + loot; death = the skull)");
        noMatch = config.Bind("Slots", "NoMatch", "", "What no match does: an effect, silly (a random naughty 30 s one), or empty for nothing");
    }

    public static string ReelText { get => reel.Value; set => reel.Value = value; }
    public static string NoMatchText { get => noMatch.Value; set => noMatch.Value = value; }

    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        string viewer = p.Viewer;
        Stage.Add("the slots", () =>
        {
            var known = Payout.Known().ToList();
            var symbols = WheelSlices.Parse(reel.Value, known);
            if (symbols.Count < 3) symbols = WheelSlices.Parse(SlotMachine.Default, known);
            return symbols.Count < 2 ? null : new Pull(viewer, symbols);
        });
        return true;
    }

    public void Stop() { }

    /// <summary>A symbol's face: the 7, the skull, or the effect's short name.</summary>
    static string Face(string id) => id switch
    {
        DiceTable.Jackpot => "7",
        "death" => "SKULL",
        DiceTable.Silly => "???",
        _ => Streamer.Def(id)?.Name ?? id,
    };

    sealed class Pull : IShow
    {
        const float H = 100f, ReelW = 190f, Gap = 210f;
        const int Strip = 40;

        readonly string viewer;
        readonly string[] result = new string[3];
        readonly string[][] strips = new string[3][];
        readonly float[] offset = new float[3], stopAt = new float[3], from = new float[3], to = new float[3];
        readonly bool[] stopping = new bool[3], stopped = new bool[3];
        readonly List<(Image tile, TextMeshProUGUI text, string shown)>[] tiles = new List<(Image, TextMeshProUGUI, string)>[3];
        readonly RectTransform panel, lever;
        readonly TextMeshProUGUI title, line;
        readonly float started;
        float doneAt = -1f;
        bool tense;

        public Pull(string viewer, List<WheelSlice> symbols)
        {
            this.viewer = viewer;
            started = Time.unscaledTime;
            for (int r = 0; r < 3; r++)
            {
                result[r] = Weighted.Pick(symbols, Random.value);
                strips[r] = new string[Strip];
                for (int i = 0; i < Strip; i++) strips[r][i] = Weighted.Pick(symbols, Random.value);
                offset[r] = Random.Range(0, Strip);
            }
            tense = result[0] == result[1];
            stopAt[0] = 1.9f; stopAt[1] = 2.5f; stopAt[2] = tense ? 4.6f : 3.1f;

            panel = GambleUi.Box("BS_Slots", GambleUi.Root, new Vector2(860f, 640f));
            panel.anchoredPosition = new Vector2(0f, 30f);
            GambleUi.Panel("Shadow", panel, new Vector2(760f, 520f), new Color(0f, 0f, 0f, 0.45f)).rectTransform.anchoredPosition = new Vector2(8f, -10f);
            GambleUi.Panel("Rim", panel, new Vector2(760f, 520f), new Color(0.95f, 0.75f, 0.3f));
            GambleUi.Panel("Body", panel, new Vector2(736f, 496f), new Color(0.55f, 0.08f, 0.12f));
            GambleUi.Panel("Window", panel, new Vector2(3 * Gap + 20f, 3 * H + 30f), new Color(0.2f, 0.06f, 0.06f)).rectTransform.anchoredPosition = new Vector2(0f, -10f);
            for (int r = 0; r < 3; r++)
            {
                var window = GambleUi.Panel("Reel" + r, panel, new Vector2(ReelW, 3 * H), new Color(0.98f, 0.95f, 0.88f), 0.25f).rectTransform;
                window.anchoredPosition = new Vector2((r - 1) * Gap, -10f);
                window.gameObject.AddComponent<RectMask2D>();
                tiles[r] = new List<(Image, TextMeshProUGUI, string)>();
                for (int k = 0; k < 5; k++)
                {
                    var tile = GambleUi.Panel("Tile" + k, window, new Vector2(ReelW - 20f, H - 12f), Color.white, 0.3f);
                    var text = GambleUi.Text("Face", tile.transform, "", 30f, Color.white, new Vector2(ReelW - 34f, H - 24f));
                    text.enableAutoSizing = true; text.fontSizeMin = 12f; text.fontSizeMax = 34f;
                    tiles[r].Add((tile, text, null));
                }
            }
            // the pay line: a gold bar each side of the middle row
            foreach (float side in new[] { -1f, 1f })
                GambleUi.Shape("Line", panel, "triangle", new Vector2(30f, 34f), new Color(1f, 0.85f, 0.3f)).rectTransform.anchoredPosition = new Vector2(side * (1.5f * Gap + 14f), -10f);
            panel.Find("Line").localEulerAngles = new Vector3(0f, 0f, 180f);

            // the lever on the right: a stick and a red knob, pulled down when it starts
            lever = GambleUi.Box("Lever", panel, new Vector2(10f, 10f));
            lever.anchoredPosition = new Vector2(395f, 10f);
            var stick = GambleUi.Panel("Stick", lever, new Vector2(16f, 170f), new Color(0.75f, 0.75f, 0.8f), 0.15f).rectTransform;
            stick.pivot = new Vector2(0.5f, 0f);
            var knob = GambleUi.Shape("Knob", lever, "disc", new Vector2(46f, 46f), new Color(0.9f, 0.1f, 0.15f)).rectTransform;
            knob.anchoredPosition = new Vector2(0f, 170f);

            title = GambleUi.Text("Title", panel, $"Pull the Slots: {viewer}", 40f, new Color(1f, 0.93f, 0.82f), new Vector2(900f, 60f));
            title.rectTransform.anchoredPosition = new Vector2(0f, 205f);
            line = GambleUi.Text("Result", panel, "", 46f, Color.white, new Vector2(1100f, 64f));
            line.rectTransform.anchoredPosition = new Vector2(0f, -232f);
            panel.localScale = Vector3.zero;
            Draw();
            GambleUi.Sound("Play_UI_Tutorial_Panel_Popup");
        }

        public bool Tick()
        {
            float age = Time.unscaledTime - started;
            panel.localScale = Vector3.one * GambleUi.Pop(age / 0.4f);
            // the lever: down from 0.5 s, back up by 1 s
            float pull = age < 0.5f ? 0f : age < 0.7f ? (age - 0.5f) / 0.2f : 1f - Mathf.Clamp01((age - 0.75f) / 0.35f);
            lever.localScale = new Vector3(1f, Mathf.Lerp(1f, -0.6f, pull), 1f);
            if (age >= 0.6f && age - Time.unscaledDeltaTime < 0.6f) GambleUi.Sound("Play_UI_Creation_Randomize");
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            for (int r = 0; r < 3; r++)
            {
                if (stopped[r] || age < 0.6f) continue;
                if (!stopping[r])
                {
                    // (spinning: up to speed; the tense third reel slows as its time comes)
                    float speed = Mathf.Min(22f, (age - 0.6f) * 40f);
                    if (r == 2 && tense && age > stopAt[1]) { speed *= Mathf.Lerp(1f, 0.35f, Mathf.Clamp01((age - stopAt[1]) / (stopAt[2] - stopAt[1]))); line.text = "..."; }
                    offset[r] += speed * dt;
                    if (age >= stopAt[r])
                    {
                        // where it lands: some symbols on, with the result put there
                        stopping[r] = true;
                        from[r] = offset[r];
                        to[r] = Mathf.Floor(offset[r]) + (tense && r == 2 ? 4f : 7f);
                        strips[r][Mod((int)to[r])] = result[r];
                    }
                }
                else
                {
                    float t = Mathf.Clamp01((age - stopAt[r]) / (tense && r == 2 ? 1.1f : 0.7f));
                    offset[r] = Mathf.LerpUnclamped(from[r], to[r], GambleUi.Pop(t));
                    if (t >= 1f) { stopped[r] = true; offset[r] = to[r]; GambleUi.Sound("Play_UI_General_ChangeValueA"); }
                }
            }
            Draw();
            if (!stopped[2]) return true;
            if (doneAt < 0f) Landed();
            float since = Time.unscaledTime - doneAt;
            line.alpha = 0.75f + 0.25f * Mathf.PingPong(since * 4f, 1f);
            if (since < 2.8f) return true;
            return Payout.PopOut(panel, since, 2.8f) > 0f;
        }

        static int Mod(int i) => ((i % Strip) + Strip) % Strip;

        /// <summary>Each reel's tiles where its offset puts them (the symbol at the offset sits on the line).</summary>
        void Draw()
        {
            for (int r = 0; r < 3; r++)
            {
                int at = Mathf.FloorToInt(offset[r]);
                for (int k = 0; k < 5; k++)
                {
                    int idx = at + k - 2;
                    var (tile, text, shown) = tiles[r][k];
                    string id = strips[r][Mod(idx)];
                    tile.rectTransform.anchoredPosition = new Vector2(0f, -(idx - offset[r]) * H);
                    if (shown == id) continue;
                    tile.color = Payout.Colour(id);
                    text.text = Face(id);
                    text.color = id == DiceTable.Jackpot ? new Color(0.75f, 0.05f, 0.1f) : Color.white;
                    text.fontSizeMax = id == DiceTable.Jackpot ? 72f : 34f;
                    tiles[r][k] = (tile, text, id);
                }
            }
        }

        void Landed()
        {
            doneAt = Time.unscaledTime;
            var (id, size) = SlotMachine.Pays(result[0], result[1], result[2], noMatch.Value.Trim().ToLowerInvariant());
            if (id != "" && id != DiceTable.Silly && id != DiceTable.Jackpot && Streamer.Def(id) == null) id = "";
            bool three = result[0] == result[1] && result[1] == result[2];
            line.text = id == "" ? (tense ? "So close!" : "No luck this time")
                      : id == DiceTable.Jackpot ? "JACKPOT!"
                      : $"{(three ? "Three of a kind! " : "")}{Payout.Name(id)}{(size > 1f ? " x2" : "")}";
            line.color = id == "" ? new Color(0.8f, 0.8f, 0.85f) : id == DiceTable.Jackpot ? new Color(1f, 0.85f, 0.2f) : Payout.Good(id) ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.45f);
            GambleUi.Sound(id == "" ? "Play_UI_General_ChangeValueB" : Payout.Good(id) ? "Play_Player_LevelUp" : "Play_UI_General_Select_Important");
            Log.Info($"Streaming: the slots for {viewer}: {string.Join(" ", result)} -> {(id == "" ? "nothing" : id)} x{size}");
            Payout.Fire(id, viewer, string.Join(" | ", result.Select(Face)), size, "slots");
        }

        public void Destroy() { if (panel) Object.Destroy(panel.gameObject); }
    }
}
