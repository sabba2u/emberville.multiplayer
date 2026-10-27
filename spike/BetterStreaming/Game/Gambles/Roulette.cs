using System.Collections.Generic;
using BepInEx.Configuration;
using BetterStreaming.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterStreaming;

/// <summary>
/// Roulette (the casino's, not the weapon roulette): a European wheel turns one way, the ball races round the rim the
/// other, slows, drops in, rattles over a few pockets and settles. Red is something naughty ([Roulette] Red), black
/// something nice ([Roulette] Black), the green 0 the jackpot ([Roulette] Green). "red", "black" or "green" in the cheer
/// is a bet: if it lands on that colour, it's doubled.
/// </summary>
internal sealed class CasinoRoulette : IEffect
{
    public string Id => "casino";

    static ConfigEntry<string> red, black, green;

    public static void Bind(ConfigFile config)
    {
        red = config.Bind("Roulette", "Red", "raid,chickens,poison,drunk,freeze,storm", "What red can be (one of these at random)");
        black = config.Bind("Roulette", "Black", "heal,haste,shield,chainlightning,loot,regen", "What black can be (one at random)");
        green = config.Bind("Roulette", "Green", "jackpot", "What the green 0 is (jackpot = heal + chain lightning + loot)");
    }

    public static string RedText { get => red.Value; set => red.Value = value; }
    public static string BlackText { get => black.Value; set => black.Value = value; }

    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        string viewer = p.Viewer, bet = RouletteTable.Bet(p.Message);
        Stage.Add("roulette", () => new Spin(viewer, bet));
        return true;
    }

    public void Stop() { }

    sealed class Spin : IShow
    {
        const float R = 240f, Pocket = 360f / 37f;
        static readonly Color Red = new(0.78f, 0.12f, 0.12f), Black = new(0.1f, 0.09f, 0.1f), Green = new(0.1f, 0.55f, 0.2f), Gold = new(0.95f, 0.75f, 0.3f);

        readonly string viewer, bet;
        readonly RectTransform panel, spinner, ball;
        readonly List<Image> pockets = new();
        readonly TextMeshProUGUI title, line;
        readonly float started;
        float wheel, wheelSpeed = 50f;        // (degrees; the wheel turns anticlockwise)
        float ballAngle, ballSpeed;           // (round the rim, clockwise: negative)
        float rel, relSpeed, dropAt = -1f, nextKick, landedAt = -1f, snapFrom;
        int kicks, landed = -1;

        public Spin(string viewer, string bet)
        {
            this.viewer = viewer;
            this.bet = bet;
            started = Time.unscaledTime;
            ballAngle = Random.Range(0f, 360f);
            ballSpeed = -Random.Range(560f, 760f);

            panel = GambleUi.Box("BS_Roulette", GambleUi.Root, new Vector2(2 * R + 200f, 2 * R + 220f));
            panel.anchoredPosition = new Vector2(0f, 20f);
            GambleUi.Shape("Shadow", panel, "disc", new Vector2(2 * R + 60f, 2 * R + 60f), new Color(0f, 0f, 0f, 0.45f)).rectTransform.anchoredPosition = new Vector2(6f, -8f);
            GambleUi.Shape("Bowl", panel, "disc", new Vector2(2 * R + 50f, 2 * R + 50f), new Color(0.4f, 0.22f, 0.1f));
            GambleUi.Shape("Track", panel, "disc", new Vector2(2 * R + 26f, 2 * R + 26f), new Color(0.55f, 0.32f, 0.14f));

            spinner = GambleUi.Box("Spinner", panel, new Vector2(2 * R, 2 * R));
            for (int i = 0; i < 37; i++)
            {
                int n = RouletteTable.Order[i];
                var img = GambleUi.Shape("Pocket" + n, spinner, "disc", new Vector2(2 * R - 40f, 2 * R - 40f), ColourOf(n));
                img.type = Image.Type.Filled;
                img.fillMethod = Image.FillMethod.Radial360;
                img.fillOrigin = (int)Image.Origin360.Right;
                img.fillClockwise = false;
                img.fillAmount = 1f / 37f;
                img.rectTransform.localEulerAngles = new Vector3(0f, 0f, i * Pocket);
                pockets.Add(img);
                var arm = GambleUi.Box("Num" + n, spinner, new Vector2(10f, 10f));
                arm.localEulerAngles = new Vector3(0f, 0f, (i + 0.5f) * Pocket);
                var label = GambleUi.Text("Text", arm, n.ToString(), 20f, Color.white, new Vector2(40f, 26f));
                label.rectTransform.anchoredPosition = new Vector2(R - 42f, 0f);
                label.rectTransform.localEulerAngles = new Vector3(0f, 0f, -90f);
            }
            GambleUi.Shape("Cone", spinner, "disc", new Vector2(2 * R * 0.6f, 2 * R * 0.6f), new Color(0.45f, 0.25f, 0.1f));
            for (int i = 0; i < 4; i++)
            {
                var spoke = GambleUi.Box("Spoke" + i, spinner, new Vector2(2 * R * 0.5f, 10f));
                spoke.localEulerAngles = new Vector3(0f, 0f, i * 45f);
                spoke.gameObject.AddComponent<Image>().color = Gold;
            }
            GambleUi.Shape("Hub", spinner, "disc", new Vector2(60f, 60f), Gold);
            ball = GambleUi.Shape("Ball", panel, "disc", new Vector2(22f, 22f), new Color(0.97f, 0.97f, 0.95f)).rectTransform;

            string betting = bet == "" ? "" : $" (bets {bet})";
            title = GambleUi.Text("Title", panel, $"Roulette: {viewer}{betting}", 40f, new Color(1f, 0.93f, 0.82f), new Vector2(2 * R + 400f, 60f));
            title.rectTransform.anchoredPosition = new Vector2(0f, R + 70f);
            line = GambleUi.Text("Result", panel, "", 46f, Color.white, new Vector2(2 * R + 500f, 64f));
            line.rectTransform.anchoredPosition = new Vector2(0f, -R - 70f);
            panel.localScale = Vector3.zero;
            Layout(R + 2f);
            GambleUi.Sound("Play_UI_Tutorial_Panel_Popup");
        }

        static Color ColourOf(int n) => RouletteTable.Colour(n) switch { RouletteTable.Red => Red, RouletteTable.Green => Green, _ => Black };

        static int PocketAt(float relDegrees) => ((Mathf.FloorToInt(relDegrees / Pocket) % 37) + 37) % 37;

        void Layout(float radius)
        {
            spinner.localEulerAngles = new Vector3(0f, 0f, wheel);
            float a = ballAngle * Mathf.Deg2Rad;
            ball.anchoredPosition = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
        }

        public bool Tick()
        {
            float age = Time.unscaledTime - started;
            panel.localScale = Vector3.one * GambleUi.Pop(age / 0.4f);
            if (age < 0.6f) return true;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            wheelSpeed = Mathf.Max(18f, wheelSpeed - 4f * dt);
            wheel += wheelSpeed * dt;
            if (dropAt < 0f)
            {
                // round the rim, slowing, until it's too slow to stay up
                ballSpeed *= Mathf.Exp(-dt / 2.6f);
                ballAngle += ballSpeed * dt;
                Layout(R + 2f);
                if (ballSpeed > -150f) { dropAt = age; rel = ballAngle - wheel; relSpeed = ballSpeed - wheelSpeed; nextKick = 0.15f; }
                return true;
            }
            float inFor = age - dropAt;
            float radius = Mathf.Lerp(R + 2f, R - 38f, Mathf.Clamp01(inFor / 0.45f)) + Mathf.Abs(Mathf.Sin(inFor * 14f)) * 10f * Mathf.Max(0f, 1f - inFor);
            if (landed < 0)
            {
                // in among the pockets: it rattles over a few (a kick each), slows down with the wheel, and settles
                relSpeed *= Mathf.Exp(-dt / 0.45f);
                if (inFor >= nextKick && kicks < 4)
                {
                    kicks++;
                    nextKick = inFor + Random.Range(0.18f, 0.35f);
                    relSpeed += Random.Range(-1f, 1f) * Pocket * 6f / kicks;
                    GambleUi.Sound(kicks % 2 == 0 ? "Play_UI_General_ChangeValueA" : "Play_UI_General_ChangeValueB");
                }
                rel += relSpeed * dt;
                if (Mathf.Abs(relSpeed) < 6f && inFor > 1.2f) { landed = PocketAt(rel); snapFrom = rel; landedAt = age; }
            }
            else
            {
                // into the middle of its pocket
                float k = Mathf.Clamp01((age - landedAt) / 0.3f);
                rel = Mathf.Lerp(snapFrom, (Mathf.Floor(snapFrom / Pocket) + 0.5f) * Pocket, k);
            }
            ballAngle = wheel + rel;
            Layout(radius);
            if (landed < 0) return true;
            float since = age - landedAt;
            if (since < 0.3f) return true;
            if (line.text == "") Landed();
            pockets[landed].color = Color.Lerp(ColourOf(RouletteTable.Order[landed]), Color.white, Mathf.PingPong(since * 4f, 1f) * 0.5f);
            if (since < 3.2f) return true;
            return Payout.PopOut(panel, since, 3.2f) > 0f;
        }

        void Landed()
        {
            int n = RouletteTable.Order[landed];
            string colour = RouletteTable.Colour(n);
            string id = Payout.Pick(colour == RouletteTable.Red ? red.Value : colour == RouletteTable.Black ? black.Value : green.Value);
            bool won = bet != "" && bet == colour;
            line.text = $"{n} {colour.ToUpperInvariant()}: {Payout.Name(id)}{(won ? " x2 (the bet came in!)" : "")}";
            line.color = colour == RouletteTable.Green ? new Color(0.5f, 1f, 0.6f) : colour == RouletteTable.Red ? new Color(1f, 0.5f, 0.45f) : new Color(0.85f, 0.85f, 0.95f);
            GambleUi.Sound(Payout.Good(id) ? "Play_Player_LevelUp" : "Play_UI_General_Select_Important");
            Log.Info($"Streaming: roulette for {viewer}: {n} {colour}{(bet != "" ? $" (bet {bet})" : "")} -> {(id == "" ? "nothing" : id)}");
            Payout.Fire(id, viewer, $"{n} {colour}", won ? 2f : 1f, "casino");
        }

        public void Destroy() { if (panel) Object.Destroy(panel.gameObject); }
    }
}
