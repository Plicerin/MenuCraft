# MenuCraft

A small Windows 11 utility for cleaning up right-click menus. Preview your desktop's **Show more options** menu, or open **Compact menu** to hide supported app integrations. Changes have an undo record.

[Download the latest Windows x64 release](https://github.com/Plicerin/MenuCraft/releases/latest) · [Report an issue](https://github.com/Plicerin/MenuCraft/issues)

## Getting started

1. Download `MenuCraft-v0.2.0-win-x64.zip` from Releases.
2. Extract the entire ZIP to a folder, then open `MenuCraft.exe` inside that folder. Keep the accompanying files together.
3. Select a command in the desktop preview and choose **Remove from menu**.
4. For eligible system-wide commands, choose **Restart as administrator**, accept the Windows prompt, then review and confirm removal.
5. To clean up app entries in the first Windows 11 menu, choose **Compact menu**, select an app handler, and choose **Hide**. Choose **Restore** to bring it back. These changes apply to the current user without administrator access.

The release bundles .NET and the Windows App SDK runtime. No installer or separate runtime installation is needed. This release is unsigned; Windows may show a publisher warning. Supported release platform: Windows 11 x64.

No menu entries change merely by opening the app. **Undo last change** restores the previous registration values.

## What it can do

- Preview the desktop's actual classic menu labels, separators, and order.
- Hide supported static commands, including eligible Git and other application registrations.
- Read the normal Explorer menu even when the editor runs as administrator.
- Preserve a durable undo history and refuse to overwrite conflicting newer changes.
- Inspect common file, folder, background, and drive registrations in **Advanced entries**.
- Start with commands that can be hidden or restored, including an administrator restart action for supported system-wide commands. Unsupported commands are excluded by default.
- Add and edit custom commands owned by MenuCraft for the current user.
- Hide and restore supported compact-menu app handlers, grouping commands that share a handler.

## Current limits

- The desktop preview shows **Show more options**. The compact view lists installed package registrations; it is not an exact preview of the menu for the current desktop or selected file.
- Compact changes block an entire handler for the current user, across all its commands and file types. A shared handler may also affect classic menu entries or more than one app version.
- Core Windows actions, system-package handlers, ambiguous classic matches, and dynamic classic handlers remain protected. Existing blocks created by other tools or an administrator are read-only.
- Dynamic classic extensions have no editable category. **Edit my command** appears only for your own MenuCraft commands; third-party commands offer Hide or Restore where supported.
- Desktop preview shows top-level entries; it does not expand or edit submenus such as **New**.
- Commands registered for folder backgrounds can affect both folder and desktop menus.
- Explorer can cache a removed entry. Reopen its menu and refresh the preview; signing out and back in may be necessary. Application updates can recreate registrations.
- The elevated editor and Explorer must run as the same Windows account. Elevating with another account's credentials is not supported.

Classic removal uses `LegacyDisable`; compact removal creates a CLSID value in the current user's `Shell Extensions\Blocked` key. Restore only removes compact blocks owned by MenuCraft. Neither method uninstalls the owning application or deletes its command. MenuCraft matches classic canonical verbs rather than guessing from similar labels. Compact-menu behavior depends on Windows and the app's integration; built-in items are not universally editable.

## Recovery

Undo records live in `%LOCALAPPDATA%\MenuCraft\History`. Keep that folder. **Undo last change** works newest first, including interrupted operations, and preserves unrelated values. If another program changes a recorded value, undo stops instead of overwriting that change.

If an edit reports an interrupted write, use Undo before making further changes. The history JSON files are recovery records, not `.reg` imports.

## Build from source

Use Windows 11 with the .NET 8 SDK and a working WinUI build toolchain. The script restores NuGet dependencies and uses temporary build intermediates.

```powershell
.\MenuCraft\Build.ps1 -Configuration Release -Publish -OutputDirectory .\artifacts\MenuCraft-win-x64 -Run
```

This publishes a portable build for the current machine architecture. The downloadable release is tested on x64. ARM64 and x86 are not validated release targets.

## Tests

Build first, then point the integration tests at that executable:

```powershell
$env:MENUCRAFT_TEST_EXE = (Resolve-Path .\artifacts\MenuCraft-win-x64\MenuCraft.exe).Path
dotnet test .\MenuCraft.Tests\MenuCraft.Tests.csproj -c Debug -p:Platform=x64
```

The full suite needs an interactive Windows desktop with Explorer running. Its shell integration test temporarily registers one uniquely named classic command, verifies hide and undo, then removes the fixture. A compact test writes and restores a random unused handler CLSID. Startup tests open and close both editor views. Existing app handlers are not edited by the tests.

Validation covers registry recovery, conflict detection, canonical matching, reader permissions, empty-menu errors, Explorer enumeration, compact-handler grouping and protections, and startup. The administrator reader has also been checked manually. Visual verification of actual compact-menu removal across app integrations, Narrator, and high-contrast acceptance testing remain outstanding.

## License

MenuCraft source is licensed under [MIT](LICENSE). Bundled runtime components retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md) and the `licenses` folder.
