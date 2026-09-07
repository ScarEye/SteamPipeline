# SteamPipeline

Unity **UPM** package: build Windows 64 and upload to Steam via bundled SteamCMD / ContentBuilder (`sdk/`).

Lives under **Packages** (separate from your game Assets). Menu: **SteamPipeline**.

## Install (recommended)

In your Unity project:

1. **Window → Package Manager**
2. **+ → Add package from git URL…**
3. Paste:
   ```text
   https://github.com/ScarEye/SteamPipeline.git
   ```
4. Unity installs `com.scareye.steampipeline` under `Packages/`

Or add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.scareye.steampipeline": "https://github.com/ScarEye/SteamPipeline.git"
  }
}
```

For a private repo, use a URL Git can authenticate (SSH or a credential helper), e.g. `git@github.com:ScarEye/SteamPipeline.git`.

### Alternative: clone under Assets

Still works if you prefer:

```bash
cd YourGame/Assets
git clone https://github.com/ScarEye/SteamPipeline.git SteamPipeline
```

## After install

1. Menu **SteamPipeline → Create Settings Asset** (save under your project `Assets/`)
2. Set real **AppID** / **DepotID** from [partner.steamgames.com](https://partner.steamgames.com/)
3. **SteamPipeline → Open** → username + password → **Login / Refresh Guard** once
4. **Build & Upload** — a small progress window shows build → stage → upload (elapsed time + bar; steamcmd stays hidden)

Staging (builds for upload) goes to your project’s `Library/SteamPipeline/` — not inside the package — so Package Manager updates don’t wipe uploads mid-flight.

## AppID / DepotID (important)

Do **not** use:

| ID | Why |
| --- | --- |
| `480` | Spacewar test AppID |
| `1000` / `1001` / `1002` | Sample IDs in SDK scripts |

See [SETUP_STEAM_SDK.md](SETUP_STEAM_SDK.md).

## Security

- Never commit Steam passwords, `config.vdf`, or `ssfn*`
- Keep the GitHub repo **private** (contains Steamworks SDK)
- Prefer uploading to `beta`, promote default in the partner site

## Layout

```text
package.json     UPM manifest (com.scareye.steampipeline)
Editor/          Unity Editor scripts + VDF templates
sdk/             Steamworks SDK (steamcmd / ContentBuilder)
```
