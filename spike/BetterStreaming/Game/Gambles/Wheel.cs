using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BetterStreaming.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterStreaming;

/// <summary>
/// Spin the Wheel: a game-show wheel pops up in the middle of the screen and spins (Core.WheelPhysics: pegs between
/// the slices, a rubber flapper on the right that clicks over each one and throws the wheel back off a peg it's too slow
/// to clear). Green slices are nice, red naughty, gold spins again, the black sliver is instant death. Wherever it
/// stops happens, with the viewer's name. The slices and their widths are the streamer's ([Wheel] Slices); the feel
/// ([Wheel] Friction, Stiffness, Reach) too. Spins bought while one is going wait their turn.
/// </summary>
internal sealed class Wheel : IEffect
{
    public string Id => "wheel";

    static ConfigEntry<string> slicesSetting;
    static ConfigEntry<float> friction, stiffness, reach;

    public static void Bind(ConfigFile config)
    {
        slicesSetting = config.Bind("Wheel", "Slices", WheelSlices.Default, "The wheel's slices: effect:width, comma separated (again = spin once more; widths are shares: death:0.35 is a sliver)");
        friction = config.Bind("Wheel", "Friction", new WheelSettings().Friction, "How fast the wheel slows by itself (turns/s²)");
        stiffness = config.Bind("Wheel", "Stiffness", new WheelSettings().Stiffness, "How stiff the flapper is: stiffer = each peg costs more speed and throws the wheel back more often");
        reach = config.Bind("Wheel", "Reach", new WheelSettings().Reach, "How far the flapper reaches over the pegs (in turns of the wheel)");
    }

    public static string SlicesText { get => slicesSetting.Value; set => slicesSetting.Value = value; }

