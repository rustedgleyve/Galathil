using System.Globalization;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Abstractions;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Masters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

internal enum AppearanceOutputMode
{
    GalathilOutput,
    EspfePatch
}

internal sealed record AppearanceAssignment(
    string SourceMod,
    string SourcePlugin,
    string SourceName,
    string SourceFormKey,
    string TargetName,
    string TargetFormKey,
    bool HasCustomSkin,
    bool HasCustomBody,
    bool HasNewOutfit = false,
    string? AppearancePlugin = null,
    string? AppearanceFormKey = null,
    string? SourceRaceKey = null,
    bool SourceIsFemale = false,
    string? TargetRaceKey = null,
    bool TargetIsFemale = false,
    string? AppearanceMod = null,
    string? ProvidedOutfitFormKey = null,
    string? ProvidedOutfitPlugin = null,
    string? ProvidedOutfitName = null);

internal sealed record ModOutfit(string Name, string FormKeyText, string Plugin, string WinningPlugin, string ModName)
{
    public override string ToString() => $"{Name} — {ModName} ({WinningPlugin})";
}

internal sealed record OutfitAssignment(
    string TargetName,
    string TargetFormKey,
    string OutfitName,
    string OutfitFormKey,
    string OutfitPlugin,
    string OutfitWinningPlugin,
    string OutfitModName);

internal sealed record GalathilOutputManifest(
    int Version,
    bool CopySkin,
    bool CopyBody,
    bool CopyOutfit,
    IReadOnlyList<AppearanceAssignment> Assignments,
    IReadOnlyList<string>? DisabledSourcePlugins = null,
    IReadOnlyList<GalathilAssetLink>? AssetLinks = null,
    IReadOnlyDictionary<string, int>? SourcePluginLoadOrder = null,
    bool? ProvideModOutfit = null,
    IReadOnlyList<OutfitAssignment>? OutfitAssignments = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? AssetMarkerFiles = null,
    IReadOnlyList<GalathilFaceGenLink>? FaceGenLinks = null);

internal sealed record GalathilAssetLink(string SourceMod, string SourcePlugin);
internal sealed record GalathilFaceGenLink(string AssetMod, string SourcePlugin, string TargetFormKey, string MeshSha256, string TintSha256);

internal sealed record GalathilModEntry(string Name, bool Enabled);

internal sealed record AppearanceGenerationResult(
    string ModName,
    string ModFolder,
    string? PluginPath,
    string? SkyPatcherPath,
    string BackupPath,
    IReadOnlyList<string> Warnings,
    int? AppliedLookCount = null,
    int? AppliedOutfitCount = null);

internal sealed class LightPluginFormIdCapacityException(FormKey formKey)
    : Exception($"The new NPC record {formKey} would exceed the ESL local FormID range.")
{
    public FormKey FormKey { get; } = formKey;
}

internal static class NpcAppearanceGenerator
{
    private const uint FirstLightObjectId = 0x001;
    private const uint LastLightObjectId = 0xFFF;
    private const string MeshFolder = @"meshes\actors\character\FaceGenData\FaceGeom";
    private const string TintFolder = @"textures\actors\character\FaceGenData\FaceTint";
    internal const string OutputModName = "Galathil Output";
    internal const string OutputManifestName = "Galathil.Output.json";

