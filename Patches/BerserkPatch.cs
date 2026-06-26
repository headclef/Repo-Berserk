using System;
using static Character_Stats.Character_Stats;
using HarmonyLib;
using UnityEngine;

namespace Berserk.Patches;

[HarmonyPatch]
internal static class BerserkPatch
{
    // ── State ──
    private static bool _active;
    private static float _drainAccum;

    // Exactly what we applied, so we can reverse it precisely even if the config
    // values change while the state is active.
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
                    Activate(avatar);
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
        float grabDelta = 0.2f * strBonus;
        if (avatar.physGrabber != null)
            avatar.physGrabber.grabStrength += grabDelta;
        if (avatar.tumble != null)
            avatar.tumble.tumbleLaunch += launchBonus;

        // Remember precisely what we applied so Deactivate reverses the same amounts.
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

        // Reverse the live component effects on whoever we applied them to.
        var avatar = PlayerController.instance != null
            ? PlayerController.instance.playerAvatarScript
            : null;
        if (avatar != null)
        {
            if (avatar.physGrabber != null)
                avatar.physGrabber.grabStrength -= _appliedGrabDelta;
            if (avatar.tumble != null)
                avatar.tumble.tumbleLaunch -= _appliedLaunchBonus;
        }

        // Clear the stat overlay.
        if (!string.IsNullOrEmpty(_overlaySteamId))
        {
            ClearTemporaryBonus(_overlaySteamId!, "Strength");
            ClearTemporaryBonus(_overlaySteamId!, "Launch");
        }

        _overlaySteamId = null;
        _appliedStrengthBonus = 0;
        _appliedLaunchBonus = 0;
        _appliedGrabDelta = 0f;
        _drainAccum = 0f;

        // Berserk.Logger.LogInfo("Berserk OFF.");
    }

    /// <summary>Safe teardown for plugin unload — never throws.</summary>
    internal static void ForceDeactivate()
    {
        try { Deactivate(); }
        catch { /* shutting down — nothing useful to do */ }
    }
}
