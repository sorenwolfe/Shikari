# Shikari buddy artwork

Original red wyrmling illustration created for the optional Shikari buddy. The creature is an original design inspired by the general visual language of fantasy role-playing games, with no named character as an image reference.

## Production

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
