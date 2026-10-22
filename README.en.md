# Kyouka MCLauncher

A from-scratch Minecraft: Java Edition launcher for Windows, built around one idea:
**download files from whichever source actually works, and keep working when the usual ones don't.**

> Not an official Minecraft product. Not approved by or associated with Mojang or Microsoft.
> No game files are bundled or redistributed — everything is downloaded from official or mirror sources at runtime, in line with the EULA.

---

## Why this exists

In late 2026, several mainstream Chinese launchers removed or restricted their "download the game" feature.
This project re-implements that pipeline from scratch, with two deliberate rules:

1. **Never depend on a single source.** Multiple Mojang mirrors are probed and raced; the fastest healthy one wins.
2. **Never ship game files.** The installer only fetches what the user is entitled to, from authorized sources.

---

## Features

| Area | What you get |
|---|---|
| **Download routing** | 9 Mojang mirrors, health-probed and speed-ranked, automatic failover mid-download; no third-party redirect API |
| **Vanilla install** | Full version closure: jars, libraries, assets, logging config, `version.json` merge via `inheritsFrom` |
| **Loaders** | Forge, NeoForge, Fabric, Quilt, Legacy Fabric |
| **Cleanroom (1.12.2)** | One-click repair: fills missing libs, rebuilds classpath, extracts LWJGL3 natives, picks a suitable Java, heals stale absolute paths |
| **Mods** | Browse / search / install from Modrinth and CurseForge (CF needs your own API key), plus install-from-link |
| **Resource packs & shaders** | Local management (enable/disable/import/delete) **and** direct download from Modrinth |
| **Multi-instance** | Named instances under `versions/<name>/` with their own `mods/`, `saves/`, `config/` — recognized as normal versions by HMCL / PCL / the official launcher |
| **External login** | authlib-injector integration: authenticate against a Yggdrasil-compatible skin server (LittleSkin etc.); token stored locally only |
| **Graceful close** | Closing the game sends `WM_CLOSE` to every window in the whole process tree (`java.exe` → `javaw.exe`), then force-ends the tree if needed; orphaned game processes are detected on startup |
| **Auto-update** | Checks GitHub releases, verifies, swaps the exe and restarts |

---

## Quick start

1. Download `KyoukaMCLauncher.exe` from [Releases](https://github.com/Hourai-chiyo/Kyouka-MCLauncher/releases).
2. Run it and pick a data directory (or keep the default).
3. Launch page → choose a version → **Install / Launch**. Java is detected automatically; if it is missing, the launcher offers to download a suitable JDK.
4. For modded play, install a loader from the same page.

Command-line (legacy host, useful for diagnostics):

```text
mcroute launch <version>               # launch
mcroute build <version>                # print the command line and report missing files
mcroute verify <version> [--fix]       # check / repair an installation
mcroute fetch <version> [--no-assets]  # download the full closure
mcroute loader <fabric|forge|...> <version>
mcroute install <pack.mrpack|pack.zip> # Modrinth / MultiMC / CurseForge packs
mcroute sources                        # list mirrors and probe them
```

---

## Instances (version isolation)

Instances live in `versions/<name>/`, exactly like normal version isolation:

```text
versions/
└── My Tech Pack/
    ├── My Tech Pack.json     {"id":"My Tech Pack","inheritsFrom":"1.20.1","jar":"1.20.1"}
    ├── instance.json         name / base version / created time
    └── mods/ saves/ config/
```

Because the layout is standard, other launchers see each instance as an ordinary version.
Legacy `instances/` folders from v1.4.x are migrated automatically on first open.

---

## External login (skin servers)

The launcher embeds [authlib-injector](https://github.com/yushijinhun/authlib-injector) and adds
`-javaagent:<jar>=<api-root>` when an account is configured.
It talks to the standard Yggdrasil endpoints (`/authserver/authenticate`).
Microsoft accounts are **not** supported — the Mojang AppID application was rejected, so the project
does not pretend to offer Microsoft login.

---

## Limitations (honest list)

- **Windows only.** WPF + .NET Framework 4.8.
- **No Microsoft login.** Use offline mode or an external (Yggdrasil) account.
- **CurseForge requires your own free API key** (stored in `cf_api_key.txt` in the data directory).
- **Cleanroom 1.12.2 needs Java 17+** (Java 21 recommended). The launcher detects and warns.
- Mirrors are third-party services; availability changes. The launcher re-probes them, but cannot guarantee any single mirror.
- mcmod.cn is only used as a *search redirect* — the launcher does not scrape or bypass it.

---

## Verification

Every build is checked in CI, and the core has a dependency-free unit test suite:

```powershell
# quick self-tests (also available in releases)
mcroute --selftest-security
mcroute --selftest-packs
mcroute --selftest-instances
mcroute --selftest-cleanroom <version> --dir <data-dir> --dry

# unit tests (81 assertions, plain net48 console — no NuGet)
dotnet build tests/Mcroute.Tests/Mcroute.Tests.csproj -c Release -o tests-out
tests-out/Mcroute.Tests.exe
```

Coverage: JSON parser, version merging/rules, update version comparison, Zip-Slip protection,
mod metadata (Fabric / Forge / old Forge), resource-pack metadata and toggling,
instance create/copy/rename/delete, authlib URL normalization.

---

## Build from source

```powershell
git clone https://github.com/Hourai-chiyo/Kyouka-MCLauncher.git
cd Kyouka-MCLauncher
dotnet build app/KyoukaMCLauncher.csproj -c Release -o build
dotnet build legacy/winforms/McrouteWinForms.csproj -c Release -o build-legacy
```

Requirements: Windows, .NET SDK 8 (net48 targeting pack included with the SDK).

---

## License & compliance

- Source: **MIT** (see `LICENSE`).
- No game files, no assets, no Mojang binaries are distributed.
- Microsoft login is not implemented; no client IDs are embedded or shared.
- Icon created with [EmoteLab](https://emotelab.net); attribution kept in the app's Settings and release notes.
- “Minecraft” is a trademark of Mojang Synergies AB. This project is unofficial.

---

## Links

- Chinese README: [README.md](README.md)
- Issues: https://github.com/Hourai-chiyo/Kyouka-MCLauncher/issues
- Releases: https://github.com/Hourai-chiyo/Kyouka-MCLauncher/releases
