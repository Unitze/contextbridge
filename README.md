# ContextBridge

Add **your own programs** to the new Windows 11 context menu — the compact menu you see first when you right-click. Apps that don't support the new menu become reachable without clicking "Show more options".

```
Right-click
├─ Open
├─ ...
└─ ContextBridge ▸ ├─ Open Git Bash here
                   ├─ ───────────────
                   ├─ Editors            (group heading)
                   ├─ Open in VS Code
                   └─ Open in Notepad
```

- Show a command only for specific targets (files, folders, folder background, drives), file extensions and selection counts
- Pass all selected files to one launch, or launch once per file
- **Migrate** items from the classic context menu straight from the registry — no retyping
- **Share** command definitions as files; importing always shows exactly what will run first
- Settings app in English and Japanese (follows the Windows display language by default; switchable on the Settings tab)
- No network access, ever (not even update checks)

## Requirements

- Windows 11 21H2 (build 22000) or later, x64 or ARM64
- Administrator rights during installation (see below)

## Installation

1. Download `ContextBridge-<version>-x64-setup.exe` (`arm64` for ARM64 PCs) from [Releases](../../releases) and run it.
2. If **SmartScreen** warns you, choose "More info" → "Run anyway". The releases are not code-signed, so this warning is expected. You can verify the download against `SHA256SUMS.txt` on the release page.
3. The installer runs **as administrator**. Windows only allows unsigned packages — which are required to place items in the new menu — to be registered from an elevated session (registering as a standard user fails with error 0x80073D2B).

> If you use a standard account and elevate with a *different* administrator account's credentials, Windows registers the unsigned package for **that administrator account**, and the menu does not appear for your standard account. Either grant your account administrator rights temporarily while registering, or build and sign the package yourself.

Without the installer, extract the `portable.zip` anywhere and run this in an elevated PowerShell:

```powershell
.\register.ps1 -Action Register
```

## Usage

Open **ContextBridge** from the Start menu.

1. Click **Add** and choose a program (.exe, etc.)
2. Set the title, group, when to show, and arguments — the preview shows exactly what will be launched
3. **Save** (Ctrl+S). Changes apply the next time you right-click in Explorer

More things you can do:

- **Try it…** launches the command against files or folders you pick, exactly as Explorer would. If the command would not appear for that selection, it tells you why
- Drop a program (.exe or a .lnk shortcut) or a share file (.json) onto the window to add or import it
- Groups split the ContextBridge menu into titled sections (Windows 11 does not open submenus nested inside another submenu, so a group is shown as a heading followed by its commands)
- Delete a group on the Groups tab (or press Delete), or with the delete button next to the group box in the command editor. You choose whether its commands move to the top level or are deleted too
- The ▾ next to **Save** offers **Save and restart Explorer** (Ctrl+Shift+S). With **Always restart Explorer when saving** turned on, every save restarts Explorer. This is rarely needed: changes normally apply on the next right-click
- When Explorer restarts, the folder windows that were open are reopened at their previous position (turn this off on the Settings tab). Separate tabs come back as separate windows
- In the command list: Ctrl+F filters (Esc clears), drag or Alt+↑/↓ reorders, Delete removes, Ctrl+D duplicates. The same actions are on the right-click menu. Commands with problems are marked with ⚠; click an entry in the **Issues** panel to jump to the command
- Window position and size, the list width and the last open tab are restored on the next start
- If the title is left blank, the menu shows "Open in &lt;program&gt;" ("&lt;program&gt; で開く" on Japanese Windows)

### Argument placeholders

| Placeholder | Meaning |
| --- | --- |
| `%1` | The first selected path (the folder itself for a folder background). Write `"%1"` so paths with spaces work |
| `%*` | All selected paths. **Each path is quoted automatically** (writing `"%*"` does not double the quotes) |
| `%d` | The target folder: the parent of the selection, the folder itself for a background, the drive for a drive |
| `%%` | A literal `%` |
| `%LOCALAPPDATA%` etc. | Environment variables. They also work in the program path |

Examples: `-d "%d"` (Windows Terminal), `--cd="%1"` (Git Bash), `%*` (pass several files to one launch)

### Migrating from the classic context menu

**Migrate legacy menu…** lists the classic menu items registered in the registry (`*\shell`, `Directory\shell`, `Directory\Background\shell`, `Drive\shell`, per-extension `shell` keys, and so on). Checked items are imported as regular commands.