    /// <summary>The slices that can happen now (an effect that's switched off, or can't happen at all, is left out).</summary>
    static List<WheelSlice> Slices()
    {
        var known = Payout.Known().Append(WheelSlices.Again);
        var list = WheelSlices.Parse(slicesSetting.Value, known);
        return list.Count >= 2 ? list : WheelSlices.Parse(WheelSlices.Default, known);
    }

    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        Queue(p.Viewer);
        return true;
    }

    public void Stop() { }

    // ---- the spin ----

    /// <summary>A spin for them, when the stage is free (next: straight after this one).</summary>
    static void Queue(string viewer, bool next = false) => Stage.Add("the wheel", () =>
    {
        var slices = Slices();
        if (slices.Count >= 2) return new Spin(viewer, slices);
        Log.Warn("Streaming: the wheel has no slices that can happen here");
        return null;
    }, next);

    sealed class Spin : IShow
    {
        const float R = 250f;
        static readonly Color Rim = new(0.95f, 0.75f, 0.3f), Peg = new(1f, 0.9f, 0.6f), Line = new(0.25f, 0.15f, 0.08f);

        readonly string viewer;
        readonly List<WheelSlice> slices;
        readonly WheelPhysics wheel;
        readonly RectTransform panel, spinner, flapper;
        readonly List<Image> pieces = new();
        readonly List<Color> colours = new();
        readonly TextMeshProUGUI title, passing;
        float started, landedAt = -1f, lastClickSound;
        int clicks, shownSlice = -1;
        bool spun, fired;

        public Spin(string viewer, List<WheelSlice> slices)
        {
            this.viewer = viewer;
            this.slices = slices;
            wheel = new WheelPhysics(slices.Select(s => s.Weight).ToList(), new WheelSettings
            {
                Friction = Mathf.Max(0.01f, friction.Value), Stiffness = Mathf.Max(1f, stiffness.Value), Reach = Mathf.Clamp(reach.Value, 0.002f, 0.05f),
            });
            wheel.Angle = Random.value;
            started = Time.unscaledTime;

            panel = GambleUi.Box("BS_Wheel", GambleUi.Root, new Vector2(2 * R + 160f, 2 * R + 200f));
            panel.anchoredPosition = new Vector2(0f, 20f);
            GambleUi.Shape("Shadow", panel, "disc", new Vector2(2 * R + 40f, 2 * R + 40f), new Color(0f, 0f, 0f, 0.45f)).rectTransform.anchoredPosition = new Vector2(6f, -8f);
            GambleUi.Shape("Rim", panel, "disc", new Vector2(2 * R + 28f, 2 * R + 28f), Rim);

            spinner = GambleUi.Box("Spinner", panel, new Vector2(2 * R, 2 * R));
            for (int i = 0; i < slices.Count; i++)
            {
                var col = SliceColour(slices[i].Id, i);
                colours.Add(col);
                var img = GambleUi.Shape("Slice" + i, spinner, "disc", new Vector2(2 * R, 2 * R), col);
                img.type = Image.Type.Filled;
                img.fillMethod = Image.FillMethod.Radial360;
                img.fillOrigin = (int)Image.Origin360.Right;
                img.fillClockwise = false;
                img.fillAmount = wheel.Width(i);
                img.rectTransform.localEulerAngles = new Vector3(0f, 0f, wheel.Starts[i] * 360f);
                pieces.Add(img);
            }
            for (int i = 0; i < slices.Count; i++)
            {
                // the line between slices, its peg on the rim, and the slice's name running out along it
                var line = GambleUi.Box("Line" + i, spinner, new Vector2(R, 3f));
                line.pivot = new Vector2(0f, 0.5f);
                line.localEulerAngles = new Vector3(0f, 0f, wheel.Starts[i] * 360f);
                line.gameObject.AddComponent<Image>().color = Line;
                var peg = GambleUi.Shape("Peg" + i, line, "disc", new Vector2(14f, 14f), Peg);
                peg.rectTransform.anchorMin = peg.rectTransform.anchorMax = new Vector2(0f, 0.5f);
                peg.rectTransform.anchoredPosition = new Vector2(R - 12f, 0f);

                float mid = (wheel.Starts[i] + wheel.Width(i) / 2f) * 360f;
                var arm = GambleUi.Box("Label" + i, spinner, new Vector2(10f, 10f));
                arm.localEulerAngles = new Vector3(0f, 0f, mid);
                float tall = Mathf.Clamp(2f * Mathf.PI * R * 0.62f * wheel.Width(i) * 0.8f, 10f, 46f);
                var label = GambleUi.Text("Text", arm, Name(slices[i].Id), 26f, Color.white, new Vector2(R * 0.72f, tall));
                label.rectTransform.anchoredPosition = new Vector2(R * 0.56f, 0f);
                label.alignment = TextAlignmentOptions.Right;
                label.enableAutoSizing = true;
                label.fontSizeMin = 8f;
                label.fontSizeMax = 26f;
            }
            GambleUi.Shape("Hub", spinner, "disc", new Vector2(70f, 70f), Rim);
            GambleUi.Shape("HubIn", spinner, "disc", new Vector2(46f, 46f), new Color(0.57f, 0.27f, 1f));

            // the flapper: on the right, a rubber tongue over the pegs, bending as they push it
            flapper = GambleUi.Box("FlapperPivot", panel, new Vector2(10f, 10f));
            flapper.anchoredPosition = new Vector2(R + 34f, 0f);
            var tongue = GambleUi.Shape("Flapper", flapper, "triangle", new Vector2(64f, 34f), new Color(0.85f, 0.15f, 0.2f));
            tongue.rectTransform.pivot = new Vector2(1f, 0.5f);
            tongue.rectTransform.anchoredPosition = new Vector2(6f, 0f);

            title = GambleUi.Text("Title", panel, $"Spin the Wheel: {viewer}", 40f, new Color(1f, 0.93f, 0.82f), new Vector2(2 * R + 300f, 60f));
            title.rectTransform.anchoredPosition = new Vector2(0f, R + 62f);
            passing = GambleUi.Text("Passing", panel, "", 46f, Color.white, new Vector2(2 * R + 300f, 64f));
            passing.rectTransform.anchoredPosition = new Vector2(0f, -R - 62f);
            panel.localScale = Vector3.zero;
            Layout();
            GambleUi.Sound("Play_UI_Tutorial_Panel_Popup");
        }

        /// <summary>A frame: pop in, spin, show where it landed, fire it, pop out. False: over.</summary>
        public bool Tick()
        {
            float age = Time.unscaledTime - started;
            if (!spun)
            {
                panel.localScale = Vector3.one * GambleUi.Pop(age / 0.4f);
                if (age < 0.7f) return true;
                spun = true;
                wheel.Spin(WheelPhysics.RandomSpeed(Random.value, 1f));
            }
            if (!wheel.Done)
            {
                wheel.Step(Mathf.Min(Time.unscaledDeltaTime, 0.05f));
                Layout();
                if (wheel.Done) Landed();
                return true;
            }
            float since = Time.unscaledTime - landedAt;
            int at = wheel.PointerSlice;
            pieces[at].color = Color.Lerp(colours[at], Color.white, Mathf.PingPong(since * 4f, 1f) * 0.6f);   // (the winner flashes)
            if (since < 2.2f) return true;
            panel.localScale = Vector3.one * (1f - Mathf.Clamp01((since - 2.2f) / 0.3f));
            return since < 2.5f;
        }

        void Layout()
        {
            spinner.localEulerAngles = new Vector3(0f, 0f, wheel.Angle * 360f);
            flapper.localEulerAngles = new Vector3(0f, 0f, -wheel.Bend * 32f);   // (a peg coming up from below pushes the tip up)
            if (wheel.Clicks != clicks)
            {
                clicks = wheel.Clicks;
                if (Time.unscaledTime - lastClickSound > 0.035f)
                {
                    lastClickSound = Time.unscaledTime;
                    GambleUi.Sound(clicks % 2 == 0 ? "Play_UI_General_ChangeValueA" : "Play_UI_General_ChangeValueB");
                }
            }
            int now = wheel.PointerSlice;
            if (now != shownSlice)
            {
                if (shownSlice >= 0) pieces[shownSlice].color = colours[shownSlice];
                shownSlice = now;
                pieces[now].color = Color.Lerp(colours[now], Color.white, 0.25f);
                passing.text = Name(slices[now].Id);
                passing.color = Color.Lerp(colours[now], Color.white, 0.35f);
            }
        }

        void Landed()
        {
            landedAt = Time.unscaledTime;
            var id = slices[wheel.PointerSlice].Id;
            bool good = id == WheelSlices.Again || Payout.Good(id);
            GambleUi.Sound(good ? "Play_Player_LevelUp" : "Play_UI_General_Select_Important");
            passing.text = id == WheelSlices.Again ? "Spin again!" : $"{Name(id)}!";
            Log.Info($"Streaming: the wheel for {viewer} landed on {id}{(wheel.RolledBack ? " (rolled back off a peg)" : "")} after {wheel.Clicks} pegs");
            if (fired) return;
            fired = true;
            if (id == WheelSlices.Again) { Queue(viewer, next: true); return; }   // (straight after this one)
            Payout.Fire(id, viewer, "the wheel says so", 1f, "wheel");
        }

        public void Destroy() { if (panel) Object.Destroy(panel.gameObject); }
    }

    static string Name(string id) => id == WheelSlices.Again ? "Spin again" : Payout.Name(id);

    /// <summary>Green for nice, red for naughty (two shades taking turns), gold for spin again, black for death.</summary>
    static Color SliceColour(string id, int i)
    {
        if (id == WheelSlices.Again) return new Color(0.95f, 0.72f, 0.15f);
        if (id == "death") return new Color(0.08f, 0.06f, 0.07f);
        if (id == DiceTable.Jackpot) return new Color(1f, 0.85f, 0.35f);
        if (id == DiceTable.Silly) return new Color(0.85f, 0.4f, 0.65f);
        bool nice = Streamer.Def(id)?.Mood == Mood.Nice;
        bool light = i % 2 == 0;
        return nice ? (light ? new Color(0.2f, 0.62f, 0.25f) : new Color(0.13f, 0.48f, 0.2f))
                    : (light ? new Color(0.78f, 0.2f, 0.2f) : new Color(0.6f, 0.13f, 0.15f));
    }
}
