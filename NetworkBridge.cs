using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Berserk;

/// <summary>
/// Co-op bridge — the reason Berserk needs one is that BOTH of its real effects live on the
/// HOST's machine in multiplayer: grab forces are computed only by the master
/// (<c>PhysGrabObject.PhysicsGrabbingManipulation</c> returns on non-masters) and the tumble
/// launch force is applied inside <c>PlayerTumble.TumbleSet</c>, which runs on the master via
/// a MasterClient RPC — both always from the master's OWN replica of the player's components.
/// A client bumping its local <c>grabStrength</c>/<c>tumbleLaunch</c> changes nothing the
/// host's simulation can see, and every stat-writing game RPC is MasterOnly.
///
/// So: a client that toggles berserk broadcasts its bonus over a custom Photon event, and the
/// Berserk installed on the HOST applies it to that player's replica (and reverses it on
/// toggle-off). A hello/ack handshake tells the client whether the host actually runs
/// Berserk — without the ack the toggle refuses to activate, so the health drain can never
/// run without the power actually being applied.
///
/// Security mirrors the game's OwnerOnlyRPC: the receiver resolves the player from the
/// Photon actor number of the event's SENDER, so a payload can never speak for another
/// player; the ack is only accepted from the current master.
/// </summary>
internal static class NetworkBridge
{
    private const byte EventCode = 118;
    private const string Magic = "headclef.Berserk";
    private const byte OpHello = 1;
    private const byte OpAck = 2;
    private const byte OpState = 3;

    private const float TickInterval = 0.5f;
    private const float HelloRetryInterval = 10f;
    private const float Eps = 0.001f;

    private static bool _initialized;
    private static float _nextTick;
    private static float _nextHello;

    /// <summary>True once the current room's master confirmed it runs Berserk.
    /// Always true outside multiplayer and for the master itself.</summary>
    internal static bool HostReady =>
        !SemiFunc.IsMultiplayer() || PhotonNetwork.IsMasterClient || _hostAck;

    private static bool _hostAck;

    // ── Receiver state (this machine is the master) ──
    // actor → (strength levels, launch levels) the sender's berserk currently grants.
    private static readonly Dictionary<int, (int str, int launch)> _desired = new();
    private static readonly Dictionary<(int actor, int stat), AppliedStat> _applied = new();

    private sealed class AppliedStat
    {
        public Object? component;
        public float baseValue;
        public float lastWritten;
    }

    internal static void Initialize()
    {
        if (_initialized) return;
        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        _initialized = true;
    }

    internal static void Shutdown()
    {
        if (!_initialized) return;
        PhotonNetwork.NetworkingClient.EventReceived -= OnEvent;
        _initialized = false;
    }

    /// <summary>Called every frame from the plugin; does real work at most twice a second.</summary>
    internal static void Update()
    {
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + TickInterval;

        try
        {
            if (!PhotonNetwork.InRoom)
            {
                _hostAck = false;
                _desired.Clear();
                _applied.Clear();
                return;
            }

            HelloTick();
            ReceiverTick();
        }
        catch (System.Exception ex)
        {
            Berserk.Logger.LogDebug($"NetworkBridge tick skipped: {ex.Message}");
        }
    }

    /// <summary>Scene changed — berserk never survives a scene switch on any machine
    /// (the sender force-deactivates and broadcasts zeros), and the components we applied
    /// to die with the scene, so drop everything except the handshake.</summary>
    internal static void OnSceneSwitch()
    {
        _desired.Clear();
        _applied.Clear();
    }

