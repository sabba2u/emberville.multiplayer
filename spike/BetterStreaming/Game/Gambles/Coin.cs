using BepInEx.Configuration;
using BetterStreaming.Core;
using TMPro;
using UnityEngine;

namespace BetterStreaming;

/// <summary>
/// Coin flip, the cheap gamble: a gold coin spins up into the air and lands. Heads is something nice ([Coin] Heads),
/// tails something naughty ([Coin] Tails). "heads" or "tails" in the cheer calls it: called right, it's doubled. Once
/// in a while ([Coin] EdgeChance) it lands on its edge: the jackpot.
/// </summary>
internal sealed class Coin : IEffect
{
    public string Id => "coin";

    static ConfigEntry<string> heads, tails;
    static ConfigEntry<float> edge;

    public static void Bind(ConfigFile config)
    {
        heads = config.Bind("Coin", "Heads", "heal,haste,shield,regen,gold", "What heads can be (one of these at random)");
        tails = config.Bind("Coin", "Tails", "silly", "What tails can be (one at random; silly = a random naughty 30 s effect)");
        edge = config.Bind("Coin", "EdgeChance", 0.01f, "How often it lands on its edge: the jackpot (0 to 0.2)");
    }

    public static string HeadsText { get => heads.Value; set => heads.Value = value; }
    public static string TailsText { get => tails.Value; set => tails.Value = value; }

    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        string viewer = p.Viewer, call = CoinCall.Of(p.Message);
        Stage.Add("the coin", () => new Flip(viewer, call));
        return true;
    }

    public void Stop() { }

    sealed class Flip : IShow
    {
        const float Air = 1.7f, Height = 330f, D = 230f;

        readonly string viewer, call, side;
        readonly int halfTurns;
        readonly RectTransform panel, coin;
        readonly UnityEngine.UI.Image face;
        readonly TextMeshProUGUI letter, line;
        readonly float started;
        float landedAt = -1f;

        public Flip(string viewer, string call)
        {
            this.viewer = viewer;
            this.call = call;
            float roll = Random.value;
            side = roll < Mathf.Clamp(edge.Value, 0f, 0.2f) ? "edge" : Random.value < 0.5f ? "heads" : "tails";
            halfTurns = 16 + (side == "tails" ? 1 : 0);
            started = Time.unscaledTime;

            panel = GambleUi.Box("BS_Coin", GambleUi.Root, new Vector2(800f, 700f));
            panel.anchoredPosition = new Vector2(0f, 0f);
            var shadow = GambleUi.Shape("Shadow", panel, "disc", new Vector2(D, D * 0.25f), new Color(0f, 0f, 0f, 0.4f));
            shadow.rectTransform.anchoredPosition = new Vector2(0f, -170f);
            coin = GambleUi.Box("Coin", panel, new Vector2(D, D));
            GambleUi.Shape("Rim", coin, "disc", new Vector2(D, D), new Color(0.75f, 0.5f, 0.1f));
            face = GambleUi.Shape("Face", coin, "disc", new Vector2(D - 22f, D - 22f), new Color(1f, 0.8f, 0.25f));
            GambleUi.Shape("Ring", coin, "ring", new Vector2(D - 50f, D - 50f), new Color(0.85f, 0.6f, 0.15f));
            letter = GambleUi.Text("Letter", coin, "H", 110f, new Color(0.6f, 0.38f, 0.05f), new Vector2(D, D));
            letter.outlineWidth = 0f;

            string calling = call == "" ? "" : $" (calls {call})";
            var title = GambleUi.Text("Title", panel, $"Coin flip: {viewer}{calling}", 40f, new Color(1f, 0.93f, 0.82f), new Vector2(1000f, 60f));
            title.rectTransform.anchoredPosition = new Vector2(0f, 290f);
            line = GambleUi.Text("Result", panel, "", 46f, Color.white, new Vector2(1100f, 64f));
            line.rectTransform.anchoredPosition = new Vector2(0f, -250f);
            panel.localScale = Vector3.zero;
            Place(0f);
            GambleUi.Sound("Play_UI_Tutorial_Panel_Popup");
        }

        /// <summary>The coin t seconds into its flight: up and down, turning over (squashed: we see it edge-on).</summary>
        void Place(float t)
        {
            float k = Mathf.Clamp01(t / Air);
            float y = 4f * Height * k * (1f - k) - 120f;
            float phase = halfTurns * Mathf.PI * (1f - (1f - k) * (1f - k));
            float c = Mathf.Cos(phase);
            if (side == "edge" && k > 0.85f) c = Mathf.Lerp(c, 0.06f, (k - 0.85f) / 0.15f);   // (standing up as it lands)
            coin.anchoredPosition = new Vector2(0f, y);
            coin.localScale = new Vector3(1f, Mathf.Max(0.06f, Mathf.Abs(c)), 1f);
            letter.text = c >= 0f ? "H" : "T";
            face.color = c >= 0f ? new Color(1f, 0.8f, 0.25f) : new Color(0.95f, 0.72f, 0.2f);
        }

        public bool Tick()
        {
            float age = Time.unscaledTime - started;
            panel.localScale = Vector3.one * GambleUi.Pop(age / 0.4f);
            if (age < 0.6f) return true;
            float t = age - 0.6f;
            if (t < Air) { Place(t); return true; }
            if (landedAt < 0f) { Place(Air); Landed(); }
            float since = Time.unscaledTime - landedAt;
            // a little bounce on landing, and an edge coin wobbling
            coin.anchoredPosition = new Vector2(0f, -120f + Mathf.Abs(Mathf.Sin(since * 9f)) * 30f * Mathf.Max(0f, 1f - since * 2f));
            if (side == "edge") coin.localEulerAngles = new Vector3(0f, 0f, Mathf.Sin(since * 12f) * 6f * Mathf.Max(0f, 1.5f - since));
            if (since < 2.6f) return true;
            return Payout.PopOut(panel, since, 2.6f) > 0f;
        }

        void Landed()
        {
            landedAt = Time.unscaledTime;
            string id = side == "edge" ? DiceTable.Jackpot : Payout.Pick(side == "heads" ? heads.Value : tails.Value);
            bool called = call != "" && call == side;
            float size = called ? 2f : 1f;
            string what = side == "edge" ? "It landed on its EDGE!" : side == "heads" ? "Heads!" : "Tails!";
            line.text = $"{what} {Payout.Name(id)}{(called ? " x2 (called it!)" : "")}";
            line.color = side == "edge" ? new Color(1f, 0.85f, 0.2f) : Payout.Good(id) ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.45f);
            GambleUi.Sound(Payout.Good(id) ? "Play_Player_LevelUp" : "Play_UI_General_Select_Important");
            Log.Info($"Streaming: the coin for {viewer}: {side}{(call != "" ? $" (called {call})" : "")} -> {(id == "" ? "nothing" : id)}");
            Payout.Fire(id, viewer, what, size, "coin");
        }

        public void Destroy() { if (panel) Object.Destroy(panel.gameObject); }
    }
}
