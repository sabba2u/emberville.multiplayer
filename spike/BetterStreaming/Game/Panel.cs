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
    static string simName = "TestViewer", simMessage = "";
    static int simBits = 100;

    /// <summary>One section's effects (nice: heals and help; naughty: the rest), each with its settings and a Test.</summary>
    static void Effects(bool nice, string label)
    {
        if (ImGui.BeginTable("##bs_fx" + label, 6, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("On"); ImGui.TableSetupColumn("Effect"); ImGui.TableSetupColumn("Bits");
            ImGui.TableSetupColumn("Up to"); ImGui.TableSetupColumn("Seconds"); ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();
            foreach (var d in Streamer.Defs)
            {
                if ((d.Mood == Mood.Nice) != nice) continue;
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
        Effects(true, "nice");
        int pct = ChainLightning.Percent.Value, boss = ChainLightning.BossPercent.Value;
        ImGui.TextDisabled("Chain lightning takes");
        ImGui.SameLine(); ImGui.SetNextItemWidth(60);
        if (ImGui.InputInt("% of health a strike,##clp", ref pct, 0)) ChainLightning.Percent.Value = System.Math.Max(1, System.Math.Min(100, pct));
        ImGui.SameLine(); ImGui.SetNextItemWidth(60);
        if (ImGui.InputInt("% from bosses##clb", ref boss, 0)) ChainLightning.BossPercent.Value = System.Math.Max(1, System.Math.Min(100, boss));

        ImGui.SeparatorText("Naughty");
        Effects(false, "naughty");

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
