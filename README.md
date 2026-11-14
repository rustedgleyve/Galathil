# Galathil

## Simple NPC Replacer Swapper

Simple NPC Replacer swapper that creates a shared Galathil Output mod with ESPFE plugin(s) and one combined SkyPatcher setup. It can also replace the source ESP directly. Designed to be simpler than its alternatives.

Optional features include bringing over custom skin textures, custom bodies, and new outfits when they are detected.

## Requirements

- Windows x64
- Skyrim SE 1.6.1170 (the only runtime tested. Theoretically nothing stops it from testing on newer versions, and older versions with BEEs. Just haven't tested it)
- Mod Organizer 2 with the intended profile selected
- .NET 10 Desktop Runtime
- SkyPatcher enabled for SkyPatcher output

## Output Settings

Choose **Galathil Output + SkyPatcher** to add or update NPC appearance mappings in one MO2 mod. Galathil creates the output ESPFE plugin(s), a combined SkyPatcher config, and notes that identify the asset mods used by each source ESP.

The other option is to directly replace the existing ESP of the replacer.

Each linked replacer mod stays enabled so the game can use its face and body assets. Galathil disables that mod's source ESP in the selected profile when no active plugin depends on it, the output does not need it as a master, and any older Galathil routes have been migrated. The source ESP file stays inside the asset mod so Galathil can read the replacer and recover its appearance later. If another plugin, output record, or unmigrated route needs the source ESP, Galathil leaves it enabled and explains why.

In **Galathil Output**, `Galathil Asset Mods.txt` lists all linked asset mods. Each source ESP also gets a separate note named `Asset Mod - <source ESP> - <asset mod>.txt`. The asset mod contains a matching `Galathil Link - <source ESP>.txt` note. This is so clicking on Galathil Output in MO2 highlights all Asset folders it makes, for easy visuals.

Skyrim SE 1.6.1130 and later support light-plugin local FormIDs through `0xFFF`; Galathil allocates IDs `0x001` through `0xFFF` and creates additional ESPFE plugins if the output needs more NPC records. Earlier runtimes may require Backported Extended ESL Support for plugins using IDs below `0x800`. In the event that it doesn't work, the non-SkyPatcher option should still work and generate an ESPFE.

## Other output choices

**Replace source ESP** writes the transferred appearance directly into the existing source plugin.

**Same ESP + SkyPatcher** adds an appearance record and SkyPatcher rule to the source mod.

Galathil asks before a SkyPatcher transfer changes an NPC's race or sex. These changes can affect voice compatibility, animations, and race-based behavior. The direct ESP option blocks a different-sex transfer.

## Run from Mod Organizer 2

1. Extract the Galathil release ZIP to a folder, for example `C:\Modding\Galathil`.
2. In MO2, add `Galathil.exe` as an executable and set **Start in** to that folder.
3. Select the MO2 profile, the replacer mod and ESP, then select the character receiving the look. If no recipient is selected, Galathil uses the existing Galathil mapping or the NPC the replacer overrides.
4. Click **Assign look** to add the mapping to Galathil Output.
5. Select detected optional features and click **Apply look**. Galathil shows a completion message and creates a backup before replacing its output.

## Build from source

Install the .NET 10 SDK, then run:

```powershell
dotnet publish Galathil.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

The release is framework-dependent. Install the .NET 10 Desktop Runtime if it is not already installed.
