# Night City medical clean-room reference

Functional reference only; no Aurora source code copied.

## Scope

Night City implements its own RobustToolbox ECS medical system. Aurora is used only
as a behavioural completeness checklist for localized wounds, organ failure,
bleeding, surgery, diagnostics, prosthetics and long-term consequences. Source code,
UI structure, text, icons, formulas and data from Aurora must not be copied.

## Pinned references

- Night City substrate: `NightCity2045/night-city`, revision
  `cf518c3dd57c2c148fde2097368386fe92929729`.
- Aurora behavioural reference: `Aurorastation/Aurora.3`, revision
  `4fe4ce9c21cf59befd6f3376c3fa9c415adf2fef`.
- Combat and armor substrate revision from the companion design:
  `796ee1265b412d46143141f8c33a6ab0b701a669`.

## Project decisions overriding the source designs

- Only human and cybernetically modified human anatomy is in scope.
- Human medicine is Authoritative from the first playable release.
- RED Core, attributes, skills, TECH, DV, LUCK and random action checks are absent.
- Physical projectile trajectory and collision are the only hit test.
- Medical and surgical actions resolve deterministically from prototype-defined
  requirements and observable world state.
- `DamageableComponent` is not authoritative for human trauma, consciousness or
  death.

## Asset boundary

- `Resources/Textures/_NC/Interface/Targeting/Status/*.rsi` retain their existing
  CC-BY-SA-3.0 metadata and attribution.
- Loose `Doll/*.png` files are third-party targeting overlays. They must not receive
  Astro copyright headers; their provenance must be recorded before public release.
- No Aurora assets are accepted into this repository without an independently
  verified compatible license and attribution.
