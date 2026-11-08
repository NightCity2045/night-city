<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
# Cyberpsychosis episodes

Extends the existing brain-owned `NCHumanityComponent`; no parallel humanity pool, actor identity,
NPC takeover, automatic targeting or weapon attack. Ordinary zero-burden medical replacement does not
trigger episodes. Persistent adaptation remains governed by `CyberwareHumanity.md`.

## Lifecycle

The production Humanity profile enables episodes. Risk is strain + 0.25 × missing humanity +
0.15 × exhaustion, clamped to 100. At least 20 strain and positive enhancement burden are required.
Risk thresholds are 35/60/85. Each upward transition needs eight accumulated seconds and advances only
one stage. Falling below the current threshold minus five (or below 15 strain) for thirty seconds enables
recovery. Acute episodes end after at most ninety online seconds, followed by 120 seconds without relapse.
Recovery does not restore humanity or erase causes. Exhaustion rises during dissociation/acute states
and falls slowly when strain is below 35.

The old deterministic band algorithm remains available for profiles without `episodesEnabled`, preserving
older content contracts. Optional snapshot fields default to a neutral state in existing saves. New fields
are cloned and validated; remaining durations do not advance offline. Existing physical brain transfer and
medical persistence carry recovery, consent, symptom cooldowns and exhaustion.

## Inputs and treatment

Native wound impacts, amputations and observed nearby deaths emit bounded stimuli. Victim and instigator
receive separate, cooldown-limited trauma/violence consequences. Witnesses need consciousness, proximity
and an unobstructed path. Pain, shock, overdose and loaded enhancements contribute capped continuous strain.
The actual physiology scheduler advances this state, rather than a second city-wide simulation loop.

Existing neural stabilizer, restorative medium and combined support remain the pharmaceutical authority.
Their concentration, clearance, toxic falloff, sedation and interactions are unchanged. Grounding is an
8-second native DoAfter: both participants must remain conscious and close; movement/damage interrupts.
Consent and a 60-second shared patient cooldown are checked again at completion, preventing crowd stacking.
It removes five strain and supplies 30 seconds at 0.3 strain/second. Safe mode uses native ability shutdown
and load APIs, preserves life support, cancels pending reflexes and inhibits new ones for thirty seconds.

## Presentation

Five hardware-weighted episode profiles select one symptom at a time. Personal sounds use local synthetic
positions. The private heartbeat goes only to the body's attached session, with a 2.5-second client lease.
Changing body, losing updates or becoming unconscious clears the effects. Hallucinations never instantiate
server entities, inflict damage, alter real chat messages or inspect hidden players.

Threat misidentification selects an unobstructed nearby person with a free right hand and adds a private, directional native in-hand weapon layer. Other phantoms copy the complete sprite of a random visible player (the observer is the fallback when alone), including clothing and body layers. They occupy world coordinates and use ordinary sprite rendering, lighting and occlusion. Body echoes copy the observer. Real inventories and sprites remain unchanged. Threats may fire a three-shot local illusion: the ordinary GunSystem muzzle animation/light, the BulletPistol sprite moving at the normal projectile speed, BulletImpactEffect and optional spatial sound, with no projectile, collision or damage. All local dolls and shot audio are cleared when the symptom ends or the observer changes body/map, and reduced effects disables them. Motor warnings precede a
native blade deployment by four seconds, only during dissociation and only if native hardware checks pass.
There is never an automatic swing or shot. Acute crises retain the existing enhancement restrictions.

The self interaction opens a compact stability panel; the HUD remains visible without the panel.
Client-only archived CVars `nc.psychosis_reduced_effects` and `nc.psychosis_quiet_sounds` preserve accessibility
preferences without suppressing authoritative state or textual warnings. No camera shake or flashing.
Medical scanners and surgical consoles disclose the actual status, including exhaustion/support.

Sprites: six 32×32 states in `_NC/Interface/Cyberpsychosis/episodes.rsi`, ImageGen source and deterministic
normalizer in `Tools/_NC/Humanity`. EN/RU text and a medical guidebook entry are included.

## Verification

DebugOpt client/server builds passed. All 39 selected humanity/episode unit tests passed, including
serialization of the new fields. The YAML linter reported no errors; the RSI validator passed.
Automated integration fixtures were not executed. Runtime recordings use a local admin-prepared scene with real medical state
transitions and native interactions; recorded stimuli are not a claim of ordinary unassisted play.

Live checks additionally rejected grounding from a sleeping helper and after patient refusal.
The recorded scene exercises native grounding, neural stabilizer dosing, safe mode and the full recovery interval.
Audio output is not captured by the virtual client; the videos are silent.

A narrow context-menu fix retains server verbs received before a hovered submenu becomes visible.
Closed menus still clear the active menu reference. This prevents losing the stability interaction on fast local replies.
The rebuilt client displayed the server actions in the normal context submenu; the stability panel opened by clicking its entry.

The revised doll rendering was verified in the live client: full sprite clones, native right-hand weapon frames through turns, weapon/impact effects, and cleanup at symptom end. The one-player fixture exercised the self-appearance fallback; selection between multiple remote player appearances was not separately exercised. Client/server builds and YAML validation passed.

The hand-drawn gunfire overlay was removed. Local shot visuals now reuse GunSystem.CreateEffect through a client-only wrapper, the actual BulletPistol Sprite component definition, and its configured ImpactEffect. Only visual components are copied to the flying bullet; no Projectile, Physics, Fixtures or damage components are created.
