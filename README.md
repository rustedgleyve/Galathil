# Galathil

## Simple NPC Replacer Swapper

**Tested only on Skyrim SE 1.6.1170.**

Use an NPC replacer or follower mod to give its look to a different NPC. Galathil runs from Mod Organizer 2 (MO2) and can also bring over custom skin, bodies, and outfits.

[Download the latest release](https://github.com/rustedgleyve/Galathil/releases/latest)

## Get started

1. Extract the release ZIP to a folder such as `C:\Modding\Galathil`. Keep the files together.
2. Add `Galathil.exe` to MO2's executable list and set **Start in** to the folder you extracted. Launch it from MO2 with Skyrim closed.
3. Select your profile, the mod that provides the look, and the character whose look you want to use. Then pick the NPC who should receive it.
4. Choose any optional changes and click **Add Mapping**.
5. Choose **Galathil Output ESP** or **Replace source ESP**, then click **Apply Look**.

Use Windows x64, MO2, and the x64 .NET 10 Desktop Runtime. [Download the desktop runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Choose an output

- **Galathil Output ESP** puts your swaps together in an MO2 mod called **Galathil Output**. It uses light ESPs (ESPFE) to apply the looks directly in Skyrim. Keep the original mods enabled in MO2's left panel for their assets. Galathil hides each original plugin when it can safely disable it and tells you if another plugin still depends on it.
- **Replace source ESP** edits one replacer mod's own plugin and face files. Use this when you want the existing mod to point to a different NPC. This option needs loose face files in the source mod.

Use **Galathil Output ESP** to turn a follower into a replacer. Select the follower as the source and an existing NPC as the recipient. When Galathil disables the follower's plugin, the follower and any other game content supplied by that plugin disappear from the profile. Galathil stops if another active plugin still needs the follower's plugin.

You can use the same NPC on both sides of different mappings. For example, **Arcadia → Camilla** and **Rayya → Arcadia** work in one batch. Camilla gets the look from the Arcadia replacer you selected, and Arcadia gets the look from the Rayya replacer. The order of the two rows can be reversed.

## Skin, body, and outfits

- **Skin** and **Body** become available when Galathil detects custom skin textures or body meshes. When a mod links both through one record, they may transfer together.
- **Replace Outfit** brings over the outfit assigned by the replacer if it changed the source NPC's outfit.
- **Add Outfit** lets you choose an outfit from an enabled mod. It takes priority over **Replace Outfit**. To change only a character's outfit, click **Clear look**, select a recipient and outfit, and click **Add Mapping**. The outfit picker lists outfits defined by plugins. Keep the outfit mod enabled for its meshes and textures.

Galathil asks before changing the recipient's race or sex in shared output. That can affect voices, animations, and race-specific behavior. Different-sex swaps require shared output.

## How shared output handles files

Galathil gives the face mesh and tint the recipient's ID and updates the tint reference inside the mesh. The face files stay in the selected asset mod. If the original face files are in an archive, Galathil writes the recipient's files as loose files alongside it.

The output contains the light ESPs, saved mappings, and numbered notes such as `Galathil ESP 1.txt`. Each note lists the swaps in that ESP. Linked asset mods get an empty note of the same name so MO2 can show the links.

If another enabled mod would still overwrite the recipient's face files, Galathil tells you which mod to check. If you rename an MO2 mod, Galathil can find its saved mappings by the original ESP name. Select the source again if you rename the ESP itself.

## Remove or undo a change

- **Galathil Output ESP:** Select the row in **Mappings**, click **Remove Mapping**, then click **Apply Look**. If you deleted the source mod in MO2, **Apply Look** also clears its old mapping from the output. Restore the source mod's original face files from `Backups\Galathil Output\<timestamp>\rekeyed-facegen.zip` or reinstall the source mod if you want to use it as originally shipped.
- **Replace source ESP:** Restore `source-files.zip` from `Backups\<source mod>\<timestamp>` into the original mod folder. The included `backup-info.txt` lists any new files to remove.

Find `Backups` next to `Galathil.exe`. Galathil checks the generated plugin and face files during **Apply Look**. Check the look in Skyrim afterward.

## Build from source

With the .NET 10 SDK installed, run:

```powershell
dotnet publish Galathil.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

[Changelog](CHANGELOG.md)
