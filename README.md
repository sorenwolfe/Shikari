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

**A Shikari share code:** open **Plan → Share**, paste the code, and choose **Import as a new plan**. Use the same tab to copy your own plan's code for your group.

**A raidplan.io link:** open **Plan → Import** and paste the link to bring in its slides and editable diagrams.

**A WTFDIG fight link:** open **Plan → Import**, load the guide, and choose your strategy, role, group, and any additional options. Check the preview, then import the guide or one of its linked editable raidplans. Guide images are references, so they don't provide calibrated Live positions. Imported guide timings stay disabled until you review and link them.

**An FF Logs report:** open **Plan → Import** to bring in a fight timeline and available cooldown assignments. This needs your FF Logs API credentials, which you can set up in the plugin.

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

## Look back after a pull

Open **Review** or use `/shikari review`. Pick an attempt and a mechanic, then play or scrub through the recorded positions over your plan. You can compare attempts to see what changed.

Recording and saved-attempt controls are under **Review → Recording & storage**. Recordings stay on your computer and aren't included in shared plan codes.

## Moving from RaidPlan

Disable or uninstall the old **RaidPlan** plugin in `/xlplugins` and keep **Shikari** enabled. They are separate installations, so leaving both enabled can open the old viewer or show duplicate windows. Use `/shikari` or `/rp`; Shikari no longer registers `/raidplan`.

## Need a hand?

If something looks wrong, check your selected seat and the alignment message first. For bugs or suggestions, [open an issue](https://github.com/sorenwolfe/Shikari/issues) with what you were doing and a screenshot if possible.
