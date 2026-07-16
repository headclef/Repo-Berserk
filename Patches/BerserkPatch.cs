using System;
using static Character_Stats.Character_Stats;
using HarmonyLib;
using UnityEngine;

namespace Berserk.Patches;

[HarmonyPatch]
internal static class BerserkPatch
{
    // Identifies our contribution to Relay, which sums every mod's report per stat.
    private const string RelaySource = "headclef.Berserk";

    // ── State ──
    private static bool _active;
    private static float _drainAccum;

    // Exactly what we applied — and the avatar we applied it to — so we can reverse it
    // precisely even if the config changes or the avatar is replaced (respawn) while
    // the state is active.
    private static PlayerAvatar? _appliedAvatar;
    private static string? _overlaySteamId;
    private static int _appliedStrengthBonus;
    private static int _appliedLaunchBonus;
    private static float _appliedGrabDelta;

    /// <summary>True while the berserk state is on. Read by the HUD.</summary>
    internal static bool IsActive => _active;

    /// <summary>The Strength bonus currently applied (0 when inactive).</summary>
    internal static int ActiveStrengthBonus => _active ? _appliedStrengthBonus : 0;

    /// <summary>The Launch bonus currently applied (0 when inactive).</summary>
    internal static int ActiveLaunchBonus => _active ? _appliedLaunchBonus : 0;

    /// <summary>
    /// True the frame the toggle key goes down. We check the raw main key (plus any
    /// configured modifiers) instead of <c>KeyboardShortcut.IsDown()</c>: IsDown() also
    /// requires that NO other key is held, so holding WASD to move would suppress the
    /// press — meaning the toggle wouldn't fire while you were moving.
    /// </summary>
    private static bool TogglePressed()
    {
        var key = Berserk.ToggleKey.Value;
        if (key.MainKey == KeyCode.None || !Input.GetKeyDown(key.MainKey))
            return false;

        foreach (var modifier in key.Modifiers)
            if (!Input.GetKey(modifier))
                return false;

        return true;
    }

