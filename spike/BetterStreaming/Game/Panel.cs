using BetterStreaming.Core;
using ImGuiNET;
using Vector4 = ImGuiNET.Vector4;

namespace BetterStreaming;

/// <summary>
/// The streamer's tab (MODS > Streaming): connect Twitch, the chaos dial, every effect (on, price, cap, seconds, a
/// free Test), a simulated cheer that goes through exactly what a real one does, and the overlay's place.
/// </summary>
internal static class Panel
{
    static string simName = "TestViewer", simMessage = "", chatLine = "!raid", slices, table;
    static int simBits = 100;

    /// <summary>One section's effects (nice, naughty or gambles), each with its settings and a Test.</summary>
    static void Effects(Mood mood, string label)
    {
        if (ImGui.BeginTable("##bs_fx" + label, 6, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("On"); ImGui.TableSetupColumn("Effect"); ImGui.TableSetupColumn("Bits");
            ImGui.TableSetupColumn("Up to"); ImGui.TableSetupColumn("Seconds"); ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();
            foreach (var d in Streamer.Defs)
            {
                if (d.Mood != mood) continue;
                var s = Streamer.Settings(d.Id);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool on = s.on.Value;
                if (ImGui.Checkbox($"##on{d.Id}", ref on)) { s.on.Value = on; Streamer.Apply(); }
                ImGui.TableNextColumn();
                string why = "";
                bool ready = Streamer.Effects.TryGetValue(d.Id, out var fx) && fx.Available(out why);
                if (ready) ImGui.Text(d.Name);
                else ImGui.TextDisabled($"{d.Name}  ({why})");
                ImGui.TableNextColumn();
                int bits = s.bits.Value;
                ImGui.SetNextItemWidth(80);
                if (ImGui.InputInt($"##bits{d.Id}", ref bits, 0)) { s.bits.Value = System.Math.Max(1, bits); Streamer.Apply(); }
                ImGui.TableNextColumn();
                if (d.Scales)
                {
                    int cap = s.cap.Value;
                    ImGui.SetNextItemWidth(80);
                    if (ImGui.InputInt($"##cap{d.Id}", ref cap, 0)) { s.cap.Value = System.Math.Max(s.bits.Value, cap); Streamer.Apply(); }
                }
                ImGui.TableNextColumn();
                if (d.Seconds > 0f || s.secs.Value > 0f)
                {
                    float secs = s.secs.Value;
                    ImGui.SetNextItemWidth(70);
                    if (ImGui.InputFloat($"##secs{d.Id}", ref secs, 0f, 0f, "%.0f")) { s.secs.Value = System.Math.Max(1f, secs); Streamer.Apply(); }
                }
                ImGui.TableNextColumn();
                if (!ready) ImGui.BeginDisabled();
                if (ImGui.SmallButton($"Test##t{d.Id}")) Streamer.Submit(new Pending { EffectId = d.Id, Viewer = "Test", Source = "test", Size = 1f });
                if (!ready) ImGui.EndDisabled();
            }
            ImGui.EndTable();
        }

    }

    static string reelText, noMatchText, binsText, headsText, tailsText, redText, blackText, raceNice, raceNaughty;

    /// <summary>A text setting: the box, its value saved as it's typed.</summary>
    static void Field(string label, ref string value, System.Action<string> save, System.Func<string> load, float width = 560f)
    {
        value ??= load();
        ImGui.SetNextItemWidth(width);
        if (ImGui.InputText(label, ref value, 600)) save(value);
    }

    public static void Draw()
    {
        ImGui.SeparatorText("Twitch");
        bool busy = TwitchAuth.Started != null;
        if (TwitchAuth.SignedIn)
        {
            ImGui.Text($"Signed in as {TwitchAuth.Login}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Sign out")) TwitchAuth.Revoke();
        }
        else
        {
            if (TwitchAuth.NoClientId) ImGui.BeginDisabled();
            if (ImGui.Button(busy ? "Waiting for twitch.tv..." : "Connect Twitch")) TwitchAuth.Begin();
            if (TwitchAuth.NoClientId) ImGui.EndDisabled();
            if (TwitchAuth.Started is DeviceStart s)
            {
                ImGui.TextWrapped($"On twitch.tv, go to {s.VerificationUri} and enter the code:");
                ImGui.SetWindowFontScale(1.6f);
                ImGui.TextColored(new Vector4(0.7f, 0.5f, 1f, 1f), s.UserCode);
                ImGui.SetWindowFontScale(1f);
                if (ImGui.SmallButton("Open the link")) UnityEngine.Application.OpenURL(s.VerificationUri);
            }
        }
        ImGui.TextDisabled($"{TwitchAuth.Status}   |   {TwitchSocket.Status}");
        ImGui.TextDisabled("Bits and channel points need a Twitch Affiliate or Partner channel. No password is ever typed in the game.");

        ImGui.SeparatorText("Chaos dial");
        int dial = Streamer.Dial;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderInt("##dial", ref dial, 0, ChaosQueue.Gaps.Length - 1, ChaosQueue.DialNames[dial])) Streamer.Dial = dial;
        ImGui.SameLine();
        ImGui.TextDisabled($"a mean effect at most every {ChaosQueue.Gaps[Streamer.Dial]:0} s; the rest wait in the queue ({Streamer.Queue.Waiting.Count} now)");

