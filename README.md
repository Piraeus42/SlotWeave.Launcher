# SlotWeave Launcher

**Mod manager for Luck be a Landlord** — install, update, and manage [SlotWeave](https://github.com/Piraeus42/SlotWeave) mods from the command line.

Created by Piraeus.

## Features

- **Auto-detect** Luck be a Landlord installation (Steam, custom paths)
- **One-click install/update** SlotWeave framework and mods from GitHub Releases
- **Version checking** — compares local vs remote, highlights available updates
- **Safe operations** — backup before mutation, auto-rollback on failure
- **Cache management** — clears SlotWeave cache after updates
- **Legacy migration** — detects and removes old GDWeave installations
- **Self-update** — checks for launcher updates on GitHub
- **Bilingual** — 中文 / English with first-launch language picker

## Installation

1. Download `SlotWeave.Launcher.exe` from the [latest release](https://github.com/Piraeus42/SlotWeave.Launcher/releases/latest)
2. Place it anywhere (desktop, game folder, etc.)
3. Run it — `.NET 8 Desktop Runtime` is required (same as SlotWeave)

On first launch, the launcher auto-creates `launcher_config.json` next to itself.

## Usage

```
╔═══════════════════════════════════════════════════════════════╗
║              SlotWeave Launcher v1.0.0                        ║
║              Created by Piraeus                               ║
╚═══════════════════════════════════════════════════════════════╝

  [1] Update Mods ⚠
  [2] Uninstall Mods
  [3] Launch Game
  [4] Refresh Check
  [0] Exit
```

### Menu Options

| Option | Description |
|--------|-------------|
| Install Mods | Install SlotWeave framework + available mods |
| Update Mods | Download and apply latest versions |
| Uninstall Mods | Remove mods with optional backup |
| Launch Game | Start Luck be a Landlord |
| Refresh Check | Re-scan local files and GitHub releases |

### Managed Components

- **[SlotWeave](https://github.com/Piraeus42/SlotWeave)** — GDScript modding framework (fork of GDWeave)
- **[Better Landlord](https://github.com/Piraeus42/BetterLandlord)** — Run history database with timeline viewer

## Build from Source

```powershell
dotnet publish -c Release -r win-x64 -o ./publish
# Output: publish/SlotWeave.Launcher.exe (~240 KB, single file)
```

Requires .NET 8 SDK.

## Directory Structure

```
Luck be a Landlord/
├── Luck be a Landlord.exe
├── winmm.dll                    ← SlotWeave proxy loader
├── SlotWeave/
│   ├── version.json
│   ├── core/                    ← Framework assemblies
│   └── mods/
│       └── Piraeus.BetterLandlord/
└── .slotweave_launcher/         ← Launcher data (backups, settings)
```

## License

MIT
