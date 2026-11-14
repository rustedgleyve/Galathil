# Changelog

## 1.1.0 — 2026-11-14

### Added

- Added a searchable catalog of active Outfit records. A look and outfit can be assigned together, or an outfit can be assigned by itself after clearing the look selection.
- Combined look and outfit assignments by recipient in one list, including saved outfit mappings and migration from earlier mapping plans.
- Reworked the main window into three columns, put MO2's highest-priority mods at the top, and set a minimum size so controls stay visible.
- Added numbered `Galathil ESP N.txt` notes: each output note lists `Original NPC -> Recipient NPC`, and linked asset mods get a matching empty note.

### Changed

- Consolidated shared transfers into Galathil Output ESPFE plugin(s) and one SkyPatcher configuration. Removed the separate **Same ESP + SkyPatcher** output choice; direct source ESP replacement remains available.
- Generate a clean appearance record and copy supported linked appearance records into Galathil Output, including head parts, skin armor, armor addons, texture sets, hair colors, and outfits. Loose meshes and textures remain in the enabled asset mod.
- Disable a source replacer ESP when Galathil Output and other active plugins no longer need it. Keep it enabled when a remaining dependency requires it, and explain why.
- Use the effective winning FaceGen mesh and tint from MO2's file priority, and report which mod supplied them when another mod overrides the replacer's files.
- Write transferred FaceGen mesh and tint files under the recipient NPC's FormID. Apply race and gender changes explicitly in SkyPatcher rules, and keep the appearance helper's race and gender flags aligned with the donor.
- Size ESPFE shards for both NPCs and copied appearance records, and keep the appearance mapping output within the supported light-plugin FormID range.

### Fixed

- Prevented replacer gameplay data from being copied into appearance helper NPCs or source-restoration overrides.
- Fixed malformed transferred heads caused by writing FaceGen under the helper NPC's FormID and giving the helper the recipient's race and flags.
- Kept selected outfit records and unsupported source-plugin references from being silently lost when Galathil disables a replacer ESP.
- Preserved backups of the previous output and changed asset notes so mappings can be removed or restored safely.

## 1.0.0 — 2026-11-13

- Initial release of Galathil with MO2 profile scanning, NPC look mapping, direct source ESP replacement, and shared ESPFE plus SkyPatcher output.
