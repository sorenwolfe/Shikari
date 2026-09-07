<p align="center">
  <img src="images/icon.png" alt="Shikari icon" width="144" height="144">
</p>

<h1 align="center">Shikari</h1>
<p align="center">Plan the pull. Find your spot. Review what happened.</p>

Shikari is a raid-planning plugin for Final Fantasy XIV. Make your own strategy or import one, keep your next position visible during the fight, and look back at your movement after a pull.

## Install

With XIVLauncher and Dalamud set up:

1. Open `/xlplugins`, then go to **Settings → Experimental → Custom Plugin Repositories**.
2. Add the repository below and save your settings.
3. Search for **Shikari** in the plugin installer and install it.

```text
https://raw.githubusercontent.com/sorenwolfe/Shikari/main/repo.json
```

Type `/shikari` to get started. Future updates appear in the plugin installer.

## Your first plan

1. Open **Plan**. Use **Plans → New plan…** to start from scratch, or bring in a plan using the options below.
2. Open **Roster** and choose your seat. This tells Shikari which planned position belongs to you.
3. In **Slides**, draw a mechanic with player tokens, waymarks, AOEs, and notes. Each slide is a moment in the fight.
4. Use **Timeline** to link mechanics to slides and add cooldown reminders. Enable **Follow** when you're ready for the slides to advance with the fight.
5. Open `/shikari mini` to keep the current slide beside your game.

You can also build slides from observed mechanics in **Learned** after a few pulls. If you're drawing by hand, scroll over the board to zoom and hold the middle mouse button to pan.

## Commands

| Command | What it does |
| --- | --- |
| `/shikari` | Open or close the main window. |
| `/shikari config` | Open or close settings. |
| `/shikari mini` | Show or hide the small combat window. |
| `/shikari review` | Open mechanic replay. |
| `/shikari next` | Move to the next slide and show the planner. |
| `/shikari prev` | Move to the previous slide and show the planner. |
| `/shikari reset` | Return to the first slide. |
| `/shikari follow` | Turn automatic slide changes on or off. |
| `/shikari calls` | Turn shotcall reminders on or off. |

`/rp` works as a shortcut: `/rp mini`, for example. `/shikari replay` also opens Review.

## Bring in a strategy

Open **Plan → Import**, paste a link or Shikari share code into the bar, and press **Import** (or Enter). Shikari recognizes the source and shows the options you need:

- **raidplan.io:** brings in the slides and editable diagrams as a new plan.
- **WTFDIG:** loads the guide so you can check your strategy, role, group, and variants. Strategy links such as `#caro` preselect that strategy. Import the guide or choose one of its linked editable raidplans.
- **FF Logs:** lets you choose a pull and preview its timeline and available cooldown assignments. Choose **Review this pull with this plan** to inspect its recorded statuses and positions alongside your strategy. API setup appears here when needed.
- **Shikari share code:** imports a new copy. Enable **Update the saved plan with the same ID** to replace a matching saved plan with an update from your raid lead.

Use **Import a saved file** for a raidplan JSON file or a Shikari share-code text file. To send your own plan to the group, open **Plan → Share**.

WTFDIG guide images are references and don't provide calibrated Live positions. Imported guide timings stay disabled until you review and link them. Importing a guide or log does not automatically create adaptive rules.

## Find your spot during a pull

The **Live** workspace shows the current plan and alignment status. For a smaller view, use `/shikari mini`:

| Mini-window marker | Meaning |
| --- | --- |
| **YOUR SPOT · cyan crosshair** | Your planned destination. |
| **YOU · white diamond** | Your current tracked position. |
| **IN POSITION · green ring** | You're within the destination tolerance set in settings. |
| **OPTION** | Your seat has several possible positions on this slide. |

The connecting line points to your destination; it doesn't find a safe route around AOEs.

Live positions need matching waymarks in the duty and on the plan. The mini window explains when alignment is missing. Check **Plan → Roster** if your seat isn't being picked up correctly.

Unlock the mini window in settings to move or resize it outside combat. During a pull it ignores mouse clicks. Slide notes appear underneath the board.

## Calls and changing mechanics

Choose how you receive reminders in **Settings**: on-screen calls, chat, notifications, sound, or speech. `/shikari calls` toggles reminders as a whole; voice has its own setting.

For mechanics where your assignment changes with a buff or debuff, use **Plan → Adaptive** to set up the conditions and corresponding slides. Check each rule before enabling it. Imported rules start disabled, and importing a guide doesn't automatically create these assignments.

A rule can require up to four statuses at once, with optional duration and parameter conditions. This lets you describe combinations such as a role assignment plus a numbered debuff. Plans containing these combined conditions need an updated Shikari client to import.

## Look back after a pull

Open **Review** or use `/shikari review`. Pick an attempt and a mechanic, then play or scrub through the recorded positions over your plan. You can compare attempts to see what changed.

Recording and saved-attempt controls are under **Review → Recording & storage**. Recordings stay on your computer and aren't included in shared plan codes.

### Connect a log to your strategy

1. Import or open your raidplan or WTFDIG strategy first. Then paste an FF Logs report into the same import bar, select a fight, and choose **Review this pull with this plan**.
2. In **Review**, select a cast and scrub to the assignment. Expand **Statuses & strategy**, choose a player, and set their plan seat to compare the same role across pulls.
3. Read the slide notes beside that player's active buffs and debuffs. Choose the strategy slide that matches their assignment. **Link cast to this slide** saves a timeline link for you to review before enabling it.
4. Select the relevant statuses, set the duty, and use **Test selected assignment**. If the result fits the strategy, choose **Add disabled rule to plan**, then review its conditions in **Plan → Adaptive** before enabling it.

The test checks one recorded player's assignment. Check the other possible assignments and more pulls before relying on the rule. Guide prose and example positions don't automatically establish which outcome is correct.

Log positions appear on a source map because their coordinates differ from the plan. **Overlay on strategy** needs three known landmarks entered in both coordinate systems. Alignment belongs to that slide; missing position samples remain gaps. When comparing pulls, Shikari lines them up at the same cast occurrence.

New local recordings also retain readable party buffs, debuffs, and positions, so you can inspect your own pulls through the same controls. Older recordings keep their existing playback but won't gain status history retroactively. Unknown status parameters stay unknown; incomplete log imports can be inspected but can't generate rules.

Rule drafts use observed status durations with a two-second margin. Review those ranges for mechanics where timer lengths determine the assignment. Replay evidence stays local; sharing a plan sends the rules you authored, not the recorded players or their history.

## Moving from RaidPlan

Disable or uninstall the old **RaidPlan** plugin in `/xlplugins` and keep **Shikari** enabled. They are separate installations, so leaving both enabled can open the old viewer or show duplicate windows. Use `/shikari` or `/rp`; Shikari no longer registers `/raidplan`.

## Need a hand?

If something looks wrong, check your selected seat and the alignment message first. For bugs or suggestions, [open an issue](https://github.com/sorenwolfe/Shikari/issues) with what you were doing and a screenshot if possible.
