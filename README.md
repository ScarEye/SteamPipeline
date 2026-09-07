# SteamPipeline

Git-based Unity Editor tooling: **Build Windows 64** and **upload to Steam** via the Steamworks ContentBuilder bundled in this repo (`sdk/`).

Not a UPM package — clone or submodule this repo under your game’s `Assets/` folder.

## Quick start

1. Clone into your project:
   ```text
   YourGame/Assets/SteamPipeline/   ← this repo
   ```
2. Open Unity → menu **SteamPipeline > Create Settings Asset** → set your real **AppID** and **DepotID**
3. **SteamPipeline > Open** → enter Steam username (password is session-only)
4. Click **Login / Refresh Guard** once (Steam Guard)
5. Click **Build & Upload**

## AppID / DepotID (important)

Do **not** use:

| ID | Why |
| --- | --- |
| `480` | Spacewar test AppID |
| `1000` / `1001` / `1002` | Sample IDs in `sdk/tools/ContentBuilder/scripts/` |

Get your real values from [Steamworks Partner](https://partner.steamgames.com/) → your app → **SteamPipe** → Depots / Builds.

See [SETUP_STEAM_SDK.md](SETUP_STEAM_SDK.md) for SDK layout, steamcmd bootstrap, and branch notes.

## Security

- Never commit Steam passwords, `config.vdf`, or `ssfn*` files (`.gitignore` covers these).
- Prefer a **private** GitHub repo — this tree includes the Steamworks SDK under `sdk/`.
- Prefer uploading to a non-`default` branch (e.g. `beta`), then promote in the partner site.

## Layout

```text
Editor/          Unity Editor scripts + VDF templates
sdk/             Steamworks SDK (ContentBuilder / steamcmd)
```
