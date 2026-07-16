# Berserk

A [BepInEx](https://github.com/BepInEx/BepInEx) mod for **R.E.P.O.** that adds a toggleable *berserk* state: bleed health every second in exchange for a big **Strength** and **Tumble Launch** boost. Live fast, hit hard.

## What This Mod Does

Press **B** to toggle berserk on or off. While it's on:

- You **lose 10 HP every second** (configurable — and it *can* kill you).
- You gain **+5 Strength** levels — stronger grab/throw force, and everything that reads your Strength level (Armor, the UI overlay, …) sees the boost too.
- You gain **+5 Tumble Launch** levels — more launch force, and if you run **Increase Tumble Damage**, your tumble hits land for a *lot* more.

A pulsing red **BERSERK** badge appears at the top of the screen so you always know it's active. It turns itself off automatically when you die or leave the level.

> It's a glass-cannon trade: enormous burst power for a constant bleed. Pop it for a fight, then turn it off before the drain takes you.

## How It Works (and why it plays nice with other mods)

The boost is applied two ways at once, and **neither touches the saved upgrade data**:

1. **Live game effect** — exactly mirroring the game's own upgrade-apply helpers, but only on the live components:
   - `physGrabber.grabStrength += 0.2 × StrengthBonus`
   - `tumble.tumbleLaunch += LaunchBonus`
2. **Stat-level overlay** — a temporary additive layer in [Character Stats](https://github.com/headclef/Repo-CharacterStats) that other mods read, so Armor / Increase Tumble Damage / UI all treat you as `+5` while berserk.

Because nothing is written into the game's `playerUpgrade*` dictionaries, **Improve never absorbs the bonus and it's never baked into your `.es3` save.** The health drain bypasses **Armor** (it writes health directly instead of going through the damage path), so the bleed is never softened. Everything reverses to the exact applied amount when you toggle off.

## Configuration

Settings are in `BepInEx/config/headclef.Berserk.cfg` or in the **in-game mod config menu**:

| Key | Default | Range | Description |
|-----|---------|-------|-------------|
| Toggle Key | `B` | — | Key to toggle the berserk state |
| Health Drain Per Second | `10` | 0–100 | HP drained every second while active |
| Strength Bonus | `5` | 0–50 | Temporary Strength levels added while active |
| Launch Bonus | `5` | 0–50 | Temporary Tumble Launch levels added while active |
| Can Be Lethal | `true` | — | If `true` the drain can take you to 0 HP and kill you; if `false` it stops at 1 HP |
| Show Indicator | `true` | — | Show the on-screen BERSERK badge while active |

**Berserk itself has no multiplayer setting.** If you play as a co-op client, the one switch that matters lives in Relay — see [Multiplayer](#multiplayer).

## Requirements

- [BepInEx 5.x](https://github.com/BepInEx/BepInEx) installed for R.E.P.O.
- [Character Stats](https://github.com/headclef/Repo-CharacterStats) — required dependency
- **[Relay](https://github.com/headclef/Repo-Relay) — required as of 1.1.0.** Berserk will not load without it. Thunderstore installs it for you; if you install manually, install Relay too.

## Installation

1. Install via **Thunderstore** (recommended) — Character Stats and Relay will be installed automatically.
2. Or manually: place `Character Stats.dll`, `Relay.dll` and `Berserk.dll` into your `BepInEx/plugins` folder.
3. Launch the game — the config file is generated on first run.
4. Playing as a **co-op client**? Turn Relay's switch on — see [Multiplayer](#multiplayer). Without it Berserk will simply refuse to activate, on purpose.

### Upgrading from 1.0.x

- **1.1.0 requires Relay.** Nothing else changes: every Berserk setting keeps its name, its place and your value.
- **1.0.5 and earlier refused to work as a co-op client** (1.0.4 and earlier were worse — see below). With Relay switched on, 1.1.0 works as a client for the first time.
- If you play only single player or only as the host, nothing about your setup changes.

## Multiplayer

- Runs **per-client** — only you go berserk, and only your own stats and health are affected.
- **As the host or in single player**, both boosts apply in full. Nothing below matters to you.
- Death is fully networked (it uses the game's own death call), so dying while berserk syncs correctly. The drain itself is applied locally, so other players' copy of your health bar may lag slightly until the next sync.

### As a co-op client — turn Relay on, or Berserk stays off

Berserk's two effects are **Grab Strength** and **Tumble Launch**, and those are precisely the two stats R.E.P.O. computes on the **host**, from the host's own copy of your character. Your machine is never asked, and every message the game has for changing a stat is host-only by design. So writing them locally does exactly nothing for a client.

The health drain, however, is local and works perfectly. That combination is the trap: **all cost, no benefit.** Up to 1.0.4 nothing stopped you from activating, so a client burned health for a boost that never existed, while the badge and the stat overlay cheerfully showed a bonus that did nothing.

So Berserk asks [Relay](https://github.com/headclef/Repo-Relay) one question before it will turn on: *can this boost actually be delivered?*

| Situation | Berserk |
|---|---|
| Single player, or you are the host | Works — your writes are the simulation |
| Client, Relay switched off | **Refuses**, and says why in the log |
| Client, Relay on but the host does not run it | **Refuses** — the host never answers, so nothing could land |
| Client, Relay on for both of you | Works — Relay applies the boost on the host |

To use it as a client, **you and the host both**:

1. Have Relay installed (it comes with Berserk).
2. Set `BepInEx/config/headclef.Relay.cfg` → `[Multiplayer]` → `Enabled = true`, or use the in-game mod config menu.

There is nothing to set in Berserk. If it refuses, the log line tells you which half is missing. Relay ships **off by default** because an earlier version of that bridge broke multiplayer connect — read Relay's readme before switching it on.

Improve's allocations and Berserk's boost **stack** rather than fight: both report to Relay, which adds them up and applies the total once. That is the whole reason Relay exists as its own mod.

## Development

### Project Structure
```
├── Berserk.cs                  # Plugin entry point, config & the BERSERK HUD badge
├── Patches/
│   └── BerserkPatch.cs         # Toggle, health drain + death, stat application, auto-off
└── README.md
```

### Building

This project is part of the `Repo.slnx` solution and references Character Stats at compile time. Build the **whole solution** so dependencies build in the correct order:

```bash
dotnet build ../Repo.slnx
```

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.

---

These mods (all) generated by using AI tools. If there's anything wrong use NexusMods to give a feedback, I use there more than ThunderStore.
