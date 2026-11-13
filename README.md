# IndustryPlanner

A node-based production planner for **Satisfactory**, built to plan factories the way you think about them: machines as nodes, belts as links, and live numbers on everything.

> Early version (v0.0.1). Satisfactory 1.2 data. Other games are planned.

<!-- Add a screenshot or GIF of the editor here: ![Editor](docs/editor.png) -->

## Download

Grab the installer from the [latest release](../../releases/latest) and run it (Windows, 64-bit).

Windows may show "Windows protected your PC" because the installer isn't signed yet. Click **More info › Run anyway**.

## What it does

**Plan with nodes**
- Every machine, recipe and alternate recipe from Satisfactory 1.0: 152 items and 276 recipes
- A node is a group of machines: set the count, the clock speed (50–250%, with the power shards it needs) and Somersloops
- Miners with node purity, water and oil extractors, resource wells, and every generator with its fuels
- Splitters (1 in, 3 out, even split with overflow) and mergers (3 in, 1 out) that take whatever you connect
- Belts and pipes have tiers: links are capped at Belt Mk.1–6 or Pipe Mk.1–2 speeds, and overloaded links turn red

**See what's going on**
- Live flow: starved machines show how fed they are, and arrows move along links towards the receiving node
- Analysis panel: power used and produced, raw inputs, outputs, and the balance of every item
- "Machines needed for the input rate": how many machines the incoming supply can actually run, with one click to apply it

**Work fast**
- A Shapez 2-style hotbar: number keys pick a category, press again to cycle buildings
- Double-click anywhere to search every recipe, or drag a pin onto empty space to add a building that uses (or makes) that item
- Box select, copy and paste, duplicate, snap to grid, align to other nodes
- Blueprints: save a group of nodes and reuse it in any production
- Bigger picture: use a whole production line as a single node in an overview

**Stay honest to your save**
- Each production remembers your tier (0–9) and whether alternates are unlocked; anything you haven't unlocked is hidden

**Make it yours**
- Key bindings per game, link style (curved or right angles), snap and animation settings

## Default controls

| Action | Key |
|---|---|
| Pick a building category | 1–9, 0 (press again to cycle) |
| Add any recipe | Double-click empty space |
| Pan / box select / zoom | Left drag / right drag / wheel |
| Copy / paste / duplicate | Ctrl+C / Ctrl+V / Ctrl+D |
| Delete selection or link | Del |
| Save | Ctrl+S |
| Frame all | F |
| Side panel / analysis / blueprints | Tab / A / B |
| Snap to grid / hold to align | G / Left Ctrl while dragging |

All of these can be changed in **Settings**.

## Build from source

1. Install **Unity 6000.6.4f1** (Unity 6.6) with Windows Build Support.
2. Open the `My project` folder in Unity and load `Assets/_IndustryPlanner/Scenes/MainScene`.
3. Optional: **IndustryPlanner › Download Satisfactory icons** fetches item and building images from the Satisfactory Wiki. Without them the app shows coloured placeholder tiles.
4. To regenerate the game data: `python Tools/convert_satisfactory.py data1.0.json Assets/Resources/Games/satisfactory.json`.
5. The installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) from `Installer/IndustryPlanner.iss`.

## Roadmap

- Undo / redo
- Smart and programmable splitters (mixed belts)
- Folders, favourites and previews for productions
- More games

## Credits

- Game data from [SatisfactoryTools](https://github.com/greeny/SatisfactoryTools) (MIT).
- Rajdhani font by Indian Type Foundry (SIL Open Font License).
- Satisfactory and its artwork are © Coffee Stain Studios. IndustryPlanner is an unofficial fan tool, not affiliated with or endorsed by Coffee Stain Studios.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
