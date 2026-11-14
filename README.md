# Galathil

## Simple NPC Replacer Swapper

Tested only on Skyrim SE 1.6.1170.

Simple NPC Replacer swapper that creates a shared Galathil Output mod with ESPFE plugin(s) and one combined SkyPatcher setup. It can also replace the source ESP directly.

Optional features include bringing over custom skin textures, custom bodies, and outfits.

## Outfit options

Outfits are added through the same mapping list as look transfers:

- **Replace Outfit** gives the recipient the outfit assigned to the replacer, when that outfit differs from the NPC's earlier version.
- Tick **Add Outfit**, search active Outfit records, choose one, select a recipient, then click **Add Mapping**. If a look source is selected, the row includes both the look and outfit. Click **Clear look** to add an outfit-only row for changing an NPC's outfit without a look transfer.

An added outfit takes priority over **Replace Outfit** for that recipient. Outfit assignments require **Galathil Output + SkyPatcher**. Galathil keeps the outfit's active plugin enabled so SkyPatcher can use the record. Outfit records are plugin data; loose clothing meshes or textures by themselves are not an Outfit record.

If another enabled MO2 mod overrides the selected replacer's FaceGen mesh or tint, Galathil uses the effective winning file for the transfer and reports which mod supplied it.

## Shared Galathil Output

Choose **Galathil Output + SkyPatcher** to add or update NPC appearance mappings in one MO2 mod. Galathil creates the output ESPFE plugin(s), a combined SkyPatcher config, and simple notes that link each replacer asset mod to the output ESP containing its mappings.

Each linked replacer mod stays enabled so the game can use its face and body assets. Galathil copies the appearance NPC data and supported linked appearance records into its output, so the source ESP can usually be disabled while its MO2 asset mod remains enabled. Galathil disables that ESP in the selected profile when no active plugin depends on it, the output does not need it as a master, and any older Galathil routes have been migrated. The source ESP file stays inside the asset mod so Galathil can read the replacer and recover its appearance later. If an unsupported linked record, another plugin, or an unmigrated route still needs the source ESP, Galathil leaves it enabled and explains why. Records from other active plugins, such as a custom race framework or a separately selected outfit, remain normal output masters.

Each output ESP has a matching note such as `Galathil ESP 1.txt`. It contains one line per look transfer in the form `Original NPC -> Recipient NPC`. The replacer asset mod used for those transfers gets an empty note with the same filename. If output is split across multiple ESPs, the notes are numbered to match and each asset mod only gets notes for ESPs that use it.

Skyrim SE 1.6.1130 and later support light-plugin local FormIDs through `0xFFF`; Galathil allocates IDs `0x001` through `0xFFF` and creates additional ESPFE plugins if the output needs more NPC records. Earlier runtimes may require Backported Extended ESL Support for plugins using IDs below `0x800`.

## Other output choices

**Replace source ESP** writes the transferred appearance directly into the existing source plugin.

Galathil asks before a SkyPatcher transfer changes an NPC's race or sex. The generated SkyPatcher rules apply those changes explicitly; `copyVisualStyle` only transfers appearance data. Race and sex changes can affect voice compatibility, animations, and race-based behavior. The direct ESP option blocks a different-sex transfer.

## Requirements

- Windows x64
- Skyrim SE 1.6.1170 (the only runtime tested)
- Mod Organizer 2 with the intended profile selected
- .NET 10 Desktop Runtime
- SkyPatcher enabled for SkyPatcher output

## Run from Mod Organizer 2

1. Extract the Galathil release ZIP to a folder, for example `C:\Modding\Galathil`.
2. In MO2, add `Galathil.exe` as an executable and set **Start in** to that folder.
3. Select the MO2 profile, the replacer mod and ESP, then select a source NPC and its recipient in the middle column.
4. Select a look source and recipient, then click **Add Mapping**. To include an Outfit record, tick **Add Outfit** and choose the record before adding the mapping. For an outfit-only change, click **Clear look**, choose a recipient and Outfit record, then click **Add Mapping**.
5. Choose the output type and optional features, then click **Apply Look**. Galathil shows a completion message and creates a backup before replacing its output.

## Removing or undoing changes

- **Galathil Output + SkyPatcher:** Select the mapping in the bottom list, click **Remove Mapping**, then click **Apply Look**. Galathil rebuilds the shared output without that mapping and restores the source ESP in the selected profile when no remaining output mapping needs it. The previous output and changed asset notes are backed up under `C:\Modding\Galathil\Backups`.
- **Replace source ESP:** Galathil saves `source-files.zip` under `C:\Modding\Galathil\Backups\<source mod>\<timestamp>`. To undo, restore the backed-up files into that mod folder, replacing the edited ESP and FaceGen files. If `backup-info.txt` lists files that were new, remove those files from the mod folder too.

The shared output contains recipient-specific FaceGen mesh and tint files because Skyrim looks them up by the recipient's FormID. The replacer's body, hair, clothing, and other loose assets remain in the original asset mod, which should stay enabled.

## Safety and checks

Galathil reads the selected profile, checks plugin masters and record links, verifies generated ESPFE flags, FormID ranges and FaceGen files, and backs up the previous output under `C:\Modding\Galathil\Backups`. It does not open xEdit or require another executable. Check the result in game for visual details; structural file checks cannot confirm the in-game appearance.

## Build from source

Install the .NET 10 SDK, then run:

```powershell
dotnet publish Galathil.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

The release is framework-dependent. Install the .NET 10 Desktop Runtime if it is not already installed.

See [CHANGELOG.md](CHANGELOG.md) for release history.
