using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Berserk;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("headclef.CharacterStats", BepInDependency.DependencyFlags.HardDependency)]
public class Berserk : BaseUnityPlugin
{
    private const string PluginGuid = "headclef.Berserk";
    private const string PluginName = "Berserk";
    private const string PluginVersion = "1.0.1";

    internal static Berserk Instance { get; private set; } = null!;
    internal new static ManualLogSource Logger => Instance._logger;
    private ManualLogSource _logger => base.Logger;
    internal Harmony? Harmony { get; set; }

    // ── Config ──
    internal static ConfigEntry<KeyboardShortcut> ToggleKey = null!;
    internal static ConfigEntry<float> HealthDrainPerSecond = null!;
    internal static ConfigEntry<int> StrengthBonus = null!;
    internal static ConfigEntry<int> LaunchBonus = null!;
    internal static ConfigEntry<bool> CanBeLethal = null!;
    internal static ConfigEntry<bool> ShowIndicator = null!;

    // ── Public API (read-only) so other mods (e.g. the UI overlay) can reflect the
    //    berserk state. Returns the live applied bonuses, or 0/false when inactive. ──
    public static bool IsActive => Patches.BerserkPatch.IsActive;
    public static int ActiveStrengthBonus => Patches.BerserkPatch.ActiveStrengthBonus;
    public static int ActiveLaunchBonus => Patches.BerserkPatch.ActiveLaunchBonus;

    private void Awake()
    {
        Instance = this;
        this.gameObject.transform.parent = null;
        this.gameObject.hideFlags = HideFlags.HideAndDontSave;

        BindConfiguration();
        Harmony ??= new Harmony(Info.Metadata.GUID);
        Harmony.PatchAll();

        Logger.LogInfo($"{Info.Metadata.GUID} v{Info.Metadata.Version} has loaded!");
    }

    private void OnDestroy()
    {
        // Make sure we never leave a bonus or a half-drained state behind.
        Patches.BerserkPatch.ForceDeactivate();
        Harmony?.UnpatchSelf();
    }

    private void BindConfiguration()
    {
        const string section = "Berserk";

        ToggleKey = Config.Bind(section, "Toggle Key", new KeyboardShortcut(KeyCode.B),
            "Key to toggle the berserk state on and off.");

        HealthDrainPerSecond = Config.Bind(section, "Health Drain Per Second", 10f,
            new ConfigDescription(
                "Health drained every second while berserk is active. Can kill you " +
                "(see Can Be Lethal).",
                new AcceptableValueRange<float>(0f, 100f)));

        StrengthBonus = Config.Bind(section, "Strength Bonus", 5,
            new ConfigDescription(
                "Temporary Strength levels added while berserk is active (real grab/throw " +
                "power plus everything that reads your Strength level).",
                new AcceptableValueRange<int>(0, 50)));

        LaunchBonus = Config.Bind(section, "Launch Bonus", 5,
            new ConfigDescription(
                "Temporary Tumble Launch levels added while berserk is active (real launch " +
                "force plus everything that reads your Launch level).",
                new AcceptableValueRange<int>(0, 50)));

        CanBeLethal = Config.Bind(section, "Can Be Lethal", true,
            "If true, the drain can take you to 0 HP and kill you. If false, the drain " +
            "stops at 1 HP and never kills you directly.");

        ShowIndicator = Config.Bind(section, "Show Indicator", true,
            "Show the on-screen BERSERK indicator while the state is active.");
    }

    // ══════════════════════════════════════════════════════
    //  On-screen indicator — a pulsing red "BERSERK" badge
    // ══════════════════════════════════════════════════════

    private static Texture2D? _whiteTex;
    private static Texture2D WhiteTex
    {
        get
        {
            if (_whiteTex == null)
            {
                _whiteTex = new Texture2D(1, 1);
                _whiteTex.SetPixel(0, 0, Color.white);
                _whiteTex.Apply();
            }
            return _whiteTex;
        }
    }

    private static void FillRect(Rect r, Color c)
    {
        Color prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, WhiteTex);
        GUI.color = prev;
    }

    private static void DrawBorder(Rect r, float t, Color c)
    {
        FillRect(new Rect(r.x, r.y, r.width, t), c);            // top
        FillRect(new Rect(r.x, r.yMax - t, r.width, t), c);     // bottom
        FillRect(new Rect(r.x, r.y, t, r.height), c);           // left
        FillRect(new Rect(r.xMax - t, r.y, t, r.height), c);    // right
    }

    private void OnGUI()
    {
        if (!ShowIndicator.Value) return;
        if (!Patches.BerserkPatch.IsActive) return;

        // 0..1 heartbeat that keeps pulsing even while the game is paused.
        float pulse = Mathf.PingPong(Time.unscaledTime * 2.2f, 1f);

        const float w = 220f, h = 42f;
        float x = (Screen.width - w) / 2f;
        float y = 52f;
        var rect = new Rect(x, y, w, h);

        // Dark red plate + brighter pulsing border
        FillRect(rect, new Color(0.42f, 0f, 0f, Mathf.Lerp(0.55f, 0.82f, pulse)));
        DrawBorder(rect, 2f, new Color(1f, 0.22f, 0.12f, Mathf.Lerp(0.55f, 1f, pulse)));

        var style = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 21,
            fontStyle = FontStyle.Bold
        };

        // Drop shadow
        style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
        GUI.Label(new Rect(x + 2f, y + 2f, w, h), "BERSERK", style);

        // Pulsing red text
        style.normal.textColor = Color.Lerp(
            new Color(1f, 0.45f, 0.32f), new Color(1f, 0.12f, 0.06f), pulse);
        GUI.Label(rect, "BERSERK", style);
    }
}
