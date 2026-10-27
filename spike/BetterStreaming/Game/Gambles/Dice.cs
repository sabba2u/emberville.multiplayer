using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BetterStreaming.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterStreaming;

/// <summary>
/// Roll the Dice: three dice tumble in from the left, bounce, spin and land side by side; their total (3 to 18) picks
/// the outcome from the streamer's table ([Dice] Table, Core.DiceTable): low is bad, the middle mild, high good. Three
/// 1s: instant death; three 6s: the jackpot (a heal, chain lightning and a loot drop at once). "silly" is a random
/// naughty 30-second effect. Rolls bought while one is going wait their turn.
/// </summary>
internal sealed class Dice : IEffect
{
    public string Id => "dice";

    static ConfigEntry<string> table;

    public static void Bind(ConfigFile config) =>
        table = config.Bind("Dice", "Table", DiceTable.Default, "What each total does: total(s):effect, comma separated (silly = a random naughty 30 s effect, jackpot = heal + chain lightning + loot)");

    public static string TableText { get => table.Value; set => table.Value = value; }

    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        string viewer = p.Viewer;
        Stage.Add("the dice", () => new Roll(viewer, DiceTable.Parse(table.Value)));
        return true;
    }

    public void Stop() { }

    sealed class Roll : IShow
    {
        const float Size = 120f, Land = 1.6f, Gap = 150f;
        // where the pips go on a face, for 1 to 6 (in thirds of the die)
        static readonly Vector2[][] Pips =
        {
            new[] { new Vector2(0, 0) },
            new[] { new Vector2(-1, 1), new Vector2(1, -1) },
            new[] { new Vector2(-1, 1), new Vector2(0, 0), new Vector2(1, -1) },
            new[] { new Vector2(-1, 1), new Vector2(1, 1), new Vector2(-1, -1), new Vector2(1, -1) },
            new[] { new Vector2(-1, 1), new Vector2(1, 1), new Vector2(0, 0), new Vector2(-1, -1), new Vector2(1, -1) },
            new[] { new Vector2(-1, 1), new Vector2(1, 1), new Vector2(-1, 0), new Vector2(1, 0), new Vector2(-1, -1), new Vector2(1, -1) },
        };

        readonly string viewer, outcome;
        readonly int[] result = new int[3];
        readonly RectTransform panel;
        readonly RectTransform[] dice = new RectTransform[3];
        readonly List<Image>[] pips = new List<Image>[3];
        readonly int[] showing = new int[3];
        readonly float[] lastBounce = new float[3];
        readonly TextMeshProUGUI title, sum;
        readonly float started;
        bool landed;

        public Roll(string viewer, DiceTable t)
        {
            this.viewer = viewer;
            for (int i = 0; i < 3; i++) result[i] = Random.Range(1, 7);
            outcome = t.Outcome(result[0], result[1], result[2]);
            started = Time.unscaledTime;

            panel = GambleUi.Box("BS_Dice", GambleUi.Root, new Vector2(900f, 420f));
            panel.anchoredPosition = new Vector2(0f, 60f);
            title = GambleUi.Text("Title", panel, $"Roll the Dice: {viewer}", 40f, new Color(1f, 0.93f, 0.82f), new Vector2(900f, 60f));
            title.rectTransform.anchoredPosition = new Vector2(0f, 170f);
            sum = GambleUi.Text("Sum", panel, "", 46f, Color.white, new Vector2(1100f, 64f));
            sum.rectTransform.anchoredPosition = new Vector2(0f, -150f);
            for (int i = 0; i < 3; i++)
            {
                var die = GambleUi.Shape("Die" + i, panel, "rounded", new Vector2(Size, Size), new Color(0.98f, 0.95f, 0.88f)).rectTransform;
                GambleUi.Shape("Edge", die, "rounded", new Vector2(Size - 10f, Size - 10f), Color.white);
                dice[i] = die;
                pips[i] = new List<Image>();
                for (int k = 0; k < 6; k++) pips[i].Add(GambleUi.Shape("Pip" + k, die, "disc", new Vector2(Size * 0.18f, Size * 0.18f), new Color(0.15f, 0.08f, 0.08f)));
                Face(i, Random.Range(1, 7));
            }
            GambleUi.Sound("Play_UI_Creation_Randomize");
        }

        void Face(int i, int n)
        {
            showing[i] = n;
            var at = Pips[n - 1];
            for (int k = 0; k < 6; k++)
            {
                bool on = k < at.Length;
                pips[i][k].enabled = on;
                if (on)
                {
                    pips[i][k].rectTransform.anchoredPosition = at[k] * Size * 0.27f;
                    pips[i][k].color = n == 1 ? new Color(0.8f, 0.1f, 0.1f) : new Color(0.15f, 0.08f, 0.08f);
                }
            }
        }

        public bool Tick()
        {
            float age = Time.unscaledTime - started;
            for (int i = 0; i < 3; i++)
            {
                // each die a little behind the last: in from the left, three bounces getting smaller, spinning down
                float t = Mathf.Clamp01((age - i * 0.12f) / Land);
                float x = Mathf.Lerp(-1150f, (i - 1) * Gap, 1f - (1f - t) * (1f - t));
                float bounce = Mathf.Abs(Mathf.Sin(t * Mathf.PI * 3.5f)) * (1f - t) * 170f;
                dice[i].anchoredPosition = new Vector2(x, bounce);
                dice[i].localEulerAngles = new Vector3(0f, 0f, (1f - t) * (1f - t) * 900f * (i % 2 == 0 ? 1f : -1f));
                if (t < 1f)
                {
                    if (Time.unscaledTime - lastBounce[i] > 0.09f) { lastBounce[i] = Time.unscaledTime; Face(i, Random.Range(1, 7)); }   // (tumbling: faces flicking by)
                }
                else if (showing[i] != result[i]) { Face(i, result[i]); GambleUi.Sound("Play_UI_General_ChangeValueA"); }
            }
            float done = Land + 0.24f;
            if (age < done) return true;
            if (!landed) Landed();
            float since = age - done;
            if (since < 2.6f) return true;
            panel.localScale = Vector3.one * (1f - Mathf.Clamp01((since - 2.6f) / 0.3f));
            return since < 2.9f;
        }

        void Landed()
        {
            landed = true;
            int total = result.Sum();
            bool triple = result[0] == result[1] && result[1] == result[2];
            string how = triple && (result[0] == 1 || result[0] == 6) ? (result[0] == 1 ? "Three 1s!" : "Three 6s!") : $"{result[0]} + {result[1]} + {result[2]} = {total}";
            sum.text = $"{how}   {Payout.Name(outcome)}";
            bool good = Payout.Good(outcome);
            sum.color = outcome == DiceTable.Jackpot ? new Color(1f, 0.85f, 0.2f) : good ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.45f);
            GambleUi.Sound(good ? "Play_Player_LevelUp" : "Play_UI_General_Select_Important");
            Log.Info($"Streaming: the dice for {viewer}: {result[0]} {result[1]} {result[2]} -> {(outcome == "" ? "nothing" : outcome)}");
            Payout.Fire(outcome, viewer, how, 1f, "dice");
        }

        public void Destroy() { if (panel) Object.Destroy(panel.gameObject); }
    }
}
