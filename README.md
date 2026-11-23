# League Overlay

A small Windows companion app for League of Legends: an in-game overlay plus a window with your game history.

**[⬇ Download the latest installer](../../releases/latest)**: run `LeagueOverlay-Setup-vX.Y.Z.exe`. It installs for your Windows account only, so no admin rights are needed.

## What it shows

**In game (overlay)**
- How far your team is ahead or behind in gold spent on items
- Which lane is furthest ahead or behind (item gold + levels)
- What to buy next, with item icons, and reminders like swapping to Farsight Alteration or Oracle Lens
- Adjustable background and overall opacity, and movable with a hotkey

**App window**
- Gold difference over the game, with every purchase and level-up for all ten players
- You vs your lane opponent: item gold, CS per minute, KDA, level and vision
- Rival mode: pick a friend, then compare stats in every game you play together, with a running head-to-head record
- History of your last 10 games, including games played without the app (from Riot's match history), with:
  - a match report: damage, laning at 10 and 15 minutes, objectives and highlights
  - your build next to what the overlay suggested
  - lighthearted titles for what you did well
- A shareable image of any game, to paste into Discord

## Optional: better item suggestions

With a free Riot API key ([developer.riotgames.com](https://developer.riotgames.com)), item suggestions follow what Challenger and Grandmaster players build. History also gets match reports and your recent games. Paste the key in **Settings**. Development keys expire after 24 hours, so paste a fresh one when the app says so. Everyone should use their own key.

The installer comes with a set of popular builds already collected. You can share yours with **Settings → Export builds / Import builds**.

## Requirements

- Windows 10 or 11 (64-bit)
- League in **Borderless** or **Windowed** mode (the overlay can't draw over exclusive fullscreen)

Windows may warn that the installer is from an unknown publisher (it isn't code-signed). Click **More info → Run anyway**.

## Building from source

Needs the .NET 10 SDK. Building the installer also needs Inno Setup 6.

```powershell
dotnet test LeagueOverlay.Tests --filter "Category!=RealData"
powershell -ExecutionPolicy Bypass -File .\publish.ps1   # installer + portable zip in .\dist
```

| Project | Contents |
|---|---|
| `LeagueOverlay.Core` | live game data, analysis, recommendations, Riot API, history |
| `LeagueOverlay` | the WPF app (overlay, app window, settings, share image) |
| `LeagueOverlay.Tests` | xUnit tests |
| `installer` | the Inno Setup script |

## Privacy

The app reads the game data League provides on your own PC (`https://127.0.0.1:2999`) and public data from Riot. Your API key (encrypted for your Windows account), settings, saved games and collected builds stay in `%LOCALAPPDATA%\LeagueOverlay` on your PC.

---

League Overlay isn't endorsed by Riot Games and doesn't reflect the views or opinions of Riot Games or anyone officially involved in producing or managing Riot Games properties. Riot Games and all associated properties are trademarks or registered trademarks of Riot Games, Inc.