    /// <summary>
    /// Whether the berserk state may be turned on at all.
    ///
    /// Both of Berserk's real effects are consumed by R.E.P.O. on the HOST's machine, from
    /// the host's own replica of the player: grab forces come from
    /// <c>PhysGrabObject.PhysicsGrabbingManipulation</c>, which returns early on non-masters,
    /// and the launch force is computed in <c>PlayerTumble.TumbleSet</c>, reached only by a
    /// <c>RpcTarget.MasterClient</c> RPC. A co-op client writing <c>grabStrength</c> and
    /// <c>tumbleLaunch</c> locally changes nothing at all.
    ///
    /// The health drain, on the other hand, is entirely local and works perfectly. So a client
    /// that activates without the bonus landing pays the full cost and receives none of the
    /// benefit — strictly worse than not running the mod, and made worse still by the
    /// Character Stats overlay, which would show a boosted level that does nothing.
    ///
    /// <see cref="global::Relay.Relay.CanDeliver"/> answers exactly that question: it is true
    /// as the host and in single player, where our writes are the simulation, and on a co-op
    /// client only once the bridge is switched on AND the host has proved it runs Relay too.
    /// So the cost can never run without the payoff.
    /// </summary>
    private static bool CanActivate(out string reason)
    {
        if (!global::Relay.Relay.CanDeliver)
        {
            reason = "you are a co-op client and the Relay bridge is not delivering — R.E.P.O. " +
                     "simulates Grab Strength and Tumble Launch on the host, so the bonus could " +
                     "not reach you while the health drain still would. Turn Relay's Multiplayer " +
                     "switch on, and make sure the host runs Relay with it on too";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Postfix on PlayerController.Update — runs for the local player. Handles the
    /// toggle key, the per-frame health drain, and the auto-off safety conditions.
    /// </summary>
    [HarmonyPatch(typeof(PlayerController), "Update")]
    [HarmonyPostfix]
    private static void PlayerController_Update_Postfix(PlayerController __instance)
    {
        try
        {
            if (__instance != PlayerController.instance)
                return;

            var avatar = __instance.playerAvatarScript;

            // Auto-off: dead, no avatar, or no longer in a level.
            if (_active && (avatar == null || avatar.deadSet || !SemiFunc.RunIsLevel()))
            {
                Deactivate();
                return;
            }

            // Toggle on the key press.
            if (TogglePressed())
            {
                if (_active)
                    Deactivate();
                else if (avatar != null && !avatar.deadSet && SemiFunc.RunIsLevel())
                {
                    if (CanActivate(out string reason))
                        Activate(avatar);
                    else
                        Berserk.Logger.LogWarning($"Berserk stayed off: {reason}.");
                }
            }

            // Drain while active.
            if (_active && avatar != null)
                DrainTick(avatar);
        }
        catch (Exception ex)
        {
            Berserk.Logger.LogError($"Berserk update exception: {ex.Message}");
            // Don't leave a half-applied state lingering on an error.
            Deactivate();
        }
    }

    private static void DrainTick(PlayerAvatar avatar)
    {
        var health = avatar.playerHealth;
        if (health == null)
            return;

        float rate = Berserk.HealthDrainPerSecond.Value;
        if (rate <= 0f)
            return;

        // Accumulate fractional damage so the rate is smooth regardless of framerate.
        _drainAccum += rate * Time.deltaTime;
        if (_drainAccum < 1f)
            return;

        int dmg = (int)_drainAccum;
        _drainAccum -= dmg;

        int newHealth = health.health - dmg;

        if (newHealth <= 0)
        {
            if (Berserk.CanBeLethal.Value)
            {
                health.health = 0;
                Deactivate();              // remove our bonus before the death sequence
                avatar.PlayerDeath(-1);    // full, networked death (deadSet-guarded)
            }
            else
            {
                health.health = 1;         // floor — never lethal by itself
            }
        }
        else
        {
            health.health = newHealth;
        }
    }

    private static void Activate(PlayerAvatar avatar)
    {
        if (_active)
            return;  // already active — never stack the bonus on top of itself

        // Authoritative gate, repeated here so no future caller can start the drain on a
        // machine where the bonus cannot land. See CanActivate.
        if (!CanActivate(out _))
            return;

        string? steamId = PlayerController.instance != null
            ? PlayerController.instance.playerSteamID
            : null;
        if (string.IsNullOrEmpty(steamId))
            return;

        int strBonus = Berserk.StrengthBonus.Value;
        int launchBonus = Berserk.LaunchBonus.Value;

        // ── Stat-level overlay ──
        // Layered on top of Character Stats' cached levels at read time, so every
        // consumer (Armor, Increase Tumble Damage, UI, …) sees the boosted level. It is
        // NEVER written into the game's StatsManager dictionaries, so Improve's reconcile
        // watchdog never absorbs it and it is never baked into the .es3 save.
        SetTemporaryBonus(steamId!, "Strength", strBonus);
        SetTemporaryBonus(steamId!, "Launch", launchBonus);

        // ── Real game effect ──
        // Mirror exactly what the game's own apply helpers do, but on the live component
        // fields only (no dictionary write):
        //   UpdateGrabStrengthRightAway: physGrabber.grabStrength += 0.2 * level
        //   UpdateTumbleLaunchRightAway: tumble.tumbleLaunch       += level
        // This is what makes the boost real as the host and in single player, where our own
        // components ARE the simulation.
        float grabDelta = 0.2f * strBonus;
        if (avatar.physGrabber != null)
            avatar.physGrabber.grabStrength += grabDelta;
        if (avatar.tumble != null)
            avatar.tumble.tumbleLaunch += launchBonus;

        // ── Co-op client: hand the same numbers to Relay ──
        // As a client the writes above reach nothing, because the master simulates these two
        // stats from ITS replica of us. Relay reports them to the host, which applies them
        // there. Relay sums every contributor and writes once, so Improve's allocations and
        // our bonus stack instead of fighting over the same field. Harmless as the host: Relay
        // never bridges the local player.
        global::Relay.Relay.Report(RelaySource, global::Relay.Relay.StatGrabStrength, strBonus);
        global::Relay.Relay.Report(RelaySource, global::Relay.Relay.StatTumbleLaunch, launchBonus);

        // Remember precisely what we applied — and where — so Deactivate reverses the
        // same amounts on the same avatar.
        _appliedAvatar = avatar;
        _overlaySteamId = steamId;
        _appliedStrengthBonus = strBonus;
        _appliedLaunchBonus = launchBonus;
        _appliedGrabDelta = grabDelta;
        _drainAccum = 0f;
        _active = true;

        // Berserk.Logger.LogInfo(
        //     $"Berserk ON (+{strBonus} Strength, +{launchBonus} Launch, " +
        //     $"drain {Berserk.HealthDrainPerSecond.Value}/s).");
    }

    private static void Deactivate()
    {
        if (!_active)
            return;

        _active = false;

        // Reverse the live component effects on the EXACT avatar we boosted — not a
        // freshly-fetched one, which after a respawn could be a different avatar whose
        // base values we'd corrupt. Unity-null-safe: a destroyed avatar compares == null,
        // in which case there is nothing to reverse (it's gone) and we just drop it.
        var avatar = _appliedAvatar;
        if (avatar != null)
        {
            if (avatar.physGrabber != null)
                avatar.physGrabber.grabStrength -= _appliedGrabDelta;
            if (avatar.tumble != null)
                avatar.tumble.tumbleLaunch -= _appliedLaunchBonus;
        }

        // Always clear the stat overlay, whatever happened to the avatar — this is the
        // part other mods read, so it must never be left dangling in Character Stats.
        if (!string.IsNullOrEmpty(_overlaySteamId))
        {
            ClearTemporaryBonus(_overlaySteamId!, "Strength");
            ClearTemporaryBonus(_overlaySteamId!, "Launch");
        }

        // Withdraw from Relay too, so the host stops applying our bonus to its replica of us.
        global::Relay.Relay.Clear(RelaySource);

        _appliedAvatar = null;
        _overlaySteamId = null;
        _appliedStrengthBonus = 0;
        _appliedLaunchBonus = 0;
        _appliedGrabDelta = 0f;
        _drainAccum = 0f;

        // Berserk.Logger.LogInfo("Berserk OFF.");
    }

    /// <summary>
    /// Force the berserk state off whenever the scene/level changes, so neither the
    /// bonus nor the health drain carries across a transition — and the Character Stats
    /// overlay can never be left behind on the next level.
    /// </summary>
    [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.OnSceneSwitch))]
    [HarmonyPrefix]
    private static void OnSceneSwitch_Prefix() => ForceDeactivate();

    /// <summary>Safe teardown for plugin unload — never throws.</summary>
    internal static void ForceDeactivate()
    {
        try { Deactivate(); }
        catch { /* shutting down — nothing useful to do */ }
    }
}
