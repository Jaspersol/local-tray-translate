<div align="center">

<img src="docs/logo.png" width="120" alt="TranslateLite logo">

# TranslateLite

### A lightweight Windows dictionary that answers offline

**Press a hotkey to look up the word under your cursor, or drag a box over a screen region and let Windows OCR recognise the text. Everything comes from a local SQLite dictionary; no network request, no account.**

[![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-8-512BD4.svg)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/UI-WPF-0078D4.svg)](https://learn.microsoft.com/dotnet/desktop/wpf/)
[![SQLite](https://img.shields.io/badge/search-SQLite%20FTS5-003B57.svg)](https://www.sqlite.org/fts5.html)
[![License](https://img.shields.io/badge/license-not%20set-lightgrey.svg)](#license)

English | [中文](README_ZH.md)

**[Quick Start](#quick-start) · [Hotkeys](#hotkeys) · [Features](#features) · [Dictionary Data](#dictionary-data) · [FAQ](#faq)**

</div>

## Why TranslateLite?

Most dictionary tools grow into a browser tab, a subscription, or a 2 GB download that loads the whole word list into memory. TranslateLite answers lookups from a local SQLite file and stays in the tray at a few dozen megabytes.

- **Local only**: a single `dictionary.db` (ECDICT + CC-CEDICT) queried through FTS5; no server
- **Two hotkeys, two workflows**: `Ctrl+Alt+X` takes the current selection and searches it, while `Ctrl+Alt+S` opens a capture overlay, OCRs the region, and searches the recognised text
- **Tiered lookup instead of a single `LIKE`**: exact match, then user entries, then tokenised candidates, then FTS5 prefix search, then edit distance, so `helo` still finds `halo`
- **A separate user dictionary**: your own entries live in `user_dictionary.db`, so replacing the main dictionary never touches them
- **OCR, not machine translation**: screen capture reads the characters and looks *those* up, which suits reading rather than translating full sentences

## Requirements

- **Windows 10 build 19041 (20H1) or later**: the app targets `net8.0-windows10.0.19041.0`
- **.NET 8 SDK** with the Windows Desktop workload, to build from source
- **Windows OCR language packs**: install the Chinese and English OCR features in Windows Settings for the best recognition

## Quick Start

```powershell
dotnet restore .\TranslateLite.sln
dotnet build .\TranslateLite.sln
dotnet run --project .\src\TranslateLite\TranslateLite.csproj
```

The app starts hidden with a tray icon; a query window opens from the tray menu.

## Hotkeys

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+X` | Take the current selection (via clipboard) and look it up |
| `Ctrl+Alt+S` | Drag a box on screen, OCR it, and look the text up |

Both are configurable from the tray icon → 设置. A global hotkey must include Ctrl, Alt, Shift or Win, and the two actions cannot share the same combination. If Windows refuses a combination (a screenshot tool or an IME already owns it), the tray menu marks it as 未生效 and a balloon explains which one failed; the app itself keeps running.

## Features

### Lookup

- **Tiered fallback**, each tier only filling the results still missing:
  1. exact match on the main dictionary (`normalized_word` index)
  2. exact match plus translation/example substring match on the user dictionary
  3. tokenised candidates, matched exactly one by one (Latin split on spaces and punctuation, Chinese with a 2–4 character sliding window)
  4. FTS5 prefix search over the full text, ordered by `rank`
  5. edit-distance fallback (OSA distance; budget 1 for ≤4 characters, 2 for ≤8, 3 above that), which is what makes `helo`→`halo` and `wrold`→`world` work
- **User entries win**: a word you saved yourself is returned before the main dictionary's entry for the same word
- **Reused result popup**: one always-on-top window, kept out of the taskbar and shown again for each lookup; closing it hides the window for later reuse

### Capture and OCR

- **Frozen-screen overlay**: capture mode snapshots the whole virtual screen on entry and shows it in an opaque, non-layered window; drag a box to pick the region and everything outside it dims
- **Debounced capture**: while a capture is in progress, further hotkey presses and tray clicks are ignored, so repeated presses cannot stack overlays
- **Frame-rate drag updates**: mouse moves are coalesced into one selection update per rendered frame, and the crop comes from the frozen frame with no fixed wait
- **DPI-correct crop**: the selection uses Win32 physical screen coordinates, so display scaling does not shift or shrink the captured region
- **In-memory OCR**: the crop is decoded through an in-memory stream with a reused `OcrEngine`, so no temporary PNG is written to disk
- **Windows built-in OCR**: `Windows.Media.Ocr`, preferring the OCR languages you have installed
- **Clipboard-safe selection capture**: `Ctrl+C` is sent, the selection read, and the previous clipboard content restored afterwards

### Tray and settings

- **Tray resident**: closing the query window hides it; the tray menu has 打开 / 划词翻译 (with its hotkey) / 截图翻译 (with its hotkey) / 设置 / 退出
- **Settings in `settings.json`**: both hotkeys (modifiers + virtual-key code) and the auto-start flag are persisted as JSON; a corrupt file falls back to defaults rather than blocking startup
- **Auto-start**: enabled from the 常规 section of the settings window via the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry key; no admin rights needed, and the path is refreshed on the next start if you move the app folder

### Checking for updates

- **Two check paths** — check manually from 关于与更新 in the settings window, or enable the startup auto-check (at most once every 6 hours; it only notifies, never downloads). The primary path calls the GitHub REST API `repos/Jaspersol/local-tray-translate/releases/latest` for the version, release notes, asset and checksum. When the API is rate-limited (403) or unreachable it switches to a direct-link fallback that never touches `api.github.com`: the version tag comes from the `releases/latest` redirect and the asset name plus SHA256 from `SHA256SUMS.txt`.
- **Two download paths** — installation tries the asset's `browser_download_url` first, then the API asset URL, and finally the `releases/latest/download/<name>` direct link; each candidate is retried once.
- **Mandatory verification** — the SHA256 comes from the API asset `digest`, `<zip>.sha256`, or `SHA256SUMS.txt`; a missing or mismatching hash aborts the install. Downloads also enforce an HTTPS + GitHub host allowlist (checked on every redirect hop), a size cap and a zip magic check.
- **Install** — the portable build has no installer: the verified package is extracted beside the app folder into `.new`, and after the app exits a script atomically swaps the directory and restarts it. User data stays in `%LOCALAPPDATA%\TranslateLite`.

### User dictionary

- **Separate file**: `%LOCALAPPDATA%\TranslateLite\user_dictionary.db` next to the main dictionary, so upgrading the dictionary never overwrites user data
- **Manager window**: add, edit (double-click a row), delete, filter by word/translation/tag, and bulk import
- **Bulk import format**: one entry per line, lines starting with `#` ignored; tab-separated (`.tsv`/`.txt`) or comma-separated CSV with optional quoted fields:

```text
word<TAB>translation[<TAB>definition[<TAB>example]]
```

## Dictionary Data

On first run the app creates `%LOCALAPPDATA%\TranslateLite\dictionary.db` and inserts a few sample rows so the UI can be exercised. Real data goes into the `entries` table defined in `data/schema.sql`:

```sql
INSERT INTO entries(word, normalized_word, lang, phonetic, definition, translation, example, source)
VALUES ('example', 'example', 'en', 'ig-zam-puhl', 'a representative item', '示例翻译', 'This is an example.', 'ecdict');
```

Recommended sources:

- **ECDICT** for English → Chinese entries
- **CC-CEDICT** for Chinese → English entries

Import offline and query on demand: loading a whole dictionary into memory would defeat the point of a tray-resident tool.

### Importing ECDICT and CC-CEDICT

The repository ships two import tools:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\download_dictionaries.ps1
python .\tools\import_dictionaries.py --ecdict .\data\raw\ecdict.csv --cedict .\data\raw\cedict_ts.u8 --output .\data\dictionary.db
python .\tools\verify_dictionary.py .\data\dictionary.db
```

`download_dictionaries.ps1` fetches ECDICT's CSV and CC-CEDICT's gzipped text into `data/raw/` and decompresses the latter. `import_dictionaries.py` rebuilds `entries` and `entries_fts`. `verify_dictionary.py` prints the row count and a few sample lookups, which is the fastest way to tell a broken import from a broken query.

The generated `data/dictionary.db` is copied into the build output, and on first launch the app copies it to `%LOCALAPPDATA%\TranslateLite\dictionary.db`.

## Build and Publish

```powershell
# Option 1: the publish script
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\TranslateLite\publish.ps1

# Option 2: dotnet publish directly
dotnet publish .\src\TranslateLite\TranslateLite.csproj -c Release -p:PublishProfile=FolderProfile -o .\publish
```

The result is a self-contained folder with `TranslateLite.exe` and `dictionary.db`. `publish.ps1` also packs it into `LocalTrayTranslate-<version>-win-x64.zip` and writes `<zip>.sha256` plus `SHA256SUMS.txt`; upload all three files to the Release, because both the API path and the direct-link fallback depend on the checksums.

## Project Structure

```text
src/TranslateLite
|-- App.xaml(.cs)                          Startup and service wiring
|-- Interop/HotkeyService.cs               Win32 global hotkeys
|-- Models/DictionaryEntry.cs              Lookup result model
|-- Models/AppSettings.cs                  Settings model
|-- Services/DictionaryService.cs          SQLite and FTS queries
|-- Services/OcrService.cs                 Windows OCR adapter
|-- Services/TrayService.cs                Tray icon and menu
|-- Services/ClipboardSelectionService.cs  Selection capture via the clipboard
|-- Services/SettingsService.cs            settings.json persistence
|-- Services/AutoStartService.cs           Auto-start registry entry
|-- Models/UpdateInfo.cs               Update result and download progress models
|-- Services/UpdateService.cs          Update check/download/install (API + direct-link paths)
`-- Windows/                               WPF windows: main, popup, capture, settings, user dictionary
```

## Tech Stack

C# WPF on `net8.0-windows10.0.19041.0` · `Microsoft.Data.Sqlite` with SQLite FTS5 · Windows built-in OCR (`Windows.Media.Ocr`) · Win32 `RegisterHotKey` · WinForms `NotifyIcon` for the tray

## FAQ

<details>
<summary><strong>Is screen capture translating sentences?</strong></summary>

No. It is OCR plus dictionary lookup: the recognised text is looked up as words. Full-sentence machine translation would need a model the app deliberately doesn't ship.

</details>

<details>
<summary><strong>The screen hotkey finds nothing on a web page or an image.</strong></summary>

Windows OCR only reads text the way the OS sees it; heavily styled web text, dark themes, and small fonts often fall below its confidence. Zoom in or capture a tighter box. Also confirm the Chinese and English OCR language packs are installed.

</details>

<details>
<summary><strong>My clipboard content changed after using the selection hotkey.</strong></summary>

`Ctrl+Alt+X` sends `Ctrl+C` and then restores the previous clipboard content on a best-effort basis. Applications that reject or delay the copy can leave the clipboard holding the selection.

</details>

<details>
<summary><strong>Where is my data?</strong></summary>

`%LOCALAPPDATA%\TranslateLite\` holds `dictionary.db`, `user_dictionary.db`, and `settings.json`. Deleting `dictionary.db` makes the app recreate it from the copy in its install folder on the next start.

</details>

<details>
<summary><strong>Can I replace the main dictionary while keeping my own entries?</strong></summary>

Yes. User entries live in `user_dictionary.db`, a separate file, so swapping `dictionary.db` leaves them untouched.

</details>

<details>
<summary><strong>A hotkey does nothing at all.</strong></summary>

Another application already registered the combination (screenshot tools and IMEs are the usual suspects). The tray menu then shows the shortcut as 未生效 and a balloon names it; the app still starts, so pick a different combination in 设置.

</details>

<details>
<summary><strong>The box and the captured region do not line up on my multi-monitor setup.</strong></summary>

The crop uses Win32 physical pixels. On monitors with different scaling factors, a single full-screen WPF window cannot map DIPs to pixels linearly, so the box can drift from the crop on the secondary monitor. Monitors sharing one scaling factor are unaffected.

</details>

## License

No license file is included in this repository yet; copyright belongs to the respective authors, all rights reserved. Open an issue if you need explicit permission to reuse it.
