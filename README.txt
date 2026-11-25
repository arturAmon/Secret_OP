Secret OP
=========

A small in-game overlay for League of Legends. While you play it shows:
  - how much more gold (spent on items) the enemy team has than yours
  - which lane is furthest ahead or behind, counting item gold and levels
  - what to buy next, plus reminders such as swapping to Farsight Alteration or Oracle Lens

The app window shows how far your team is ahead or behind in gold over the game
(above zero = ahead), with every item purchase and level-up underneath, and how you
compare with your lane opponent: item gold, CS per minute, KDA, level and vision.

The History tab keeps your last 10 games (played while the app was running): result,
KDA, CS, final items, your lane matchup, gold and lanes at the end, the scoreboard, the
gold graph, and your build next to what the overlay suggested at each purchase.
Each game also hands out a few lighthearted titles for what you did well, like
"CS Machine" or "Light in the Darkness", with a little score.

Rival mode: pick the friend you play with (click the star next to them in a game's
scoreboard, or enter their Riot ID in Settings). In every game you play together the app
compares your KDA, kill participation, CS, vision, gold and more, and keeps a running
head-to-head record. With an API key, gold earned, damage, turret plates and wards are added
from Riot's match history a few minutes after the game. "Copy image" puts a picture of the
game on your clipboard to paste into a chat; "Save image" saves it as a PNG.

With an API key, each game in History also gets a match report from Riot: damage for all
ten players, your damage types, damage taken, healing and shielding, crowd control,
laning (CS and gold at 10 and 15 minutes against your lane opponent, with a minute-by-minute
chart), objectives, multikills and more, plus extra titles like "Smite Thief" or "Guardian Angel".

History also fills itself with your most recent games from Riot's match history, including
games played without the app running ("Get my recent games from Riot" in the History tab).
Those show everything except the build comparison, since the app wasn't there to suggest
anything. The app learns your Riot ID from your first game with it running, or you can
enter it in Settings.


Getting started
---------------
1. Run SecretOP-Setup.exe. It installs for your Windows account only (no admin
   needed) and adds Secret OP to the Start menu. Windows may say "Windows protected
   your PC" because the installer isn't signed: click "More info", then "Run anyway".
   (Got the zip instead? Put SecretOP.exe anywhere and double-click it.)
2. In League, go to Settings > Video and set Window Mode to "Borderless" or "Windowed".
   The overlay can't show on top of full-screen mode.
3. Start a game. The overlay appears by itself once you're past the loading screen.

Ctrl+Shift+O shows / hides the overlay (change it in Settings). To move the overlay,
use Settings > Overlay look > Move overlay, drag it, then double-click it to lock it.
Closing the app window stops the app.

In Settings you can also make the overlay's background more see-through (or remove it),
or fade the whole overlay, so it covers less of the game.


Better item suggestions (optional, free)
----------------------------------------
Without this, suggestions are worked out from your champion's class and the enemy team.
With it, they follow what Challenger and Grandmaster players actually build.

1. Go to https://developer.riotgames.com and sign in with your Riot account.
2. Copy the "Development API Key" shown on that page.
3. In the app, click Settings, pick your region, paste the key, click "Test key", then Save.

The app then collects recent high-ranked games in the background. It pauses while you're
in a game, so it never uses your connection mid-match. A champion switches over to these
suggestions once it has enough games; the app window shows the progress.

Development keys stop working after 24 hours. When the app says the key was rejected,
get a new one from the same page and paste it in Settings. Games already collected
keep working without a key. Don't share your key: everyone should use their own.


Sharing builds
--------------
The installer comes with the popular builds collected by the friend who sent it, so the
better suggestions work straight away. To pass on builds later: Settings > Export builds,
send the file, and the other person uses Settings > Import builds. Games they already
have are skipped.


Your data
---------
Settings, your API key (encrypted for your Windows account) and collected builds are
stored in %LOCALAPPDATA%\LeagueOverlay. To remove the app, uninstall "Secret OP"
from Windows Settings > Apps; it asks whether to delete that folder too.

The app only reads game data that League itself provides on your PC
(https://127.0.0.1:2999) and public data from Riot. It doesn't change the game.


Secret OP isn't endorsed by Riot Games and doesn't reflect the views or opinions of
Riot Games or anyone officially involved in producing or managing Riot Games properties.
Riot Games and all associated properties are trademarks or registered trademarks of
Riot Games, Inc.
