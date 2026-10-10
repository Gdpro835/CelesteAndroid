# Everest (mod loader) support — feasibility and plan

> **This is a plan, not an implementation.** No code in this repository loads mods yet.
> Written 2026-10-10 against [EverestAPI/Everest](https://github.com/EverestAPI/Everest) `dev` @ `322fb9e19d89`
> (2026-10-08), MonoMod @ `dfc30a15` (Everest's pin) / NuGet `MonoMod.Patcher 25.0.1` (ours), and this repository @ `586936e`.
> Every file:line reference below was read from those sources. Nothing here has been measured on a device — §6 lists what has to be.

## 0. Verdict

Everest support is feasible, and it doesn't require giving up anything this port already does: the toolchain is the
same one we already run on the device (MonoMod v2 + Mono.Cecil), Everest is MIT licensed, and Everest does **not**
patch `Monocle.Engine.RenderCore`, so the pillarbox background and the on-screen pad survive untouched.

But it is a second project of roughly the size of the port itself, and the work splits into three very different pieces:

| | Piece | Where the work is | Can it be validated without a phone? |
|---|---|---|---|
| A | Host plumbing (mods folder, patch chaining, launcher, MMHOOK) | this repository | **mostly yes** — `Celeste.Desktop` runs the same patched dll |
| B | An Everest build that runs against *our* FNA | an Everest fork + its CI | partly (compile + desktop host) |
| C | `MonoMod.RuntimeDetour` and mod loading on ARM64 Mono | device only | **no** |

C is the risk; A and B are engineering. [celeste-wasm](https://github.com/SaturnZone/celeste-wasm-android-and-xbox)
needed a WASM-specific MonoMod port precisely because detours are platform-specific, so "it works on the desktop host"
says nothing about the phone. **Do C first as a spike (§5, Stage 0) — it is the go/no-go gate for everything else.**

Recommended order: **Stage 0 spike → Stage 1 host plumbing → Stage 2 Everest build → Stage 3 Android fixes for
Everest itself → Stage 4 first mods on a device.** Stage 1 is worth doing even if Everest never lands: multi-module
patching, a `Mods/` directory and mod import are the foundation for any mod support at all.

## 1. What Everest actually does

### 1.1 Install (`MiniInstaller`)

`MiniInstaller/Program.cs:99-125`, helpers in `MiniInstaller/DepCalls.cs`:

1. **NETCoreifier** over the vanilla `orig/Celeste.exe` → `Celeste.dll` (rewrites .NET Framework references to modern
   ones), and over `everest-lib/FNA.dll`. Everest ships its own FNA — `lib-stripped/FNA.dll` is **v21.3.5.0** (SDL2).
2. `MonoMod.Patcher` over **`FNA.dll`** with `mods = { everest-lib/, Celeste.Mod.mm.dll }`.
3. `MonoMod.Patcher` over **`Celeste.dll`** with the same mods → game + Everest core merged into one assembly.
4. `MonoMod.RuntimeDetour.HookGen --private` over the result → **`MMHOOK_Celeste.dll`** (the `On.Celeste.*` /
   `IL.Celeste.*` hooks mods compile against), then MonoMod over MMHOOK again ("relink it against the legacy MonoMod layer").
5. `runtimeconfig` + apphost, so `Celeste.exe` starts `Celeste.dll` whose entry point is Everest's.

Rules that matter for us (`Celeste.Mod.mm/MonoModRules.Game.cs`):

- **It refuses an already-modded assembly**: `if (modder.FindType("MonoMod.WasHere")?.Scope == modder.Module) throw`
  (line 74-77). Everest must see the clean game → our module goes in the *same* MonoMod pass or in a *later* one, never before.
- `MinimumGameVersion = 1.4.0.0` (line 63), detected from the IL of `Celeste..ctor` (line 112+) — the version this port is tested with.
- Flags set at patch time: `FNA=true`, `XNA=false`, `Steamworks=<game references Steamworks>`, `NoLauncher=!Steamworks`,
  `OS:Windows` (lines 88-95). Many patches are `[MonoModIfFlag("RelinkXNA")]` / `("XNA")` and simply don't apply to an
  FNA install — including **all the `System.Drawing` code** (`Mod/Everest/ContentExtensions.cs:247-300`). That is good
  news: `System.Drawing.Common` is Windows-only on modern .NET and we never reach it.
- `[GameDependencyPatch("FNA")]` types are dropped when the target is the game (`MonoModRules.Dependency.cs:84-99`),
  so the FNA patches (`Patches/FNA/*`: `Game.BeforeLoop` raising the window after the splash, `Color`, `Keyboard`,
  `MathHelper`, `Vector`, headless mode) only matter when patching `FNA.dll` — see §3.
- `RelinkSDLCalls(modder, 3, 2)` (line 108-109, implementation 219-227), with the comment *"Vanilla does a few sdl
  calls, Celeste 1.4.1.0 is supposed to ship with SDL3 instead, so downgrade those"*: it rewrites
  `SDL3.SDL::SDL_GetPlatform` / `SDL_GetPrefPath` into `SDL2.SDL::…`, because Everest's FNA is SDL2-based.
  **This is also a bug for us, independent of Everest** — see §7.
- `PatchCelesteMain` (an IL rule over the *vanilla* `Celeste.Main`, `Patches/Celeste.cs:421-470`) rewrites the
  `call SDL2.SDL::SDL_GetPlatform` inside `Main` into the constant `"Windows"`, and **throws** if it can't find two
  other IL patterns there (`dup; callvirt ToString; call Console.WriteLine` and `ldstr "Failed to open the log!"`).
  Everest's rules therefore need an untouched `Main` body — which our `SDLShim` relink also targets. Ordering has to be
  deliberate (§4.3).

### 1.2 Boot

- The patched assembly's entry point is **`Celeste.Mod.BOOT.Main`** (`[MakeEntryPoint]`, `Mod/Everest/BOOT.cs:27`),
  not `Celeste.Main`. It sets the culture, reads `modsettings-Everest` (compatibility mode, colored logging), probes
  SELinux on Linux (line 54-77), sets native lib paths (line 182-229), optionally starts the splash, then calls
  `patch_Celeste.Main(args)` (line 128) and finally **`Environment.Exit(0)`** (line 150).
- `patch_Celeste.Main` is `[MonoModPublic] public static void Main` with `[PatchCelesteMain] extern orig_Main`
  (`Patches/Celeste.cs:37-40`) — **after Everest, `Celeste.Main` is public**.
- `Main` writes into the *current directory*: `BuildIsFNA.txt`, `everest-launch.txt` (it creates it with comments),
  reads `everest-env.txt`, and rotates `log.txt` into `LogHistory/` (`Patches/Celeste.cs:47-64`, `143-180`).
- `MainInner` → `Everest.ParseArgs`, `ParseFNAArgs` (sets `FNA_AUDIO_DISABLE_SOUND=1`, and `FNA3D_FORCE_DRIVER` from
  `--graphics`, calling native `setenv` **only** `if IsOSPlatform(Linux)`), then `orig_Main(args)` — the real game —
  then `Everest.Shutdown()`. On exception: `CriticalFailureHandler` → `ErrorLog.Write/Open` + `Environment.Exit(-1)`
  (`Patches/Celeste.cs:202-297`).
- `Everest.Boot()` (`Mod/Everest/Everest.cs:314+`):
  - `PathGame = Path.GetDirectoryName(typeof(Celeste).Assembly.Location)` (line 324), `PathTmp = EVEREST_TMPDIR ?? PathGame` (line 325);
  - an `AssemblyResolve` handler loads `Mono.Cecil*`, `YamlDotNet`, `NLua`, `DotNetZip`, `Newtonsoft.Json`, `Jdenticon`
    **from `PathGame`** via `Assembly.LoadFrom` (line 328-343);
  - it preloads `MonoMod.RuntimeDetour`, `MonoMod.Utils`, `Mono.Cecil`, `YamlDotNet`, `Newtonsoft.Json`, `Jdenticon`
    with plain **`Assembly.Load(name)`** (line 346-351) — these must resolve by simple name from the default load context;
  - `PathEverest = PathGame` unless a file called `EverestXDGFlag` exists, in which case it uses
    `SpecialFolder.LocalApplicationData/Everest` (line 353-361).

### 1.3 Mods

- `Mods/` (+ `Mods/Cache/`, `blacklist.txt`, `whitelist.txt`, `favorites.txt`, `modoptionsorder.txt`) under
  `PathEverest` (`Mod/Everest/Everest.Loader.cs:116-190`).
- A mod is a **`.zip` read with `ZipArchive`** or a folder; metadata comes from `everest.yaml` / `everest.yml`
  (`Everest.Loader.cs:264-290`, `369-390`). A `FileSystemWatcher` picks up new files when `AutoLoadNewMods` is on
  (line 102, 215, 241).
- Mod code runs in `EverestModuleAssemblyContext : AssemblyLoadContext, IAssemblyResolver`
  (`Mod/Module/EverestModuleAssemblyContext.cs:19`). .NET Framework mod dlls are coreified **at runtime** with
  `NETCoreifier.Coreifier.ConvertToNetCore(modder, sharedDeps: true, preventInlining: true)`
  (`Mod/Everest/Everest.Relinker.cs:228`) and cached in `Mods/Cache` → Mono.Cecil is needed at runtime and that
  directory must be writable.
- Native mod libraries are looked for in `lib-win-x64` / `lib-linux` / `lib-osx`; on Android none matches, so
  `UnmanagedLibraryFolder` is **null** (`EverestModuleAssemblyContext.cs:33-38`).
- Dependency closure (`Celeste.Mod.mm/Celeste.Mod.mm.csproj`): Mono.Cecil, MonoMod.Patcher / Utils / RuntimeDetour /
  RuntimeDetour.HookGen, YamlDotNet, Newtonsoft.Json, NLua (**`ShouldIncludeNativeLua=false`**), MAB.DotIgnore,
  DotNetZip, Jdenticon-net, System.Drawing.Common, plus the `DiscordGameSDK`, `NETCoreifier` and `EverestSplash`
  projects. `everest-lib/` (EverestAPI/Everest-libs, `binaries`) carries the natives: FNA 21.3.5.0, SDL2, FNA3D,
  FAudio, FMOD, steam_api, discord_game_sdk, theorafile, `piton*` (the splash runtime) — **all desktop-only**.

## 2. Gap analysis

What Everest assumes, what actually happens here, and what we would do about it.

| # | Everest assumes | Where | On Android / in this port | What we do |
|---|---|---|---|---|
| 1 | FNA 21.3.5.0 + SDL2 | `Celeste.Mod.mm.csproj`, `lib-stripped/FNA.dll` | our FNA is the submodule at `7c26942` (2026-09-23, SDL3), compiled from source into the APK | Everest core has to be built against our FNA, or its references relinked — §3 |
| 2 | `SDL2.SDL` exists | `Mod/Everest/TextInput.cs:3,74-75` (`SDL_GetClipboardText`/`SetClipboardText`), `Mod/Everest/Everest.DebugRC.cs:613` (`GetType("SDL2.SDL")`) | our FNA exposes `SDL3.SDL`; `Celeste.Mod.TextInput` would fail to load — and that is exactly the class our IME path feeds | fork: `using SDL3;`, or relink `SDL2.SDL::*` → `SDL3.SDL::*` and extend `SDLShim` (`src/Celeste.Android.Patches/Shims.cs`) with the two clipboard calls |
| 3 | Entry point is `BOOT.Main`; `Celeste.Main` is private | `BOOT.cs:27`, `Patches/Celeste.cs:39-40` | `CelesteLauncher.Run` reflects `GetMethod("Main", NonPublic \| Static)` (`src/Celeste.Android/CelesteLauncher.cs`) → **`null`/ambiguous once Everest is in** | call the module entry point when Everest is on; look `Main` up with `Public \| NonPublic`; expect `Environment.Exit` to end the `:game` process |
| 4 | `Assembly.Location` points at the game folder | `Everest.cs:324`, `BOOT.cs:37` | our patched dll is `files/patched/Celeste.dll` (and `Location` can be empty on Android — the same problem ARCHITECTURE.md documents for `Engine.AssemblyDirectory`) → `PathGame` = `files/patched`, so `Mods/`, `Content/` and `log.txt` would be looked for in the wrong place | patch `Everest.Boot`/`BOOT.Main` to take the directory from `HostConfig` (§4.2) |
| 5 | Dependencies resolve by name | `Everest.cs:346-351` (`Assembly.Load`) | APK **assets are not probing paths** — a dll that only exists as an asset cannot be `Assembly.Load`ed | ship Everest's *runtime* deps as app references (they land in the assembly store); only *patch-time* files (`Celeste.Mod.mm.dll`, `MonoMod.Patcher`, Cecil, HookGen) stay assets — the split `_AddPatcherAssets` in `Celeste.Android.csproj` already makes |
| 6 | Dependencies also live next to the game | `Everest.cs:328-343` (`Assembly.LoadFrom(PathGame/<name>.dll)`) | the game dir is writable internal storage | copy the same dlls into `files/Celeste/` too (cheap, and it is what that fallback expects) |
| 7 | `IsOSPlatform(Linux)` tells Linux from Windows | `BOOT.cs:54-77` (SELinux `mprotect(RWX)` probe), `BOOT.cs:219-220` (`LD_LIBRARY_PATH` + **process restart**), `Patches/Celeste.cs:231-256` (`setenv`), `Patches/UserIO.cs:41-50` | .NET for Android reports the platform as `"ANDROID"` (dotnet/runtime `OperatingSystem.cs`: `OSPlatformName`, and `IsOSPlatform` is a string compare), and `OSPlatform` has no Android member → **every Linux branch is skipped** | good: no SELinux probe, no restart loop. bad: `FNA3D_FORCE_DRIVER` set through the environment never reaches native `getenv` (we already use `SDL_SetHint` in `GameActivity.Main`), and anything in mods that branches on "Linux" sees an unknown platform |
| 8 | Child processes exist | `BOOT.cs:203-205,237-277` (`StartCelesteProcess`, `StartVanilla`), `Mod/Everest/Everest.Updater.cs:567-617` (MiniInstaller as a child process), `Mod/Everest/EverestSplashHandler.cs:61-83` (the `piton` binary), `Mod/UI/CriticalErrorHandler.cs:381`, `Patches/Commands.cs:298`, `Patches/Monocle/ErrorLog.cs:115` | `Process.Start` doesn't exist on Android | don't ship `EverestSplash/` (the splash then skips itself: `File.Exists` check), disable the in-game updater, no-op the "open log / open folder" paths |
| 9 | Discord RPC works | `Mod/Core/CoreModuleSettings.cs:292` (`DiscordRichPresence = true` **by default**), `Mod/Core/CoreModule.cs:56`, `Mod/Everest/Everest.DiscordSDK.cs` | there is no Android `discord_game_sdk` → `DllNotFoundException` on first load | force the setting off (and never construct `DiscordSDK.Instance`) from our Everest patch module |
| 10 | A local HTTP server is fine | `Mod/Everest/Everest.DebugRC.cs:29-95` (`HttpListener` on `localhost:<port>`), `:609` (`user32.dll` DllImport) | `HttpListener` would probably work but is useless on a phone; the `user32` import is Windows-only code | keep the setting off (it is off by default) and make sure the Windows path is unreachable |
| 11 | Mods may have native libs | `EverestModuleAssemblyContext.cs:33-38` | `UnmanagedLibraryFolder` is null on Android | document: mods shipping native libraries are not supported |
| 12 | Native Lua is next to the game | `Celeste.Mod.mm.csproj` (`ShouldIncludeNativeLua=false`), `Mod/Everest/Everest.LuaLoader*.cs` | no `liblua*.so` for arm64-android in Everest's libs | either build Lua with the NDK (like SDL3/FNA3D/FAudio in `scripts/build-natives-android.ps1`) or disable the Lua loader and say so in the UI |
| 13 | Saves live in the game/XDG dir | `Patches/UserIO.cs:23-25` (`SavePath` field is `[MonoModIfFlag("RelinkXNA")]`), `:36-60` (`GetSavePath` honours `EVEREST_SAVEPATH` first) | on an FNA install that field isn't created, so vanilla `UserIO.GetSavePath` → `SDL_GetPrefPath` → our shim → `files/userdata/` — which is what *Import saves* and `backup_rules.xml` expect | verify on device; if anything moves, set `EVEREST_SAVEPATH=<files/userdata>` |
| 14 | It may patch `Engine.RenderCore` | our `src/Celeste.Android.Patches/Monocle/patch_Engine.cs` | it doesn't: `RenderCore` appears nowhere in `Celeste.Mod.mm`. Everest's `Patches/Monocle/Engine.cs` replaces `RunWithLogging` and adds `SetViewWidth/Height/Viewport` | no conflict. Keep our module **last** so our `orig_` trampolines chain through Everest's |
| 15 | It patches `Monocle.ErrorLog` | our `patch_ErrorLog.cs` vs `Patches/Monocle/ErrorLog.cs` | Everest's replacements there are `[MonoModIfFlag("RelinkXNA")]` (lines 19-23) | ordering again: ours last wins, and our no-op `Open()` is what Android needs |
| 16 | One MonoMod | ours: NuGet `MonoMod.Patcher 25.0.1`; Everest's: submodule `dfc30a15` (2025-08-18, branch `reorganize`) | the merged assembly references the MonoMod Everest was built with | pin our patcher to the version Everest ships (or use Everest's build for the device patch) and ship exactly those dlls. Good news: `MonoModder.Mods` is a `List<ModuleReference>` and `ReadMod` can be called per module → **one pass can carry both modules** |
| 17 | Mods are portable | — | mods that P/Invoke Windows libraries, use WinForms/WPF, `System.Drawing`, or their own natives will fail | a compatibility ceiling to document, not something we can fix |

## 3. Which Everest do we build?

### Strategy A — stock Everest binaries, relinked at patch time

Take the upstream `Celeste.Mod.mm.dll` (+ its managed deps) as published, and fix the mismatch at patch time:

- add `MonoModLinkFrom` entries to our shim for `SDL2.SDL::SDL_GetClipboardText/SetClipboardText` → `SDL3.SDL::…`
  (the same trick `Shims.cs` already uses for the game's `SDL_GetPlatform`/`SDL_GetPrefPath`), or reuse Everest's own
  `RelinkSDLCalls(modder, 2, 3)` idea from our module's rules;
- let Everest's `FNA` assembly reference (v21.3.5.0) resolve by simple name to our FNA — .NET Core binding doesn't check versions.

Cost: a day on the desktop host. Risk: **four years of FNA drift** (21.03 → main). Everest core touches FNA APIs all
over `Mod/` and `Patches/Monocle/`; every member that was renamed or removed surfaces as a `MissingMethodException`
when the JIT first compiles the method that uses it — i.e. late, at runtime, in play. It also can't be fixed by us
without patching Everest's IL, which is exactly what strategy B avoids.

### Strategy B — a fork rebuilt against our FNA (recommended to ship)

Fork `EverestAPI/Everest@dev`, retarget `Celeste.Mod.mm.csproj`'s `Celeste`/`FNA`/`Steamworks.NET` references at what
this repository already has (`Celeste/Celeste.exe`, `external/FNA/FNA.Core.csproj`, `src/Steamworks.NET`), and build
`Celeste.Mod.mm.dll` in CI. The fork delta stays small and reviewable:

1. references → our FNA (compile errors then point at every real API drift, instead of runtime surprises);
2. `Mod/Everest/TextInput.cs` and `Mod/Everest/Everest.DebugRC.cs`: `SDL2` → `SDL3`;
3. Android guards for the splash, the updater, the `Process.Start` paths, Discord and DebugRC. `OperatingSystem.IsAndroid()`
   is public and a compile-time constant (`true` for the Android target, dotnet/runtime `OperatingSystem.cs`), so the
   guards cost nothing — and note `IsOSPlatform(OSPlatform.Linux)` is already *false* here, so the existing Linux
   branches don't need touching, they simply never run;
4. drop `DiscordGameSDK`/`EverestSplash`/`piton` from the Android build.

Everything else — including the fixes in §2 rows 4, 8, 9 — can live in **our own second patch module** instead of the
fork, because Everest core is merged into the same `Celeste.dll` we already patch (§4.2). Keep the fork delta as close
to "build configuration" as possible so tracking upstream stays cheap.

Use **A as the first experiment** (it answers "how bad is the FNA drift?" for a day of work, on the desktop host, with
no fork), and **B as the thing we ship**.

### MMHOOK

Mods compile against `MMHOOK_Celeste.dll`, which upstream generates during install. Two options:

- **on the device**, right after the patch: we already run Cecil there, so this is "just" another pass
  (`HookGen --private` + a MonoMod relink pass over its output). Cost: seconds-to-minutes of ARM64 time over a ~30 MB
  merged assembly, and it must be measured (Stage 4);
- **pre-generated at APK build time** from the `Celeste/Celeste.exe` the build used, shipped as an asset, and
  regenerated on the device only when the imported game's version differs (Everest has game-version/checksum logic to
  key on). Faster first launch, but the APK then carries an artifact tied to one game version.

Recommendation: implement the on-device path (correct for any game version), keep the pre-generated one as an
optimisation if the measured cost is bad.

## 4. Proposed shape in this repository

### 4.1 Where the binaries come from

Same model as FMOD and the game itself: **nothing binary is committed**.

- `everest/android/` (git-ignored) holds the Android Everest build: `Celeste.Mod.mm.dll`, its managed dependencies,
  `NETCoreifier.dll`, `MonoMod.RuntimeDetour.HookGen.dll`, and a `version.txt`.
- `scripts/fetch-everest.ps1 -Archive <file> | -Url <url>` validates and unpacks it (ELF/architecture checks where
  relevant, a manifest check that the required dlls are there), like `scripts/fetch-fmod.ps1`.
- `docs/BUILDING.md` §3 gets a row; CI gets an **optional** `EVEREST_URL` secret (like `NATIVES_ANDROID_URL`): without
  it the workflow builds the normal APK, with it the APK supports mods.
- `Celeste.Android.csproj` gets a `HasEverest` conditional mirroring `HasGameArt`, adding `everest/android/**` as
  `AndroidAsset` under `assets/everest/`, plus `PackageReference`s for the runtime deps (so `Assembly.Load` in
  `Everest.Boot` resolves). **Without the folder the app builds and behaves exactly as today** — Everest is opt-in and off by default.

### 4.2 Patching: three modules, one pass

`Celeste.Patcher.CelestePatcher.Patch` takes a *list* of mod modules instead of one, and calls `ReadMod` per module,
in this order (both callers change with it: `GameInstaller` on the device and `src/Celeste.Desktop/Program.cs`, which
is also why the desktop host can validate the whole patch chain without a phone):

1. `Celeste.Mod.mm.dll` (Everest core) — must run against the clean game (`MonoMod.WasHere` check, §1.1) and its IL
   rules need the vanilla `Celeste.Main`;
2. `Celeste.Android.mm.dll` (ours) — last, so our `Engine.RenderCore`, `ErrorLog.Open` and `SDLShim` are the final word;
3. **new** `Celeste.Android.Everest.mm.dll` — a separate project (`src/Celeste.Android.Patches.Everest/`) with the
   Android fixes for Everest's *own* merged types: `[MonoModPatch("Celeste.Mod.BOOT")]`,
   `[MonoModPatch("Celeste.Mod.Everest")]` (PathGame/PathTmp/PathEverest from `HostConfig`), CoreModule's Discord
   default, the updater, `CriticalErrorHandler`'s `Process.Start`, DebugRC.

A separate project (not `#if`s in the existing one) keeps `Celeste.Android.Patches` free of optional references: it
compiles identically when Everest isn't present, and the Everest fixes only exist in builds that have them.

`patch.stamp` must cover all three modules *and* the Everest on/off decision, so flipping the switch re-patches on the
device exactly like an APK update does today (`GameInstaller.IsPatchCurrent`).

### 4.3 The one ordering decision to make deliberately

Everest's `PatchCelesteMain` rule replaces the `call SDL2.SDL::SDL_GetPlatform` inside `Celeste.Main` with the literal
`"Windows"`; our `SDLShim` relinks that same call to report `"Android"`. With Everest first, Everest wins inside `Main`
(the call is already a constant by the time our relink runs) while the rest of the game reports `Android`. Whether that
matters depends on what vanilla `Main` does with the value (it is in the crash/`ErrorLog` path) — **read the vanilla IL
on the desktop host during Stage 2 and decide**; if the Windows branch does something Android can't (a message box,
`Process.Start`), our module should neutralise it explicitly rather than rely on ordering.

### 4.4 Installer, launcher, host contract

- `GameInstaller`: copy `assets/everest/*` into `files/Celeste/`, create `Mods/`, and add
  `ImportMod(Uri)` (a `.zip` via SAF → `files/Celeste/Mods/<name>.zip`, validating `everest.yaml`/`everest.yml`
  before committing, with the same staging + free-space check the game import uses), `DeleteMod`, and a mod list read
  from `everest.yaml` (name/version/author) for the UI.
- `LauncherActivity` / `LauncherPrefs`: an **Everest** switch (warns that turning it on re-patches, ~1-2 min) and a
  **Mods** screen: list, *Import mod (.zip)*, delete, enable/disable. Enable/disable should write
  `Mods/blacklist.txt` — Everest's own mechanism — so the launcher and the game always agree, including after an
  in-game change.
- `HostConfig` (`src/Shared/HostConfig.cs`): add `GameDir`, `ModsDir`, `EverestEnabled`. The patch modules read them
  the way they read `PrefPath` today.
- `CelesteLauncher.Run`: when Everest is on, publish the new keys, set `EVEREST_SAVEPATH` if §2 row 13 says we need
  it, load the patched dll, set `Engine.AssemblyDirectory` as today, then invoke the **assembly entry point**
  (`BOOT.Main`) rather than `Celeste.Main`, and treat the process exiting (`Environment.Exit(0)` at the end of
  `BOOT.Main`) as a normal end of session, not a crash.
- Backup: `Mods/` stays **out** of Auto Backup by construction — `Resources/xml/backup_rules.xml` (and
  `data_extraction_rules.xml`) include only `userdata/` and the preferences and exclude `Celeste/` outright. That is
  the right behaviour (a mod pack can be a gigabyte and mods are re-importable) as long as §2 row 13 holds and mod
  *settings* really do land in `userdata/`, which is backed up. Worth a line in the UI so nobody expects their mod
  list to survive a reinstall.
- Touch/UX: Everest's screens (Mod Options, the mod toggler, the version list, the critical-error screen) are
  `TextMenu`-based, so the pad's arrow keys + Z/X/C should navigate them — but that needs a device pass, and mods that
  draw their own HUD will draw *under* the pad (our `RenderCore` patch draws after `scene.Render()`), which is what we want.

## 5. Staged plan

| Stage | What | Where it's validated | Rough size |
|---|---|---|---|
| **0** | **Spike: do detours work here at all?** A throwaway test that (a) applies a `MonoMod.RuntimeDetour.Hook` and an `ILHook` to a method in a loaded-from-disk assembly, (b) creates a non-collectible *and* a collectible `AssemblyLoadContext`, loads a net4x dll coreified by `NETCoreifier`, calls into it, (c) creates a `FileSystemWatcher` on internal storage, (d) reports what `mprotect(RW→RX)` does. Plus `OperatingSystem.IsAndroid()` / `IsOSPlatform(Linux)` / `Assembly.Location` / `AppContext.BaseDirectory` values, logged. | **device only** | 1-2 days |
| **0b** | Desktop merge smoke test: stock Everest `Celeste.Mod.mm.dll` + our module in one MonoModder pass over `Celeste/Celeste.exe`, run through `Celeste.Desktop`. Tells us how much FNA drift hurts (§3 A) *without* a phone. | desktop host | 1 day |
| **1** | Host plumbing, no Everest binaries required: multi-module `CelestePatcher` + stamp, `Mods/` + `ImportMod`/`DeleteMod`, launcher Mods screen and Everest switch, `HostConfig` keys, `fetch-everest.ps1`, `BUILDING.md`/CI secret. Testable end-to-end with a *dummy* second mm module. | desktop host + device (UI) | 2-4 days |
| **2** | Everest Android build: the fork of §3 B, references retargeted at our FNA, SDL2→SDL3, Android guards; CI producing `everest-android-<ver>.zip`; MMHOOK generation path chosen and implemented. | fork CI + desktop host | 1-2 weeks |
| **3** | `Celeste.Android.Patches.Everest`: PathGame/entry point/Discord/updater/splash/ErrorLog/Process fixes; `CelesteLauncher` boots through `BOOT.Main`; saves verified against `userdata/` and the backup rules. | desktop host, then device | 3-5 days |
| **4** | First mods on a device: a map-only mod (no code) → a code mod → a big one (CollabUtils2 / Extended Variants class). Mod Options navigable with the pad, `log.txt` reachable, crash handler behaves, first-launch cost measured. | device | 1-2 weeks |
| **5** | Optional: native Lua for arm64 (Lua mods), a mod browser/downloader (GameBanana), an "Everest update" story that fits an APK (Everest version moves with the app, not in-game), `lib-android-arm64` for mod natives. | device | open-ended |

Stages 0 and 0b answer the two unknowns; everything after them is mechanical. If Stage 0 fails, the fallback is the
celeste-wasm route (an IL-level detour backend), which is a different and much larger project — better to know on day 2.

## 6. Risks and open questions, ranked

1. **Detours on ARM64 Mono/Android** — the whole idea rests on `MonoMod.RuntimeDetour` working in .NET for Android
   (JIT, no AOT, no trimming: our release build already runs that way). *Mitigation: Stage 0 before anything else.*
2. **`AssemblyLoadContext` behaviour on Mono/Android** — per-mod contexts, `GetLoadContext`, `Resolving`, and whether
   collectible contexts work at all (Everest unloads mods). *Mitigation: Stage 0.*
3. **FNA drift 21.03 → main** — surfaced as compile errors under strategy B, as runtime `MissingMethodException`
   under A. *Mitigation: Stage 0b to size it, B to fix it.*
4. **First-launch cost** — patch + MMHOOK + coreify on ARM64, over ~1.1 GB of content that stays untouched. The
   launcher already patches on device, so the ceiling is known-ish, but HookGen and per-mod coreifying are new.
   *Mitigation: measure in Stage 4; pre-generate MMHOOK if needed; show progress like the import does.*
5. **Mod compatibility ceiling** — native libs, Lua, Windows-only mods, mods that assume a keyboard/mouse. Publish a
   short "what works on Android" note rather than discovering it per bug report.
6. **Process semantics** — `Environment.Exit(0/-1)`, `EverestRestart`, `StartVanilla`: on Android these must all end
   up as "the `:game` process finished", never as a restart loop the launcher can't see.
7. **Storage** — `Mods/` + `Mods/Cache/` are unbounded; the free-space check the game import does has to cover mod
   imports too, and cache growth needs a cleanup story.
8. **Licensing/attribution** — Everest is MIT, so building and shipping it is fine; keep it out of git regardless
   (size + it moves fast), fetch by URL/secret like FMOD, and add it to `THIRD_PARTY_NOTICES.md`. Discord SDK,
   Steamworks and FMOD natives stay out of the Android build entirely.

## 7. Side findings (independent of Everest, worth fixing anyway)

- **Celeste 1.4.1.0 will break our SDL shim.** Everest's own rules say 1.4.1.0 "is supposed to ship with SDL3", and our
  `SDLShim` only relinks `SDL2.SDL::SDL_GetPlatform` / `SDL_GetPrefPath`
  (`src/Celeste.Android.Patches/Shims.cs`) — a 1.4.1.0 import would P/Invoke `SDL2` into an SDL3-only native library.
  Adding the `SDL3.SDL::…` `MonoModLinkFrom` twins is a few lines and needs no device to write.
- **`IsOSPlatform(OSPlatform.Linux)` is false on Android** (`OSPlatform` has no Android member; the runtime's platform
  name is `"ANDROID"`). Anything we or a mod writes that branches on Linux silently takes neither branch — worth a
  line in ARCHITECTURE.md's gotchas.
- **`CelesteLauncher` reflecting a *private* `Main`** is fragile for any future patch that changes its visibility;
  `Public | NonPublic` costs nothing.

## 8. References

- Everest `dev` @ `322fb9e19d89`: `MiniInstaller/{Program,DepCalls,Globals}.cs`,
  `Celeste.Mod.mm/Celeste.Mod.mm.csproj`, `Celeste.Mod.mm/MonoModRules{,.Game,.Mod,.Dependency,.Utils}.cs`,
  `Celeste.Mod.mm/Patches/{Celeste,UserIO}.cs`, `Celeste.Mod.mm/Patches/Monocle/{Engine,ErrorLog}.cs`,
  `Celeste.Mod.mm/Patches/FNA/*`, `Celeste.Mod.mm/Mod/Everest/{BOOT,Everest,Everest.Loader,Everest.Relinker,TextInput,Everest.DebugRC,Everest.DiscordSDK,EverestSplashHandler,Everest.Updater,ContentExtensions}.cs`,
  `Celeste.Mod.mm/Mod/Module/EverestModuleAssemblyContext.cs`, `Celeste.Mod.mm/Mod/Core/{CoreModule,CoreModuleSettings}.cs`
- Everest binaries: `EverestAPI/Everest-libs` @ `binaries` (FNA 21.3.5.0, per-platform natives, `piton*`)
- MonoMod: `MonoMod/MonoMod` @ `dfc30a15` (Everest's pin; `MonoModder.Mods`, `ReadMod`) and NuGet `MonoMod.Patcher 25.0.1` (ours)
- .NET runtime: `dotnet/runtime` `src/libraries/System.Private.CoreLib/src/System/OperatingSystem.cs`
  (`OSPlatformName` = `"ANDROID"`, `IsOSPlatform` string compare), `.../RuntimeInformation{,.Unix}.cs`
- This repository: `src/Celeste.Patcher/CelestePatcher.cs`, `src/Celeste.Android/{GameInstaller,CelesteLauncher,GameActivity,LauncherActivity,LauncherPrefs}.cs`,
  `src/Celeste.Android.Patches/{Shims.cs,Monocle/patch_Engine.cs,Monocle/patch_ErrorLog.cs,Touch/*}`,
  `src/Shared/HostConfig.cs`, `docs/ARCHITECTURE.md`, `docs/BUILDING.md`, `.github/workflows/build-apk.yml`