    public static AppearanceGenerationResult Generate(
        string instancePath,
        string profileName,
        string profilePath,
        string modsPath,
        string dataPath,
        IReadOnlyList<string> activePluginOrder,
        IReadOnlySet<string> activePlugins,
        IReadOnlyDictionary<string, string> pluginPaths,
        IReadOnlyList<GalathilModEntry> modOrder,
        IReadOnlyList<AppearanceAssignment> assignments,
        IReadOnlyList<OutfitAssignment> outfitAssignments,
        AppearanceOutputMode mode,
        bool copySkin,
        bool copyBody,
        bool replaceOutfit,
        bool changeTargetRace)
    {
        if (mode == AppearanceOutputMode.GalathilOutput)
            return GenerateGalathilOutput(instancePath, profileName, profilePath, modsPath, dataPath,
                activePluginOrder, activePlugins, pluginPaths, modOrder, assignments, outfitAssignments,
                copySkin, copyBody, replaceOutfit, changeTargetRace);

        if (assignments.Any(a => IsAddedNpcFormKey(a.SourceFormKey, a.SourcePlugin)))
            throw new InvalidOperationException("An NPC added by a follower mod can only supply its look through Galathil Output ESP. Choose an existing NPC as the recipient.");

        if (outfitAssignments.Count > 0)
            throw new InvalidOperationException("New outfit assignments require Galathil Output ESP. Choose that output type before applying.");

        if (assignments.Count == 0) throw new InvalidOperationException("Add an appearance assignment first.");
        var sourceMods = assignments.Select(a => a.SourceMod).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sourcePlugins = assignments.Select(a => a.SourcePlugin).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sourceMods.Length != 1 || sourcePlugins.Length != 1)
            throw new InvalidOperationException("Apply one source mod/plugin at a time. This lets Galathil safely replace that ESP without adding another plugin.");
        var duplicateTargets = assignments.GroupBy(a => a.TargetFormKey, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateTargets is not null)
            throw new InvalidOperationException($"More than one look is assigned to {duplicateTargets.First().TargetName}. Remove one assignment before creating the mod.");
        var sourceKeys = assignments.Select(a => NormalizeFormKey(a.SourceFormKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (assignments.Any(a => sourceKeys.Contains(NormalizeFormKey(a.TargetFormKey))))
            throw new InvalidOperationException("A recipient in this batch is also a source NPC. Apply those transfers one at a time so Galathil can keep each appearance with the correct FormID.");

        foreach (var assignment in assignments)
        {
            if (!activePlugins.Contains(assignment.SourcePlugin))
                throw new InvalidOperationException($"{assignment.SourcePlugin} is disabled in this MO2 profile. Enable that plugin before generating its appearance transfer.");
            if (!modOrder.Any(m => m.Enabled && m.Name.Equals(assignment.SourceMod, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"The source mod '{assignment.SourceMod}' is disabled in this MO2 profile.");
        }

        var sourceMod = sourceMods[0];
        var sourcePlugin = sourcePlugins[0];
        var sourceModFolder = Path.GetFullPath(Path.Combine(modsPath, sourceMod));
        var sourcePluginPath = Path.GetFullPath(Path.Combine(sourceModFolder, sourcePlugin));
        if (!IsChildPath(modsPath, sourceModFolder) || !IsChildPath(sourceModFolder, sourcePluginPath))
            throw new InvalidOperationException("The selected source path is outside its MO2 mod folder.");
        if (!File.Exists(sourcePluginPath))
            throw new FileNotFoundException($"The source plugin is not in the selected MO2 mod folder: {sourcePluginPath}");
        if (!pluginPaths.TryGetValue(sourcePlugin, out var activeSourcePath) ||
            !Path.GetFullPath(activeSourcePath).Equals(sourcePluginPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Another enabled mod is overriding {sourcePlugin}. Galathil will not replace a plugin file that is not the active MO2 winner.");
        if (Process.GetProcessesByName("SkyrimSE").Length > 0)
            throw new InvalidOperationException("Close Skyrim before replacing a plugin in its MO2 mod folder.");

        var requestedKeys = assignments.SelectMany(a => new[] { a.SourceFormKey, a.TargetFormKey })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var npcChains = ReadNpcChains(activePluginOrder, pluginPaths, requestedKeys);
        var prepared = new List<PreparedAssignment>();
        var warnings = new List<string>();
        var assetResolver = new FaceGenAssetResolver(modsPath, dataPath, modOrder, pluginPaths);

        foreach (var assignment in assignments)
        {
            var sourceChain = GetChain(npcChains, assignment.SourceFormKey, assignment.SourceName);
            var sourceIndex = sourceChain.FindIndex(r => r.Plugin.Equals(assignment.SourcePlugin, StringComparison.OrdinalIgnoreCase));
            if (sourceIndex < 0)
                throw new InvalidOperationException($"{assignment.SourceName} was not found as an override in {assignment.SourcePlugin}.");
            var laterNonGalathil = sourceChain.Skip(sourceIndex + 1)
                .FirstOrDefault(r => !r.Plugin.StartsWith("Galathil - ", StringComparison.OrdinalIgnoreCase));
            if (laterNonGalathil is not null)
                throw new InvalidOperationException($"{assignment.SourceName} is overwritten by '{laterNonGalathil.Plugin}' after '{assignment.SourcePlugin}'. Choose the active winning replacer instead.");
            if (sourceIndex == 0)
                throw new InvalidOperationException($"{assignment.SourceName} is not an override in {assignment.SourcePlugin}. Direct replacement only works for an existing NPC replacer.");

            var source = sourceChain[sourceIndex].Record;
            var beforeSource = sourceChain[sourceIndex - 1].Record;
            var appearance = source;
            var appearanceKey = source.FormKey;
            if (!string.IsNullOrWhiteSpace(assignment.AppearanceFormKey))
            {
                var appearancePlugin = assignment.AppearancePlugin ?? sourcePlugin;
                if (!appearancePlugin.Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The saved Galathil appearance for {assignment.SourceName} is stored in {appearancePlugin}. Direct replacement can only simplify a route whose appearance record is in {sourcePlugin}; restore or convert that route through Galathil Output ESP first.");
                appearanceKey = ParseOutfitFormKey(assignment.AppearanceFormKey, assignment.SourceName);
                using var appearanceLease = PluginFileAccess.Open(sourcePlugin, sourcePluginPath);
                appearance = appearanceLease.Mod.Npcs.FirstOrDefault(n => n.FormKey == appearanceKey)?.DeepCopy()
                    ?? throw new InvalidOperationException($"The saved Galathil appearance record {FormKeyText(appearanceKey)} is missing from {sourcePlugin}. Restore the matching Galathil backup first.");
                if (string.IsNullOrWhiteSpace(appearance.EditorID) || !appearance.EditorID.StartsWith("GALATHIL_", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The appearance record {FormKeyText(appearanceKey)} is not a Galathil helper NPC. Galathil will not delete an unrelated NPC record.");
            }
            var targetChain = GetChain(npcChains, assignment.TargetFormKey, assignment.TargetName);
            var target = targetChain[^1].Record;
            if (source.FormKey == target.FormKey)
                throw new InvalidOperationException($"{assignment.SourceName} and {assignment.TargetName} resolve to the same NPC record.");
            if (mode == AppearanceOutputMode.EspfePatch)
            {
                var sourceOrder = activePluginOrder.IndexOf(sourcePlugin);
                var targetWinnerOrder = activePluginOrder.IndexOf(targetChain[^1].Plugin);
                if (sourceOrder < 0 || targetWinnerOrder < 0 || targetWinnerOrder >= sourceOrder)
                    throw new InvalidOperationException($"The source plugin loads before {targetChain[^1].Plugin}, which currently wins for {assignment.TargetName}. An in-place ESP cannot safely override it; choose Galathil Output ESP or move the target's winning plugin earlier.");
            }
            var sourceFemale = IsFemale(appearance);
            var targetFemale = IsFemale(target);
            if (sourceFemale != targetFemale && mode == AppearanceOutputMode.EspfePatch)
                throw new InvalidOperationException($"{assignment.SourceName} and {assignment.TargetName} have different genders. Use Galathil Output ESP to transfer this look.");

            var raceMismatch = appearance.Race.FormKey != target.Race.FormKey;
            if (raceMismatch && !changeTargetRace)
                warnings.Add($"{assignment.TargetName} keeps its original race; its appearance may not match perfectly because it differs from {assignment.SourceName}.");
            else if (raceMismatch)
                warnings.Add($"{assignment.TargetName}'s race is changed to match {assignment.SourceName}. Race changes can affect race-based behavior.");
            var copySourceSkinArmor = (copySkin && assignment.HasCustomSkin) || (copyBody && assignment.HasCustomBody);
            var skinMismatch = source.WornArmor?.FormKeyNullable != target.WornArmor?.FormKeyNullable;
            if (skinMismatch && (assignment.HasCustomSkin || assignment.HasCustomBody) && !copySourceSkinArmor)
                warnings.Add($"{assignment.TargetName} keeps its current skin and body. Select Skin or Body to use {assignment.SourceName}'s unique assets.");
            if (assignment.HasCustomSkin && assignment.HasCustomBody && copySkin != copyBody)
                warnings.Add($"{assignment.SourceName}'s unique skin textures and body mesh are linked through the same Skyrim skin record. Copying either applies that linked record, so both may transfer together.");
            var hasNewOutfit = assignment.HasNewOutfit || (appearance.DefaultOutfit is { IsNull: false } &&
                appearance.DefaultOutfit.FormKeyNullable != beforeSource.DefaultOutfit?.FormKeyNullable);
            if (hasNewOutfit && !replaceOutfit)
                warnings.Add($"{assignment.TargetName} keeps its own outfit because Replace Outfit was not selected.");

            assetResolver.EnsureRecipientFaceGenWillWin(assignment.SourceMod, target.FormKey, assignment.TargetName);
            FaceGenFiles sourceFace;
            try
            {
                sourceFace = assetResolver.ReadCurrentReplacerFaceGen(assignment.SourceMod, appearanceKey, warnings);
            }
            catch (Exception ex) when (!string.IsNullOrWhiteSpace(assignment.AppearanceFormKey) && ex is FileNotFoundException or InvalidDataException)
            {
                sourceFace = ReadLegacyGalathilFaceGenBackup(assignment.SourceMod, source.FormKey, warnings);
            }
            if (assetResolver.HasArchivedFaceGen(assignment.SourceMod, source.FormKey) ||
                (appearanceKey != source.FormKey && assetResolver.HasArchivedFaceGen(assignment.SourceMod, appearanceKey)))
                throw new InvalidOperationException($"FaceGen for {assignment.SourceName} is stored in a BSA inside the source mod. Galathil cannot remove those archived donor-ID files safely; extract the replacer's FaceGen to loose files first, then try again.");

            prepared.Add(new PreparedAssignment(assignment, source, appearance, beforeSource, target, sourceFace,
                raceMismatch && changeTargetRace, copySourceSkinArmor, replaceOutfit && hasNewOutfit));
        }

        var targetKeys = prepared.Select(item => item.Target.FormKey).ToHashSet();
        var obsoleteSourceFiles = CollectInPlaceFilesToRemove(sourceModFolder, sourcePlugin, prepared, targetKeys);

        var pluginFileName = sourcePlugin;
        var stageFolder = Path.Combine(Path.GetTempPath(), "Galathil", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageFolder);

        string? pluginPath = null;
        string? skyPatcherPath = null;
        string? backupPath = null;
        var expectedFaceGen = new Dictionary<FormKey, FaceGenFiles>();
        var originalDeletedNpcCount = 0;
        try
        {
            var output = SkyrimMod.Create(SkyrimRelease.SkyrimSE)
                .FromPath(new ModPath(ModKey.FromFileName(pluginFileName), sourcePluginPath))
                .Mutable()
                .Construct();
            var requiresLightFlag = output.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small);
            originalDeletedNpcCount = output.Npcs.Count(n => n.IsDeleted);

            foreach (var item in prepared)
            {
                var targetWinner = GetChain(npcChains, item.Assignment.TargetFormKey, item.Assignment.TargetName)[^1].Plugin;
                var requiredPlugins = new List<string> { item.Target.FormKey.ModKey.FileName.ToString(), targetWinner };
                foreach (var requiredPlugin in requiredPlugins.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (requiredPlugin.Equals(output.ModKey.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!output.ModHeader.MasterReferences.Any(m => StringComparer.OrdinalIgnoreCase.Equals(m.Master.FileName.ToString(), requiredPlugin)))
                        throw new InvalidOperationException($"{pluginFileName} does not already require {requiredPlugin}. Galathil will not add a new master to the source plugin; use Galathil Output ESP for this target.");
                }

                var targetOverride = output.Npcs.GetOrAddAsOverride(item.Target);
                CopyAppearance(targetOverride, item.Appearance, item.CopyRace, item.CopySkinArmor, includeOutfit: false);
                if (item.CopyOutfit)
                    targetOverride.DefaultOutfit = new FormLinkNullable<IOutfitGetter>(item.Appearance.DefaultOutfit?.FormKeyNullable);

                var targetFaceGen = RekeyFaceGen(item.SourceFaceGen, item.Source.FormKey, item.Appearance.FormKey, item.Target.FormKey);
                WriteFaceGen(stageFolder, item.Target.FormKey, targetFaceGen);
                expectedFaceGen[item.Target.FormKey] = targetFaceGen;
            }

            var removedNpcKeys = new HashSet<FormKey>();
            foreach (var sourceKey in prepared.Select(item => item.Source.FormKey).Distinct())
            {
                if (targetKeys.Contains(sourceKey)) continue;
                output.Npcs.Remove(sourceKey);
                removedNpcKeys.Add(sourceKey);
            }
            foreach (var item in prepared)
            {
                if (item.Appearance.FormKey == item.Source.FormKey || targetKeys.Contains(item.Appearance.FormKey)) continue;
                output.Npcs.Remove(item.Appearance.FormKey);
                removedNpcKeys.Add(item.Appearance.FormKey);
            }

            pluginPath = Path.Combine(stageFolder, pluginFileName);
            output.WriteToBinary(pluginPath, BinaryWriteParameters.Default with
            {
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck
            });
            var expectedGroupCounts = CountMajorRecordGroups(output);
            var expectedMasters = output.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToArray();
            var expectedFlags = output.ModHeader.Flags;
            ValidateWrittenPlugin(pluginPath, output.ModKey, expectedGroupCounts, expectedMasters, expectedFlags, requiresLightFlag, originalDeletedNpcCount);

            var stagedFiles = Directory.GetFiles(stageFolder, "*", SearchOption.AllDirectories);
            var replacedFiles = stagedFiles.Select(path => (Stage: path, Relative: Path.GetRelativePath(stageFolder, path), Destination: Path.Combine(sourceModFolder, Path.GetRelativePath(stageFolder, path)))).ToList();
            var touchedFiles = replacedFiles.Select(f => f.Relative)
                .Concat(obsoleteSourceFiles.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var existingFiles = replacedFiles.Where(f => File.Exists(f.Destination)).Select(f => f.Relative)
                .Concat(obsoleteSourceFiles.Keys)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var backupFolder = Path.Combine(AppContext.BaseDirectory, "Backups", SafeFileStem(sourceMod), DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(backupFolder);
            backupPath = Path.Combine(backupFolder, "source-files.zip");
            var backupNotes =
                $"Galathil replaced {sourcePlugin} in place for profile '{profileName}'.\r\n" +
                string.Join("\r\n", assignments.Select(a => $"{a.SourceName} ({a.SourceFormKey}) -> {a.TargetName} ({a.TargetFormKey})")) +
                "\r\n\r\nFiles written:\r\n" + string.Join("\r\n", replacedFiles.Select(f => f.Relative)) +
                "\r\n\r\nDonor FaceGen and old Galathil route files removed:\r\n" + string.Join("\r\n", obsoleteSourceFiles.Keys) +
                "\r\n\r\nRestore the ZIP to undo; remove entries absent from it.";
            var backupFiles = replacedFiles.Where(f => existingFiles.Contains(f.Relative)).Select(f => f.Destination)
                .Concat(obsoleteSourceFiles.Keys.Select(relative => Path.Combine(sourceModFolder, relative)));
            CreateBackupArchive(backupPath, sourceModFolder, backupFiles, backupNotes);

            try
            {
                foreach (var file in replacedFiles) ReplaceFileAtomically(file.Stage, file.Destination);
                foreach (var (relative, originalBytes) in obsoleteSourceFiles)
                {
                    var path = Path.Combine(sourceModFolder, relative);
                    if (!File.Exists(path)) continue;
                    if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(originalBytes))
                        throw new IOException($"{relative} changed while Galathil was preparing the transfer. No files were removed; try again.");
                    File.Delete(path);
                }
                ValidateWrittenPlugin(sourcePluginPath, ModKey.FromFileName(sourcePlugin), expectedGroupCounts, expectedMasters, expectedFlags, requiresLightFlag, originalDeletedNpcCount);
                ValidateWrittenFaceGen(sourceModFolder, expectedFaceGen);
                ValidateRemovedNpcRecords(sourcePluginPath, removedNpcKeys);
                ValidateRemovedDonorFaceGen(sourceModFolder, prepared, targetKeys);
                pluginPath = sourcePluginPath;

                var obsoleteMods = FindSupersededGalathilMods(modsPath, modOrder, assignments);
                var obsoleteArchives = ArchiveSupersededMods(modsPath, obsoleteMods, backupFolder);
                RemoveSupersededModsFromProfile(profilePath, obsoleteMods);
                foreach (var oldMod in obsoleteMods)
                {
                    try
                    {
                        if (!IsChildPath(modsPath, oldMod.Folder)) throw new InvalidOperationException("The generated folder is outside the MO2 mods directory.");
                        Directory.Delete(oldMod.Folder, recursive: true);
                    }
                    catch (Exception ex) { warnings.Add($"Removed {oldMod.Name} from the active profile, but its folder could not be cleaned up: {ex.Message}"); }
                }
                if (obsoleteArchives.Count > 0) warnings.Add("The older Galathil patch was removed from MO2 and stored in the backup ZIP.");
            }
            catch
            {
                RestoreBackupArchive(backupPath, sourceModFolder, touchedFiles, existingFiles);
                throw;
            }
            return new AppearanceGenerationResult(sourceMod, sourceModFolder, pluginPath, skyPatcherPath, backupPath, warnings);
        }
        catch
        {
            if (Directory.Exists(stageFolder)) Directory.Delete(stageFolder, recursive: true);
            throw;
        }
    }

    private static AppearanceGenerationResult GenerateGalathilOutput(
        string instancePath,
        string profileName,
        string profilePath,
        string modsPath,
        string dataPath,
        IReadOnlyList<string> activePluginOrder,
        IReadOnlySet<string> activePlugins,
        IReadOnlyDictionary<string, string> pluginPaths,
        IReadOnlyList<GalathilModEntry> modOrder,
        IReadOnlyList<AppearanceAssignment> assignments,
        IReadOnlyList<OutfitAssignment> outfitAssignments,
        bool copySkin,
        bool copyBody,
        bool replaceOutfit,
        bool changeTargetRace)
    {
        if (Process.GetProcessesByName("SkyrimSE").Length > 0)
            throw new InvalidOperationException("Close Skyrim before updating Galathil Output ESPs.");

        var warnings = new List<string>();
        var missingSourceMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var liveAssignments = new List<AppearanceAssignment>();
        foreach (var assignment in assignments)
        {
            var sourceFolder = Path.GetFullPath(Path.Combine(modsPath, assignment.SourceMod));
            var sourcePluginExists = IsChildPath(modsPath, sourceFolder) && Directory.Exists(sourceFolder) &&
                PluginFileAccess.FindInModFolder(sourceFolder, assignment.SourcePlugin) is not null;
            if (sourcePluginExists)
            {
                liveAssignments.Add(assignment);
                continue;
            }
            missingSourceMods.Add(assignment.SourceMod);
        }
        assignments = liveAssignments;
        var liveOutfitAssignments = outfitAssignments.Where(assignment =>
        {
            if (assignment.OutfitModName.Equals("Game Data", StringComparison.OrdinalIgnoreCase)) return true;
            var folder = Path.GetFullPath(Path.Combine(modsPath, assignment.OutfitModName));
            var exists = IsChildPath(modsPath, folder) && Directory.Exists(folder) &&
                (PluginFileAccess.FindInModFolder(folder, assignment.OutfitWinningPlugin) is not null ||
                 PluginFileAccess.FindInModFolder(folder, assignment.OutfitPlugin) is not null);
            if (!exists) missingSourceMods.Add(assignment.OutfitModName);
            return exists;
        }).ToArray();
        outfitAssignments = liveOutfitAssignments;
        if (missingSourceMods.Count > 0)
            warnings.Add($"Removed saved look or outfit entries from missing mods: {string.Join(", ", missingSourceMods.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))}. Galathil Output will be rebuilt without them.");

        var duplicateTargets = assignments.GroupBy(a => NormalizeFormKey(a.TargetFormKey), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateTargets is not null)
            throw new InvalidOperationException($"More than one look is assigned to {duplicateTargets.First().TargetName}. Remove one assignment before creating the output.");
        var sourceKeys = assignments.Select(a => NormalizeFormKey(a.SourceFormKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateOutfitTargets = outfitAssignments.GroupBy(a => NormalizeFormKey(a.TargetFormKey), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateOutfitTargets is not null)
            throw new InvalidOperationException($"More than one outfit is assigned to {duplicateOutfitTargets.First().TargetName}. Remove one outfit assignment before creating the output.");

        foreach (var outfitAssignment in outfitAssignments)
            ValidateOutfitAssignment(outfitAssignment, activePlugins, pluginPaths);

        var sourcePluginReadPaths = new Dictionary<string, string>(pluginPaths, StringComparer.OrdinalIgnoreCase);
        var selectedSourcePluginPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments)
        {
            if (!modOrder.Any(m => m.Enabled && m.Name.Equals(assignment.SourceMod, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"The source mod '{assignment.SourceMod}' is disabled in this MO2 profile.");
            var sourceFolder = Path.GetFullPath(Path.Combine(modsPath, assignment.SourceMod));
            if (!IsChildPath(modsPath, sourceFolder))
                throw new InvalidOperationException($"The source mod folder '{assignment.SourceMod}' is outside the MO2 mods folder.");
            var sourcePluginPath = PluginFileAccess.FindInModFolder(sourceFolder, assignment.SourcePlugin);
            if (sourcePluginPath is null)
                throw new InvalidOperationException($"Could not find {assignment.SourcePlugin} inside the selected MO2 mod '{assignment.SourceMod}'.");
            sourcePluginPath = Path.GetFullPath(sourcePluginPath);
            if (!IsChildPath(sourceFolder, sourcePluginPath))
                throw new InvalidOperationException($"The plugin path for {assignment.SourcePlugin} is outside the selected MO2 mod folder.");
            if (activePlugins.Contains(assignment.SourcePlugin) &&
                (!pluginPaths.TryGetValue(assignment.SourcePlugin, out var activePath) ||
                 !Path.GetFullPath(activePath).Equals(sourcePluginPath, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Another mod is overriding {assignment.SourcePlugin}. Select the active MO2 winner for this replacer.");
            if (selectedSourcePluginPaths.TryGetValue(assignment.SourcePlugin, out var existingSourcePath) &&
                !Path.GetFullPath(existingSourcePath).Equals(sourcePluginPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"More than one selected mod contains {assignment.SourcePlugin}. Choose one source mod for that plugin.");
            selectedSourcePluginPaths[assignment.SourcePlugin] = sourcePluginPath;
            sourcePluginReadPaths[assignment.SourcePlugin] = sourcePluginPath;
        }

        var outputFolder = Path.GetFullPath(Path.Combine(modsPath, OutputModName));
        if (!IsChildPath(modsPath, outputFolder)) throw new InvalidOperationException("The Galathil Output folder must be inside the MO2 mods folder.");
        var manifestPath = Path.Combine(outputFolder, OutputManifestName);
        if (Directory.Exists(outputFolder) && Directory.GetFileSystemEntries(outputFolder).Length > 0 && !File.Exists(manifestPath))
            throw new InvalidOperationException($"'{OutputModName}' already contains files that Galathil does not own. Rename or move that MO2 mod folder before creating the managed output.");
        var previousManifest = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<GalathilOutputManifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidDataException("The saved Galathil Output plan is empty or damaged.")
            : null;
        var sourcePlugins = assignments.Select(a => a.SourcePlugin).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var priorDisabledPlugins = (previousManifest?.DisabledSourcePlugins ?? Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availablePriorDisabledPlugins = priorDisabledPlugins.Where(plugin => IsPluginAvailable(
            modsPath, dataPath, modOrder, activePlugins, pluginPaths, plugin)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forgottenSourcePlugins = priorDisabledPlugins.Where(plugin => !activePlugins.Contains(plugin) &&
            !availablePriorDisabledPlugins.Contains(plugin)).ToArray();
        var previouslyManagedDisabled = availablePriorDisabledPlugins;
        if (forgottenSourcePlugins.Length > 0)
            warnings.Add($"Removed stale profile entries for deleted source plugins: {string.Join(", ", forgottenSourcePlugins)}.");
        var outputMasterOrder = activePluginOrder.ToList();
        foreach (var sourcePlugin in sourcePlugins.Where(name => !activePlugins.Contains(name)))
        {
            var savedPosition = previousManifest?.SourcePluginLoadOrder is { } positions && positions.TryGetValue(sourcePlugin, out var index)
                ? Math.Clamp(index, 0, outputMasterOrder.Count)
                : outputMasterOrder.Count;
            outputMasterOrder.Insert(savedPosition, sourcePlugin);
        }
        var allowedOutputMasters = new HashSet<string>(activePlugins, StringComparer.OrdinalIgnoreCase);
        allowedOutputMasters.UnionWith(previouslyManagedDisabled);

        var requestedKeys = assignments.SelectMany(a => new[] { a.SourceFormKey, a.TargetFormKey })
            .Concat(outfitAssignments.Select(a => a.TargetFormKey))
            .Select(NormalizeFormKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var npcChains = requestedKeys.Count == 0
            ? new Dictionary<string, List<PluginNpc>>(StringComparer.OrdinalIgnoreCase)
            : ReadNpcChains(activePluginOrder, pluginPaths, requestedKeys);
        foreach (var outfitAssignment in outfitAssignments)
            _ = GetChain(npcChains, NormalizeFormKey(outfitAssignment.TargetFormKey), outfitAssignment.TargetName);
        var prepared = new List<SharedPreparedAssignment>();
        var assetResolver = new FaceGenAssetResolver(modsPath, dataPath, modOrder, pluginPaths);
        var previousFaceGen = IndexManagedFaceGenFiles(modsPath, modOrder, previousManifest?.FaceGenLinks, warnings);

        foreach (var assignment in assignments)
        {
            var sourcePluginPath = sourcePluginReadPaths[assignment.SourcePlugin];
            var normalizedSourceKey = NormalizeFormKey(assignment.SourceFormKey);
            List<PluginNpc> sourceChain = npcChains.TryGetValue(normalizedSourceKey, out var activeSourceChain) ? activeSourceChain : [];
            var sourceIndex = sourceChain.FindIndex(r => r.Plugin.Equals(assignment.SourcePlugin, StringComparison.OrdinalIgnoreCase));
            var sourcePluginActive = activePlugins.Contains(assignment.SourcePlugin);
            Npc source;
            Npc beforeSource;
            if (sourcePluginActive)
            {
                if (sourceIndex < 0)
                    throw new InvalidOperationException($"Could not find {assignment.SourceName} in active plugin {assignment.SourcePlugin}.");
                var laterOverride = sourceChain.Skip(sourceIndex + 1).FirstOrDefault(r => !IsGalathilOutputPlugin(r.Plugin));
                if (laterOverride is not null)
                    throw new InvalidOperationException($"{assignment.SourceName} is overwritten by '{laterOverride.Plugin}' after '{assignment.SourcePlugin}'. Select the active winning replacer.");
                source = sourceChain[sourceIndex].Record;
                beforeSource = sourceChain.Take(sourceIndex).LastOrDefault(r => !IsGalathilOutputPlugin(r.Plugin))?.Record ?? source;
            }
            else
            {
                using var sourcePluginLease = PluginFileAccess.Open(assignment.SourcePlugin, sourcePluginPath);
                var sourcePluginMod = sourcePluginLease.Mod;
                source = sourcePluginMod.Npcs.FirstOrDefault(n => FormKeyText(n.FormKey).Equals(normalizedSourceKey, StringComparison.OrdinalIgnoreCase))?.DeepCopy()
                    ?? throw new InvalidOperationException($"{assignment.SourceName} is not present in {assignment.SourcePlugin}.");
                beforeSource = IsAddedNpcFormKey(assignment.SourceFormKey, assignment.SourcePlugin)
                    ? source
                    : sourceChain.LastOrDefault(r => !IsGalathilOutputPlugin(r.Plugin))?.Record
                        ?? throw new InvalidOperationException($"Could not find the earlier active NPC record needed for {assignment.SourceName}.");
            }
            var normalizedTargetKey = NormalizeFormKey(assignment.TargetFormKey);
            var targetChain = GetChain(npcChains, normalizedTargetKey, assignment.TargetName);
            // An incoming look must start from the recipient's own record, even when
            // its old replacer is donating a look elsewhere in this batch.
            var target = sourceKeys.Contains(normalizedTargetKey)
                ? targetChain.LastOrDefault(r => !IsGalathilOutputPlugin(r.Plugin) &&
                    !sourcePlugins.Contains(r.Plugin, StringComparer.OrdinalIgnoreCase))?.Record
                    ?? throw new InvalidOperationException($"{assignment.TargetName} needs a recipient record outside the source plugins being replaced.")
                : targetChain[^1].Record;
            var appearance = source;
            if (!string.IsNullOrWhiteSpace(assignment.AppearanceFormKey))
            {
                var appearancePlugin = assignment.AppearancePlugin ?? assignment.SourcePlugin;
                if (!sourcePluginReadPaths.TryGetValue(appearancePlugin, out var appearancePath))
                    throw new InvalidOperationException($"The saved appearance for {assignment.SourceName} refers to missing plugin {appearancePlugin}.");
                using var appearancePluginLease = PluginFileAccess.Open(appearancePlugin, appearancePath);
                var appearancePluginMod = appearancePluginLease.Mod;
                appearance = appearancePluginMod.Npcs.FirstOrDefault(n => FormKeyText(n.FormKey).Equals(NormalizeFormKey(assignment.AppearanceFormKey), StringComparison.OrdinalIgnoreCase))?.DeepCopy()
                    ?? throw new InvalidOperationException($"The saved appearance for {assignment.SourceName} is not present in {appearancePlugin}. Restore the source mod's Galathil backup first.");
            }

            var copiedSkin = copySkin && assignment.HasCustomSkin || copyBody && assignment.HasCustomBody;
            var hasNewOutfit = assignment.HasNewOutfit || (appearance.DefaultOutfit is { IsNull: false } &&
                appearance.DefaultOutfit.FormKeyNullable != beforeSource.DefaultOutfit?.FormKeyNullable);
            var appearanceMod = assignment.AppearanceMod ?? assignment.SourceMod;
            var previousTarget = previousManifest?.Assignments.FirstOrDefault(previous =>
                previous.SourcePlugin.Equals(assignment.SourcePlugin, StringComparison.OrdinalIgnoreCase) &&
                NormalizeFormKey(previous.SourceFormKey).Equals(NormalizeFormKey(assignment.SourceFormKey), StringComparison.OrdinalIgnoreCase));
            var previousFaceGenTarget = previousTarget is null
                ? (FormKey?)null
                : ParseOutfitFormKey(previousTarget.TargetFormKey, previousTarget.TargetName);
            var sourceFaceGen = assetResolver.ReadSharedOutputFaceGen(appearanceMod, appearance.FormKey, previousFaceGenTarget, previousFaceGen, warnings);

            if (!IsFemale(appearance).Equals(IsFemale(target)))
                warnings.Add($"Galathil will change {assignment.TargetName}'s sex to match {assignment.SourceName}; check voice, animations, and behavior in game.");
            if (appearance.Race.FormKey != target.Race.FormKey)
                warnings.Add($"Galathil will change {assignment.TargetName}'s race to match {assignment.SourceName}; this can affect race-based behavior.");
            if (!copiedSkin && (assignment.HasCustomSkin || assignment.HasCustomBody))
                warnings.Add($"{assignment.TargetName} keeps its current skin/body. Select Skin or Body to transfer the replacer's unique assets.");
            if (assignment.HasCustomSkin && assignment.HasCustomBody && copySkin != copyBody)
                warnings.Add($"{assignment.SourceName}'s skin textures and body mesh use the same skin record, so both may transfer together.");
            if (hasNewOutfit && !replaceOutfit && !outfitAssignments.Any(o => NormalizeFormKey(o.TargetFormKey).Equals(NormalizeFormKey(assignment.TargetFormKey), StringComparison.OrdinalIgnoreCase)))
                warnings.Add($"{assignment.TargetName} keeps its current outfit because Replace Outfit was not selected.");

            var copyRace = changeTargetRace && appearance.Race.FormKey != target.Race.FormKey;
            var copyGender = changeTargetRace && !IsFemale(appearance).Equals(IsFemale(target));
            prepared.Add(new SharedPreparedAssignment(assignment, source, appearance, beforeSource, target,
                sourceFaceGen, copyRace, copyGender, copiedSkin, replaceOutfit && hasNewOutfit));
        }

        var stageFolder = Path.Combine(modsPath, ".galathil-output-stage-" + Guid.NewGuid().ToString("N"));
        if (!IsChildPath(modsPath, stageFolder)) throw new InvalidOperationException("Could not create Galathil Output staging folder inside the MO2 mods directory.");
        Directory.CreateDirectory(stageFolder);
        var pluginNames = new List<string>();
        var transferredFaceGen = new List<(SharedPreparedAssignment Item, FaceGenFiles Files)>();
        var outputMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var shardCapacity = (int)(LastLightObjectId - FirstLightObjectId + 1);
            var recordSourcePlugins = sourcePlugins
                .Concat(assignments.Select(a => a.AppearancePlugin).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!))
                .Concat(outfitAssignments.Select(o => o.OutfitPlugin))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            using var appearanceRecordLibrary = new AppearanceRecordLibrary(recordSourcePlugins, sourcePluginReadPaths);
            var copyableRecordsByAssignment = prepared.ToDictionary(item => item,
                item => appearanceRecordLibrary.GetCopyableClosure(AppearanceRecordRoots(item, outfitAssignments,
                    recordSourcePlugins.ToHashSet(StringComparer.OrdinalIgnoreCase))));
            var shardAssignmentsList = BuildSharedOutputShards(prepared, copyableRecordsByAssignment, shardCapacity);
            var outputRules = new List<SharedOutputRule>();
            var copiedOutfitKeys = new HashSet<FormKey>();
            var outfitAssetMarkerLinks = new List<(string ModName, string PluginName)>();
            for (var shardIndex = 0; shardIndex < shardAssignmentsList.Count; shardIndex++)
            {
                var shard = shardIndex + 1;
                var pluginName = OutputPluginName(shard);
                pluginNames.Add(pluginName);
                var output = new SkyrimMod(ModKey.FromFileName(pluginName), SkyrimRelease.SkyrimSE);
                output.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
                var shardAssignments = shardAssignmentsList[shardIndex].ToArray();
                uint localId = FirstLightObjectId;
                var npcOverrides = new List<(SharedPreparedAssignment Item, Npc Override)>();
                foreach (var item in shardAssignments)
                {
                    // Galathil Output only owns recipient records. Let the donor resolve through
                    // the normal plugin chain instead of pinning an earlier record in this output.
                    var targetOverride = output.Npcs.GetOrAddAsOverride(item.Target);
                    var targetFaceGen = RekeyFaceGen(item.SourceFaceGen, item.Source.FormKey, item.Appearance.FormKey, item.Target.FormKey);
                    transferredFaceGen.Add((item, targetFaceGen));
                    npcOverrides.Add((item, targetOverride));
                }

                var shardCopyableRecords = shardAssignments.SelectMany<SharedPreparedAssignment, FormKey>(item => (IEnumerable<FormKey>)copyableRecordsByAssignment[item])
                    .Distinct().OrderBy(key => key.ModKey.FileName.ToString(), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(key => key.ID).ToArray();
                var copiedRecords = appearanceRecordLibrary.CopyToOutput(shardCopyableRecords, output, ref localId);
                foreach (var (item, npcOverride) in npcOverrides)
                {
                    var appearanceOutfit = item.Appearance.DeepCopy();
                    appearanceOutfit.RemapLinks(copiedRecords);
                    CopyAppearance(npcOverride, appearanceOutfit, item.CopyRace, item.CopySkinArmor, includeOutfit: false);
                    if (item.CopyGender)
                        npcOverride.Configuration.Flags = (npcOverride.Configuration.Flags & ~NpcConfiguration.Flag.Female) |
                            (appearanceOutfit.Configuration.Flags & NpcConfiguration.Flag.Female);

                    var selectedOutfit = outfitAssignments.FirstOrDefault(o =>
                        NormalizeFormKey(o.TargetFormKey).Equals(NormalizeFormKey(item.Assignment.TargetFormKey), StringComparison.OrdinalIgnoreCase));
                    var transferOutfit = selectedOutfit is not null
                        ? RemapCopiedFormKey(ParseOutfitFormKey(selectedOutfit.OutfitFormKey, selectedOutfit.OutfitName), copiedRecords)
                        : item.CopyOutfit ? appearanceOutfit.DefaultOutfit?.FormKeyNullable : null;
                    if (selectedOutfit is not null || item.CopyOutfit)
                        npcOverride.DefaultOutfit = new FormLinkNullable<IOutfitGetter>(transferOutfit);

                    outputRules.Add(new SharedOutputRule(item, pluginName, copiedRecords));
                    foreach (var outfit in outfitAssignments.Where(o =>
                                 NormalizeFormKey(o.TargetFormKey).Equals(NormalizeFormKey(item.Assignment.TargetFormKey), StringComparison.OrdinalIgnoreCase)))
                    {
                        var outfitKey = ParseOutfitFormKey(outfit.OutfitFormKey, outfit.OutfitName);
                        if (copiedRecords.ContainsKey(outfitKey)) copiedOutfitKeys.Add(outfitKey);
                        outfitAssetMarkerLinks.Add((outfit.OutfitModName, pluginName));
                    }
                }

                ValidateLightPlugin(output, new HashSet<FormKey>());
                var pluginPath = Path.Combine(stageFolder, pluginName);
                output.BeginWrite.ToPath(new ModPath(output.ModKey, pluginPath))
                    .WithNoLoadOrder()
                    .WithMastersListContent(MastersListContentOption.Iterate)
                    .WithMastersListOrdering(outputMasterOrder.Select(name => ModKey.FromFileName(name)))
                    .Write();
                using var check = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
                if (!check.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small))
                    throw new InvalidDataException($"{pluginName} was not saved as an ESPFE plugin.");
                var unresolvedMasters = check.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString())
                    .Where(name => !allowedOutputMasters.Contains(name)).ToArray();
                if (unresolvedMasters.Length > 0)
                {
                    var disabledSource = unresolvedMasters.FirstOrDefault(name => sourcePlugins.Contains(name, StringComparer.OrdinalIgnoreCase));
                    if (disabledSource is not null)
                        throw new InvalidOperationException($"Galathil Output needs {disabledSource} as a master, but that ESP is disabled in MO2. Enable it in this profile and apply again.");
                    throw new InvalidDataException($"{pluginName} requires inactive or missing masters: {string.Join(", ", unresolvedMasters)}.");
                }
                var currentMasters = check.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToArray();
                var masterIndexes = currentMasters.Select(name => outputMasterOrder.IndexOf(name)).ToArray();
                if (masterIndexes.Any(index => index < 0) || !masterIndexes.SequenceEqual(masterIndexes.OrderBy(index => index)))
                    throw new InvalidDataException($"{pluginName} has missing or incorrectly ordered masters for the selected profile.");
                foreach (var master in currentMasters) outputMasters.Add(master);
                if (check.Npcs.Any(n => n.IsDeleted || (n.FormKey.ModKey == output.ModKey &&
                    (n.FormKey.ID < FirstLightObjectId || n.FormKey.ID > LastLightObjectId))))
                    throw new InvalidDataException($"{pluginName} has deleted NPCs or local IDs outside the extended ESL range.");
            }

            var lookTargetKeys = prepared.Select(item => NormalizeFormKey(item.Assignment.TargetFormKey))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outfitOnlyAssignments = outfitAssignments
                .Where(outfit => !lookTargetKeys.Contains(NormalizeFormKey(outfit.TargetFormKey))).ToArray();
            if (outfitOnlyAssignments.Length > 0)
            {
                var outfitRecords = outfitOnlyAssignments.ToDictionary(outfit => outfit,
                    outfit => appearanceRecordLibrary.GetCopyableClosure([
                        ParseOutfitFormKey(outfit.OutfitFormKey, outfit.OutfitName)]));
                var outfitChunks = new List<List<OutfitAssignment>>();
                var currentOutfits = new List<OutfitAssignment>();
                var currentRecords = new HashSet<FormKey>();
                foreach (var outfit in outfitOnlyAssignments)
                {
                    var outfitKeys = outfitRecords[outfit];
                    var added = outfitKeys.Count(key => !currentRecords.Contains(key));
                    if (currentOutfits.Count > 0 && currentRecords.Count + added > shardCapacity)
                    {
                        outfitChunks.Add(currentOutfits);
                        currentOutfits = [];
                        currentRecords = [];
                        added = outfitKeys.Count;
                    }
                    if (outfitKeys.Count > shardCapacity)
                        throw new InvalidOperationException($"{outfit.OutfitName} needs more linked records than one ESL output can hold.");
                    currentOutfits.Add(outfit);
                    currentRecords.UnionWith(outfitKeys);
                }
                if (currentOutfits.Count > 0) outfitChunks.Add(currentOutfits);

                foreach (var outfitChunk in outfitChunks)
                {
                    var pluginName = OutputPluginName(pluginNames.Count + 1);
                    pluginNames.Add(pluginName);
                    var output = new SkyrimMod(ModKey.FromFileName(pluginName), SkyrimRelease.SkyrimSE);
                    output.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
                    uint localId = FirstLightObjectId;
                    var chunkKeys = outfitChunk.SelectMany(outfit => outfitRecords[outfit]).Distinct()
                        .OrderBy(key => key.ModKey.FileName.ToString(), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(key => key.ID).ToArray();
                    var copiedRecords = appearanceRecordLibrary.CopyToOutput(chunkKeys, output, ref localId);
                    foreach (var outfit in outfitChunk)
                    {
                        var target = GetChain(npcChains, NormalizeFormKey(outfit.TargetFormKey), outfit.TargetName)[^1].Record;
                        var targetOverride = output.Npcs.GetOrAddAsOverride(target);
                        var outfitKey = ParseOutfitFormKey(outfit.OutfitFormKey, outfit.OutfitName);
                        targetOverride.DefaultOutfit = new FormLinkNullable<IOutfitGetter>(RemapCopiedFormKey(outfitKey, copiedRecords));
                        if (copiedRecords.ContainsKey(outfitKey)) copiedOutfitKeys.Add(outfitKey);
                        outfitAssetMarkerLinks.Add((outfit.OutfitModName, pluginName));
                    }

                    ValidateLightPlugin(output, new HashSet<FormKey>());
                    var pluginPath = Path.Combine(stageFolder, pluginName);
                    output.BeginWrite.ToPath(new ModPath(output.ModKey, pluginPath))
                        .WithNoLoadOrder()
                        .WithMastersListContent(MastersListContentOption.Iterate)
                        .WithMastersListOrdering(outputMasterOrder.Select(name => ModKey.FromFileName(name)))
                        .Write();
                    using var check = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
                    var unresolvedMasters = check.ModHeader.MasterReferences.Select(master => master.Master.FileName.ToString())
                        .Where(name => !allowedOutputMasters.Contains(name)).ToArray();
                    if (unresolvedMasters.Length > 0)
                        throw new InvalidDataException($"{pluginName} requires inactive or missing masters: {string.Join(", ", unresolvedMasters)}.");
                    var currentMasters = check.ModHeader.MasterReferences.Select(master => master.Master.FileName.ToString()).ToArray();
                    var masterIndexes = currentMasters.Select(name => outputMasterOrder.IndexOf(name)).ToArray();
                    if (masterIndexes.Any(index => index < 0) || !masterIndexes.SequenceEqual(masterIndexes.OrderBy(index => index)))
                        throw new InvalidDataException($"{pluginName} has missing or incorrectly ordered masters for the selected profile.");
                    foreach (var master in currentMasters) outputMasters.Add(master);
                    if (!check.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small))
                        throw new InvalidDataException($"{pluginName} was not saved as an ESPFE plugin.");
                }
            }

            // The library caches source plugin readers. Close them before install hides source ESPs.
            appearanceRecordLibrary.Dispose();

            var assetLinks = assignments.Select(a => new GalathilAssetLink(a.SourceMod, a.SourcePlugin))
                .Concat(outfitAssignments.Select(o => new GalathilAssetLink(o.OutfitModName, o.OutfitPlugin)))
                .DistinctBy(link => AssetLinkKey(link.SourceMod, link.SourcePlugin), StringComparer.OrdinalIgnoreCase).ToArray();
            var oldConfigPaths = FindMigratedLegacyConfigs(modsPath, assetLinks, assignments, warnings);
            var legacyConfigBlocks = FindUnmigratedLegacyConfigPlugins(modsPath, assetLinks, assignments);
            var disabledSourcePlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourcePlugin in sourcePlugins)
            {
                var hasAddedNpcDonor = assignments.Any(a => a.SourcePlugin.Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase) &&
                    IsAddedNpcFormKey(a.SourceFormKey, a.SourcePlugin));
                var dependent = FindActivePluginDependent(sourcePlugin, activePlugins, pluginPaths, pluginNames);
                var requiredByOutput = outputMasters.Contains(sourcePlugin);
                var requiredByOutfit = outfitAssignments.Any(o =>
                {
                    if (!o.OutfitPlugin.Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase) &&
                        !o.OutfitWinningPlugin.Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase)) return false;
                    var outfitKey = ParseOutfitFormKey(o.OutfitFormKey, o.OutfitName);
                    return !copiedOutfitKeys.Contains(outfitKey);
                });
                var hasUnmigratedConfig = legacyConfigBlocks.Contains(sourcePlugin);
                if (!activePlugins.Contains(sourcePlugin))
                {
                    if (previouslyManagedDisabled.Contains(sourcePlugin))
                    {
                        if (!requiredByOutput && !requiredByOutfit && dependent is null && !hasUnmigratedConfig)
                            disabledSourcePlugins.Add(sourcePlugin);
                        else
                        {
                            if (hasAddedNpcDonor)
                                throw new InvalidOperationException($"Galathil cannot use this added follower NPC without disabling {sourcePlugin}, but {(requiredByOutput ? "Galathil Output needs it as a master" : requiredByOutfit ? "the selected outfit still uses one of its records" : dependent is not null ? $"{dependent} requires it" : "its old Galathil config still has routes")}. Resolve that dependency first so the follower does not remain in game.");
                            warnings.Add($"Re-enabled {sourcePlugin} because {(requiredByOutput ? "Galathil Output uses records from it" : requiredByOutfit ? "an outfit assignment uses a record from it" : dependent is not null ? $"{dependent} requires it" : "its old Galathil config still has routes")}. Its asset mod remains enabled.");
                        }
                    }
                    else if (requiredByOutput || requiredByOutfit || dependent is not null || hasUnmigratedConfig)
                    {
                        var reason = requiredByOutput ? "Galathil Output needs it as a master" : requiredByOutfit ? "a selected outfit comes from it" : dependent is not null ? $"{dependent} requires it" : "it still contains SkyPatcher routes";
                        throw new InvalidOperationException($"{sourcePlugin} is disabled in MO2, but {reason}. Enable it in this profile and apply again.");
                    }
                    continue;
                }
                if (!requiredByOutput && !requiredByOutfit && dependent is null && !hasUnmigratedConfig)
                    disabledSourcePlugins.Add(sourcePlugin);
                else
                {
                    var reason = requiredByOutput ? "Galathil Output uses records from it" : requiredByOutfit ? "a selected outfit comes from it" : dependent is not null ? $"{dependent} requires it" : "its old Galathil config has routes not yet imported";
                    if (hasAddedNpcDonor)
                        throw new InvalidOperationException($"Galathil cannot use this added follower NPC without disabling {sourcePlugin}, but {reason}. Resolve that dependency first so the follower does not remain in game.");
                    warnings.Add($"Kept {sourcePlugin} enabled because {reason}. Its mod still provides the linked assets.");
                }
            }
            foreach (var sourcePlugin in disabledSourcePlugins.Where(plugin => assignments.Any(assignment =>
                         assignment.SourcePlugin.Equals(plugin, StringComparison.OrdinalIgnoreCase) &&
                         IsAddedNpcFormKey(assignment.SourceFormKey, assignment.SourcePlugin))))
                warnings.Add($"Disabled {sourcePlugin} in this MO2 profile so the donor follower is removed. This also removes its other NPCs, quests, dialogue, and plugin records; its asset mod remains enabled.");

            var restoreSourcePlugins = availablePriorDisabledPlugins
                .Where(name => !disabledSourcePlugins.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var sourcePluginLoadOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (previousManifest?.SourcePluginLoadOrder is { } savedPluginOrder)
                foreach (var (name, index) in savedPluginOrder) sourcePluginLoadOrder[name] = index;
            foreach (var sourcePlugin in disabledSourcePlugins)
            {
                if (sourcePluginLoadOrder.ContainsKey(sourcePlugin)) continue;
                var position = activePluginOrder.IndexOf(sourcePlugin);
                if (position >= 0) sourcePluginLoadOrder[sourcePlugin] = position;
            }
            var plannedFaceGen = PlanTransferredFaceGen(modsPath, transferredFaceGen, previousFaceGen, warnings);
            foreach (var item in prepared)
                assetResolver.EnsureRecipientFaceGenWillWin(item.Assignment.AppearanceMod ?? item.Assignment.SourceMod,
                    item.Target.FormKey, item.Assignment.TargetName, plannedFaceGen);
            var assetMarkerFiles = BuildAssetMarkerFiles(pluginNames, outputRules, outfitAssetMarkerLinks);
            var faceGenLinks = transferredFaceGen.Select(transfer => new GalathilFaceGenLink(
                transfer.Item.Assignment.AppearanceMod ?? transfer.Item.Assignment.SourceMod,
                transfer.Item.Assignment.SourcePlugin,
                FormKeyText(transfer.Item.Target.FormKey),
                Sha256Hex(transfer.Files.Mesh),
                Sha256Hex(transfer.Files.Tint))).ToArray();
            var manifest = new GalathilOutputManifest(1, copySkin, copyBody, replaceOutfit, assignments.ToArray(),
                disabledSourcePlugins.ToArray(), assetLinks, sourcePluginLoadOrder, null, outfitAssignments.ToArray(), assetMarkerFiles, faceGenLinks);
            File.WriteAllText(Path.Combine(stageFolder, OutputManifestName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            WriteOutputAssetMarkers(stageFolder, pluginNames, outputRules);

            var backupFolder = Path.Combine(AppContext.BaseDirectory, "Backups", "Galathil Output", DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(backupFolder);
            var backupPath = Path.Combine(backupFolder, "previous-output.zip");
            if (Directory.Exists(outputFolder)) ZipFile.CreateFromDirectory(outputFolder, backupPath, CompressionLevel.Optimal, includeBaseDirectory: true);
            else File.WriteAllText(backupPath + ".txt", "No prior Galathil Output mod existed.");

            var markerChanges = UpdateAssetModMarkers(modsPath,
                previousManifest?.AssetLinks ?? Array.Empty<GalathilAssetLink>(), assetLinks, previousManifest?.AssetMarkerFiles,
                assetMarkerFiles, backupFolder);
            var removedConfigChanges = new List<AssetMarkerChange>();
            var consumedFaceGenChanges = new List<AssetMarkerChange>();
            try
            {
                removedConfigChanges = BackupAndRemoveLegacyConfigs(oldConfigPaths, modsPath, backupFolder);
                consumedFaceGenChanges = ApplyTransferredFaceGenToAssetMods(modsPath, plannedFaceGen, backupFolder);
                var visibilityPluginNames = disabledSourcePlugins.Concat(restoreSourcePlugins)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var sourcePluginFilePaths = ResolveSourcePluginFilePaths(modsPath, modOrder, visibilityPluginNames,
                    selectedSourcePluginPaths, pluginPaths, assetLinks, previousManifest?.AssetLinks ?? Array.Empty<GalathilAssetLink>());
                var profilePluginPaths = new Dictionary<string, string>(pluginPaths, StringComparer.OrdinalIgnoreCase);
                foreach (var (plugin, path) in sourcePluginFilePaths) profilePluginPaths[plugin] = path;
                InstallGalathilOutput(stageFolder, outputFolder, profilePath, pluginNames, pluginPaths, modsPath,
                    disabledSourcePlugins, restoreSourcePlugins, forgottenSourcePlugins, profilePluginPaths, sourcePluginLoadOrder, sourcePluginFilePaths);
            }
            catch
            {
                RollbackAssetModMarkers(consumedFaceGenChanges);
                RollbackAssetModMarkers(removedConfigChanges);
                RollbackAssetModMarkers(markerChanges);
                throw;
            }
            var finalPluginPath = pluginNames.Count == 0 ? null : Path.Combine(outputFolder, pluginNames[0]);
            return new AppearanceGenerationResult(OutputModName, outputFolder, finalPluginPath, null,
                backupFolder, warnings.Distinct(StringComparer.Ordinal).ToArray(), assignments.Count, outfitAssignments.Count);
        }
        catch
        {
            if (Directory.Exists(stageFolder) && IsChildPath(modsPath, stageFolder)) Directory.Delete(stageFolder, recursive: true);
            throw;
        }
    }

    private static void InstallGalathilOutput(string stageFolder, string outputFolder, string profilePath,
        IReadOnlyList<string> pluginNames, IReadOnlyDictionary<string, string> pluginPaths, string modsPath,
        IReadOnlySet<string> disableSourcePlugins, IReadOnlyList<string> restoreSourcePlugins,
        IReadOnlyList<string> forgetSourcePlugins,
        IReadOnlyDictionary<string, string> profilePluginPaths,
        IReadOnlyDictionary<string, int> sourcePluginLoadOrder,
        IReadOnlyDictionary<string, string> sourcePluginFilePaths)
    {
        foreach (var name in pluginNames)
            if (pluginPaths.TryGetValue(name, out var path) && !Path.GetFullPath(path).StartsWith(outputFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Another mod already provides {name}. Disable or rename that plugin before using Galathil Output.");

        var oldFolder = outputFolder + ".galathil-old-" + Guid.NewGuid().ToString("N");
        var movedOld = false;
        var pluginFileRenames = new List<PluginFileRename>();
        try
        {
            if (Directory.Exists(outputFolder))
            {
                Directory.Move(outputFolder, oldFolder);
                movedOld = true;
            }
            Directory.Move(stageFolder, outputFolder);
            ApplySourcePluginVisibility(disableSourcePlugins, restoreSourcePlugins, sourcePluginFilePaths, pluginFileRenames);
            UpdateGalathilOutputProfile(profilePath, pluginNames, disableSourcePlugins, restoreSourcePlugins, forgetSourcePlugins, profilePluginPaths, sourcePluginLoadOrder);
        }
        catch (Exception originalError)
        {
            Exception? visibilityRollbackError = null;
            try { RollbackPluginFileRenames(pluginFileRenames); }
            catch (Exception ex) { visibilityRollbackError = ex; }
            if (Directory.Exists(outputFolder) && !Directory.Exists(stageFolder)) Directory.Move(outputFolder, stageFolder);
            if (movedOld && Directory.Exists(oldFolder) && !Directory.Exists(outputFolder)) Directory.Move(oldFolder, outputFolder);
            if (visibilityRollbackError is not null)
                throw new AggregateException("Galathil could not finish the output update and could not fully restore the source ESP visibility changes.", originalError, visibilityRollbackError);
            throw;
        }
        if (movedOld)
        {
            try { Directory.Delete(oldFolder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record PluginFileRename(string FromPath, string ToPath);

    private static Dictionary<string, string> ResolveSourcePluginFilePaths(
        string modsPath,
        IReadOnlyList<GalathilModEntry> modOrder,
        IReadOnlyList<string> plugins,
        IReadOnlyDictionary<string, string> selectedSourcePluginPaths,
        IReadOnlyDictionary<string, string> activePluginPaths,
        IReadOnlyList<GalathilAssetLink> currentAssetLinks,
        IReadOnlyList<GalathilAssetLink> previousAssetLinks)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allLinks = currentAssetLinks.Concat(previousAssetLinks)
            .Where(link => plugins.Contains(link.SourcePlugin, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(link => AssetLinkKey(link.SourceMod, link.SourcePlugin), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var plugin in plugins.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!PluginFileAccess.IsPluginName(plugin))
                throw new InvalidDataException($"Cannot hide or restore invalid plugin filename '{plugin}'.");

            if (selectedSourcePluginPaths.TryGetValue(plugin, out var selectedPath) && File.Exists(selectedPath))
            {
                EnsurePluginPathIsInModsFolder(modsPath, plugin, selectedPath);
                resolved[plugin] = Path.GetFullPath(selectedPath);
                continue;
            }

            var linkedCandidates = allLinks.Where(link => link.SourcePlugin.Equals(plugin, StringComparison.OrdinalIgnoreCase))
                .Select(link => Path.GetFullPath(Path.Combine(modsPath, link.SourceMod)))
                .Where(folder => IsChildPath(modsPath, folder) && Directory.Exists(folder))
                .Select(folder => PluginFileAccess.FindInModFolder(folder, plugin))
                .Where(path => path is not null).Select(path => Path.GetFullPath(path!))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (linkedCandidates.Length == 1)
            {
                resolved[plugin] = linkedCandidates[0];
                continue;
            }

            if (linkedCandidates.Length > 1)
            {
                var hiddenCandidates = linkedCandidates.Where(PluginFileAccess.IsHiddenFile).ToArray();
                if (hiddenCandidates.Length == 1)
                {
                    resolved[plugin] = hiddenCandidates[0];
                    continue;
                }
                if (activePluginPaths.TryGetValue(plugin, out var activePath) && linkedCandidates.Contains(Path.GetFullPath(activePath), StringComparer.OrdinalIgnoreCase))
                {
                    resolved[plugin] = Path.GetFullPath(activePath);
                    continue;
                }
                throw new InvalidOperationException($"Galathil found more than one copy of {plugin} in its linked asset mods and cannot safely hide or restore the right file.");
            }

            var installedCandidates = modOrder
                .Select(mod => Path.GetFullPath(Path.Combine(modsPath, mod.Name)))
                .Where(folder => IsChildPath(modsPath, folder) && Directory.Exists(folder))
                .Select(folder => PluginFileAccess.FindInModFolder(folder, plugin))
                .Where(path => path is not null).Select(path => Path.GetFullPath(path!))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (installedCandidates.Length == 1)
            {
                resolved[plugin] = installedCandidates[0];
                continue;
            }
            var uniqueHidden = installedCandidates.Where(PluginFileAccess.IsHiddenFile).ToArray();
            if (uniqueHidden.Length == 1)
            {
                resolved[plugin] = uniqueHidden[0];
                continue;
            }
            if (activePluginPaths.TryGetValue(plugin, out var activeInstalledPath) &&
                IsChildPath(modsPath, activeInstalledPath) && installedCandidates.Contains(Path.GetFullPath(activeInstalledPath), StringComparer.OrdinalIgnoreCase))
            {
                resolved[plugin] = Path.GetFullPath(activeInstalledPath);
                continue;
            }
            throw new FileNotFoundException($"Galathil could not identify a single MO2 mod file for {plugin} to hide or restore.");
        }
        return resolved;
    }

    private static void EnsurePluginPathIsInModsFolder(string modsPath, string plugin, string path)
    {
        if (!IsChildPath(modsPath, path) || !PluginFileAccess.TryGetPluginName(path, out var foundName) ||
            !foundName.Equals(plugin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Refusing to hide or restore {plugin} outside its MO2 mod folder: {path}");
    }

    private static void ApplySourcePluginVisibility(
        IReadOnlySet<string> disablePlugins,
        IReadOnlyList<string> restorePlugins,
        IReadOnlyDictionary<string, string> pluginPaths,
        ICollection<PluginFileRename> appliedRenames)
    {
        foreach (var plugin in disablePlugins)
        {
            var path = pluginPaths[plugin];
            if (PluginFileAccess.IsHiddenFile(path)) continue;
            var hiddenPath = path + PluginFileAccess.HiddenSuffix;
            if (File.Exists(hiddenPath))
                throw new IOException($"Cannot hide {plugin}: both the visible and .mohidden files already exist in its MO2 mod.");
            File.Move(path, hiddenPath);
            appliedRenames.Add(new PluginFileRename(path, hiddenPath));
        }

        foreach (var plugin in restorePlugins)
        {
            var path = pluginPaths[plugin];
            if (!PluginFileAccess.IsHiddenFile(path)) continue;
            var visiblePath = path[..^PluginFileAccess.HiddenSuffix.Length];
            if (File.Exists(visiblePath))
                throw new IOException($"Cannot restore {plugin}: both the visible and .mohidden files already exist in its MO2 mod.");
            File.Move(path, visiblePath);
            appliedRenames.Add(new PluginFileRename(path, visiblePath));
        }
    }

    private static void RollbackPluginFileRenames(IReadOnlyList<PluginFileRename> renames)
    {
        var errors = new List<Exception>();
        foreach (var rename in renames.Reverse())
        {
            try
            {
                if (!File.Exists(rename.ToPath)) continue;
                if (File.Exists(rename.FromPath))
                    throw new IOException($"Cannot restore {rename.FromPath}; another file now occupies that path.");
                File.Move(rename.ToPath, rename.FromPath);
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException("Could not restore all source ESP filenames after a failed Galathil output update.", errors);
    }

    private static string AssetLinkKey(string sourceMod, string sourcePlugin) => sourceMod + "|" + sourcePlugin;

    private static string SourceAssetMarkerName(GalathilAssetLink link) =>
        "Galathil Link - " + SafeFileStem(link.SourcePlugin) + ".txt";

    private static string OutputAssetMarkerName(int espNumber) => $"Galathil ESP {espNumber}.txt";

    private static Dictionary<string, IReadOnlyList<string>> BuildAssetMarkerFiles(
        IReadOnlyList<string> outputPlugins,
        IReadOnlyList<SharedOutputRule> outputRules,
        IReadOnlyList<(string ModName, string PluginName)> outfitAssetMarkerLinks)
    {
        var pluginNumbers = outputPlugins.Select((plugin, index) => (plugin, number: index + 1))
            .ToDictionary(pair => pair.plugin, pair => pair.number, StringComparer.OrdinalIgnoreCase);
        var numbersByMod = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in outputRules)
        {
            if (!pluginNumbers.TryGetValue(rule.PluginName, out var number))
                throw new InvalidDataException($"Could not find the output ESP number for {rule.PluginName}.");
            var sourceMod = rule.Item.Assignment.SourceMod;
            if (!numbersByMod.TryGetValue(sourceMod, out var numbers)) numbersByMod[sourceMod] = numbers = [];
            numbers.Add(number);
        }
        foreach (var link in outfitAssetMarkerLinks)
        {
            if (!pluginNumbers.TryGetValue(link.PluginName, out var number))
                throw new InvalidDataException($"Could not find the output ESP number for {link.PluginName}.");
            if (!numbersByMod.TryGetValue(link.ModName, out var numbers)) numbersByMod[link.ModName] = numbers = [];
            numbers.Add(number);
        }
        return numbersByMod.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Order().Select(OutputAssetMarkerName).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteOutputAssetMarkers(string stageFolder, IReadOnlyList<string> outputPlugins,
        IReadOnlyList<SharedOutputRule> outputRules)
    {
        for (var index = 0; index < outputPlugins.Count; index++)
        {
            var plugin = outputPlugins[index];
            var lines = outputRules.Where(rule => rule.PluginName.Equals(plugin, StringComparison.OrdinalIgnoreCase))
                .Select(rule => $"{rule.Item.Assignment.SourceName} -> {rule.Item.Assignment.TargetName}")
                .ToArray();
            var text = lines.Length == 0 ? "" : string.Join(Environment.NewLine, lines) + Environment.NewLine;
            File.WriteAllText(Path.Combine(stageFolder, OutputAssetMarkerName(index + 1)), text, new UTF8Encoding(false));
        }
    }

    private static List<AssetMarkerChange> UpdateAssetModMarkers(string modsPath,
        IReadOnlyList<GalathilAssetLink> previousLinks,
        IReadOnlyList<GalathilAssetLink> currentLinks,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? previousMarkerFiles,
        IReadOnlyDictionary<string, IReadOnlyList<string>> currentMarkerFiles,
        string backupFolder)
    {
        var changes = new List<AssetMarkerChange>();
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trackedPrevious = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var legacyMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (previousMarkerFiles is not null)
        {
            foreach (var (modName, markerNames) in previousMarkerFiles)
            foreach (var markerName in markerNames)
            {
                var resolvedModName = ResolvePreviousAssetModName(modsPath, modName, previousLinks, currentLinks);
                if (resolvedModName is not null)
                    trackedPrevious.Add(AssetMarkerPath(modsPath, resolvedModName, markerName));
            }
        }
        foreach (var link in previousLinks)
        {
            var resolvedModName = ResolvePreviousAssetModName(modsPath, link.SourceMod, previousLinks, currentLinks, link.SourcePlugin);
            if (resolvedModName is null) continue;
            var folder = SafeAssetModFolder(modsPath, resolvedModName);
            legacyMarkers.Add(Path.Combine(folder, SourceAssetMarkerName(link)));
        }
        foreach (var (modName, markerNames) in currentMarkerFiles)
        foreach (var markerName in markerNames)
            desired.Add(AssetMarkerPath(modsPath, modName, markerName));

        var paths = desired.Concat(trackedPrevious).Concat(legacyMarkers)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            foreach (var path in paths)
            {
                var isDesired = desired.Contains(path);
                var wasTracked = trackedPrevious.Contains(path);
                var isLegacy = legacyMarkers.Contains(path);
                byte[]? oldBytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if (isLegacy && oldBytes is not null &&
                    !Encoding.UTF8.GetString(oldBytes).StartsWith("; Generated by Galathil.", StringComparison.Ordinal))
                    throw new IOException($"Cannot safely remove a non-Galathil note file: {path}");
                if (isDesired && oldBytes is { Length: > 0 } && !wasTracked)
                    throw new IOException($"A non-empty file already exists where Galathil needs an empty asset note: {path}");

                changes.Add(new AssetMarkerChange(path, oldBytes));
                if (oldBytes is not null) BackupAssetMarker(path, oldBytes, backupFolder);
                if (isDesired) File.WriteAllBytes(path, Array.Empty<byte>());
                else if (File.Exists(path)) File.Delete(path);
            }
            return changes;
        }
        catch
        {
            RollbackAssetModMarkers(changes);
            throw;
        }
    }

    private static string? ResolvePreviousAssetModName(string modsPath, string previousModName,
        IReadOnlyList<GalathilAssetLink> previousLinks, IReadOnlyList<GalathilAssetLink> currentLinks,
        string? sourcePlugin = null)
    {
        var previousFolder = Path.GetFullPath(Path.Combine(modsPath, previousModName));
        if (!IsChildPath(modsPath, previousFolder))
            throw new InvalidDataException($"Previous asset mod name is outside the MO2 mods folder: {previousModName}");
        if (Directory.Exists(previousFolder)) return previousModName;

        var pluginNames = previousLinks.Where(link => link.SourceMod.Equals(previousModName, StringComparison.OrdinalIgnoreCase) &&
                (sourcePlugin is null || link.SourcePlugin.Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase)))
            .Select(link => link.SourcePlugin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pluginNames.Count == 0) return null;

        var currentFolders = currentLinks.Where(link => pluginNames.Contains(link.SourcePlugin))
            .Select(link => link.SourceMod).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name =>
            {
                var folder = Path.GetFullPath(Path.Combine(modsPath, name));
                if (!IsChildPath(modsPath, folder))
                    throw new InvalidDataException($"Current asset mod name is outside the MO2 mods folder: {name}");
                return Directory.Exists(folder);
            }).ToArray();
        return currentFolders.Length == 1 ? currentFolders[0] : null;
    }

    private static string AssetMarkerPath(string modsPath, string modName, string markerName)
    {
        if (!Path.GetFileName(markerName).Equals(markerName, StringComparison.Ordinal) ||
            !Regex.IsMatch(markerName, @"^Galathil ESP \d+\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException($"Invalid Galathil asset marker name: {markerName}");
        return Path.Combine(SafeAssetModFolder(modsPath, modName), markerName);
    }

    private static void BackupAssetMarker(string path, byte[] bytes, string backupFolder)
    {
        var backupFile = Path.Combine(backupFolder, "asset-markers",
            SafeFileStem(Path.GetFileName(Path.GetDirectoryName(path)) + "-" + Path.GetFileName(path)) + ".bak");
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.WriteAllBytes(backupFile, bytes);
    }
    private static string SafeAssetModFolder(string modsPath, string modName)
    {
        var folder = Path.GetFullPath(Path.Combine(modsPath, modName));
        if (!IsChildPath(modsPath, folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Linked asset mod folder is missing: {modName}");
        return folder;
    }

    private static void RollbackAssetModMarkers(IReadOnlyList<AssetMarkerChange> changes)
    {
        foreach (var change in changes.Reverse())
        {
            if (change.OriginalBytes is null)
            {
                if (File.Exists(change.Path)) File.Delete(change.Path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
                File.WriteAllBytes(change.Path, change.OriginalBytes);
            }
        }
    }

    private static HashSet<string> FindUnmigratedLegacyConfigPlugins(string modsPath,
        IReadOnlyList<GalathilAssetLink> links, IReadOnlyList<AppearanceAssignment> assignments)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            var path = LegacyConfigPath(modsPath, link);
            if (!File.Exists(path)) continue;
            var configCloneKeys = File.ReadLines(path).Select(line => Regex.Match(line, @"(?:^|:)copyVisualStyle=(?<key>[^:]+)", RegexOptions.IgnoreCase))
                .Where(match => match.Success).Select(match => NormalizeFormKey(match.Groups["key"].Value)).ToArray();
            var importedCloneKeys = assignments.Where(a => a.SourceMod.Equals(link.SourceMod, StringComparison.OrdinalIgnoreCase) &&
                    a.SourcePlugin.Equals(link.SourcePlugin, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(a.AppearancePlugin) && a.AppearancePlugin.Equals(link.SourcePlugin, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(a.AppearanceFormKey))
                .Select(a => NormalizeFormKey(a.AppearanceFormKey!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (configCloneKeys.Length == 0 || configCloneKeys.Any(key => !importedCloneKeys.Contains(key))) blocked.Add(link.SourcePlugin);
        }
        return blocked;
    }

    private static List<string> FindMigratedLegacyConfigs(string modsPath,
        IReadOnlyList<GalathilAssetLink> links, IReadOnlyList<AppearanceAssignment> assignments, List<string> warnings)
    {
        var result = new List<string>();
        foreach (var link in links)
        {
            var path = LegacyConfigPath(modsPath, link);
            if (!File.Exists(path)) continue;
            var cloneKeys = File.ReadLines(path).Select(line => Regex.Match(line, @"(?:^|:)copyVisualStyle=(?<key>[^:]+)", RegexOptions.IgnoreCase))
                .Where(match => match.Success).Select(match => NormalizeFormKey(match.Groups["key"].Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var imported = assignments.Where(a => a.SourceMod.Equals(link.SourceMod, StringComparison.OrdinalIgnoreCase) &&
                    a.SourcePlugin.Equals(link.SourcePlugin, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(a.AppearancePlugin) && a.AppearancePlugin.Equals(link.SourcePlugin, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(a.AppearanceFormKey))
                .Select(a => NormalizeFormKey(a.AppearanceFormKey!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (cloneKeys.Count > 0 && cloneKeys.All(imported.Contains)) result.Add(path);
            else warnings.Add($"Kept the old SkyPatcher config in {link.SourceMod}; it contains mappings that were not added to Galathil Output.");
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string LegacyConfigPath(string modsPath, GalathilAssetLink link)
    {
        var folder = SafeAssetModFolder(modsPath, link.SourceMod);
        return Path.Combine(folder, "SKSE", "Plugins", "SkyPatcher", "npc",
            "Galathil_" + SafeFileStem(Path.GetFileNameWithoutExtension(link.SourcePlugin)) + ".ini");
    }

    private static List<AssetMarkerChange> BackupAndRemoveLegacyConfigs(IReadOnlyList<string> paths, string modsPath, string backupFolder)
    {
        var existing = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existing.Length == 0) return [];
        var archivePath = Path.Combine(backupFolder, "legacy-skypatcher-configs.zip");
        CreateBackupArchive(archivePath, modsPath, existing, "Legacy Galathil SkyPatcher configs replaced by the shared Galathil Output config.");
        var removed = new List<AssetMarkerChange>();
        try
        {
            foreach (var path in existing)
            {
                removed.Add(new AssetMarkerChange(path, File.ReadAllBytes(path)));
                File.Delete(path);
            }
            return removed;
        }
        catch
        {
            RollbackAssetModMarkers(removed);
            throw;
        }
    }

    private static Dictionary<string, byte[]?> PlanTransferredFaceGen(
        string modsPath,
        IReadOnlyList<(SharedPreparedAssignment Item, FaceGenFiles Files)> transfers,
        IReadOnlyDictionary<string, string> previousFaceGen,
        ICollection<string> warnings)
    {
        var planned = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, files) in transfers)
        {
            var appearanceModFolder = SafeAssetModFolder(modsPath, item.Assignment.AppearanceMod ?? item.Assignment.SourceMod);
            var targetKey = item.Target.FormKey;
            var targetMesh = Path.Combine(appearanceModFolder, FaceGenRelative(MeshFolder, targetKey, ".nif"));
            var targetTint = Path.Combine(appearanceModFolder, FaceGenRelative(TintFolder, targetKey, ".dds"));
            AddWrite(targetMesh, files.Mesh);
            AddWrite(targetTint, files.Tint);

            foreach (var modFolder in new[]
                     {
                         appearanceModFolder,
                         SafeAssetModFolder(modsPath, item.Assignment.SourceMod)
                     }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                AddDeleteFaceGen(modFolder, item.Source.FormKey);
                if (item.Appearance.FormKey != item.Source.FormKey &&
                    StringComparer.OrdinalIgnoreCase.Equals(item.Appearance.FormKey.ModKey.FileName.ToString(), item.Assignment.SourcePlugin) &&
                    item.Appearance.EditorID?.StartsWith("GALATHIL_", StringComparison.OrdinalIgnoreCase) == true)
                    AddDeleteFaceGen(modFolder, item.Appearance.FormKey);
            }

            if (item.SourceFaceGen.SourceIsArchived)
                warnings.Add($"The selected FaceGen for {item.Assignment.SourceName} came from an archive. Galathil wrote loose recipient-ID files into {item.Assignment.AppearanceMod ?? item.Assignment.SourceMod}; archived files remain unchanged.");

            void AddWrite(string path, byte[] bytes)
            {
                var fullPath = Path.GetFullPath(path);
                if (!IsChildPath(appearanceModFolder, fullPath))
                    throw new InvalidDataException($"Refusing to write FaceGen outside the selected asset mod: {path}");
                if (planned.TryGetValue(fullPath, out var existing) && existing is not null && !existing.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException($"Two transferred looks would write different FaceGen files to {fullPath}.");
                planned[fullPath] = bytes;
            }

            void AddDeleteFaceGen(string modFolder, FormKey key)
            {
                AddDelete(Path.Combine(modFolder, FaceGenRelative(MeshFolder, key, ".nif")));
                AddDelete(Path.Combine(modFolder, FaceGenRelative(TintFolder, key, ".dds")));
            }

            void AddDelete(string path)
            {
                var fullPath = Path.GetFullPath(path);
                if (!IsChildPath(modsPath, fullPath))
                    throw new InvalidDataException($"Refusing to remove FaceGen outside the MO2 mods folder: {path}");
                // All donors are read before planning, so the incoming look wins
                // when it reuses a path being vacated by another donor.
                if (File.Exists(fullPath)) planned.TryAdd(fullPath, null);
            }
        }

        foreach (var (path, expectedHash) in previousFaceGen)
        {
            if (planned.ContainsKey(path) || !File.Exists(path)) continue;
            if (HashMatches(File.ReadAllBytes(path), expectedHash)) planned[path] = null;
            else warnings.Add($"Left previously generated FaceGen at {path} because it no longer matches Galathil's saved copy.");
        }
        return planned;
    }

    private static List<AssetMarkerChange> ApplyTransferredFaceGenToAssetMods(
        string modsPath, IReadOnlyDictionary<string, byte[]?> planned, string backupFolder)
    {
        if (planned.Count == 0) return [];
        var changes = planned.Select(pair => new AssetMarkerChange(pair.Key,
            File.Exists(pair.Key) ? File.ReadAllBytes(pair.Key) : null)).ToList();
        var existingPaths = changes.Where(change => change.OriginalBytes is not null).Select(change => change.Path).ToArray();
        var createdPaths = changes.Where(change => change.OriginalBytes is null).Select(change => Path.GetRelativePath(modsPath, change.Path)).ToArray();
        var notes = "Galathil rekeyed these FaceGen files inside the selected replacer asset mod. To undo, restore the files in this ZIP and remove any new files listed below. Reinstalling the source replacer also restores its original files.\r\n\r\nChanged files:\r\n" +
            string.Join("\r\n", changes.Select(change => Path.GetRelativePath(modsPath, change.Path))) +
            "\r\n\r\nNew files to remove when undoing:\r\n" + string.Join("\r\n", createdPaths);
        CreateBackupArchive(Path.Combine(backupFolder, "rekeyed-facegen.zip"), modsPath, existingPaths, notes);

        try
        {
            foreach (var (path, bytes) in planned)
            {
                if (bytes is null)
                {
                    if (File.Exists(path)) File.Delete(path);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException($"Galathil could not verify the recipient FaceGen file {path}.");
            }

            return changes;
        }
        catch
        {
            RollbackAssetModMarkers(changes);
            throw;
        }

    }

    private static Dictionary<string, string> IndexManagedFaceGenFiles(string modsPath, IReadOnlyList<GalathilModEntry> modOrder,
        IReadOnlyList<GalathilFaceGenLink>? links, ICollection<string> warnings)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links ?? Array.Empty<GalathilFaceGenLink>())
        {
            var folder = ResolveManagedFaceGenFolder(modsPath, modOrder, link, warnings);
            if (folder is null) continue;
            var key = ParseOutfitFormKey(link.TargetFormKey, link.TargetFormKey);
            files[Path.Combine(folder, FaceGenRelative(MeshFolder, key, ".nif"))] = link.MeshSha256;
            files[Path.Combine(folder, FaceGenRelative(TintFolder, key, ".dds"))] = link.TintSha256;
        }
        return files;
    }

    private static string? ResolveManagedFaceGenFolder(string modsPath, IReadOnlyList<GalathilModEntry> modOrder,
        GalathilFaceGenLink link, ICollection<string> warnings)
    {
        var folder = Path.GetFullPath(Path.Combine(modsPath, link.AssetMod));
        if (!IsChildPath(modsPath, folder))
            throw new InvalidDataException($"Previous FaceGen asset mod is outside the MO2 mods folder: {link.AssetMod}");
        var key = ParseOutfitFormKey(link.TargetFormKey, link.TargetFormKey);
        if (Directory.Exists(folder))
        {
            var meshPath = Path.Combine(folder, FaceGenRelative(MeshFolder, key, ".nif"));
            var tintPath = Path.Combine(folder, FaceGenRelative(TintFolder, key, ".dds"));
            if (File.Exists(meshPath) || File.Exists(tintPath)) return folder;
        }

        var matches = new List<string>();
        foreach (var mod in modOrder)
        {
            var candidate = Path.GetFullPath(Path.Combine(modsPath, mod.Name));
            if (!IsChildPath(modsPath, candidate) || !Directory.Exists(candidate)) continue;
            var meshPath = Path.Combine(candidate, FaceGenRelative(MeshFolder, key, ".nif"));
            var tintPath = Path.Combine(candidate, FaceGenRelative(TintFolder, key, ".dds"));
            if (!File.Exists(meshPath) || !File.Exists(tintPath)) continue;
            if (HashMatches(File.ReadAllBytes(meshPath), link.MeshSha256) && HashMatches(File.ReadAllBytes(tintPath), link.TintSha256))
                matches.Add(candidate);
        }
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
            warnings.Add($"Could not safely find the single MO2 mod holding Galathil's old FaceGen for {link.TargetFormKey}; duplicate copies were left untouched.");
        return null;
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static bool HashMatches(byte[] data, string expectedHash)
    {
        try { return Convert.ToHexString(SHA256.HashData(data)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static string? FindActivePluginDependent(string sourcePlugin, IReadOnlySet<string> activePlugins,
        IReadOnlyDictionary<string, string> pluginPaths, IReadOnlyList<string> generatedPlugins)
    {
        var generated = generatedPlugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in activePlugins)
        {
            if (plugin.Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase) || IsGalathilOutputPlugin(plugin) || generated.Contains(plugin)) continue;
            if (!pluginPaths.TryGetValue(plugin, out var path)) return $"an active plugin ({plugin}) whose master list could not be checked";
            try
            {
                using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                if (mod.ModHeader.MasterReferences.Any(master => master.Master.FileName.ToString().Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase)))
                    return plugin;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not check if {plugin} depends on {sourcePlugin}: {ex.Message}");
                return $"an active plugin ({plugin}) whose master list could not be checked";
            }
        }
        return null;
    }

    private sealed record AssetMarkerChange(string Path, byte[]? OriginalBytes);

    private static void UpdateGalathilOutputProfile(string profilePath, IReadOnlyList<string> pluginNames,
        IReadOnlySet<string> disableSourcePlugins, IReadOnlyList<string> restoreSourcePlugins,
        IReadOnlyList<string> forgetSourcePlugins,
        IReadOnlyDictionary<string, string> profilePluginPaths,
        IReadOnlyDictionary<string, int> sourcePluginLoadOrder)
    {
        var modListPath = Path.Combine(profilePath, "modlist.txt");
        var pluginsPath = Path.Combine(profilePath, "plugins.txt");
        var loadOrderPath = Path.Combine(profilePath, "loadorder.txt");
        if (!File.Exists(modListPath) || !File.Exists(pluginsPath) || !File.Exists(loadOrderPath))
            throw new DirectoryNotFoundException("The selected MO2 profile is missing modlist.txt, plugins.txt, or loadorder.txt.");

        var outputPlugins = File.ReadLines(loadOrderPath).Select(line => line.Trim())
            .Where(IsGalathilOutputPlugin).Concat(File.ReadLines(pluginsPath).Select(line => line.TrimStart('*').Trim()).Where(IsGalathilOutputPlugin))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in pluginNames) outputPlugins.Add(plugin);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var paths = new[] { modListPath, pluginsPath, loadOrderPath };
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var backup = path + ".galathil-" + stamp + ".bak";
            File.Copy(path, backup, overwrite: false);
            backups[path] = backup;
            staged[path] = path + ".galathil.tmp";
        }

        try
        {
            var modLines = File.ReadAllLines(modListPath).Where(line =>
                line.Length < 2 || !"+-".Contains(line[0]) || !line[1..].Trim().Equals(OutputModName, StringComparison.OrdinalIgnoreCase)).ToList();
            modLines.Add("+" + OutputModName);
            File.WriteAllLines(staged[modListPath], modLines, new UTF8Encoding(false));

            var pluginLines = File.ReadAllLines(pluginsPath).Where(line =>
                !outputPlugins.Contains(line.TrimStart('*').Trim()) &&
                !forgetSourcePlugins.Contains(line.TrimStart('*').Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var sourcePlugin in disableSourcePlugins)
            {
                var existing = pluginLines.FindIndex(line => line.TrimStart('*').Trim().Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) pluginLines[existing] = sourcePlugin;
            }
            foreach (var sourcePlugin in restoreSourcePlugins)
            {
                if (!profilePluginPaths.ContainsKey(sourcePlugin))
                    throw new FileNotFoundException($"Cannot restore {sourcePlugin}: its plugin file is missing from the enabled MO2 mods or game Data folder.");
                pluginLines.RemoveAll(line => line.TrimStart('*').Trim().Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase));
                pluginLines.Add("*" + sourcePlugin);
            }
            pluginLines.AddRange(pluginNames.Select(name => "*" + name));
            File.WriteAllLines(staged[pluginsPath], pluginLines, new UTF8Encoding(false));

            var orderLines = File.ReadAllLines(loadOrderPath).Where(line =>
                !outputPlugins.Contains(line.Trim()) &&
                !forgetSourcePlugins.Contains(line.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
            orderLines.RemoveAll(line => disableSourcePlugins.Contains(line.Trim()));
            foreach (var sourcePlugin in restoreSourcePlugins)
            {
                if (!orderLines.Contains(sourcePlugin, StringComparer.OrdinalIgnoreCase)) orderLines.Add(sourcePlugin);
            }
            orderLines.AddRange(pluginNames);
            foreach (var sourcePlugin in restoreSourcePlugins.OrderBy(name => sourcePluginLoadOrder.TryGetValue(name, out var index) ? index : int.MaxValue))
            {
                var currentIndex = orderLines.FindIndex(line => line.Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase));
                if (currentIndex < 0) continue;
                orderLines.RemoveAt(currentIndex);
                var originalIndex = sourcePluginLoadOrder.TryGetValue(sourcePlugin, out var savedIndex)
                    ? Math.Clamp(savedIndex, 0, orderLines.Count)
                    : orderLines.Count;
                orderLines.Insert(originalIndex, sourcePlugin);
            }
            File.WriteAllLines(staged[loadOrderPath], orderLines, new UTF8Encoding(false));
            foreach (var path in paths) File.Move(staged[path], path, overwrite: true);
        }
        catch
        {
            foreach (var (path, backup) in backups) if (File.Exists(backup)) File.Copy(backup, path, overwrite: true);
            foreach (var temp in staged.Values) if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    private static string OutputPluginName(int shard) => shard == 1 ? "Galathil Output.esp" : $"Galathil Output {shard}.esp";
    private static bool IsGalathilOutputPlugin(string name) => Regex.IsMatch(name, @"^Galathil Output(?: \d+)?\.(?:esp|esl)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string FormKeyIdentifier(FormKey key) => key.ModKey.FileName + "|" + key.ID.ToString("X8", CultureInfo.InvariantCulture);

    private static FormKey ParseOutfitFormKey(string formKeyText, string outfitName)
    {
        var separator = formKeyText.LastIndexOf('|');
        if (separator < 1 || !uint.TryParse(formKeyText[(separator + 1)..], NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var id))
            throw new InvalidDataException($"The selected outfit {outfitName} has an invalid FormID.");
        return new FormKey(ModKey.FromFileName(formKeyText[..separator]), id);
    }

    private static string NormalizeIdentifier(string formKeyText)
    {
        var key = ParseOutfitFormKey(formKeyText, "record");
        return FormKeyIdentifier(key);
    }

    private static void ValidateOutfitAssignment(OutfitAssignment assignment, IReadOnlySet<string> activePlugins,
        IReadOnlyDictionary<string, string> pluginPaths)
    {
        var outfitKey = ParseOutfitFormKey(assignment.OutfitFormKey, assignment.OutfitName);
        var ownerPlugin = outfitKey.ModKey.FileName.ToString();
        if (!ownerPlugin.Equals(assignment.OutfitPlugin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The selected outfit {assignment.OutfitName} does not match its owner plugin.");
        if (!activePlugins.Contains(ownerPlugin) || !pluginPaths.TryGetValue(ownerPlugin, out var ownerPath))
            throw new InvalidOperationException($"The plugin {ownerPlugin}, which contains {assignment.OutfitName}, is not active in this MO2 profile.");
        if (!activePlugins.Contains(assignment.OutfitWinningPlugin) || !pluginPaths.TryGetValue(assignment.OutfitWinningPlugin, out var winnerPath))
            throw new InvalidOperationException($"The winning plugin {assignment.OutfitWinningPlugin}, which provides {assignment.OutfitName}, is not active in this MO2 profile.");

        using var owner = SkyrimMod.CreateFromBinaryOverlay(ownerPath, SkyrimRelease.SkyrimSE);
        var original = owner.Outfits.FirstOrDefault(record => record.FormKey == outfitKey);
        if (original is null || original.IsDeleted)
            throw new InvalidDataException($"The selected outfit {assignment.OutfitName} is no longer present in {ownerPlugin}.");
        using var winner = SkyrimMod.CreateFromBinaryOverlay(winnerPath, SkyrimRelease.SkyrimSE);
        var current = winner.Outfits.FirstOrDefault(record => record.FormKey == outfitKey);
        if (current is null || current.IsDeleted || !HasOutfitItems(current))
            throw new InvalidDataException($"The selected outfit {assignment.OutfitName} is no longer available in {assignment.OutfitWinningPlugin}. Refresh the profile and choose another outfit.");
    }

    private static bool HasOutfitItems(object outfit)
    {
        var items = outfit.GetType().GetProperty("Items")?.GetValue(outfit);
        if (items is not System.Collections.IEnumerable entries) return false;
        var iterator = entries.GetEnumerator();
        try { return iterator.MoveNext(); }
        finally { (iterator as IDisposable)?.Dispose(); }
    }

    private static Dictionary<string, List<PluginNpc>> ReadNpcChains(
        IReadOnlyList<string> pluginOrder,
        IReadOnlyDictionary<string, string> pluginPaths,
        IReadOnlySet<string> wantedKeys)
    {
        var chains = new Dictionary<string, List<PluginNpc>>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in pluginOrder)
        {
            if (!pluginPaths.TryGetValue(plugin, out var path))
                throw new FileNotFoundException($"Could not find active plugin '{plugin}' in the game Data folder, enabled MO2 mods, or Overwrite.");
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            foreach (var npc in mod.Npcs)
            {
                if (npc.IsDeleted) continue;
                var key = FormKeyText(npc.FormKey);
                if (!wantedKeys.Contains(key)) continue;
                if (!chains.TryGetValue(key, out var chain)) chains[key] = chain = [];
                chain.Add(new PluginNpc(plugin, npc.DeepCopy()));
            }
        }
        return chains;
    }

    private static List<PluginNpc> GetChain(Dictionary<string, List<PluginNpc>> chains, string key, string name) =>
        chains.TryGetValue(key, out var chain) && chain.Count > 0
            ? chain
            : throw new InvalidOperationException($"Could not find {name} ({key}) in the active plugin load order.");

    private static void CopyAppearance(Npc destination, INpcGetter source, bool includeRace, bool includeSkin, bool includeOutfit)
    {
        destination.HeadParts.Clear();
        destination.HeadParts.AddRange(source.HeadParts.Select(part => part.FormKey.ToLink<IHeadPartGetter>()));
        destination.HairColor = new FormLinkNullable<IColorRecordGetter>(source.HairColor?.FormKeyNullable);
        destination.HeadTexture = new FormLinkNullable<ITextureSetGetter>(source.HeadTexture?.FormKeyNullable);
        destination.TextureLighting = source.TextureLighting;
        destination.FaceMorph = source.FaceMorph?.DeepCopy();
        destination.FaceParts = source.FaceParts?.DeepCopy();
        destination.TintLayers.Clear();
        destination.TintLayers.AddRange(source.TintLayers.Select(layer => layer.DeepCopy()));
        destination.Height = source.Height;
        destination.Weight = source.Weight;
        CopyOptionalAppearanceProperty(destination, source, "BodyTintColor");
        if (includeSkin)
            destination.WornArmor = new FormLinkNullable<IArmorGetter>(source.WornArmor?.FormKeyNullable);
        if (includeOutfit)
            destination.DefaultOutfit = new FormLinkNullable<IOutfitGetter>(source.DefaultOutfit?.FormKeyNullable);
        if (includeRace) destination.Race = source.Race.FormKey.ToLink<IRaceGetter>();
    }

    private static void CopyOptionalAppearanceProperty(object destination, object source, string propertyName)
    {
        var sourceProperty = source.GetType().GetProperty(propertyName);
        var destinationProperty = destination.GetType().GetProperty(propertyName);
        if (sourceProperty?.CanRead != true || destinationProperty?.CanWrite != true) return;
        var value = sourceProperty.GetValue(source);
        var deepCopy = value?.GetType().GetMethod("DeepCopy", Type.EmptyTypes);
        destinationProperty.SetValue(destination, deepCopy?.Invoke(value, null) ?? value);
    }

    private static IEnumerable<FormKey> AppearanceRecordRoots(SharedPreparedAssignment item,
        IReadOnlyList<OutfitAssignment> outfitAssignments, IReadOnlySet<string> sourcePlugins)
    {
        foreach (var headPart in item.Appearance.HeadParts) yield return headPart.FormKey;
        if (item.Appearance.HairColor is { IsNull: false } hairColor) yield return hairColor.FormKey;
        if (item.Appearance.HeadTexture is { IsNull: false } headTexture) yield return headTexture.FormKey;
        if (item.CopySkinArmor && item.Appearance.WornArmor is { IsNull: false } skin) yield return skin.FormKey;
        if (item.CopyOutfit && item.Appearance.DefaultOutfit is { IsNull: false } outfit) yield return outfit.FormKey;
        if (item.Appearance.Race.FormKey != item.Target.Race.FormKey &&
            sourcePlugins.Contains(item.Appearance.Race.FormKey.ModKey.FileName.ToString()))
            yield return item.Appearance.Race.FormKey;

        var selectedOutfit = outfitAssignments.FirstOrDefault(o =>
            NormalizeFormKey(o.TargetFormKey).Equals(NormalizeFormKey(item.Assignment.TargetFormKey), StringComparison.OrdinalIgnoreCase));
        if (selectedOutfit is not null && sourcePlugins.Contains(selectedOutfit.OutfitPlugin))
            yield return ParseOutfitFormKey(selectedOutfit.OutfitFormKey, selectedOutfit.OutfitName);
    }

    private static List<List<SharedPreparedAssignment>> BuildSharedOutputShards(
        IReadOnlyList<SharedPreparedAssignment> assignments,
        IReadOnlyDictionary<SharedPreparedAssignment, HashSet<FormKey>> copyableRecordsByAssignment,
        int capacity)
    {
        var shards = new List<List<SharedPreparedAssignment>>();
        var current = new List<SharedPreparedAssignment>();
        var currentRecords = new HashSet<FormKey>();
        foreach (var item in assignments)
        {
            var itemRecords = copyableRecordsByAssignment[item];
            var addedRecords = itemRecords.Count(key => !currentRecords.Contains(key));
            if (current.Count > 0 && currentRecords.Count + addedRecords > capacity)
            {
                shards.Add(current);
                current = [];
                currentRecords = [];
                addedRecords = itemRecords.Count;
            }
            if (itemRecords.Count > capacity)
                throw new InvalidOperationException($"{item.Assignment.SourceName} needs more than {capacity} local records for its appearance and linked records. Remove unused appearance records or keep its ESP enabled.");
            current.Add(item);
            currentRecords.UnionWith(itemRecords);
        }
        if (current.Count > 0) shards.Add(current);
        return shards;
    }

    private static FormKey RemapCopiedFormKey(FormKey key, IReadOnlyDictionary<FormKey, FormKey> copiedRecords) =>
        copiedRecords.TryGetValue(key, out var copy) ? copy : key;

    private sealed class AppearanceRecordLibrary : IDisposable
    {
        private readonly HashSet<string> _sourcePlugins;
        private readonly IReadOnlyDictionary<string, string> _pluginPaths;
        private readonly Dictionary<string, PluginFileLease> _mods = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Func<FormKey, IMajorRecordGetter?>> _resolvers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<FormKey, IMajorRecordGetter?> _recordCache = [];

        public AppearanceRecordLibrary(IEnumerable<string> sourcePlugins, IReadOnlyDictionary<string, string> pluginPaths)
        {
            _sourcePlugins = sourcePlugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _pluginPaths = pluginPaths;
        }

        public HashSet<FormKey> GetCopyableClosure(IEnumerable<FormKey> roots)
        {
            var closure = new HashSet<FormKey>();
            var visited = new HashSet<FormKey>();
            foreach (var root in roots) Visit(root);
            return closure;

            void Visit(FormKey key)
            {
                var owner = key.ModKey.FileName.ToString();
                if (!_sourcePlugins.Contains(owner) || !visited.Add(key)) return;
                var record = ResolveSourceRecord(owner, key);
                if (record is null || !CanCopy(record)) return;
                closure.Add(key);
                if (record is not IFormLinkContainerGetter links) return;
                foreach (var link in links.EnumerateFormLinks(iterateNestedRecords: false))
                    if (link.FormKeyNullable is { } linkedKey) Visit(linkedKey);
            }
        }

        public IReadOnlyDictionary<FormKey, FormKey> CopyToOutput(IReadOnlyCollection<FormKey> keys,
            SkyrimMod output, ref uint nextLocalId)
        {
            var map = new Dictionary<FormKey, FormKey>();
            foreach (var key in keys)
            {
                if (nextLocalId > LastLightObjectId)
                    throw new LightPluginFormIdCapacityException(new FormKey(output.ModKey, nextLocalId));
                map[key] = new FormKey(output.ModKey, nextLocalId++);
            }

            var copied = new List<IFormLinkContainer>();
            foreach (var (sourceKey, targetKey) in map)
            {
                var source = ResolveSourceRecord(sourceKey.ModKey.FileName.ToString(), sourceKey)
                    ?? throw new InvalidDataException($"Could not read linked appearance record {FormKeyIdentifier(sourceKey)}.");
                var clone = DuplicateSupportedRecord(source, targetKey, output);
                if (clone is not IFormLinkContainer container)
                    throw new InvalidDataException($"Galathil could not remap links in copied appearance record {FormKeyIdentifier(sourceKey)}.");
                copied.Add(container);
            }
            foreach (var record in copied) record.RemapLinks(map);
            return map;
        }

        private IMajorRecordGetter? ResolveSourceRecord(string plugin, FormKey key)
        {
            if (_recordCache.TryGetValue(key, out var cached)) return cached;
            if (!_resolvers.TryGetValue(plugin, out var resolve))
            {
                if (!_pluginPaths.TryGetValue(plugin, out var path))
                    throw new FileNotFoundException($"Cannot copy appearance records from {plugin}: its plugin file is missing from the active MO2 profile.");
                var mod = PluginFileAccess.Open(plugin, path);
                _mods[plugin] = mod;
                var cache = mod.Mod.ToImmutableLinkCache();
                resolve = formKey => cache.TryResolve(formKey, out IMajorRecordGetter? record, ResolveTarget.Winner) ? record : null;
                _resolvers[plugin] = resolve;
            }
            var result = resolve(key);
            _recordCache[key] = result;
            return result;
        }

        private static bool CanCopy(IMajorRecordGetter record) => record is
            IArmorGetter or IArmorAddonGetter or IHeadPartGetter or ITextureSetGetter or
            IColorRecordGetter or IOutfitGetter or IRaceGetter;

        private static IMajorRecord DuplicateSupportedRecord(IMajorRecordGetter source, FormKey key, SkyrimMod output) => source switch
        {
            IArmorGetter armor => output.Armors.DuplicateInAsNewRecord(armor, key),
            IArmorAddonGetter armorAddon => output.ArmorAddons.DuplicateInAsNewRecord(armorAddon, key),
            IHeadPartGetter headPart => output.HeadParts.DuplicateInAsNewRecord(headPart, key),
            ITextureSetGetter textureSet => output.TextureSets.DuplicateInAsNewRecord(textureSet, key),
            IColorRecordGetter color => output.Colors.DuplicateInAsNewRecord(color, key),
            IOutfitGetter outfit => output.Outfits.DuplicateInAsNewRecord(outfit, key),
            IRaceGetter race => output.Races.DuplicateInAsNewRecord(race, key),
            _ => throw new InvalidDataException($"Galathil cannot copy the linked appearance record type {source.GetType().Name}.")
        };

        public void Dispose()
        {
            foreach (var mod in _mods.Values) mod.Dispose();
            _mods.Clear();
            _resolvers.Clear();
        }
    }

    private static string AppearanceSignature(INpcGetter npc, bool includeSkin = true, bool includeOutfit = false)
    {
        var values = new List<string>
        {
            string.Join(",", npc.HeadParts.Select(p => FormKeyText(p.FormKey))),
            LinkKey(npc.HairColor),
            LinkKey(npc.HeadTexture),
            npc.TextureLighting?.ToString() ?? "",
            ValueSignature(npc.FaceMorph),
            ValueSignature(npc.FaceParts),
            string.Join(";", npc.TintLayers.Select(ValueSignature)),
            npc.Height.ToString("R", CultureInfo.InvariantCulture),
            npc.Weight.ToString("R", CultureInfo.InvariantCulture)
        };
        if (includeSkin) values.Add(LinkKey(npc.WornArmor));
        if (includeOutfit) values.Add(LinkKey(npc.DefaultOutfit));
        return string.Join("|", values);
    }

    private static string LinkKey<T>(Mutagen.Bethesda.Plugins.IFormLinkNullableGetter<T>? link) where T : class, IMajorRecordGetter =>
        link is null || link.IsNull ? "" : FormKeyText(link.FormKey);

    private static string ValueSignature(object? value)
    {
        if (value is null) return "null";
        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Enum)
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var linkInterface = value.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(Mutagen.Bethesda.Plugins.IFormLinkGetter<>) ||
             i.GetGenericTypeDefinition() == typeof(Mutagen.Bethesda.Plugins.IFormLinkNullableGetter<>)));
        if (linkInterface is not null)
        {
            var isNull = linkInterface.GetProperty("IsNull")?.GetValue(value) as bool? ?? false;
            return isNull ? "" : FormKeyText((FormKey)linkInterface.GetProperty("FormKey")!.GetValue(value)!);
        }
        if (value is System.Collections.IEnumerable sequence)
            return "[" + string.Join(",", sequence.Cast<object?>().Select(ValueSignature)) + "]";

        var properties = value.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != "StaticRegistration")
            .OrderBy(p => p.Name, StringComparer.Ordinal);
        return "{" + string.Join(",", properties.Select(p => p.Name + "=" + ValueSignature(p.GetValue(value)))) + "}";
    }

    private static bool IsFemale(INpcGetter npc)
    {
        var flags = npc.Configuration.Flags;
        return flags.ToString().Split(',').Any(f => f.Trim().Equals("Female", StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteFaceGen(string root, FormKey formKey, FaceGenFiles files)
    {
        var id = formKey.ID.ToString("X8", CultureInfo.InvariantCulture);
        var namespaceFolder = formKey.ModKey.FileName;
        var meshPath = Path.Combine(root, MeshFolder, namespaceFolder, id + ".nif");
        var tintPath = Path.Combine(root, TintFolder, namespaceFolder, id + ".dds");
        Directory.CreateDirectory(Path.GetDirectoryName(meshPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(tintPath)!);
        File.WriteAllBytes(meshPath, files.Mesh);
        File.WriteAllBytes(tintPath, files.Tint);
    }

    private static FaceGenFiles RekeyFaceGen(FaceGenFiles files, FormKey donorKey, FormKey appearanceKey, FormKey targetKey)
    {
        var inputKeys = new HashSet<FormKey> { donorKey, appearanceKey };
        if (files.PreviousTarget is { } previous) inputKeys.Add(previous);
        var mesh = RewriteFaceTintReference(files.Mesh, inputKeys, targetKey, files.PreviousTarget != targetKey);
        return files with { Mesh = mesh };
    }

    private static byte[] RewriteFaceTintReference(byte[] nif, IReadOnlySet<FormKey> oldKeys, FormKey newKey, bool rewriteAliases)
    {
        var oldPaths = oldKeys.Where(key => key != newKey).Select(key => Encoding.ASCII.GetBytes(FaceTintNifPath(key))).ToArray();
        if (oldPaths.Length == 0 && !rewriteAliases) return nif;
        var newPath = FaceTintNifPath(newKey);
        var newBytes = Encoding.ASCII.GetBytes(newPath);
        var layout = ReadFaceGenNifLayout(nif);
        var replacements = new List<NifTexturePathReplacement>();
        foreach (var oldBytes in oldPaths)
        foreach (var match in FindAsciiMatches(nif, oldBytes))
        {
            var owner = -1;
            for (var blockIndex = 0; blockIndex < layout.Blocks.Count; blockIndex++)
            {
                var block = layout.Blocks[blockIndex];
                if (match >= block.Start + 4 && match + oldBytes.Length <= block.Start + block.Length)
                {
                    owner = blockIndex;
                    break;
                }
            }
            if (owner < 0 || BinaryPrimitives.ReadInt32LittleEndian(nif.AsSpan(match - 4, 4)) != oldBytes.Length)
                throw new InvalidDataException($"Galathil found a donor tint path in an unsupported part of the FaceGen mesh for {FormKeyText(newKey)}.");
            replacements.Add(new NifTexturePathReplacement(match - 4, match, oldBytes.Length, newBytes, owner));
        }
        if (rewriteAliases)
        {
            // Some follower ports ship a FormID DDS beside a mesh whose tint slot
            // still points to a named FaceTint file or a RaceMenu export path.
            for (var index = 0; index < layout.Blocks.Count; index++)
            {
                var block = layout.Blocks[index];
                if (block.Type != "BSShaderTextureSet" || block.Length < sizeof(uint)) continue;
                var position = block.Start;
                var count = ReadUInt32(nif, ref position);
                const int faceTintSlot = 6;
                if (count <= faceTintSlot) continue;
                var end = block.Start + block.Length;
                for (var slot = 0; slot <= faceTintSlot; slot++)
                {
                    if (end - position < sizeof(int)) throw new InvalidDataException("Invalid FaceGen texture set.");
                    var length = ReadInt32(nif, ref position);
                    if (length < 0 || length > end - position) throw new InvalidDataException("Invalid FaceGen texture path length.");
                    if (slot == faceTintSlot && length > 0 && !replacements.Any(r => r.DataOffset == position))
                    {
                        var path = Encoding.ASCII.GetString(nif, position, length).Replace('/', '\\');
                        if (!path.Equals(newPath, StringComparison.OrdinalIgnoreCase) &&
                            path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
                            (path.Contains(@"\FaceGenData\FaceTint\", StringComparison.OrdinalIgnoreCase) ||
                             path.Contains(@"SKSE\Plugins\CharGen\", StringComparison.OrdinalIgnoreCase)))
                            replacements.Add(new NifTexturePathReplacement(position - sizeof(int), position, length, newBytes, index));
                    }
                    position += length;
                }
            }
        }
        if (replacements.Count == 0) return nif;

        using var output = new MemoryStream(nif.Length + replacements.Sum(replacement => replacement.NewBytes.Length - replacement.OldLength));
        var cursor = 0;
        var blockDeltas = new int[layout.Blocks.Count];
        foreach (var replacement in replacements.OrderBy(item => item.LengthOffset))
        {
            if (replacement.LengthOffset < cursor)
                throw new InvalidDataException("Galathil found overlapping tint-path entries in a FaceGen mesh.");
            output.Write(nif, cursor, replacement.LengthOffset - cursor);
            WriteInt32(output, replacement.NewBytes.Length);
            output.Write(replacement.NewBytes);
            cursor = replacement.DataOffset + replacement.OldLength;
            blockDeltas[replacement.BlockIndex] += replacement.NewBytes.Length - replacement.OldLength;
        }
        output.Write(nif, cursor, nif.Length - cursor);
        var rewritten = output.ToArray();
        for (var index = 0; index < blockDeltas.Length; index++)
        {
            if (blockDeltas[index] == 0) continue;
            var newSize = checked(layout.Blocks[index].Length + blockDeltas[index]);
            BinaryPrimitives.WriteInt32LittleEndian(rewritten.AsSpan(layout.BlockSizeOffsets[index], 4), newSize);
        }

        var expectedNewMatches = FindAsciiMatches(nif, newBytes).Count + replacements.Count;
        var newMatches = FindAsciiMatches(rewritten, newBytes);
        if (newMatches.Count != expectedNewMatches || oldPaths.Any(path => FindAsciiMatches(rewritten, path).Count != 0))
            throw new InvalidDataException($"Galathil could not fully update the FaceTint path for {FormKeyText(newKey)}.");
        return rewritten;

        static void WriteInt32(Stream stream, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }
    }

    private static string FaceTintNifPath(FormKey key) =>
        $"Textures\\Actors\\Character\\FaceGenData\\FaceTint\\{key.ModKey.FileName}\\{key.ID:X8}.dds";

    private static List<int> FindAsciiMatches(byte[] data, byte[] pattern)
    {
        var matches = new List<int>();
        for (var start = 0; start <= data.Length - pattern.Length; start++)
        {
            var matchesAtPosition = true;
            for (var index = 0; index < pattern.Length; index++)
            {
                var left = data[start + index];
                var right = pattern[index];
                if (left is >= (byte)'A' and <= (byte)'Z') left += 32;
                if (right is >= (byte)'A' and <= (byte)'Z') right += 32;
                if (left != right)
                {
                    matchesAtPosition = false;
                    break;
                }
            }
            if (matchesAtPosition) matches.Add(start);
        }
        return matches;
    }

    private static FaceGenNifLayout ReadFaceGenNifLayout(byte[] nif)
    {
        var lineEnd = Array.IndexOf(nif, (byte)'\n');
        if (lineEnd <= 0 || lineEnd > 128)
            throw new InvalidDataException("The selected FaceGen mesh has an invalid NIF header.");
        var versionText = Encoding.ASCII.GetString(nif, 0, lineEnd);
        if (!versionText.StartsWith("Gamebryo File Format, Version ", StringComparison.Ordinal) &&
            !versionText.StartsWith("NetImmerse File Format, Version ", StringComparison.Ordinal))
            throw new InvalidDataException("The selected FaceGen mesh is not a supported Skyrim NIF file.");

        var position = lineEnd + 1;
        var fileVersion = ReadUInt32(nif, ref position);
        if (fileVersion != 0x14020007)
            throw new InvalidDataException($"Galathil can only rekey Skyrim FaceGen NIF 20.2.0.7 safely; this mesh uses version 0x{fileVersion:X8}.");
        if (ReadByte(nif, ref position) != 1)
            throw new InvalidDataException("Galathil cannot rekey a big-endian FaceGen NIF.");

        var userVersion = ReadUInt32(nif, ref position);
        var blockCount = ReadInt32(nif, ref position);
        var streamVersion = ReadUInt32(nif, ref position);
        if (!((userVersion == 11 && streamVersion == 83) || (userVersion == 12 && streamVersion == 100)))
            throw new InvalidDataException($"Galathil cannot rekey FaceGen NIF user/stream version {userVersion}/{streamVersion} safely.");
        if (blockCount < 1 || blockCount > 1_000_000)
            throw new InvalidDataException("The selected FaceGen mesh has an invalid block count.");

        SkipByteSizedString(nif, ref position);
        SkipByteSizedString(nif, ref position);
        SkipByteSizedString(nif, ref position);

        var blockTypeCount = ReadUInt16(nif, ref position);
        if (blockTypeCount == 0 || blockTypeCount > blockCount)
            throw new InvalidDataException("The selected FaceGen mesh has an invalid block-type table.");
        var blockTypes = new string[blockTypeCount];
        for (var index = 0; index < blockTypeCount; index++)
        {
            var length = ReadInt32(nif, ref position);
            EnsureAvailable(nif, position, length);
            blockTypes[index] = Encoding.ASCII.GetString(nif, position, length);
            position += length;
        }
        var blockTypeIndexes = new ushort[blockCount];
        for (var index = 0; index < blockCount; index++)
        {
            var type = ReadUInt16(nif, ref position);
            if (type >= blockTypeCount) throw new InvalidDataException("The selected FaceGen mesh has an invalid block type.");
            blockTypeIndexes[index] = type;
        }

        var blockSizeOffsets = new int[blockCount];
        var blockSizes = new int[blockCount];
        long totalBlockBytes = 0;
        for (var index = 0; index < blockCount; index++)
        {
            blockSizeOffsets[index] = position;
            blockSizes[index] = ReadInt32(nif, ref position);
            if (blockSizes[index] < 0) throw new InvalidDataException("The selected FaceGen mesh has a negative block size.");
            totalBlockBytes += blockSizes[index];
        }

        var stringCount = ReadUInt32(nif, ref position);
        _ = ReadUInt32(nif, ref position); // maximum header string length
        if (stringCount > 1_000_000) throw new InvalidDataException("The selected FaceGen mesh has an invalid header string count.");
        for (var index = 0; index < stringCount; index++) SkipSizedString(nif, ref position);

        var groupCount = ReadInt32(nif, ref position);
        if (groupCount < 0 || groupCount > 1_000_000)
            throw new InvalidDataException("The selected FaceGen mesh has an invalid group count.");
        EnsureAvailable(nif, position, checked(groupCount * sizeof(int)));
        position += groupCount * sizeof(int);

        if (totalBlockBytes > nif.Length - position)
            throw new InvalidDataException("The selected FaceGen mesh's block sizes do not fit inside the file.");
        var blocks = new List<NifBlockSpan>(blockCount);
        var blockPosition = position;
        for (var index = 0; index < blockCount; index++)
        {
            blocks.Add(new NifBlockSpan(blockPosition, blockSizes[index], blockTypes[blockTypeIndexes[index]]));
            blockPosition = checked(blockPosition + blockSizes[index]);
        }
        return new FaceGenNifLayout(blocks, blockSizeOffsets);
    }

    private static void SkipByteSizedString(byte[] data, ref int position)
    {
        var length = ReadByte(data, ref position);
        EnsureAvailable(data, position, length);
        position += length;
    }

    private static void SkipSizedString(byte[] data, ref int position)
    {
        var length = ReadUInt32(data, ref position);
        if (length > int.MaxValue) throw new InvalidDataException("The selected FaceGen mesh contains an oversized NIF string.");
        EnsureAvailable(data, position, (int)length);
        position += (int)length;
    }

    private static byte ReadByte(byte[] data, ref int position)
    {
        EnsureAvailable(data, position, 1);
        return data[position++];
    }

    private static ushort ReadUInt16(byte[] data, ref int position)
    {
        EnsureAvailable(data, position, sizeof(ushort));
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, sizeof(ushort)));
        position += sizeof(ushort);
        return value;
    }

    private static uint ReadUInt32(byte[] data, ref int position)
    {
        EnsureAvailable(data, position, sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, sizeof(uint)));
        position += sizeof(uint);
        return value;
    }

    private static int ReadInt32(byte[] data, ref int position)
    {
        EnsureAvailable(data, position, sizeof(int));
        var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position, sizeof(int)));
        position += sizeof(int);
        return value;
    }

    private static void EnsureAvailable(byte[] data, int position, int length)
    {
        if (position < 0 || length < 0 || position > data.Length - length)
            throw new InvalidDataException("The selected FaceGen mesh has truncated NIF data.");
    }

    private static void ValidateWrittenFaceGen(string root, IReadOnlyDictionary<FormKey, FaceGenFiles> expected)
    {
        foreach (var (key, files) in expected)
        {
            var meshPath = Path.Combine(root, FaceGenRelative(MeshFolder, key, ".nif"));
            var tintPath = Path.Combine(root, FaceGenRelative(TintFolder, key, ".dds"));
            if (!File.Exists(meshPath) || !File.Exists(tintPath))
                throw new InvalidDataException($"Generated FaceGen files are missing for {FormKeyText(key)}.");
            if (!File.ReadAllBytes(meshPath).AsSpan().SequenceEqual(files.Mesh) ||
                !File.ReadAllBytes(tintPath).AsSpan().SequenceEqual(files.Tint))
                throw new InvalidDataException($"Generated FaceGen files do not match the selected appearance for {FormKeyText(key)}.");
        }
    }

    private static void ValidateLightPlugin(ISkyrimMod output, IReadOnlySet<FormKey> createdNpcKeys)
    {
        if (!output.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small))
            throw new InvalidDataException("The source plugin is missing its ESL flag.");
        foreach (var npc in output.Npcs)
        {
            if (npc.IsDeleted) throw new InvalidDataException("The generated plugin contains a deleted NPC record.");
            if (npc.FormKey.ModKey == output.ModKey && (npc.FormKey.ID < FirstLightObjectId || npc.FormKey.ID > LastLightObjectId))
            {
                if (createdNpcKeys.Contains(npc.FormKey)) throw new LightPluginFormIdCapacityException(npc.FormKey);
                throw new InvalidDataException($"The source plugin already contains NPC {npc.FormKey} outside the ESL local FormID range. Galathil will not rewrite this invalid light plugin.");
            }
        }
    }

    private static void ValidateWrittenPlugin(
        string path,
        ModKey modKey,
        IReadOnlyDictionary<string, int> expectedGroupCounts,
        IReadOnlyList<string> expectedMasters,
        SkyrimModHeader.HeaderFlag expectedFlags,
        bool requireLightFlag,
        int expectedDeletedNpcCount)
    {
        using var check = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var observedGroups = CountMajorRecordGroups(check);
        if (observedGroups.Count != expectedGroupCounts.Count || expectedGroupCounts.Any(pair => !observedGroups.TryGetValue(pair.Key, out var count) || count != pair.Value))
            throw new InvalidDataException("The saved plugin's record groups do not match the edited source plugin.");
        var observedMasters = check.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToArray();
        if (!observedMasters.SequenceEqual(expectedMasters, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The replacement changed the source plugin's master list.");
        if (check.ModHeader.Flags != expectedFlags)
            throw new InvalidDataException("The replacement changed the source plugin's flags.");
        if (requireLightFlag && !check.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small))
            throw new InvalidDataException("The saved plugin did not retain its ESL flag.");
        if (check.Npcs.Count(n => n.IsDeleted) != expectedDeletedNpcCount)
            throw new InvalidDataException("The replacement changed the source plugin's deleted NPC records unexpectedly.");
        if (requireLightFlag && check.Npcs.Any(n => n.FormKey.ModKey == modKey && (n.FormKey.ID < FirstLightObjectId || n.FormKey.ID > LastLightObjectId)))
            throw new InvalidDataException("The saved plugin contains a new record outside the ESL local FormID range.");
    }

    private static Dictionary<string, int> CountMajorRecordGroups(object mod)
    {
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in mod.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0 || property.Name == "ModHeader") continue;
            if (property.GetValue(mod) is not System.Collections.IEnumerable records) continue;
            var count = records.Cast<object?>().Count(record => record is IMajorRecordGetter);
            if (count > 0) groups[property.Name] = count;
        }
        return groups;
    }

    private static string SafeFileStem(string value)
    {
        var stem = Regex.Replace(value, "[^A-Za-z0-9_.-]", "_").Trim('_', '.');
        return stem.Length == 0 ? "mod" : stem.Length > 80 ? stem[..80] : stem;
    }

    private static void CreateBackupArchive(string archivePath, string root, IEnumerable<string> files, string notes)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file)) continue;
            var entryName = Path.GetRelativePath(root, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
        var entry = archive.CreateEntry("backup-info.txt", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.WriteLine(notes);
    }

    private static void RestoreBackupArchive(string archivePath, string root, IEnumerable<string> touchedFiles, IReadOnlySet<string> previouslyExisting)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var relative in touchedFiles)
        {
            var destination = Path.Combine(root, relative);
            if (previouslyExisting.Contains(relative))
            {
                var entryName = relative.Replace('\\', '/');
                var entry = archive.GetEntry(entryName) ?? throw new InvalidDataException($"Backup is missing {relative}.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
            else if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    private static FaceGenFiles ReadLegacyGalathilFaceGenBackup(string sourceMod, FormKey donorKey, ICollection<string> warnings)
    {
        var backupRoot = Path.Combine(AppContext.BaseDirectory, "Backups", SafeFileStem(sourceMod));
        if (!Directory.Exists(backupRoot))
            throw new FileNotFoundException($"The older Galathil route is missing its helper FaceGen, and no backup folder exists for {sourceMod}.");

        var meshRelative = FaceGenRelative(MeshFolder, donorKey, ".nif").Replace('\\', '/');
        var tintRelative = FaceGenRelative(TintFolder, donorKey, ".dds").Replace('\\', '/');
        foreach (var backupDirectory in Directory.EnumerateDirectories(backupRoot).OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var archivePath = Path.Combine(backupDirectory, "source-files.zip");
            if (!File.Exists(archivePath)) continue;
            try
            {
                using var archive = ZipFile.OpenRead(archivePath);
                var info = archive.GetEntry("backup-info.txt");
                if (info is null) continue;
                using var infoReader = new StreamReader(info.Open(), Encoding.UTF8);
                if (!infoReader.ReadToEnd().Contains(FormKeyText(donorKey), StringComparison.OrdinalIgnoreCase)) continue;
                var meshEntry = archive.Entries.FirstOrDefault(entry => entry.FullName.Replace('\\', '/').Equals(meshRelative, StringComparison.OrdinalIgnoreCase));
                var tintEntry = archive.Entries.FirstOrDefault(entry => entry.FullName.Replace('\\', '/').Equals(tintRelative, StringComparison.OrdinalIgnoreCase));
                if (meshEntry is null || tintEntry is null) continue;
                var mesh = ReadZipEntry(meshEntry);
                var tint = ReadZipEntry(tintEntry);
                FaceGenAssetResolver.ValidateFaceGen(mesh, tint, donorKey);
                warnings.Add($"Recovered the old replacer's FaceGen from Galathil backup '{Path.GetFileName(backupDirectory)}' while converting its previous SkyPatcher setup.");
                return new FaceGenFiles(mesh, tint);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                Debug.WriteLine($"Could not read legacy Galathil FaceGen backup '{archivePath}': {ex.Message}");
            }
        }

        throw new FileNotFoundException($"The old Galathil appearance helper is missing valid FaceGen for {FormKeyText(donorKey)}, and Galathil could not find it in this mod's backups.");

        static byte[] ReadZipEntry(ZipArchiveEntry entry)
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }
    }

    private static Dictionary<string, byte[]> CollectInPlaceFilesToRemove(
        string sourceModFolder,
        string sourcePlugin,
        IReadOnlyList<PreparedAssignment> assignments,
        IReadOnlySet<FormKey> targetKeys)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in assignments)
        {
            if (!targetKeys.Contains(item.Source.FormKey)) AddFaceGen(item.Source.FormKey);
            if (item.Appearance.FormKey != item.Source.FormKey &&
                StringComparer.OrdinalIgnoreCase.Equals(item.Appearance.FormKey.ModKey.FileName.ToString(), sourcePlugin) &&
                item.Appearance.EditorID?.StartsWith("GALATHIL_", StringComparison.OrdinalIgnoreCase) == true &&
                !targetKeys.Contains(item.Appearance.FormKey))
                AddFaceGen(item.Appearance.FormKey);
        }

        var generatedConfigRelative = Path.Combine("SKSE", "Plugins", "SkyPatcher", "npc",
            "Galathil_" + SafeFileStem(Path.GetFileNameWithoutExtension(sourcePlugin)) + ".ini");
        var generatedConfigPath = Path.Combine(sourceModFolder, generatedConfigRelative);
        if (File.Exists(generatedConfigPath) &&
            File.ReadLines(generatedConfigPath).Take(4).Any(line => line.Contains("Generated by Galathil", StringComparison.OrdinalIgnoreCase)))
            files[generatedConfigRelative] = File.ReadAllBytes(generatedConfigPath);
        return files;

        void AddFaceGen(FormKey key)
        {
            foreach (var relative in new[]
            {
                FaceGenRelative(MeshFolder, key, ".nif"),
                FaceGenRelative(TintFolder, key, ".dds")
            })
            {
                var fullPath = Path.GetFullPath(Path.Combine(sourceModFolder, relative));
                if (!IsChildPath(sourceModFolder, fullPath) || !File.Exists(fullPath)) continue;
                files.TryAdd(relative, File.ReadAllBytes(fullPath));
            }
        }
    }

    private static void ValidateRemovedNpcRecords(string pluginPath, IReadOnlySet<FormKey> removedKeys)
    {
        if (removedKeys.Count == 0) return;
        using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        var remaining = mod.Npcs.FirstOrDefault(n => removedKeys.Contains(n.FormKey));
        if (remaining is not null)
            throw new InvalidDataException($"Galathil left obsolete NPC record {FormKeyText(remaining.FormKey)} in the converted replacer ESP.");
    }

    private static void ValidateRemovedDonorFaceGen(string sourceModFolder, IReadOnlyList<PreparedAssignment> assignments, IReadOnlySet<FormKey> targetKeys)
    {
        foreach (var item in assignments)
        {
            if (!targetKeys.Contains(item.Source.FormKey)) EnsureRemoved(item.Source.FormKey);
            if (item.Appearance.FormKey != item.Source.FormKey &&
                StringComparer.OrdinalIgnoreCase.Equals(item.Appearance.FormKey.ModKey.FileName.ToString(), item.Assignment.SourcePlugin) &&
                item.Appearance.EditorID?.StartsWith("GALATHIL_", StringComparison.OrdinalIgnoreCase) == true &&
                !targetKeys.Contains(item.Appearance.FormKey))
                EnsureRemoved(item.Appearance.FormKey);
        }

        void EnsureRemoved(FormKey key)
        {
            foreach (var relative in new[]
            {
                FaceGenRelative(MeshFolder, key, ".nif"),
                FaceGenRelative(TintFolder, key, ".dds")
            })
            {
                if (File.Exists(Path.Combine(sourceModFolder, relative)))
                    throw new InvalidDataException($"Galathil could not remove the obsolete donor FaceGen file {relative}.");
            }
        }
    }

    private static void ReplaceFileAtomically(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".galathil-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            if (File.Exists(destination)) File.Replace(temporary, destination, destinationBackupFileName: null);
            else File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static List<SupersededMod> FindSupersededGalathilMods(string modsPath, IReadOnlyList<GalathilModEntry> modOrder, IReadOnlyList<AppearanceAssignment> assignments)
    {
        var sourceMod = assignments[0].SourceMod;
        var transferredSources = assignments.Select(a => NormalizeFormKey(a.SourceFormKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = new List<SupersededMod>();
        foreach (var mod in modOrder.Where(m => m.Name.StartsWith("Galathil - ", StringComparison.OrdinalIgnoreCase) &&
                     !m.Name.Equals(sourceMod, StringComparison.OrdinalIgnoreCase)))
        {
            var folder = Path.Combine(modsPath, mod.Name);
            if (!IsChildPath(modsPath, folder)) continue;
            var readme = Path.Combine(folder, "README.txt");
            if (!File.Exists(readme)) continue;
            var oldSources = Regex.Matches(File.ReadAllText(readme), @"(?m)^- .*?\((?<key>[^()]+\|[0-9A-Fa-f]+)\) ->")
                .Select(match => NormalizeFormKey(match.Groups["key"].Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (oldSources.Length == 0 || !oldSources.Any(transferredSources.Contains)) continue;
            if (!oldSources.All(transferredSources.Contains))
                throw new InvalidOperationException($"The older Galathil output '{mod.Name}' also contains transfers for other source NPCs. Include those mappings in this operation before replacing the source ESP.");
            var plugins = Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly).Where(IsPluginFile).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToArray()
                : [];
            output.Add(new SupersededMod(mod.Name, folder, plugins));
        }
        return output;
    }

    private static List<string> ArchiveSupersededMods(string modsPath, IReadOnlyList<SupersededMod> mods, string backupFolder)
    {
        var archives = new List<string>();
        foreach (var mod in mods)
        {
            if (!Directory.Exists(mod.Folder)) continue;
            if (!IsChildPath(modsPath, mod.Folder))
                throw new InvalidOperationException($"Refusing to archive a Galathil output outside its MO2 mods folder: {mod.Folder}");
            var path = Path.Combine(backupFolder, "superseded-" + SafeFileStem(mod.Name) + ".zip");
            ZipFile.CreateFromDirectory(mod.Folder, path, CompressionLevel.Optimal, includeBaseDirectory: true);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException($"Could not back up the older Galathil output '{mod.Name}'.");
            archives.Add(path);
        }
        return archives;
    }

    private static bool IsChildPath(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPluginAvailable(string modsPath, string dataPath, IReadOnlyList<GalathilModEntry> modOrder,
        IReadOnlySet<string> activePlugins, IReadOnlyDictionary<string, string> pluginPaths, string pluginName)
    {
        if (activePlugins.Contains(pluginName) && pluginPaths.TryGetValue(pluginName, out var activePath) && File.Exists(activePath))
            return true;
        if (File.Exists(Path.Combine(dataPath, pluginName))) return true;
        foreach (var mod in modOrder)
        {
            var folder = Path.GetFullPath(Path.Combine(modsPath, mod.Name));
            if (IsChildPath(modsPath, folder) && Directory.Exists(folder) &&
                PluginFileAccess.FindInModFolder(folder, pluginName) is not null)
                return true;
        }
        return false;
    }

    private static void RemoveSupersededModsFromProfile(string profilePath, IReadOnlyList<SupersededMod> mods)
    {
        if (mods.Count == 0) return;
        var modNames = mods.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pluginNames = mods.SelectMany(m => m.Plugins).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modList = Path.Combine(profilePath, "modlist.txt");
        var plugins = Path.Combine(profilePath, "plugins.txt");
        var loadOrder = Path.Combine(profilePath, "loadorder.txt");
        var paths = new[] { modList, plugins, loadOrder };
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var temporary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var backup = path + ".galathil-" + stamp + ".bak";
            File.Copy(path, backup, overwrite: false);
            backups[path] = backup;
            temporary[path] = path + ".galathil.tmp";
        }

        try
        {
            File.WriteAllLines(temporary[modList], File.ReadLines(modList).Where(line =>
                line.Length < 2 || !"+-".Contains(line[0]) || !modNames.Contains(line[1..].Trim())), new UTF8Encoding(false));
            File.WriteAllLines(temporary[plugins], File.ReadLines(plugins).Where(line =>
                !pluginNames.Contains(line.TrimStart('*').Trim())), new UTF8Encoding(false));
            File.WriteAllLines(temporary[loadOrder], File.ReadLines(loadOrder).Where(line =>
                !pluginNames.Contains(line.Trim())), new UTF8Encoding(false));
            foreach (var path in paths) File.Move(temporary[path], path, overwrite: true);
        }
        catch
        {
            foreach (var (path, backup) in backups) if (File.Exists(backup)) File.Copy(backup, path, overwrite: true);
            foreach (var temp in temporary.Values) if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    private static string NormalizeFormKey(string value)
    {
        var separator = value.LastIndexOf('|');
        if (separator < 0 || !ulong.TryParse(value[(separator + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            return value.Trim();
        return value[..separator].Trim() + "|" + id.ToString("X", CultureInfo.InvariantCulture);
    }

    private static bool IsAddedNpcFormKey(string formKey, string sourcePlugin)
    {
        var separator = formKey.LastIndexOf('|');
        return separator > 0 && formKey[..separator].Trim().Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SupersededMod(string Name, string Folder, string[] Plugins);

    private static bool IsPluginFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".esp" or ".esm" or ".esl";

    private static string FormKeyText(FormKey key) => $"{key.ModKey.FileName}|{key.ID:X}";

    private sealed record PluginNpc(string Plugin, Npc Record);
    private sealed record PreparedAssignment(
        AppearanceAssignment Assignment,
        Npc Source,
        Npc Appearance,
        Npc BeforeSource,
        Npc Target,
        FaceGenFiles SourceFaceGen,
        bool CopyRace,
        bool CopySkinArmor,
        bool CopyOutfit);
    private sealed record NifBlockSpan(int Start, int Length, string Type);
    private sealed record FaceGenNifLayout(IReadOnlyList<NifBlockSpan> Blocks, IReadOnlyList<int> BlockSizeOffsets);
    private sealed record NifTexturePathReplacement(int LengthOffset, int DataOffset, int OldLength, byte[] NewBytes, int BlockIndex);
    private sealed record SharedPreparedAssignment(
        AppearanceAssignment Assignment,
        Npc Source,
        Npc Appearance,
        Npc BeforeSource,
        Npc Target,
        FaceGenFiles SourceFaceGen,
        bool CopyRace,
        bool CopyGender,
        bool CopySkinArmor,
        bool CopyOutfit);
    private sealed record SharedOutputRule(
        SharedPreparedAssignment Item,
        string PluginName,
        IReadOnlyDictionary<FormKey, FormKey> CopiedRecords);
    private sealed record FaceGenFiles(
        byte[] Mesh,
        byte[] Tint,
        string? MeshSourcePath = null,
        string? TintSourcePath = null,
        bool SourceIsArchived = false,
        bool FromGalathilOutput = false,
        FormKey? PreviousTarget = null);

    private sealed class FaceGenAssetResolver
    {
        private static readonly string[] SharedMeshArchives = { "Skyrim - Meshes0.bsa", "Skyrim - Meshes1.bsa" };
        private static readonly string[] SharedTextureArchives =
        {
            "Skyrim - Textures0.bsa", "Skyrim - Textures1.bsa", "Skyrim - Textures2.bsa",
            "Skyrim - Textures3.bsa", "Skyrim - Textures4.bsa", "Skyrim - Textures5.bsa",
            "Skyrim - Textures6.bsa", "Skyrim - Textures7.bsa", "Skyrim - Textures8.bsa"
        };
        private readonly string _modsPath;
        private readonly string _dataPath;
        private readonly IReadOnlyList<GalathilModEntry> _modOrder;
        private readonly IReadOnlyDictionary<string, string> _pluginPaths;
        private readonly Dictionary<string, IArchiveReader> _archives = new(StringComparer.OrdinalIgnoreCase);

        public FaceGenAssetResolver(string modsPath, string dataPath, IReadOnlyList<GalathilModEntry> modOrder, IReadOnlyDictionary<string, string> pluginPaths)
        {
            _modsPath = modsPath;
            _dataPath = dataPath;
            _modOrder = modOrder;
            _pluginPaths = pluginPaths;
        }

        public FaceGenFiles ReadCurrentReplacerFaceGen(string sourceMod, FormKey key, ICollection<string>? warnings = null)
        {
            var meshRelative = FaceGenRelative(MeshFolder, key, ".nif");
            var tintRelative = FaceGenRelative(TintFolder, key, ".dds");
            var sourceIndex = _modOrder.ToList().FindIndex(m => m.Name.Equals(sourceMod, StringComparison.OrdinalIgnoreCase));
            if (sourceIndex < 0) throw new InvalidOperationException($"MO2 mod '{sourceMod}' is missing from its mod list.");
            var sourceFolder = Path.Combine(_modsPath, sourceMod);
            var meshPath = Path.Combine(sourceFolder, meshRelative);
            var tintPath = Path.Combine(sourceFolder, tintRelative);
            var meshBytes = ReadLoose(meshPath);
            var tintBytes = ReadLoose(tintPath);
            if (meshBytes is not null && tintBytes is not null)
            {
                ValidateFaceGen(meshBytes, tintBytes, key);
                AddLaterConflictWarning(meshRelative, tintRelative);
                return new FaceGenFiles(meshBytes, tintBytes, meshPath, tintPath);
            }

            var archivedMesh = ReadFromModArchives(sourceFolder, meshRelative);
            var archivedTint = ReadFromModArchives(sourceFolder, tintRelative);
            if (archivedMesh is not null && archivedTint is not null)
            {
                ValidateFaceGen(archivedMesh, archivedTint, key);
                AddLaterConflictWarning(meshRelative, tintRelative);
                return new FaceGenFiles(archivedMesh, archivedTint, SourceIsArchived: true);
            }

            throw new FileNotFoundException($"The selected replacer mod does not contain both of its own FaceGen files for {FormKeyText(key)}. Galathil will not borrow another mod's files for the transfer.");

            void AddLaterConflictWarning(string meshPathRelative, string tintPathRelative)
            {
                var meshConflict = FindLooseConflictAfter(meshPathRelative, sourceIndex);
                var tintConflict = FindLooseConflictAfter(tintPathRelative, sourceIndex);
                if (meshConflict is not null || tintConflict is not null)
                {
                    var names = new[] { meshConflict, tintConflict }.Where(name => name is not null).Distinct(StringComparer.OrdinalIgnoreCase);
                    warnings?.Add($"Another MO2 mod ({string.Join(", ", names)}) also supplies donor-ID FaceGen. Galathil will use and remove only {sourceMod}'s files; the other mod's donor files will remain untouched.");
                }
            }
        }

        public bool HasArchivedFaceGen(string sourceMod, FormKey key)
        {
            var sourceFolder = Path.Combine(_modsPath, sourceMod);
            return ReadFromModArchives(sourceFolder, FaceGenRelative(MeshFolder, key, ".nif")) is not null ||
                ReadFromModArchives(sourceFolder, FaceGenRelative(TintFolder, key, ".dds")) is not null;
        }

        public void EnsureRecipientFaceGenWillWin(string assetMod, FormKey targetKey, string targetName,
            IReadOnlyDictionary<string, byte[]?>? planned = null)
        {
            var assetIndex = _modOrder.ToList().FindIndex(mod => mod.Name.Equals(assetMod, StringComparison.OrdinalIgnoreCase));
            if (assetIndex < 0) throw new InvalidOperationException($"MO2 mod '{assetMod}' is missing from its mod list.");
            if (!_modOrder[assetIndex].Enabled)
                throw new InvalidOperationException($"The asset mod '{assetMod}' is disabled in this MO2 profile.");

            var relativePaths = new[]
            {
                FaceGenRelative(MeshFolder, targetKey, ".nif"),
                FaceGenRelative(TintFolder, targetKey, ".dds")
            };
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = assetIndex + 1; index < _modOrder.Count; index++)
            {
                var mod = _modOrder[index];
                if (!mod.Enabled || IsGeneratedOutputMod(mod.Name)) continue;
                var folder = Path.Combine(_modsPath, mod.Name);
                if (relativePaths.Any(relative => ExistsAfterTransfer(Path.Combine(folder, relative))))
                    conflicts.Add(mod.Name);
            }

            if (conflicts.Count > 0)
                throw new InvalidOperationException($"Another higher-priority MO2 mod ({string.Join(", ", conflicts)}) supplies FaceGen for {targetName}. Move '{assetMod}' below it in MO2's left pane or resolve that asset conflict before applying.");

            bool ExistsAfterTransfer(string path) => planned is not null && planned.TryGetValue(Path.GetFullPath(path), out var bytes)
                ? bytes is not null
                : File.Exists(path);
        }

        public FaceGenFiles ReadSharedOutputFaceGen(string sourceMod, FormKey key, FormKey? previousTarget,
            IReadOnlyDictionary<string, string> previousFaceGen, ICollection<string> warnings)
        {
            var meshRelative = FaceGenRelative(MeshFolder, key, ".nif");
            var tintRelative = FaceGenRelative(TintFolder, key, ".dds");
            var sourceIndex = _modOrder.ToList().FindIndex(m => m.Name.Equals(sourceMod, StringComparison.OrdinalIgnoreCase));
            if (sourceIndex < 0) throw new InvalidOperationException($"MO2 mod '{sourceMod}' is missing from its mod list.");

            var sourceFolder = Path.Combine(_modsPath, sourceMod);
            var selectedMeshPath = Path.Combine(sourceFolder, meshRelative);
            var selectedTintPath = Path.Combine(sourceFolder, tintRelative);
            var meshWinner = FindLooseWinner(meshRelative, int.MaxValue, skipGenerated: true, includePath: IsUnmanaged);
            var tintWinner = FindLooseWinner(tintRelative, int.MaxValue, skipGenerated: true, includePath: IsUnmanaged);
            if (meshWinner is { } meshConflict && meshConflict.Index > sourceIndex && !meshConflict.ModName.Equals(sourceMod, StringComparison.OrdinalIgnoreCase))
                warnings?.Add($"A later MO2 mod ('{meshConflict.ModName}') also has {FormKeyText(key)}'s FaceGen mesh. Galathil transfers the selected mod's copy; the later mod will continue to supply its own mesh for the source NPC.");
            if (tintWinner is { } tintConflict && tintConflict.Index > sourceIndex && !tintConflict.ModName.Equals(sourceMod, StringComparison.OrdinalIgnoreCase))
                warnings?.Add($"A later MO2 mod ('{tintConflict.ModName}') also has {FormKeyText(key)}'s FaceGen tint. Galathil transfers the selected mod's copy; the later mod will continue to supply its own tint for the source NPC.");

            var selectedMesh = IsUnmanaged(selectedMeshPath) ? ReadLoose(selectedMeshPath) : null;
            var selectedTint = IsUnmanaged(selectedTintPath) ? ReadLoose(selectedTintPath) : null;
            if (selectedMesh is not null && selectedTint is not null)
            {
                ValidateFaceGen(selectedMesh, selectedTint, key);
                return new FaceGenFiles(selectedMesh, selectedTint,
                    File.Exists(selectedMeshPath) ? selectedMeshPath : null,
                    File.Exists(selectedTintPath) ? selectedTintPath : null);
            }

            if (previousTarget is { } previousKey)
            {
                var previousAssetMesh = Path.Combine(sourceFolder, FaceGenRelative(MeshFolder, previousKey, ".nif"));
                var previousAssetTint = Path.Combine(sourceFolder, FaceGenRelative(TintFolder, previousKey, ".dds"));
                var previousAssetMeshBytes = ReadLoose(previousAssetMesh);
                var previousAssetTintBytes = ReadLoose(previousAssetTint);
                if (previousAssetMeshBytes is not null && previousAssetTintBytes is not null)
                {
                    ValidateFaceGen(previousAssetMeshBytes, previousAssetTintBytes, previousKey);
                    return new FaceGenFiles(previousAssetMeshBytes, previousAssetTintBytes,
                        previousAssetMesh, previousAssetTint, PreviousTarget: previousKey);
                }

                var outputFolder = Path.Combine(_modsPath, OutputModName);
                var previousMesh = Path.Combine(outputFolder, FaceGenRelative(MeshFolder, previousKey, ".nif"));
                var previousTint = Path.Combine(outputFolder, FaceGenRelative(TintFolder, previousKey, ".dds"));
                var meshBytes = ReadLoose(previousMesh);
                var tintBytes = ReadLoose(previousTint);
                if (meshBytes is not null && tintBytes is not null)
                {
                    ValidateFaceGen(meshBytes, tintBytes, key);
                    return new FaceGenFiles(meshBytes, tintBytes, FromGalathilOutput: true, PreviousTarget: previousKey);
                }
            }

            var meshArchived = selectedMesh is null;
            var tintArchived = selectedTint is null;
            selectedMesh ??= ReadFromModArchives(sourceFolder, meshRelative);
            selectedTint ??= ReadFromModArchives(sourceFolder, tintRelative);
            if (selectedMesh is not null && selectedTint is not null)
            {
                ValidateFaceGen(selectedMesh, selectedTint, key);
                return new FaceGenFiles(selectedMesh, selectedTint,
                    File.Exists(selectedMeshPath) ? selectedMeshPath : null,
                    File.Exists(selectedTintPath) ? selectedTintPath : null,
                    meshArchived || tintArchived);
            }

            // Preserve compatibility with replacers that ship FaceGen only in a
            // separate winning asset mod. Those external files are copied, never
            // removed, because they do not belong to the selected source mod.
            var meshFallback = ReadLoose(meshWinner?.Path);
            var tintFallback = ReadLoose(tintWinner?.Path);
            if (meshFallback is null || tintFallback is null)
                throw new FileNotFoundException($"The selected replacer does not provide both FaceGen files for {FormKeyText(key)}. Galathil needs its face mesh and tint to move the look.");
            if (meshWinner is { } meshOwner && !meshOwner.ModName.Equals(sourceMod, StringComparison.OrdinalIgnoreCase))
                warnings?.Add($"The selected mod does not contain {FormKeyText(key)}'s loose FaceGen; Galathil copied the active winner from '{meshOwner.ModName}' and left that mod unchanged.");
            ValidateFaceGen(meshFallback, tintFallback, key);
            return new FaceGenFiles(meshFallback, tintFallback);

            bool IsUnmanaged(string path) => !previousFaceGen.TryGetValue(Path.GetFullPath(path), out var hash) ||
                !File.Exists(path) || !HashMatches(File.ReadAllBytes(path), hash);
        }

        private (string ModName, int Index, string Path)? FindLooseWinner(string relative, int lastIndex = int.MaxValue,
            bool skipGenerated = false, Func<string, bool>? includePath = null)
        {
            (string ModName, int Index, string Path)? winner = null;
            var end = Math.Min(lastIndex, _modOrder.Count - 1);
            for (var index = 0; index <= end; index++)
            {
                var mod = _modOrder[index];
                if (!mod.Enabled) continue;
                if (skipGenerated && IsGeneratedOutputMod(mod.Name)) continue;
                var path = Path.Combine(_modsPath, mod.Name, relative);
                if (File.Exists(path) && (includePath is null || includePath(path))) winner = (mod.Name, index, path);
            }
            return winner;
        }

        private string? FindLooseConflictAfter(string relative, int sourceIndex)
        {
            for (var index = sourceIndex + 1; index < _modOrder.Count; index++)
            {
                var mod = _modOrder[index];
                if (!mod.Enabled || IsGeneratedOutputMod(mod.Name)) continue;
                if (File.Exists(Path.Combine(_modsPath, mod.Name, relative))) return mod.Name;
            }
            return null;
        }

        private static bool IsGeneratedOutputMod(string name) =>
            name.Equals(OutputModName, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Galathil - ", StringComparison.OrdinalIgnoreCase);

        private static byte[]? ReadLoose(string? path) => path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;

        private byte[]? ReadFromModArchives(string folder, string relative)
        {
            if (!Directory.Exists(folder)) return null;
            foreach (var archive in Directory.EnumerateFiles(folder, "*.bsa", SearchOption.TopDirectoryOnly))
                if (ReadArchive(archive, relative) is { } bytes) return bytes;
            return null;
        }

        private byte[]? ReadFromGameArchives(ModKey owner, string relative)
        {
            var archiveNames = new List<string>();
            if (!owner.FileName.ToString().Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase))
                archiveNames.Add(Path.GetFileNameWithoutExtension(owner.FileName.ToString()) + ".bsa");
            // Skyrim SE packs DLC FaceGen into the shared Skyrim archives, so the
            // FaceGen owner plugin does not always have a same-named BSA.
            archiveNames.AddRange(relative.StartsWith(MeshFolder, StringComparison.OrdinalIgnoreCase)
                ? SharedMeshArchives
                : SharedTextureArchives);
            foreach (var name in archiveNames)
            {
                var path = Path.Combine(_dataPath, name);
                if (File.Exists(path) && ReadArchive(path, relative) is { } bytes) return bytes;
            }
            return null;
        }

        private byte[]? ReadArchive(string archivePath, string relative)
        {
            try
            {
                if (!_archives.TryGetValue(archivePath, out var reader))
                    _archives[archivePath] = reader = Archive.CreateReader(GameRelease.SkyrimSE, new FilePath(archivePath), new FileSystem());
                var archiveFile = reader.Files.FirstOrDefault(f => NormalizePath(f.Path).Equals(NormalizePath(relative), StringComparison.OrdinalIgnoreCase));
                return archiveFile?.GetBytes();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                return null;
            }
        }

        private static string NormalizePath(string path) => path.Replace('/', '\\').TrimStart('\\');

        public static void ValidateFaceGen(byte[] mesh, byte[] tint, FormKey key)
        {
            if (mesh.Length < 64) throw new InvalidDataException($"FaceGen mesh for {FormKeyText(key)} is empty or incomplete.");
            if (tint.Length < 128 || tint[0] != (byte)'D' || tint[1] != (byte)'D' || tint[2] != (byte)'S' || tint[3] != (byte)' ')
                throw new InvalidDataException($"FaceGen tint for {FormKeyText(key)} is not a valid DDS file.");
        }
    }

    private static string FaceGenRelative(string folder, FormKey key, string extension) =>
        Path.Combine(folder, key.ModKey.FileName, key.ID.ToString("X8", CultureInfo.InvariantCulture) + extension);
}
