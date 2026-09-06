# WTFDIG importing

Open **Plan > Import**, paste a fight link such as `https://wtfdig.info/74/m9s`, and click **Load guide**. Select the strategy, role, and group, resolve any variant choices, then expand the preview before importing.

## Two import options

- **Import guide as a new plan** creates instruction slides for the selected role/group. Available reference diagrams can be downloaded as slide backdrops. Phase descriptions, source links, selected options, retrieval time, and a source hash are retained in notes.
- **Import an editable raidplan instead** lists raidplan.io boards referenced by the selected guide. This imports the complete linked board as a separate plan, including its editable geometry through Shikari's existing raidplan importer. A URL's `#step` fragment does not crop that board.

The previous plan is saved before the prepared import becomes active. If saving fails or combat begins while downloading, the import is not applied. Cancelled imports remove their staged images.

## What to expect from this first version

This adapter reads public fight data from `mczub/wtfdig` on GitHub. It supports literal data, local constant references, spreads, and simple string concatenation. It does not run JavaScript or evaluate source functions. Unsupported expressions and diagram transformations appear in conversion warnings. A fight whose overall source structure is incompatible fails without importing a plan.

Reference images are visual guides: their arena alignment and positions are not calibrated for Live tracking. Native WTFDIG arena objects and strategy-board codes are not decoded. Source spotlight masks and rotations are not applied. Use an editable linked raidplan when available, then check its arena calibration.

Imported timeline entries remain disabled and unlinked. Guide prose is not used to invent status IDs or automatic adaptive rules. Configure and verify those separately in Shikari's adaptive mechanic editor.

Recognized link options can initialize the strategy, role, group, or matching toggles. Confirm the controls: other URL options are not applied, and invalid toggle values require a selection. Reference backdrops are local files; sharing a plan does not embed those image files, but its notes retain the image/source links.

Source fetches are bounded and do not follow redirects. Image downloads are limited to HTTPS wtfdig.info images, 24 requests, 4 MiB per image, 48 MiB total, and a 60-second preparation budget. Missing images leave source links and warnings in the imported notes.

## Validation

Run each regression in a separate PowerShell 7 process:

```powershell
pwsh -NoProfile -File tests/wtfdig.ps1
pwsh -NoProfile -File tests/wtfdig-ui.ps1
dotnet build Shikari/Shikari.csproj --configuration Release
```

The first test uses production parsing, conversion, and download code with synthetic source fixtures and fake HTTP responses. The second compiles the production UI partial against API stubs and checks load/cancellation, combat and save guards, and committing on the consuming thread. These do not replace a full build against Dalamud or an in-game test.

Optional live-source smoke check (network required):

```powershell
pwsh -NoProfile -File tests/wtfdig-live.ps1 -Url 'https://wtfdig.info/74/m9s'
```

In game, load a guide, compare a chosen role/group with the original website, inspect conversion warnings, and test both guide and editable-board imports. Confirm reference images, saved-plan reload, and cancellation. Incompatible guides should report their limitation; do not assume every page on the site is supported.
