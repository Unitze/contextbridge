# Presets

Shared command definitions that ContextBridge can import. In the settings app, click **Import…** and choose a `*.contextbridge.json` file; you review every command before anything is imported.

> **Note**: These presets are community contributions and come with no safety guarantee. Always check the programs and arguments in the import confirmation dialog.

| File | What it does |
| --- | --- |
| [editors/notepad.contextbridge.json](editors/notepad.contextbridge.json) | Open in Notepad |
| [editors/vscode.contextbridge.json](editors/vscode.contextbridge.json) | Open in VS Code (user install) |
| [developer/git-bash.contextbridge.json](developer/git-bash.contextbridge.json) | Open Git Bash / Git GUI here |
| [developer/7zip-filemanager.contextbridge.json](developer/7zip-filemanager.contextbridge.json) | Open archives in 7-Zip File Manager |

## Contributing (pull requests)

1. Create the command in the settings app and check that it works
2. Export it with **Export…** (keep "Replace user-specific folders with environment variables" turned on)
3. Add a `description` to the exported file: what the command does, which app it targets and where that app is expected to be installed
4. Put the file in a category folder (`editors/`, `developer/`, `media/`, `utilities/`, …), add it to the table above and open a pull request

## Review rules

A preset is accepted only if it meets all of the following. Some of these are also checked automatically in CI (`tests/ContextBridge.Core.Tests/SharingTests.cs`).

- It launches **the officially distributed application itself** (available from the official website, an official store or an official package manager)
- It **does not download anything or run scripts**. Definitions that go through `cmd.exe`, `powershell.exe`, `wscript.exe`, `mshta.exe` and the like, or whose arguments contain URLs or script files, are not accepted
- `runAsAdmin: true` and `window: "hidden"` only with an explanation of why they are needed
- Program paths contain no user names (use environment variables such as `%LOCALAPPDATA%`)
- The pull request **reports how it was tested** (Windows version, app version, what you tried)
- It has a `description`

The reviewer setup and the list of categories will evolve as the project grows.
