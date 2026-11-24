# Changelog

## 1.2.9 — 2026-11-23

### Fixed

- Galathil Output can apply linked looks in one batch, including when one NPC receives a look and donates its original replacer look to someone else. Each assignment uses its selected donor's FaceGen.
- Point transferred FaceGen meshes at the recipient's tint when the donor mesh still references a named FaceTint file or a RaceMenu export path.

## 1.2.8 — 2026-11-16

### Fixed

- Prune saved look and outfit assignments when their source MO2 mod or plugin has been deleted, so the next Apply Look rebuild removes their generated NPC records and asset notes.
- Remove stale disabled source plugin entries from the MO2 profile when their plugin files no longer exist, instead of failing while trying to restore a deleted ESP.
- Find saved source plugins in disabled as well as enabled MO2 mods, preserving mappings when the source mod is installed but disabled.

## 1.2.7 — 2026-11-16

### Fixed

- Close cached source plugin readers before hiding source ESPs during output installation, preventing a self-inflicted file-in-use error.

## 1.2.6 — 2026-11-16

### Fixed

- Do not write donor NPC records into Galathil Output. When the old replacer ESP is disabled, Skyrim resolves the donor through the normal active plugin order, so later replacers are not masked by a stale reset record.

## 1.2.5 — 2026-11-16

### Changed

- Automatically hide source ESP files with MO2's `.mohidden` suffix whenever Galathil disables them for shared output. Automatically unhide them when Galathil restores the ESP.
- Back out file hiding if an output update fails, so the source plugin filename and MO2 profile state remain in sync.

## 1.2.4 — 2026-11-16

### Fixed

- Rekey transferred FaceGen meshes and tint files to the recipient's FormID in both Galathil Output and direct ESP replacement. Update the FaceTint path inside the mesh so another replacer for the donor NPC cannot overwrite the transferred look.
- Keep rekeyed FaceGen inside the selected asset mod instead of duplicating it in Galathil Output. Back up donor files and remove their old paths; save hashes so obsolete recipient files can be cleaned up safely later.
- If a source ESP must remain enabled, Galathil Output restores the donor NPC's earlier record instead of leaving that replacer on the donor. Refuse a transfer if a higher-priority MO2 mod would override the recipient's loose FaceGen.

## 1.2.3 — 2026-11-16

### Fixed

- Rebind old asset-note paths to the current MO2 mod folder using the linked plugin name after a mod rename. Ignore missing historical folders while still validating every current marker destination.

## 1.2.2 — 2026-11-16

### Fixed

- Reuse FaceGen already moved into Galathil Output when a saved source mapping's MO2 mod name has changed. Match the prior mapping by plugin and NPC FormKey.

## 1.2.1 — 2026-11-16

### Fixed

- Rebind saved mappings to the enabled MO2 mod that contains the source plugin and NPC FormKey when the MO2 mod folder/name was changed.

## 1.2.0 — 2026-11-15

### Changed

- Replaced shared SkyPatcher routing with direct NPC overrides in Galathil Output ESPFE plugin(s). No SkyPatcher configuration is generated or required.
- Convert a follower NPC into a replacer by assigning its appearance to an existing NPC. Galathil copies supported appearance records and FaceGen to the recipient, then disables the follower ESP in the selected MO2 profile when no remaining plugin needs it; the follower's asset mod stays enabled.
- Copy selected Outfit records and supported linked records into the output plugin while keeping the outfit's asset mod enabled.
- Ask before changing the recipient's race or sex in shared output. Keep target gameplay data such as quests and dialogue out of the appearance transfer.
- Keep output notes and asset markers tied to the ESPFE that contains each mapping.

### Fixed

- Avoid counting recipient NPC overrides as new ESL-local records; only copied appearance and outfit records consume the light-plugin local ID range.
- Do not report an outfit source plugin as safely removable unless its selected Outfit record was actually copied into Galathil Output.

## 1.1.5 — 2026-11-15

### Fixed

- Prevented dark-face bugs after a transfer by keeping the donor FaceTint file at the path embedded in the transferred FaceGen mesh, while also writing the recipient's FaceTint file.

## 1.1.4 — 2026-11-15

### Fixed

- Fixed hidden-plugin reading by copying the hidden ESP to a temporary file with its original filename for Mutagen, then removing the temporary copy after the read completes.

## 1.1.3 — 2026-11-15

### Added

- Read MO2-hidden source plugins (`.esp.mohidden`, `.esm.mohidden`, and `.esl.mohidden`) as appearance donors for Galathil Output without unhiding or enabling them in the profile.
- Show hidden source plugins in Galathil's plugin picker with a clear status label.

## 1.1.2 — 2026-11-15

### Added

- Allow NPCs added by follower mods to act as appearance donors for existing NPCs in Galathil Output + SkyPatcher. Select the recipient explicitly; Galathil disables the follower ESP when it is safe to do so.
- Allow a donor follower's assigned outfit to be transferred with the Replace Outfit option.

### Changed

- Force shared output for added follower NPCs. If another plugin or an output master would keep the follower ESP active, Galathil stops and explains the dependency instead of leaving the follower in game.
- Preserve added-NPC donor mappings when the follower ESP is disabled, so later shared-output updates can still read the source record from its mod folder.

## 1.1.1 — 2026-11-14

### Changed

- Shared output now moves the selected replacer's loose FaceGen mesh and tint from the donor's FormID path to the recipient's FormID path. Galathil no longer writes a restoration override or FaceGen copy for the donor's previous appearance.
- Reinstall or redownload the original replacer mod after removing a mapping to restore the donor's consumed loose FaceGen files. Galathil backs up moved FaceGen in the output update backup.
- FaceGen conflicts from later MO2 mods are reported and left untouched; Galathil transfers the selected asset mod's own files. FaceGen stored only in an archive is copied but cannot be renamed within that archive.

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
