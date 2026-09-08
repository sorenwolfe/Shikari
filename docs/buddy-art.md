# Shikari buddy artwork

Original red wyrmling illustration created for the optional Shikari buddy. The creature is an original design inspired by the general visual language of fantasy role-playing games, with no named character as an image reference.

## Production

- Current asset: `Shikari/Resources/buddy-poses.png`, a **genuinely transparent RGBA** pose atlas, 1254 x 1254 pixels.
- Four equal 627 x 627 cells in reading order: awake, asleep, delighted, focused. All cell edges and surrounding background have alpha zero.
- The user explicitly authorized local extraction after the generator returned opaque checkerboard backgrounds. [Extraction provenance, exact script, and pose bounds](#authorized-transparent-presence-atlas) are recorded below.
- Runtime rendering uses isolated poses directly over the game; the earlier opaque portrait is no longer embedded in the plugin.

## Original portrait (v0.9.3 and earlier)

- Method: built-in image_gen tool, following the imagegen skill; no API/CLI fallback.
- The initial generation and one targeted edit requested transparency. Both outputs were RGB images with a painted checkerboard, so neither is included in the plugin.
- The selected third output replaces that backdrop with near-black so the illustration can sit inside a dark buddy panel. **It is not a transparent sprite.** Do not render it as if it has an alpha cutout.
- Selected asset: `Shikari/Resources/buddy-dragon.png`.
- User preview: `outputs/Shikari-buddy-dragon.png` in the parent task workspace.
- Dimensions: 1254 x 1254; RGB PNG, 1,278,447 bytes, no alpha channel.
- Pixels above a value of 12 occupy approximately x=144..1083, y=42..1209. The visible character has clear margin on all sides.
- No programmatic image edits, downscaling, cutouts, or palette replacement were performed. The selected generated file was copied intact.
- Runtime code may draw the illustration at UI size, with subtle transform animation. A black or near-black portrait area is appropriate.
- Generated source: `C:/Users/hvbig/.codex/generated_images/01a07f24-4607-7652-82d4-156c1ecb9d33/exec-09d61090-1808-47f5-bb6f-b8c5d9e72ee8.png`.

## Exact initial prompt

```text
Use case: stylized-concept
Asset type: original game UI companion character sprite for Shikari, an optional raid helper in a dark black-and-red interface.
Primary request: Create one original small red baby dragon, a charming fantasy wyrmling with a polished Japanese fantasy role-playing game illustration sensibility. This is an original creature, not an existing named character.
Scene/backdrop: genuinely transparent background with a preserved alpha channel. No scenery, no floor, no backdrop, no checkerboard painted into the image.
Subject: One full-body chibi red dragon, poised and attentive in a seated/perched pose. Broad expressive warm amber eyes and an intelligent kind face; small ivory horns; crimson red scales with tasteful dark burgundy shading; pale warm ivory belly; small charcoal-burgundy wing membranes; a curled tapering tail with a subtle spade tip. Cute without looking infantile, capable and reassuring rather than ferocious.
Style/medium: sophisticated clean cel-shaded fantasy game character illustration, crisp deliberate contours, restrained highlights and strong shape design. Tasteful thick outer contour so the silhouette reads on a dark interface. Large clear facial features, few small details, no noisy scales. Color and polish suitable for a professional gamer-facing plugin.
Composition/framing: Single full-body character centered in a square canvas, three-quarter view with its head and gaze turned slightly toward the viewer's right where a speech bubble will be placed by the application. Whole horns, wings, toes and tail visible with generous transparent padding. Compact silhouette designed to stay recognizable when drawn at 80 to 110 pixels tall. Relaxed short forepaws, balanced anatomy, exactly one creature.
Lighting/mood: soft warm light, quietly alert and friendly. No glowing aura or effects.
Constraints: transparent PNG artwork, no text, no lettering, no logos, no border, no UI, no speech bubble, no duplicated character, no sprite sheet, no cropped anatomy.
```

## Exact transparency edit prompt (not selected)

```text
Use case: background-extraction
Asset type: transparent game companion sprite.
Edit only the background of the supplied image. Preserve this exact red baby dragon character, pose, expression, anatomy, colors and illustration intact. The existing pale checkerboard is an unwanted painted background, not transparency. Remove every white and gray checkerboard pixel behind the dragon and in the spaces between its wing, body, toes and tail. Return a genuinely transparent RGBA PNG with alpha 0 outside the creature and antialiased alpha at its contour. No painted grid, no white or black backdrop, no floor, no new elements. Keep the whole dragon and existing transparent margin. This must be a clean standalone dragon cutout suitable for compositing directly over a black game interface.
```

## Exact selected background edit prompt

The original generated dragon was used as the edit target.

```text
Use case: precise-object-edit
Asset type: illustration for a fantasy game helper panel with a pure black background.
Edit the supplied dragon image by replacing ALL of the gray-and-white checkerboard background with a single perfectly flat pure black color, RGB (0, 0, 0), hex #000000. Preserve the exact red dragon's identity, anatomy, expression, pose, wings, horns, tail, crimson coloring and ivory belly. Make no other design changes.
The whole square canvas outside the creature, including every opening between wing/body/tail/toes, must be completely black with no texture, no gradient, no shadow, no border, no scenery, no white/gray checkerboard and no floor. Keep the same generous empty margins. Full character visible. The background must be black all the way to all four image edges.
```

## Presence pose atlas attempts (2026-09-08)

The optional presence update requested four equal-cell poses of the same Ember character: top-left awake, top-right asleep, bottom-left delighted, bottom-right focused. Both attempts used the built-in image_gen tool with the inspected existing Ember image as the first identity reference. No API/CLI fallback, pixel editing, masking, background removal, resizing, or palette processing was performed.

The first output and one targeted background/layout retry both returned **RGB PNGs with a painted checkerboard, not RGBA transparency**, despite explicit requests for alpha. At this stage neither was registered as a plugin pose atlas. Generation stopped after the retry. The subsequent user-authorized extraction is documented below.

- First generated source: `C:/Users/hvbig/.codex/generated_images/01a07f24-4607-7652-82d4-156c1ecb9d33/exec-995b8def-eed6-4536-bc7f-157a5839bb13.png`.
- Selected retry source: `C:/Users/hvbig/.codex/generated_images/01a07f24-4607-7652-82d4-156c1ecb9d33/exec-08d0f2d9-7fbf-4608-aa9e-78110336cc9e.png`.
- Unmodified staged source: `../ember-presence/ember-poses-rgb-source.png` relative to the repository root.
- Dimensions: 1254 × 1254; RGB, no alpha channel. Equal source cells: 627 × 627.
- Intended quadrant order: awake, asleep, delighted, focused, in reading order.
- Read-only chromatic-pixel bounds within each cell (approximate, exclude some dark contours): awake (76,98)–(493,608); asleep (74,252)–(531,546); delighted (108,74)–(531,529); focused (110,40)–(514,531).
- These are not alpha bounds. Pose baselines differ and should be fitted after extraction; no quadrant crops or offsets were applied to this source.

### Exact four-pose generation prompt

```text
Use case: stylized-concept
Asset type: production game UI companion pose atlas, a single genuinely transparent RGBA PNG.
Input image 1 is the identity/style reference for Ember, an original small red baby dragon. Preserve this exact character design: deep crimson scales, ivory swept horns and segmented cream belly, amber eyes, charcoal-burgundy bat wings, expressive large head, short legs and paws, long curled red tail with spade tip, clean sophisticated cel-shaded fantasy illustration. The reference image's black background is NOT part of the requested art.
Create ONE square sprite sheet, ideally 2048 x 2048, with four equal square cells in an invisible 2-by-2 grid. Each quadrant contains exactly one full-body pose of the SAME Ember dragon. Exact order: TOP LEFT calm awake sitting with warm open amber eyes; TOP RIGHT curled comfortably asleep, eyes gently closed, chin resting by the curled tail; BOTTOM LEFT delighted welcoming smile with little wings lifted cheerfully; BOTTOM RIGHT focused/determined alert sitting, attentive brows but still kind. All poses face three-quarter toward the viewer's RIGHT, matching the reference character. Keep equal character scale, consistent anatomy, identical colors/lighting, and consistent level of detail across cells. Sleeping pose naturally lower and wider, no exaggerated size increase.
Layout: each pose is centered horizontally in its own cell, grounded on the same baseline at 86 percent of cell height; the awake, delighted and focused poses have similar total height about 72 percent of a cell. At least 10 percent transparent margin at every cell edge. Each whole character, both horns, full wings, toes, tail and tail tip are fully contained in its quadrant and uncut. The central horizontal and vertical gutters are transparent and clear. The four cells have no visible borders or panels.
BACKGROUND REQUIREMENT: actual PNG alpha transparency (RGBA), fully alpha-zero outside the four isolated characters, including between wings/body/tail. Do NOT draw a checkerboard. Do NOT paint a black, white, gray, green or colored background. Do NOT paint a floor, shadow oval, scene, speech bubble, panel or UI. Transparent cutout sprites with clean antialiased alpha edges, no matte fringe. No words, no labels, no letters, no sleeping Zs, no symbols, no sparkles, no extra objects. Return the atlas as a transparent PNG with preserved alpha channel.
```

### Exact targeted retry prompt

```text
Use case: background-extraction.
Edit target: the supplied four-pose Ember dragon sheet. Preserve the exact four dragons, their poses, identity, colors, illustration and order. The gray-white checkerboard currently visible is painted RGB pixels, NOT transparency; it must be removed, not reproduced.
Deliver a genuine transparent PNG cutout with FOUR CHANNELS (R,G,B,A). Every pixel outside the four dragon silhouettes must have alpha exactly zero. This includes the outer canvas, center gutters, and gaps between body/wings/tail. Use antialiased alpha only at silhouette edges. Do not composite the result onto a checkerboard, white, black, gray or any solid background. No matte, no shadow, no floor. The finished image is intended to be directly composited over arbitrary desktop/game content, so an opaque background is unacceptable.
Technical layout correction only: keep the canvas square, arrange the four original poses inside EXACT equal 2x2 quadrants. Their dividing lines are at50% width and50% height, invisible. Reduce each pose enough to give at least8% clear transparent margin within its own quadrant, never crossing either midpoint. All tails/horns/wings fully contained. Keep baseline at86% of the quadrant height, same character scale across poses. Top-left awake, top-right asleep, bottom-left delighted, bottom-right focused. Do not add labels, text, Zs, decorative symbols or scene elements. Preserve this design rather than creating a different dragon. Actual RGBA PNG transparency is the essential output requirement.
```

## Authorized transparent presence atlas

The user explicitly approved local background removal: **"Yes, remove the background locally."** The selected generated RGB atlas was then processed locally with Pillow and NumPy. No further image generation, model download, anatomy change, palette replacement, or production resizing was used.

- Production asset: `Shikari/Resources/buddy-poses.png`.
- User copies in the parent task workspace: `outputs/Shikari-Ember-poses.png` and `outputs/Shikari-Ember-pose-preview.png`.
- Original selected source SHA256: `ed8f9d799c18f5a1695441214f226da1e1974156c715cf6db7614ccdc2abca17`.
- Production RGBA SHA256: `e0bc4552a768bd2c0e6107c2a028cb00e4303cd588f038e2872e076c815946aa`.
- Dimensions: **1254 x 1254**, four **627 x 627** cells; PNG color mode **RGBA**, actual alpha extrema **0, 255**.
- Alpha counts: **1,151,402 fully transparent**, **24,501 partially transparent**, **396,613 fully opaque** pixels.
- Every outer canvas/cell edge is fully transparent. No pose crosses a quadrant boundary.
- Inspection: source and final light/dark contact sheet were viewed at original resolution, and smaller companion previews checked. The enclosed opening between the delighted pose's wing and neck plus two small between-paw openings were removed explicitly. Eye glints, horn highlights, dark wings, outlines, and interior colors remain present. The RGB difference count for all opaque interior pixels outside the contour correction band is **zero**.

The method floods only connected bright neutral background pixels, then applies a narrow contour matte correction using nearby background and intact dark outline samples. Three small, visually confirmed enclosed-background regions are handled explicitly; other enclosed bright regions (eyes/horns) remain intact. Transparent pixels have zero RGB. This is an extraction tuned to this exact generated source, not a general-purpose background remover.

### Pose sampling

Bounds below are quadrant-local `[left, top, right, bottom)` rectangles containing every nonzero-alpha pixel. Cells remain unmodified; runtime drawing can fit these bounds to a stable baseline. Sleeping is naturally lower/wider and should not be stretched to upright height.

| Pose | Cell | Alpha bounds | Atlas quadrant UV |
| --- | --- | --- | --- |
| Awake | top-left | (73, 98, 497, 612) | (0, 0) to (0.5, 0.5) |
| Asleep | top-right | (71, 250, 534, 550) | (0.5, 0) to (1, 0.5) |
| Delighted | bottom-left | (104, 73, 535, 533) | (0, 0.5) to (0.5, 1) |
| Focused | bottom-right | (108, 39, 518, 535) | (0.5, 0.5) to (1, 1) |

### Exact local extraction script

The executed script is saved outside the repository at `../ember-presence/extract_ember_poses.py`, alongside the unchanged `ember-poses-rgb-source.png`. It writes `ember-poses-rgba.png`, `alpha-report.json`, and `ember-poses-contact.png` to that same directory. Dependencies: Python, Pillow, and NumPy. The selected production file is an exact copy of its RGBA output.

Script SHA256: `f34ee6a7689855fb4855e2b27cbcdb1d72f2c46319a39166c1acee9409c082d0`.

```python
"""User-authorized local checkerboard extraction of the generated Ember atlas.

Pillow and NumPy only; no generation, recoloring, scaling or pixel keying of
the dragon interior. The exterior is segmented by connected bright neutral
background. A narrow contour is unmatted against its nearby checkerboard.
"""
from collections import deque
from pathlib import Path
import hashlib
import json

import numpy as np
from PIL import Image, ImageFilter, ImageDraw, ImageFont

TASK = Path(__file__).resolve().parents[2]
WORK = Path(__file__).resolve().parent
SOURCE = WORK / 'ember-poses-rgb-source.png'
CELL = 627


def exterior_connected(candidate):
    height, width = candidate.shape
    outside = np.zeros((height, width), dtype=bool)
    frontier = deque()
    for x in range(width):
        for y in (0, height - 1):
            if candidate[y, x]:
                outside[y, x] = True
                frontier.append((y, x))
    for y in range(1, height - 1):
        for x in (0, width - 1):
            if candidate[y, x]:
                outside[y, x] = True
                frontier.append((y, x))
    while frontier:
        y, x = frontier.popleft()
        for yy, xx in ((y-1, x), (y+1, x), (y, x-1), (y, x+1)):
            if 0 <= yy < height and 0 <= xx < width and candidate[yy, xx] and not outside[yy, xx]:
                outside[yy, xx] = True
                frontier.append((yy, xx))
    return outside


def dilate(mask, radius):
    return np.asarray(Image.fromarray(mask.astype(np.uint8) * 255).filter(
        ImageFilter.MaxFilter(radius * 2 + 1))) > 0


def bounds(mask):
    yy, xx = np.where(mask)
    return [int(xx.min()), int(yy.min()), int(xx.max()) + 1, int(yy.max()) + 1] if len(xx) else None


def main():
    source = Image.open(SOURCE).convert('RGB')
    assert source.size == (CELL * 2, CELL * 2)
    rgb = np.asarray(source)
    values = rgb.astype(np.float64)
    minimum = rgb.min(axis=2)
    chroma = rgb.max(axis=2).astype(np.int16) - minimum
    potential_background = (minimum >= 232) & (chroma <= 18)
    background = exterior_connected(potential_background)
    # Three visually verified enclosed background openings. The two small
    # triangles between the front paws are split by antialiased source lines;
    # deliberately narrow ROIs keep eye glints and ivory horn highlights safe.
    hole_rectangles = [(342, 520, 361, 537), (980, 1060, 1005, 1084), (220, 829, 290, 926)]
    for left, top, right, bottom in hole_rectangles:
        background[top:bottom, left:right] |= potential_background[top:bottom, left:right]
    foreground = ~background
    # Work only on the narrow contour; interior source pixels stay exact RGB.
    band = foreground & dilate(background, 2)
    # Recover the faintest antialias pixels without retaining gray grid tiles.
    band |= background & dilate(foreground, 1) & (values.mean(axis=2) < 241)
    alpha = foreground.astype(np.float64)
    output_rgb = values.copy()
    padded_rgb = np.pad(values, ((4, 4), (4, 4), (0, 0)), mode='edge')
    padded_bg = np.pad(background, 4, constant_values=True)
    for y, x in zip(*np.where(band)):
        local = padded_rgb[y:y+9, x:x+9]
        local_bg = padded_bg[y:y+9, x:x+9]
        # Bright, nearly neutral, confirmed exterior pixels estimate the matte.
        clean_bg = local_bg & (local.min(axis=2) >= 242) & (np.ptp(local, axis=2) <= 10)
        if not clean_bg.any():
            continue
        # Closest samples track the 245/253 checker squares at contour edges.
        yy, xx = np.where(clean_bg)
        distance = (yy - 4)**2 + (xx - 4)**2
        nearest = distance <= distance.min() + 2
        matte = np.median(local[yy[nearest], xx[nearest]], axis=0)
        # Every sprite has a dark illustrated outer line. Estimate its intact
        # local color from a close, unambiguously foreground contour pixel.
        small = local[2:7, 2:7]
        small_bg = local_bg[2:7, 2:7]
        luma = small.mean(axis=2).copy()
        luma[small_bg] = 1000
        index = np.unravel_index(np.argmin(luma), luma.shape)
        ink = small[index]
        if luma[index] > 145:
            continue
        color = values[y, x]
        vector = matte - ink
        amount = float(np.clip(np.dot(matte - color, vector) / max(np.dot(vector, vector), 1), 0, 1))
        # Already-dark ink and colored interior detail should remain opaque.
        # Contour unmatting must not reduce alpha merely because nearby ink
        # happens to be a slightly darker shade of the artist's outline.
        if color.mean() <= ink.mean() + 12 or amount >= .985:
            amount = 1.0
        if amount < .035:
            amount = 0.0
        alpha[y, x] = amount
        if 0 < amount < 1:
            output_rgb[y, x] = np.clip((color - (1 - amount) * matte) / amount, 0, 255)
    alpha_byte = np.rint(alpha * 255).astype(np.uint8)
    output_rgb[alpha_byte == 0] = 0
    rgba = np.dstack([np.rint(output_rgb).astype(np.uint8), alpha_byte])
    output = Image.fromarray(rgba, 'RGBA')
    destination = WORK / 'ember-poses-rgba.png'
    output.save(destination, optimize=True)

    pose_names = ['Awake', 'Asleep', 'Delighted', 'Focused']
    report = {
        'source_sha256': hashlib.sha256(SOURCE.read_bytes()).hexdigest(),
        'output_sha256': hashlib.sha256(destination.read_bytes()).hexdigest(),
        'size': list(output.size), 'mode': output.mode,
        'alpha_extrema': [int(alpha_byte.min()), int(alpha_byte.max())],
        'transparent_pixels': int(np.sum(alpha_byte == 0)),
        'partial_pixels': int(np.sum((alpha_byte > 0) & (alpha_byte < 255))),
        'opaque_pixels': int(np.sum(alpha_byte == 255)),
        'opaque_interior_rgb_changes': int(np.sum(np.any(rgba[:, :, :3] != rgb, axis=2) & foreground & ~band)),
        'verified_hole_rectangles': hole_rectangles,
        'pose_bounds_exclusive': {},
    }
    for index, name in enumerate(pose_names):
        x, y = index % 2 * CELL, index // 2 * CELL
        a = alpha_byte[y:y+CELL, x:x+CELL]
        report['pose_bounds_exclusive'][name] = bounds(a > 0)
        assert not a[0, :].any() and not a[-1, :].any() and not a[:, 0].any() and not a[:, -1].any()
    (WORK / 'alpha-report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')

    # Same production pixels composited for light/dark and intended UI scale.
    canvas = Image.new('RGB', (1254, 1110), '#25252b')
    draw = ImageDraw.Draw(canvas)
    try:
        font = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 24)
        small_font = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 18)
    except OSError:
        font = small_font = ImageFont.load_default()
    colors = ['#f4f2ee', '#111117']
    for row, color in enumerate(colors):
        for index, name in enumerate(pose_names):
            x = index * 313
            top = row * 475
            draw.rectangle((x, top, x + 312, top + 474), fill=color)
            ink = '#1c1b22' if row == 0 else '#eeedf2'
            draw.text((x + 18, top + 14), name, fill=ink, font=font)
            sx, sy = index % 2 * CELL, index // 2 * CELL
            sprite = output.crop((sx, sy, sx+CELL, sy+CELL))
            sprite = sprite.crop(sprite.getbbox())
            sprite.thumbnail((278, 316), Image.Resampling.LANCZOS)
            canvas.paste(sprite, (x + (313 - sprite.width)//2, top + 377-sprite.height), sprite)
            draw.text((x + 18, top + 417), 'Light background' if row == 0 else 'Dark background', fill=ink, font=small_font)
    draw.text((18, 966), '128 px companion previews', fill='#eeedf2', font=font)
    for index, name in enumerate(pose_names):
        sx, sy = index % 2 * CELL, index // 2 * CELL
        sprite = output.crop((sx, sy, sx+CELL, sy+CELL))
        sprite = sprite.crop(sprite.getbbox())
        sprite.thumbnail((150, 112), Image.Resampling.LANCZOS)
        canvas.paste(sprite, (index*313 + 85, 1104-sprite.height), sprite)
    canvas.save(WORK / 'ember-poses-contact.png')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
```
