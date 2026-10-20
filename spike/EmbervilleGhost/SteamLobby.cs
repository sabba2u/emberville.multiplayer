using System;
using Steamworks;

namespace EmbervilleGhost;

/// <summary>
/// Steam lobby + invites, as the Emberville demo (SteamBoot), from our Duckov mod:
///  - Host (F8 window > HOST VIA STEAM): a friends-only lobby whose owner is the host's Steam ID; rich presence
///    "connect" so friends see "Join Game" on us in their friends list; "Invite friends" opens the Steam overlay's
///    invite dialog (the overlay needs the game started from Steam).
///  - Joining: accepting an invite or "Join Game" while the game runs (GameLobbyJoinRequested_t,
///    GameRichPresenceJoinRequested_t), or launching the game from one (Steam passes +connect_lobby &lt;id&gt;) -> join
///    the lobby -> connect to its owner through Steam's relay (SteamTransport) -> the usual join: the host's world
///    downloads, pick a character, Connect now.
/// </summary>
internal static class SteamLobby
{
    public static Plugin Plugin;

    static CallResult<LobbyCreated_t> created;
    static CallResult<LobbyEnter_t> entered;
    static Callback<GameLobbyJoinRequested_t> joinRequested;
    static Callback<GameRichPresenceJoinRequested_t> presenceJoinRequested;
    static bool hooked, commandLineChecked;

    public static CSteamID Lobby { get; private set; } = CSteamID.Nil;
    public static bool InLobby => Lobby != CSteamID.Nil;

    /// <summary>Every frame: hook Steam callbacks once Steam is up, and honour a +connect_lobby launch.</summary>
    public static void Tick()
    {
        if (!SteamBoot.Ready) return;
        if (!hooked)
        {
            hooked = true;
            created = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            entered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
            joinRequested = Callback<GameLobbyJoinRequested_t>.Create(e => Join(e.m_steamIDLobby, $"invite from {SteamFriends.GetFriendPersonaName(e.m_steamIDFriend)}"));
            presenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(e => JoinFromConnectString(e.m_rgchConnect));
        }
        // Launched from an invite / Join Game: Steam passed "+connect_lobby <id>". Once the title screen (or a world) is up.
        if (!commandLineChecked && (LocalPlayer.Get() || UnityEngine.Object.FindAnyObjectByType<MainMenuManager>()))
        {
            commandLineChecked = true;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong id))
                    Join(new CSteamID(id), "launched from an invite");
        }
    }

    public static void Host(int maxPlayers)
    {
        if (!SteamBoot.Ready) { NetLog.Error("Steam isn't running"); return; }
        NetLog.Info("Creating a Steam lobby...", toast: true);
        created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers));
    }

    static void OnLobbyCreated(LobbyCreated_t e, bool ioFailure)
    {
        if (ioFailure || e.m_eResult != EResult.k_EResultOK)
        {
            NetLog.Error($"Couldn't create a Steam lobby ({e.m_eResult})");
            Plugin.Stop();
            return;
        }
        Lobby = new CSteamID(e.m_ulSteamIDLobby);
        SteamMatchmaking.SetLobbyData(Lobby, "embervilleghost", Plugin.Version);
        SteamMatchmaking.SetLobbyJoinable(Lobby, true);
        // Friends list "Join Game": Steam launches / tells the game with this connect string.
        SteamFriends.SetRichPresence("connect", $"+connect_lobby {Lobby.m_SteamID}");
        SteamFriends.SetRichPresence("status", "Hosting Emberville co-op");
        NetLog.Success("Steam lobby ready: invite friends (F8 window or Insert menu), or they Join Game from your friends list", toast: true);
        Plugin.SteamHostReady();
    }

    /// <summary>Open the Steam overlay's "invite friends" dialog for our lobby.</summary>
    public static void OpenInviteDialog()
    {
        if (!InLobby) { NetLog.Warn("Host via Steam first", toast: true); return; }
        SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
    }

    static void JoinFromConnectString(string connect)
    {
        var parts = (connect ?? "").Split(' ');
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out ulong id))
            {
                Join(new CSteamID(id), "a friend's Join Game");
                return;
            }
    }

    static void Join(CSteamID lobby, string why)
    {
        if (InLobby && lobby == Lobby) return;
        NetLog.Info($"Joining a Steam lobby ({why})...", toast: true);
        Plugin.Stop();   // leave whatever we were in
        entered.Set(SteamMatchmaking.JoinLobby(lobby));
    }

    static void OnLobbyEntered(LobbyEnter_t e, bool ioFailure)
    {
        if (ioFailure || e.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        {
            NetLog.Error($"Couldn't join the Steam lobby ({(EChatRoomEnterResponse)e.m_EChatRoomEnterResponse})", toast: true);
            return;
        }
        Lobby = new CSteamID(e.m_ulSteamIDLobby);
        var owner = SteamMatchmaking.GetLobbyOwner(Lobby);
        if (owner == SteamUser.GetSteamID()) return;   // our own lobby
        string theirVersion = SteamMatchmaking.GetLobbyData(Lobby, "embervilleghost");
        if (theirVersion.Length > 0 && theirVersion != Plugin.Version)
            NetLog.Warn($"The host has mod v{theirVersion}, you have v{Plugin.Version}: it may not work", toast: true);
        NetLog.Info($"In the lobby, connecting to {SteamFriends.GetFriendPersonaName(owner)} through Steam...", toast: true);
        Plugin.JoinSteam(owner);
    }

    public static void Leave()
    {
        if (InLobby) SteamMatchmaking.LeaveLobby(Lobby);
        Lobby = CSteamID.Nil;
        if (SteamBoot.Ready) SteamFriends.ClearRichPresence();
    }
}
