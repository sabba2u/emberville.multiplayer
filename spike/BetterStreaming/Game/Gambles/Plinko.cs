using System.Collections.Generic;
using BepInEx.Configuration;
using BetterStreaming.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterStreaming;

/// <summary>
/// Plinko: a ball dropped onto a triangle of pegs bounces down (Core.PlinkoBoard: real bounces, nothing picked in
/// advance) into one of the bins along the bottom. The middle bins come up most, the edges rarely: the streamer's bins
/// ([Plinko] Bins, left to right) put the extremes at the edges, the jackpot on one side and death on the other.
/// </summary>
internal sealed class Plinko : IEffect
{
    public string Id => "plinko";

    public const string Default = "jackpot,chainlightning,haste,heal,silly,shield,chickens,raid,death";
    static ConfigEntry<string> bins;

    public static void Bind(ConfigFile config) =>
        bins = config.Bind("Plinko", "Bins", Default, "The bins along the bottom, left to right (3 to 13): the middle ones come up most, the edges rarely");

    public static string BinsText { get => bins.Value; set => bins.Value = value; }

    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        string viewer = p.Viewer;
        Stage.Add("plinko", () =>
        {
            var list = Payout.List(bins.Value, Default, 3);
            if (list.Count > 13) list = list.GetRange(0, 13);
            return list.Count < 3 ? null : new Drop(viewer, list);
        });
        return true;
    }

    public void Stop() { }

    sealed class Drop : IShow
    {
        readonly string viewer;
        readonly List<string> outcomes;
        readonly PlinkoBoard board;
        readonly float S, midY;
        readonly RectTransform panel, ball;
        readonly List<Image> pegs = new(), binBoxes = new();
        readonly float[] pegFlash;
        readonly TextMeshProUGUI title, line;
        readonly float started;
        float landedAt = -1f, lastSound;
        int hits;

        public Drop(string viewer, List<string> outcomes)
        {
            this.viewer = viewer;
            this.outcomes = outcomes;
            board = new PlinkoBoard(outcomes.Count, new System.Random(Random.Range(0, int.MaxValue)));
            S = Mathf.Min(68f, 760f / (outcomes.Count + 1));
            float top = 1.2f, bottom = board.Bottom - 1.8f;
            midY = (top + bottom) / 2f;
            started = Time.unscaledTime;

            float w = (outcomes.Count + 1) * S, h = (top - bottom) * S;
            panel = GambleUi.Box("BS_Plinko", GambleUi.Root, new Vector2(w + 120f, h + 200f));
            panel.anchoredPosition = new Vector2(0f, 10f);
            GambleUi.Panel("Shadow", panel, new Vector2(w + 30f, h + 30f), new Color(0f, 0f, 0f, 0.45f)).rectTransform.anchoredPosition = new Vector2(8f, -10f);
            GambleUi.Panel("Rim", panel, new Vector2(w + 30f, h + 30f), new Color(0.95f, 0.75f, 0.3f));
            GambleUi.Panel("Board", panel, new Vector2(w + 10f, h + 10f), new Color(0.12f, 0.1f, 0.25f));

            foreach (var (x, y) in board.Pegs)
            {
                var peg = GambleUi.Shape("Peg", panel, "disc", Vector2.one * Mathf.Max(9f, 2f * PlinkoBoard.PegR * S + 4f), new Color(0.85f, 0.85f, 1f));
                peg.rectTransform.anchoredPosition = At(x, y);
                pegs.Add(peg);
            }
            pegFlash = new float[pegs.Count];
            for (int i = 0; i < outcomes.Count; i++)
            {
                var box = GambleUi.Panel("Bin" + i, panel, new Vector2(S - 6f, 1.4f * S), Payout.Colour(outcomes[i]), 0.25f);
                box.rectTransform.anchoredPosition = At(board.BinX(i), board.Bottom - 1.05f);
                binBoxes.Add(box);
                var label = GambleUi.Text("Label", box.transform, Short(outcomes[i]), 22f, Color.white, new Vector2(1.3f * S, S - 10f));
                label.rectTransform.localEulerAngles = new Vector3(0f, 0f, 90f);
                label.enableAutoSizing = true; label.fontSizeMin = 9f; label.fontSizeMax = 22f;
            }
            ball = GambleUi.Shape("Ball", panel, "disc", Vector2.one * (2f * PlinkoBoard.BallR * S + 4f), new Color(1f, 0.9f, 0.45f)).rectTransform;
            GambleUi.Shape("Shine", ball, "disc", ball.sizeDelta * 0.4f, new Color(1f, 1f, 0.9f)).rectTransform.anchoredPosition = ball.sizeDelta * 0.15f;

            title = GambleUi.Text("Title", panel, $"Plinko: {viewer}", 40f, new Color(1f, 0.93f, 0.82f), new Vector2(w + 300f, 60f));
            title.rectTransform.anchoredPosition = new Vector2(0f, h / 2f + 48f);
            line = GambleUi.Text("Result", panel, "", 44f, Color.white, new Vector2(w + 400f, 64f));
            line.rectTransform.anchoredPosition = new Vector2(0f, -h / 2f - 50f);
            board.Drop((Random.value - 0.5f) * 0.3f);
            board.Step(0f);
            ball.anchoredPosition = At(board.X, board.Y);
            panel.localScale = Vector3.zero;
            GambleUi.Sound("Play_UI_Tutorial_Panel_Popup");
        }

        Vector2 At(float x, float y) => new Vector2(x * S, (y - midY) * S);

        static string Short(string id) => id == DiceTable.Jackpot ? "JACKPOT" : id == "death" ? "DEATH" : Payout.Name(id);

        public bool Tick()
        {
            float age = Time.unscaledTime - started;
            panel.localScale = Vector3.one * GambleUi.Pop(age / 0.4f);
            if (age < 0.9f) return true;
            if (!board.Done)
            {
                board.Step(Mathf.Min(Time.unscaledDeltaTime, 0.05f));
                ball.anchoredPosition = At(board.X, board.Y);
                if (board.Hits != hits)
                {
                    hits = board.Hits;
                    // (the nearest peg lights up: that's the one it hit)
                    int best = 0; float bestD = float.MaxValue;
                    for (int i = 0; i < board.Pegs.Count; i++)
                    {
                        float dx = board.Pegs[i].x - board.X, dy = board.Pegs[i].y - board.Y, d = dx * dx + dy * dy;
                        if (d < bestD) { bestD = d; best = i; }
                    }
                    pegFlash[best] = 1f;
                    if (Time.unscaledTime - lastSound > 0.04f) { lastSound = Time.unscaledTime; GambleUi.Sound(hits % 2 == 0 ? "Play_UI_General_ChangeValueA" : "Play_UI_General_ChangeValueB"); }
                }
                for (int i = 0; i < pegs.Count; i++)
                {
                    if (pegFlash[i] <= 0f) continue;
                    pegFlash[i] = Mathf.Max(0f, pegFlash[i] - Time.unscaledDeltaTime * 3f);
                    pegs[i].color = Color.Lerp(new Color(0.85f, 0.85f, 1f), new Color(1f, 0.85f, 0.3f), pegFlash[i]);
                }
                line.text = Payout.Name(outcomes[board.Bin]);
                if (board.Done) Landed();
                return true;
            }
            float since = Time.unscaledTime - landedAt;
            var box = binBoxes[board.Bin];
            box.color = Color.Lerp(Payout.Colour(outcomes[board.Bin]), Color.white, Mathf.PingPong(since * 4f, 1f) * 0.6f);
            if (since < 2.6f) return true;
            return Payout.PopOut(panel, since, 2.6f) > 0f;
        }

        void Landed()
        {
            landedAt = Time.unscaledTime;
            string id = outcomes[board.Bin];
            line.text = id == DiceTable.Jackpot ? "JACKPOT!" : $"{Payout.Name(id)}!";
            line.color = id == DiceTable.Jackpot ? new Color(1f, 0.85f, 0.2f) : Payout.Good(id) ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.45f);
            GambleUi.Sound(Payout.Good(id) ? "Play_Player_LevelUp" : "Play_UI_General_Select_Important");
            Log.Info($"Streaming: plinko for {viewer}: bin {board.Bin + 1} of {outcomes.Count} -> {id} after {board.Hits} pegs");
            Payout.Fire(id, viewer, "plinko", 1f, "plinko");
        }

        public void Destroy() { if (panel) Object.Destroy(panel.gameObject); }
    }
}
