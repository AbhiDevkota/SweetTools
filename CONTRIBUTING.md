## Building from source / Developing

```powershell
dotnet build LuaToolsGui.sln
dotnet run --project src/LuaToolsGui/LuaToolsGui.csproj
```

For iterative development:

```powershell
dotnet watch --project src/LuaToolsGui/LuaToolsGui.csproj
```

### Test

```powershell
dotnet test
```

### Releases

Official builds are packaged and signed by the maintainers, so the release tooling isn't part of
this repo. Released builds are framework-dependent (~10 MB) and the setup auto-installs the .NET 8
Desktop Runtime on a clean machine; the app then self-updates through Velopack.

To produce a local build for testing, `dotnet publish -c Release` is enough.

### SweetTools account-aware Steam launcher

Building now also requires Visual Studio C++ desktop build tools (x64) and a Windows SDK.
MSBuild compiles `src/SweetTools.Loader` and embeds its `winmm.dll` in the application.
The loader targets 64-bit Steam. No upstream loader binary is downloaded or restored from backups.
Run `powershell -File scripts/build-loader.ps1 -Test` for native account-policy, isolated registry
notification/launch, and audio-export forwarding tests. Native outputs are ignored by Git.

To enable it, open the published app while signed into the allowed Steam account and use
Settings to lock that account again (or reinstall the store plugin). The existing installer replaces
the old DLL and restarts Steam. Keep the published app in a stable folder; reopen it after moving it.
The DLL launches the app visibly once per allowed login and the app rechecks the account before WPF starts.

The DLL has one sleeping thread inside Steam, waiting indefinitely for Windows registry notifications;
there is no timer, extra process, or Windows startup entry. It watches Steam's `ActiveProcess` registry
values and a single atomic configuration value under `HKCU\Software\SweetTools\SteamLauncher`.
That value contains the app path, Steam executable path, and allowed SteamID. Saving settings refreshes it.
Missing or invalid configuration fails closed. Unrelated registry changes do not repeatedly launch the app.
The DLL forwards Windows audio exports through `winmm_real.dll`, copied from the local operating system.

Account-mismatch cleanup retains only the current verified account-aware launcher and its audio companion;
it still removes other plugin modifications. Explicit purge/uninstall and clearing the allowed account
remove the launcher too. A launcher by itself does not count as active plugin modifications.
Registry notifications are tested with simulated login changes; real Steam login/account-switch behavior
still needs manual validation. No claim of zero CPU or memory use is made.
The launcher only starts the app; the existing app account guard still performs modification cleanup.
That cleanup requires the app to remain running when switching accounts.

### Layout

| Path | Contents |
|---|---|
| `src/LuaToolsGui/` | The application: `Views/` (XAML), `ViewModels/`, `Services/`, `Models/`, `Resources/` (localization) |
| `src/LuaToolsGui/AppConfig.cs` | All compiled-in endpoints, mirrors and public client values |
| `tests/LuaToolsGui.Tests/` | xUnit tests |
| `scripts/check-i18n.py` | Translation validator, run by CI on every RESX change |

## Contribution Guidelines

Three rules are non-negotiable.

### 1. Every user-facing string must be localized

The app ships 29 languages. **Never hardcode a visible string**, whether in XAML, a toast, a
`MessageBox`, or an empty-state label. Adding text means:

1. Add the key to the English source, `src/LuaToolsGui/Resources/Strings.resx`.
2. Add the accessor to the hand-maintained `Strings.Designer.cs`
   (`public static string Key => Get(nameof(Key));`).
3. Add the same key, with a real translation, to all `Strings.<tag>.resx` files. A key missing
   from any one of them is a bug.
4. Reference it. XAML: `Text="{x:Static res:Strings.Key}"`; C#: `Resources.Strings.Key` or
   `string.Format(Resources.Strings.Key, arg)`.

`.github/workflows/i18n-check.yml` runs `scripts/check-i18n.py` on every PR touching the RESX files.
Translation contributions are pull requests against those files. See
[`src/LuaToolsGui/Resources/README.md`](src/LuaToolsGui/Resources/README.md) for the translator
guide, including which terms to leave untranslated and why.

### 2. Every GitHub request goes through `GithubProxy`

The app must work in regions where `github.com` and `api.github.com` are blocked. **Never call
GitHub directly.** Route through `Services/GithubProxy.cs` (a DI singleton), which tries the direct
URL first and then falls through capability-matched mirrors:

```csharp
await gh.SendAsync(url, ct);                          // API / metadata
await gh.DownloadAsync(url, dest, progress, ct);      // release-asset binaries
```

The Velopack auto-updater is covered too. `UpdateService` passes a `ProxiedFileDownloader` that
reuses `GithubProxy.Candidates`, so the update feed and `.nupkg` fall back to mirrors as well.

### 3. Store-page plugin calls go through `callServerMethod`

The injected store-page script runs inside an HTTPS page and cannot raw-`fetch` a localhost
backend; mixed-content policy kills it silently. Any new store-page → backend call must go through
`Millennium.callServerMethod("luatools", "<Name>", args)` and needs a matching entry in
`CefInjectorService.CallBackendMethod`'s `methodMap`. An unmapped name silently no-ops instead of
erroring.

`Millennium.callServerMethod` resolves one of two ways depending on the user's setup: under
Millennium it's the framework's own object, and otherwise it's the queue-based polyfill
`CefInjectorService` injects ahead of the plugin script. Either way the actual HTTP request is made
outside the browser, which is what sidesteps the mixed-content block.
