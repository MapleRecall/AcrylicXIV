# AcrylicXIV

Acrylic-style blur behind the native game UI — the world blurs while the UI itself stays crisp.

AcrylicXIV injects a blur pass into the game's render pipeline right before the HUD is drawn and uses the UI's own per-pixel coverage as a mask, so only the world *behind* semi-transparent windows and panels is blurred. The UI on top stays sharp.

<img height="300" alt="image" src="https://github.com/user-attachments/assets/a6ed13f3-d66d-46f4-81ef-bc6d38dac9a1" />
<img height="300" alt="image" src="https://github.com/user-attachments/assets/6d3186ae-aa2e-4a97-b557-0c33fb368d3c" />
<img height="300" alt="image" src="https://github.com/user-attachments/assets/24309b37-e157-4c71-8da8-5ab20cb87860" />

## Features

- **Blur only under the UI** — the world behind panels is blurred; everything drawn on top stays crisp.
- **Two blur kernels** — Kawase (cheapest, smoothest) or Gaussian (steadier, especially on text), each with its own strength.
- **Acrylic material** (all optional, toggled per effect):
  - **Grain** — fine noise for an acrylic look (soft or sharp).
  - **Tint** — sepia-style recolour that keeps detail.
  - **Distortion** — wavy, glass-like warp.
  - **Adjust** — brightness / saturation / contrast.
- **Transparency-aware** — start / full alpha thresholds control how UI opacity fades the blur in.
- **Skip full-screen covers** — keep maps, menus and loading screens sharp, avoiding a blur⇄sharp flash.

## Usage

Open the settings window from the plugin installer, or:

| Command | Action |
| --- | --- |
| `/acrylic` | Open settings |
| `/acrylic on` \| `off` \| `toggle` | Turn the blur on / off |

## Notes

- The blur only applies *beneath* the UI — it can't blur between overlapping windows.
- All GPU work runs on the game's render thread, so it doesn't race the renderer.

## License

AGPL-3.0-or-later.