    /// <summary>Broadcast the sender's current berserk bonus (zeros = off). No-op for
    /// the master and outside multiplayer — the local apply already is the real one.</summary>
    internal static void BroadcastState(int strengthBonus, int launchBonus)
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient) return;

        var payload = new object[] { Magic, OpState, strengthBonus, launchBonus };
        PhotonNetwork.RaiseEvent(EventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
    }

    // ══════════════════════ Handshake ══════════════════════

    private static void HelloTick()
    {
        if (PhotonNetwork.IsMasterClient || _hostAck) return;
        if (Time.unscaledTime < _nextHello) return;
        _nextHello = Time.unscaledTime + HelloRetryInterval;

        PhotonNetwork.RaiseEvent(EventCode, new object[] { Magic, OpHello, 0, 0 },
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
    }

    // ══════════════════════ Events ══════════════════════

    private static void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != EventCode) return;
        if (photonEvent.CustomData is not object[] data || data.Length < 4) return;
        if (data[0] is not string magic || magic != Magic) return;
        if (data[1] is not byte op) return;

        switch (op)
        {
            case OpHello:
                // A client asks whether this machine (if master) runs Berserk.
                if (PhotonNetwork.IsMasterClient)
                {
                    PhotonNetwork.RaiseEvent(EventCode, new object[] { Magic, OpAck, 0, 0 },
                        new RaiseEventOptions { TargetActors = new[] { photonEvent.Sender } },
                        SendOptions.SendReliable);
                }
                break;

            case OpAck:
                // Only the current master may vouch for the host.
                if (PhotonNetwork.MasterClient != null &&
                    photonEvent.Sender == PhotonNetwork.MasterClient.ActorNumber)
                {
                    if (!_hostAck) Berserk.Logger.LogInfo("Host runs Berserk — bridge ready.");
                    _hostAck = true;
                }
                break;

            case OpState:
                if (!PhotonNetwork.IsMasterClient) return;
                if (data[2] is not int str || data[3] is not int launch) return;
                // Keyed by the SENDER's actor number — an event can only affect its own player.
                _desired[photonEvent.Sender] =
                    (Mathf.Clamp(str, 0, 100), Mathf.Clamp(launch, 0, 100));
                break;
        }
    }

    // ══════════════════════ Receiver (master only) ══════════════════════

    private static void ReceiverTick()
    {
        if (!PhotonNetwork.IsMasterClient || _desired.Count == 0) return;
        if (GameDirector.instance == null) return;

        foreach (var pair in _desired)
        {
            var avatar = AvatarFromActor(pair.Key);
            if (avatar == null) continue;

            ReconcileStat(pair.Key, avatar, 0, pair.Value.str);
            ReconcileStat(pair.Key, avatar, 1, pair.Value.launch);
        }
    }

    private static PlayerAvatar? AvatarFromActor(int actorNumber)
    {
        foreach (var player in SemiFunc.PlayerGetList())
        {
            if (player == null || player.isLocal || player.photonView == null) continue;
            if (player.photonView.OwnerActorNr == actorNumber) return player;
        }
        return null;
    }

    /// <summary>
    /// Keep <c>component value == base + perLevel × desired</c> on the sender's replica,
    /// deriving the true base from what we last wrote:
    ///   • cur &gt; lastWritten → the game added on top (vanilla upgrade pickup) → fold into base
    ///   • cur &lt; lastWritten → the game re-assigned it (spawn derivation) → new base
    /// desired = 0 writes the base back, so toggling off (or dying) reverses exactly.
    /// </summary>
    private static void ReconcileStat(int actor, PlayerAvatar avatar, int stat, int desired)
    {
        Object? component = stat == 0 ? avatar.physGrabber : avatar.tumble;
        if (component == null) return;

        float cur = stat == 0 ? avatar.physGrabber.grabStrength : avatar.tumble.tumbleLaunch;
        var key = (actor, stat);

        if (!_applied.TryGetValue(key, out var applied) || applied.component != component)
        {
            if (desired == 0) return;
            applied = new AppliedStat { component = component, baseValue = cur, lastWritten = cur };
            _applied[key] = applied;
        }
        else if (cur > applied.lastWritten + Eps)
        {
            applied.baseValue += cur - applied.lastWritten;
        }
        else if (cur < applied.lastWritten - Eps)
        {
            applied.baseValue = cur;
        }

        float perLevel = stat == 0 ? 0.2f : 1f;
        float want = applied.baseValue + perLevel * desired;
        if (Mathf.Abs(want - cur) > Eps)
        {
            if (stat == 0) avatar.physGrabber.grabStrength = want;
            else avatar.tumble.tumbleLaunch = Mathf.RoundToInt(want);
        }
        applied.lastWritten = stat == 0 ? avatar.physGrabber.grabStrength : avatar.tumble.tumbleLaunch;
    }
}