        bool free = TwitchDeath.Free;
        if (ImGui.Checkbox("Twitch deaths are free", ref free)) TwitchDeath.Free = free;
        ImGui.SameLine();
        ImGui.TextDisabled(free ? "(a death chat caused: you get up where you fell, nothing lost)" : "(off: chat can kill you for real, the game's own death)");

        ImGui.SeparatorText("Nice");
        Effects(Mood.Nice, "nice");
        int pct = ChainLightning.Percent.Value, boss = ChainLightning.BossPercent.Value;
        ImGui.TextDisabled("Chain lightning takes");
        ImGui.SameLine(); ImGui.SetNextItemWidth(60);
        if (ImGui.InputInt("% of health a strike,##clp", ref pct, 0)) ChainLightning.Percent.Value = System.Math.Max(1, System.Math.Min(100, pct));
        ImGui.SameLine(); ImGui.SetNextItemWidth(60);
        if (ImGui.InputInt("% from bosses##clb", ref boss, 0)) ChainLightning.BossPercent.Value = System.Math.Max(1, System.Math.Min(100, boss));

        ImGui.SeparatorText("Naughty");
        Effects(Mood.Mean, "naughty");

        ImGui.SeparatorText("Gambles");
        Effects(Mood.Gamble, "gambles");
        slices ??= Wheel.SlicesText;
        table ??= Dice.TableText;
        ImGui.SetNextItemWidth(560);
        if (ImGui.InputText("Wheel slices##bs", ref slices, 600)) Wheel.SlicesText = slices;
        ImGui.TextDisabled("effect:width, comma separated; again = spin once more; death:0.35 is a sliver. Effects that are off are left out.");
        ImGui.SetNextItemWidth(560);
        if (ImGui.InputText("Dice table##bs", ref table, 600)) Dice.TableText = table;
        ImGui.TextDisabled("total(s):effect; silly = a random naughty 30 s effect, jackpot = heal + chain lightning + loot. Three 1s: the 3, three 6s: the 18.");
        Field("Slots reel##bs", ref reelText, v => Slots.ReelText = v, () => Slots.ReelText);
        Field("Slots no match##bs", ref noMatchText, v => Slots.NoMatchText = v, () => Slots.NoMatchText, 200);
        ImGui.TextDisabled("Reel: effect:how often (jackpot is the 7, death the skull). Three of a kind: doubled; two: once; no match: this (empty = nothing).");
        Field("Plinko bins##bs", ref binsText, v => Plinko.BinsText = v, () => Plinko.BinsText);
        ImGui.TextDisabled("Left to right, 3 to 13: the middle ones come up most, the edges rarely.");
        Field("Coin heads##bs", ref headsText, v => Coin.HeadsText = v, () => Coin.HeadsText, 270);
        ImGui.SameLine();
        Field("tails##bs", ref tailsText, v => Coin.TailsText = v, () => Coin.TailsText, 200);
        Field("Roulette red##bs", ref redText, v => CasinoRoulette.RedText = v, () => CasinoRoulette.RedText, 270);
        ImGui.SameLine();
        Field("black##bs", ref blackText, v => CasinoRoulette.BlackText = v, () => CasinoRoulette.BlackText, 270);
        ImGui.TextDisabled("One of each list at random. \"heads\"/\"tails\" or \"red\"/\"black\"/\"green\" in the cheer is a bet: right, and it's doubled. The green 0 is the jackpot.");
        Field("Race nice##bs", ref raceNice, v => Race.NiceText = v, () => Race.NiceText, 270);
        ImGui.SameLine();
        Field("naughty##bs", ref raceNaughty, v => Race.NaughtyText = v, () => Race.NaughtyText, 270);
        if (Race.Running) ImGui.BeginDisabled();
        if (ImGui.Button("Start a monster race now")) Race.Start("chat");
        if (Race.Running) ImGui.EndDisabled();
        ImGui.SameLine();
        int raceEvery = Race.EveryMinutes;
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputInt("auto every (minutes, 0 off)##race", ref raceEvery, 0)) Race.EveryMinutes = raceEvery;
        ImGui.TextDisabled("Four creatures race, two carrying something nice, two something naughty; chat cheers them on with !1 to !4.");

        ImGui.SeparatorText("Chat");
        bool votes = ChatVotes.On;
        if (ImGui.Checkbox("Chat votes", ref votes)) ChatVotes.On = votes;
        ImGui.SameLine();
        int needed = ChatVotes.Needed;
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputInt("chatters##votes", ref needed, 0)) ChatVotes.Needed = needed;
        ImGui.SameLine();
        float within = ChatVotes.Window;
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputFloat("seconds##votes", ref within, 0f, 0f, "%.0f")) ChatVotes.Window = within;
        ImGui.SameLine();
        int rest = (int)ChatVotes.Rest;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("rest after##votes", ref rest, 0, 600, rest == 0 ? "no rest" : rest < 60 || rest % 60 != 0 ? $"{rest} s" : $"{rest / 60} min")) ChatVotes.Rest = rest;
        ImGui.SameLine(); ImGui.TextDisabled("(\"!raid\" from that many sets it off, free; then that effect's votes rest)");

        if (Polls.Running) ImGui.BeginDisabled();
        if (ImGui.Button("Start a poll now")) Polls.Start();
        if (Polls.Running) ImGui.EndDisabled();
        ImGui.SameLine();
        int every = Polls.EveryMinutes;
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputInt("auto every (minutes, 0 off)##polls", ref every, 0)) Polls.EveryMinutes = every;
        if (Polls.Status != "") ImGui.TextDisabled(Polls.Status.Replace("%", "%%"));

        bool predict = Prediction.On;
        if (ImGui.Checkbox("\"Will they survive?\" predictions on big raids", ref predict)) Prediction.On = predict;
        if (Prediction.NeedsSignIn || TwitchAuth.Lacks("channel:manage:predictions")) { ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.7f, 0.4f, 1f), "(sign out and connect Twitch again to allow them)"); }
        if (Prediction.Status != "") ImGui.TextDisabled(Prediction.Status.Replace("%", "%%"));

        bool top = Board.Show;
        if (ImGui.Checkbox("Leaderboard on the overlay", ref top)) Board.Show = top;
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear the leaderboard")) Board.Clear();
        ImGui.SameLine(); ImGui.TextDisabled("(top raiders and healers by Bits, deaths caused; !top in chat)");

        bool partyOn = Party.On;
        if (ImGui.Checkbox("Raid parties (!party, then !join)", ref partyOn)) Party.On = partyOn;
        ImGui.SameLine();
        if (Party.Forming) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Start one now")) Party.Start("the streamer");
        if (Party.Forming) ImGui.EndDisabled();
        ImGui.SameLine();
        int partySecs = Party.Seconds;
        ImGui.SetNextItemWidth(60);
        if (ImGui.InputInt("s to join##party", ref partySecs, 0)) Party.Seconds = partySecs;
        ImGui.SameLine();
        int partyEvery = Party.EveryMinutes;
        ImGui.SetNextItemWidth(60);
        if (ImGui.InputInt("auto every (min)##party", ref partyEvery, 0)) Party.EveryMinutes = partyEvery;
        ImGui.TextDisabled("A monster for every raider, each wearing their name (up to 20, then tougher).");
        if (Party.Status != "") ImGui.TextDisabled(Party.Status.Replace("%", "%%"));

        bool names = ChatterEnemies.On;
        if (ImGui.Checkbox("Chatters as enemies", ref names)) ChatterEnemies.On = names;
        ImGui.SameLine();
        int which = ChatterEnemies.Which;
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("##whichnames", ref which, "Every monster Only Twitch raids ")) ChatterEnemies.Which = which;
        ImGui.SameLine();
        ImGui.TextDisabled($"(names of people chatting in the last 10 min: {ChatterEnemies.Chatters} now)");

        ImGui.SetNextItemWidth(220);
        ImGui.InputText("##chatline", ref chatLine, 100);
        ImGui.SameLine();
        if (ImGui.Button("Pretend chat says it")) { string who = "Chatter" + UnityEngine.Random.Range(1, 9999); Log.Info($"Pretend chat < {who}: {chatLine}"); Streamer.Chat(who, chatLine); }
        ImGui.SameLine(); ImGui.TextDisabled("(each press is a different chatter: to try votes without Twitch)");

        ImGui.SeparatorText("Simulate a cheer");
        ImGui.SetNextItemWidth(140); ImGui.InputText("Viewer##sim", ref simName, 25);
        ImGui.SameLine(); ImGui.SetNextItemWidth(90); ImGui.InputInt("Bits##sim", ref simBits, 0);
        ImGui.SameLine(); ImGui.SetNextItemWidth(220); ImGui.InputText("Message##sim", ref simMessage, 100);
        ImGui.SameLine();
        if (ImGui.Button("Cheer")) Streamer.Cheer(Streamer.Filter.Clean(simName, 25), System.Math.Max(1, simBits), simMessage, "test");
        ImGui.TextDisabled("Goes through the same rules as a real cheer: a word from the effect in the message picks it, else the exact amount, else the biggest raid it can buy.");

        ImGui.SeparatorText("On stream");
        bool corner = Overlay.ShowCorner;
        if (ImGui.Checkbox("Show the effects panel", ref corner)) Overlay.ShowCorner = corner;
        ImGui.SameLine();
        int where = Overlay.Corner;
        ImGui.SetNextItemWidth(140);
        if (ImGui.Combo("##corner", ref where, "Top left\0Top right\0Bottom left\0Bottom right\0")) Overlay.Corner = where;
    }
}