- Items implemented by COM handlers (such as 7-Zip's menu), cascading submenus and items shown only with Shift cannot be migrated
- Windows' own items and items whose app already supports the new menu start unchecked
- Opening the dialog again shows what changed: already imported, changed, or gone from the registry

### Sharing and presets

- **Export…** saves the chosen commands to a `*.contextbridge.json` file. User-specific paths such as `C:\Users\<name>\AppData\Local\...` are replaced with `%LOCALAPPDATA%\...`
- **Import…** reads a share file. **Every command's program and arguments are shown before anything is imported**, and nothing is imported until you confirm. Programs missing on this PC, script execution, URLs and run-as-administrator are flagged
- The [`presets/`](presets/) folder holds community presets ([acceptance rules](presets/README.md)). **They come with no safety guarantee** — always review them in the confirmation dialog

## Troubleshooting

| Symptom | What to do |
| --- | --- |
| "ContextBridge" is missing from the menu | The registration status is shown at the top of the settings app and on the Settings tab. Click **Register**. If it still doesn't show, try **Restart Explorer now** on the Settings tab, or sign out and back in |
| One command doesn't show | Check its conditions (target types, extensions, selection count). If even one selected item doesn't match, the command is hidden |
| All commands disappeared | If the settings file is corrupt, every item is hidden for safety. Open the settings app to restore from the backup |
| Nothing starts | Turn on the activity log on the Settings tab and check `%LOCALAPPDATA%\ContextBridge\logs\shell.log` |

## Uninstall

Uninstall ContextBridge from Settings → Apps → Installed apps. The package registration is removed as well, and you can choose whether to keep your command settings.

## Files

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\ContextBridge\commands.json` | Command definitions |
| `%LOCALAPPDATA%\ContextBridge\commands.json.bak` | The previously saved version |
| `%LOCALAPPDATA%\ContextBridge\icons\` | Icons converted from images |
| `%LOCALAPPDATA%\ContextBridge\logs\shell.log` | Activity log (only when enabled) |
| `%LOCALAPPDATA%\ContextBridge\app-settings.json` | Settings of the settings app only (language, window position, …; never shared) |

## Known limitations

- Windows 11 shows the items inside one "ContextBridge" submenu (the name can be changed); they cannot be placed individually at the top level
- Windows 11 only opens one level of submenu, so groups are shown as titled sections inside the ContextBridge submenu rather than as nested submenus
- Items don't appear in special locations such as the desktop background, libraries or the Recycle Bin (only items with a file-system path are supported)
- Titles cannot change dynamically the way some classic menu handlers do

## Development

```
src/Shell/               Handler DLL (C++, runs inside Explorer; core/ is OS-independent and unit-tested)
src/ContextBridge.Core/  Settings model, validation, legacy migration, sharing (.NET, OS-independent)
src/ContextBridge.App/   Settings app (WPF). Strings: Resources/Strings.resx (English, default) and Strings.ja.resx
package/                 Sparse package manifest and logos
installer/               NSIS installer script and registration script
presets/                 Shared presets
tests/                   Native and .NET tests, plus test vectors shared by both implementations
```

### Building

Every release artifact (DLL, settings app, package, installer, zips) can be built on Linux (x86_64, .NET 10 SDK, Python 3). A script fetches the cross compiler ([llvm-mingw](https://github.com/mstorsjo/llvm-mingw)) and NSIS without root:

```bash
eval "$(./scripts/bootstrap-tools.sh)"   # fetches llvm-mingw / NSIS (and cmake/ninja if needed) into ~/.cache
./scripts/build.sh                       # tests → x64 / arm64 builds → dist/
```

On Windows (Visual Studio 2022 or later with the C++ workload, CMake, .NET 10 SDK, Python 3; NSIS optional):

```powershell
./scripts/build.ps1
```

Tests only:

```bash
cmake -S . -B build/tests && cmake --build build/tests --target cb_core_tests && ctest --test-dir build/tests
dotnet test tests/ContextBridge.Core.Tests
```

CI (GitHub Actions, GitHub-hosted runners) runs the tests and builds every artifact on Linux, then checks the shipped handler DLL through COM and tries registering the package on Windows. Pushing a `v*` tag publishes the artifacts to Releases. Checking the actual menu in Explorer is still done by hand.

### Translations

UI strings live in `src/ContextBridge.App/Resources/` and `src/ContextBridge.Core/Resources/`. To add a language, add `Strings.<lang>.resx` next to `Strings.resx` in both folders and register the language in `src/ContextBridge.App/Services/UiLanguage.cs`. The tests check that every translation has the same keys and placeholders as the English file.

## License

[MIT](LICENSE)
