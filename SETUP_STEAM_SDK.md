# Steamworks SDK & App setup

## Install

Prefer **Package Manager → Add package from git URL** (`com.scareye.steampipeline`). The package stays under `Packages/`, separate from your game `Assets/`.

## SDK location

This package includes the Steamworks SDK at:

```text
SteamworksSDK~/          (folder ends with ~ — Unity will not import it as assets)
  tools/
    ContentBuilder/
      builder/     steamcmd.exe (Windows)
      scripts/     sample VDFs (reference only — AppID 1000 etc.)
```

Do **not** rename this back to `sdk/` under Assets/Packages without the `~`. Steamworks C++ (e.g. `glmgr`) will otherwise break Windows IL2CPP builds (`OpenGL/OpenGL.h` missing).

**Per-project staging** (mutable; not inside the package):

```text
YourProject/Library/SteamPipeline/
  content/     staged game files for upload
  output/      SteamPipe cache/logs
  scripts/     generated app_build_generated.vdf
```

SteamPipeline resolves `steamcmd.exe` from the package. You should not need a machine-specific absolute path.

## Bootstrap steamcmd (once)

If uploads fail because builder files are incomplete:

1. Run `SteamworksSDK~/tools/ContentBuilder/builder/steamcmd.exe` once
2. Let it update / quit (`+quit`)
3. Retry **Login** from the Unity window

## AppID, DepotID, branch

### Where to find them

1. Open https://partner.steamgames.com/
2. Select your application
3. **AppID** — on the app overview
4. **DepotID** — SteamPipe → **Depots** (do not assume AppID+1; copy the partner value)
5. **Branch** — SteamPipe → Builds / branches (e.g. `beta`)

### Sample IDs to avoid

The SDK ships example scripts such as `app_build_1000.vdf` using AppID **1000** and depots **1001** / **1002**. Tutorials often use Spacewar **480**.

SteamPipeline **blocks** those sample AppIDs and warns on sample depot IDs. Always set your real IDs on the settings asset.

### steam_appid.txt vs SteamPipe AppID

| Mechanism | Purpose |
| --- | --- |
| Settings AppID → generated VDF | Uploads the build to the correct Steam app |
| `steam_appid.txt` next to the `.exe` | Local SteamAPI / running outside Steam |

Optional: enable **Write steam_appid.txt** on settings so the staged build gets the same AppID for local testing.

### In-game Steam plugins

If you use Steamworks.NET / Facepunch / similar, keep that plugin’s AppID in sync with SteamPipeline settings manually.

## Branches and “default”

Automated upload can set a branch live via `SetLive` (the **Release Branch** field, default `beta`).

Steam restricts fully automating the **default** store branch for many apps. Recommended flow:

1. Upload to `beta` / `staging`
2. Test the build
3. Promote to default in the Steamworks partner website

Leave **Release Branch** empty to upload without setting any branch live.

## Credentials

- Username is stored in EditorPrefs (local machine).
- Password is entered in the Editor window and **not** written to disk by SteamPipeline.
- After a successful Guard login, steamcmd keeps a local session under the builder user data — do not commit those files.

## Using in another Unity project

**Package Manager (recommended):**

`+` → Add package from git URL → `https://github.com/ScarEye/SteamPipeline.git`

**Or** clone under `Assets/SteamPipeline` if you are not using UPM.

Then create settings, set AppID/DepotID, login, Build & Upload.
