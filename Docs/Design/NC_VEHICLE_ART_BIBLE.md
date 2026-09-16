# Night City vehicle art guide

## Camera, orientation and scale

World vehicles use one true orthographic top-down body frame. The vehicle nose points up in the source image, matching the local physics forward vector at zero rotation. RobustToolbox rotates that frame continuously with the entity transform, so steering no longer selects discrete directional artwork and left/right cannot be swapped by RSI frame order.

Every production frame is 128 by 128 pixels with transparent background. Passenger cars occupy 76 to 112 pixels along their longest axis according to their class; the recovery truck occupies up to 116 pixels. This keeps a common pixel density while preserving visible size differences. Collision geometry remains prototype data and must be calibrated separately from the painted silhouette.

Production frames are downsampled once from approved high-resolution sources with Lanczos filtering and restrained sharpening. Do not enlarge an already reduced production PNG or blur it a second time. Each model has one source PNG under `Docs/Design/SourceArt/VehicleTopDown/`.

## Visual language

- Use a readable roof, hood, cabin and rear silhouette at normal gameplay zoom.
- Keep the palette dark and worn, with small sodium orange, warning red and cold cyan accents.
- Put permanent wear in `body`; put fresh collision damage in the damage layers.
- Avoid perspective side panels, visible horizon, floor shadows and photographic backgrounds.
- Keep lamps and important body seams at least one final pixel wide.

## Required RSI states

| State | Purpose | Runtime source |
| --- | --- | --- |
| `shadow` | Ground contact and readable footprint | Always visible |
| `body` | Undamaged base vehicle | Always visible |
| `damage-light` | Scratches and minor dents | Body deformation threshold |
| `damage-heavy` | Crushed and exposed panels | Body deformation threshold |
| `headlights` | Front lamps | Headlight switch and battery |
| `brakes` | Rear brake lamps | Brake input and battery |
| `reverse` | White reversing lamps | Reverse gear and battery |
| `hood-open` | Open service bay | Workshop hood state |
| `wheel-damage` | Failed or badly worn wheel set | Grip function and wheel wear |
| `tuning-street` | Visible street tuning package | Vehicle tuning state |
| `smoke` | Overheated or heavily worn engine | Condition system |
| `fire` | Source art for vehicle fire effects | Existing SS14 fire visual remains authoritative |

All states except `smoke` and `fire` contain exactly one 128 by 128 frame. Smoke and fire contain four animation frames and rotate together with the vehicle sprite. The client visual system reacts to replicated component state and caches the resulting layer mask.

## Fleet assets

The production library contains the Toyota Supra A80, a recovery truck, Quadra Turbo-R, Thorton Galena, Archer Quartz, Quadra Type-66, Mizutani Shion, Thorton Colby, Villefort Alvarado, Makigai MaiMai and Rayfield Caliburn. Each vehicle keeps its own source PNG and RSI directory.

The Cyberpunk vehicle designs are original AI-assisted fan-art renditions. No texture from a CD PROJEKT RED game was copied. Their RSI metadata records `LicenseRef-CDPR-Fan-Content`; use and distribution must follow the [CD PROJEKT RED Fan Content Guidelines](https://www.cdprojektred.com/en/fan-content).

## Build and validation

Build one RSI from a transparent top-down source with its nose pointing up:

```text
python Tools/_NC/VehicleArt/build_vehicle_rsi.py SOURCE.png OUTPUT.rsi --extent 100 --copyright "..."
```

Validate the fleet with:

```text
python Tools/_NC/VehicleArt/validate_vehicle_rsi.py Resources/Textures/_NC/Vehicle/NAME.rsi
python RobustToolbox/Schemas/validate_rsis.py Resources/
```

## Vehicle audio

Vehicles use the original two-sound setup from the base SS14 content: `vehiclestartup.ogg` plays when the engine starts and `vehicleengineidle.ogg` loops while it is running. Throttle changes only the loop volume. `carhorn.ogg` remains the separate horn action. Vehicle-specific RPM bands, turbo layers and generated audio assets are not part of this setup.
