using System.Globalization;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Masters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

internal enum AppearanceOutputMode
{
    GalathilOutput,
    EspfePatch,
    SkyPatcher
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
    string? AppearanceMod = null);

internal sealed record GalathilOutputManifest(
    int Version,
    bool CopySkin,
    bool CopyBody,
    bool CopyOutfit,
    IReadOnlyList<AppearanceAssignment> Assignments,
    IReadOnlyList<string>? DisabledSourcePlugins = null,
    IReadOnlyList<GalathilAssetLink>? AssetLinks = null,
    IReadOnlyDictionary<string, int>? SourcePluginLoadOrder = null);

internal sealed record GalathilAssetLink(string SourceMod, string SourcePlugin);

internal sealed record GalathilModEntry(string Name, bool Enabled);

internal sealed record AppearanceGenerationResult(
    string ModName,
    string ModFolder,
    string? PluginPath,
    string? SkyPatcherPath,
    string BackupPath,
    IReadOnlyList<string> Warnings);

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
        AppearanceOutputMode mode,
        bool copySkin,
        bool copyBody,
        bool copyOutfit,
        bool changeTargetRace)
    {
        if (mode == AppearanceOutputMode.GalathilOutput)
            return GenerateGalathilOutput(instancePath, profileName, profilePath, modsPath, dataPath,
                activePluginOrder, activePlugins, pluginPaths, modOrder, assignments, copySkin, copyBody, copyOutfit);

        if (assignments.Count == 0) throw new InvalidOperationException("Add an appearance assignment first.");
        var sourceMods = assignments.Select(a => a.SourceMod).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sourcePlugins = assignments.Select(a => a.SourcePlugin).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sourceMods.Length != 1 || sourcePlugins.Length != 1)
            throw new InvalidOperationException("Apply one source mod/plugin at a time. This lets Galathil safely replace that ESP without adding another plugin.");
        if (mode == AppearanceOutputMode.SkyPatcher &&
            !modOrder.Any(m => m.Enabled && m.Name.Contains("SkyPatcher", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("SkyPatcher is not enabled in this MO2 profile.");

        var duplicateTargets = assignments.GroupBy(a => a.TargetFormKey, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateTargets is not null)
            throw new InvalidOperationException($"More than one look is assigned to {duplicateTargets.First().TargetName}. Remove one assignment before creating the mod.");

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
                throw new InvalidOperationException($"Could not find the earlier version of {assignment.SourceName} needed to restore its original appearance.");

            var source = sourceChain[sourceIndex].Record;
            var beforeSource = sourceChain[sourceIndex - 1].Record;
            var targetChain = GetChain(npcChains, assignment.TargetFormKey, assignment.TargetName);
            var target = targetChain[^1].Record;
            if (mode == AppearanceOutputMode.EspfePatch)
            {
                var sourceOrder = activePluginOrder.IndexOf(sourcePlugin);
                var targetWinnerOrder = activePluginOrder.IndexOf(targetChain[^1].Plugin);
                if (sourceOrder < 0 || targetWinnerOrder < 0 || targetWinnerOrder >= sourceOrder)
                    throw new InvalidOperationException($"The source plugin loads before {targetChain[^1].Plugin}, which currently wins for {assignment.TargetName}. An in-place ESP cannot safely override it; choose SkyPatcher or move the target's winning plugin earlier.");
            }
            var sourceFemale = IsFemale(source);
            var targetFemale = IsFemale(target);
            if (sourceFemale != targetFemale && mode == AppearanceOutputMode.EspfePatch)
                throw new InvalidOperationException($"{assignment.SourceName} and {assignment.TargetName} have different genders. Use a SkyPatcher output to transfer this look.");

            var raceMismatch = source.Race.FormKey != target.Race.FormKey;
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
            var hasNewOutfit = source.DefaultOutfit is { IsNull: false } &&
                source.DefaultOutfit.FormKeyNullable != beforeSource.DefaultOutfit?.FormKeyNullable;
            if (hasNewOutfit && !copyOutfit)
                warnings.Add($"{assignment.TargetName} keeps its own outfit because the optional new outfit was not selected.");

            var sourceFace = assetResolver.ReadCurrentReplacerFaceGen(assignment.SourceMod, source.FormKey);
            var restoredFace = assetResolver.ReadEarlierFaceGen(assignment.SourceMod, sourceChain[sourceIndex - 1].Plugin, beforeSource.FormKey);
            if (AppearanceSignature(source, includeSkin: true, includeOutfit: true).Equals(AppearanceSignature(beforeSource, includeSkin: true, includeOutfit: true), StringComparison.Ordinal) &&
                sourceFace.Mesh.AsSpan().SequenceEqual(restoredFace.Mesh) &&
                sourceFace.Tint.AsSpan().SequenceEqual(restoredFace.Tint))
                throw new InvalidOperationException($"{assignment.SourceName} already has the earlier appearance and FaceGen. Restore the matching Galathil backup before redirecting this NPC again.");
            prepared.Add(new PreparedAssignment(assignment, source, beforeSource, target, sourceFace, restoredFace,
                raceMismatch && changeTargetRace, copySourceSkinArmor, copyOutfit && hasNewOutfit));
        }

        var pluginFileName = sourcePlugin;
        var stageFolder = Path.Combine(Path.GetTempPath(), "Galathil", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageFolder);

        string? pluginPath = null;
        string? skyPatcherPath = null;
        string? backupPath = null;
        var skyClones = new List<(PreparedAssignment Assignment, FormKey CloneKey)>();
        var createdSkyNpcKeys = new HashSet<FormKey>();
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
            var originalGroupCounts = CountMajorRecordGroups(output);

            foreach (var item in prepared)
            {
                var sourceSignature = AppearanceSignature(item.Source, includeSkin: true, includeOutfit: true);
                var oldSourceSignature = AppearanceSignature(item.BeforeSource, includeSkin: true, includeOutfit: true);
                if (!sourceSignature.Equals(oldSourceSignature, StringComparison.Ordinal))
                {
                    var restore = output.Npcs.GetOrAddAsOverride(item.Source);
                    CopyAppearance(restore, item.BeforeSource, includeRace: false, includeSkin: true, includeOutfit: true);
                }

                if (mode == AppearanceOutputMode.EspfePatch)
                {
                    var targetWinner = GetChain(npcChains, item.Assignment.TargetFormKey, item.Assignment.TargetName)[^1].Plugin;
                    foreach (var requiredPlugin in new[] { item.Target.FormKey.ModKey.FileName.ToString(), targetWinner }.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (requiredPlugin.Equals(output.ModKey.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!output.ModHeader.MasterReferences.Any(m => StringComparer.OrdinalIgnoreCase.Equals(m.Master.FileName.ToString(), requiredPlugin)))
                            throw new InvalidOperationException($"{pluginFileName} does not already require {requiredPlugin}. Galathil will not add a new master to the source plugin; use SkyPatcher for this target.");
                    }
                    if (item.CopyRace || !AppearanceSignature(item.Target, item.CopySkinArmor, item.CopyOutfit).Equals(AppearanceSignature(item.Source, item.CopySkinArmor, item.CopyOutfit), StringComparison.Ordinal))
                    {
                        var targetOverride = output.Npcs.GetOrAddAsOverride(item.Target);
                        CopyAppearance(targetOverride, item.Source, item.CopyRace, item.CopySkinArmor, item.CopyOutfit);
                    }

                    WriteFaceGen(stageFolder, item.Target.FormKey, item.SourceFaceGen);
                    expectedFaceGen[item.Target.FormKey] = item.SourceFaceGen;
                }
                else
                {
                    FormKey? cloneFormKey = requiresLightFlag ? FindFreeLightFormKey(output) : null;
                    var clone = output.Npcs.DuplicateInAsNewRecord(item.Source, cloneFormKey);
                    clone.EditorID = MakeEditorId(item.Source, clone.FormKey);
                    createdSkyNpcKeys.Add(clone.FormKey);
                    WriteFaceGen(stageFolder, clone.FormKey, item.SourceFaceGen);
                    expectedFaceGen[clone.FormKey] = item.SourceFaceGen;
                    skyClones.Add((item, clone.FormKey));
                }

                WriteFaceGen(stageFolder, item.Source.FormKey, item.PreviousFaceGen);
                expectedFaceGen[item.Source.FormKey] = item.PreviousFaceGen;
            }

            if (requiresLightFlag) ValidateLightPlugin(output, createdSkyNpcKeys);
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

            if (mode == AppearanceOutputMode.SkyPatcher)
            {
                var configFolder = Path.Combine(stageFolder, "SKSE", "Plugins", "SkyPatcher", "npc");
                Directory.CreateDirectory(configFolder);
                skyPatcherPath = Path.Combine(configFolder, "Galathil_" + SafeFileStem(Path.GetFileNameWithoutExtension(sourcePlugin)) + ".ini");
                File.WriteAllText(skyPatcherPath, BuildSkyPatcherConfig(skyClones, pluginFileName), new UTF8Encoding(false));
            }

            var stagedFiles = Directory.GetFiles(stageFolder, "*", SearchOption.AllDirectories);
            var replacedFiles = stagedFiles.Select(path => (Stage: path, Relative: Path.GetRelativePath(stageFolder, path), Destination: Path.Combine(sourceModFolder, Path.GetRelativePath(stageFolder, path)))).ToList();
            var existingFiles = replacedFiles.Where(f => File.Exists(f.Destination)).Select(f => f.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var backupFolder = Path.Combine(AppContext.BaseDirectory, "Backups", SafeFileStem(sourceMod), DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(backupFolder);
            backupPath = Path.Combine(backupFolder, "source-files.zip");
            var backupNotes =
                $"Galathil replaced {sourcePlugin} in place for profile '{profileName}'.\r\n" +
                string.Join("\r\n", assignments.Select(a => $"{a.SourceName} ({a.SourceFormKey}) -> {a.TargetName} ({a.TargetFormKey})")) +
                "\r\n\r\nFiles written (restore files in the ZIP and remove entries absent from it to undo):\r\n" +
                string.Join("\r\n", replacedFiles.Select(f => f.Relative));
            CreateBackupArchive(backupPath, sourceModFolder, replacedFiles.Where(f => existingFiles.Contains(f.Relative)).Select(f => f.Destination), backupNotes);

            try
            {
                foreach (var file in replacedFiles) ReplaceFileAtomically(file.Stage, file.Destination);
                ValidateWrittenPlugin(sourcePluginPath, ModKey.FromFileName(sourcePlugin), expectedGroupCounts, expectedMasters, expectedFlags, requiresLightFlag, originalDeletedNpcCount);
                ValidateWrittenFaceGen(sourceModFolder, expectedFaceGen);
                if (skyPatcherPath is not null)
                {
                    skyPatcherPath = Path.Combine(sourceModFolder, Path.GetRelativePath(stageFolder, skyPatcherPath));
                    if (!File.Exists(skyPatcherPath)) throw new InvalidDataException("The SkyPatcher rule was not written into the source mod.");
                }
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
                RestoreBackupArchive(backupPath, sourceModFolder, replacedFiles.Select(f => f.Relative), existingFiles);
                throw;
            }
            return new AppearanceGenerationResult(sourceMod, sourceModFolder, pluginPath, skyPatcherPath, backupPath, warnings);
        }
        catch (LightPluginFormIdCapacityException capacityError) when (mode == AppearanceOutputMode.SkyPatcher)
        {
            if (Directory.Exists(stageFolder)) Directory.Delete(stageFolder, recursive: true);
            try
            {
                var directResult = Generate(
                    instancePath, profileName, profilePath, modsPath, dataPath,
                    activePluginOrder, activePlugins, pluginPaths, modOrder, assignments,
                    AppearanceOutputMode.EspfePatch, copySkin, copyBody, copyOutfit, changeTargetRace);
                return directResult with
                {
                    Warnings = directResult.Warnings.Append(
                        $"SkyPatcher could not fit its appearance NPC into the source ESL ({capacityError.FormKey} exceeds the light-plugin ID range), so Galathil used a direct in-place ESP transfer instead.").ToArray()
                };
            }
            catch (Exception directError)
            {
                throw new InvalidOperationException(
                    $"SkyPatcher needs a new NPC record, but {capacityError.FormKey} would exceed the source ESL's ID range. The direct in-place fallback was also blocked: {directError.Message}",
                    directError);
            }
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
        bool copySkin,
        bool copyBody,
        bool copyOutfit)
    {
        if (assignments.Count > 0 && !modOrder.Any(m => m.Enabled && m.Name.Contains("SkyPatcher", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("SkyPatcher is not enabled in this MO2 profile.");
        if (Process.GetProcessesByName("SkyrimSE").Length > 0)
            throw new InvalidOperationException("Close Skyrim before updating Galathil Output.");

        var duplicateTargets = assignments.GroupBy(a => NormalizeFormKey(a.TargetFormKey), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateTargets is not null)
            throw new InvalidOperationException($"More than one look is assigned to {duplicateTargets.First().TargetName}. Remove one assignment before creating the output.");

        foreach (var assignment in assignments)
        {
            if (!modOrder.Any(m => m.Enabled && m.Name.Equals(assignment.SourceMod, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"The source mod '{assignment.SourceMod}' is disabled in this MO2 profile.");
            var sourceFolder = Path.GetFullPath(Path.Combine(modsPath, assignment.SourceMod));
            var sourcePluginPath = Path.GetFullPath(Path.Combine(sourceFolder, assignment.SourcePlugin));
            if (!IsChildPath(modsPath, sourceFolder) || !IsChildPath(sourceFolder, sourcePluginPath) || !File.Exists(sourcePluginPath))
                throw new InvalidOperationException($"Could not find {assignment.SourcePlugin} inside the selected MO2 mod '{assignment.SourceMod}'.");
            if (!pluginPaths.TryGetValue(assignment.SourcePlugin, out var activePath) ||
                !Path.GetFullPath(activePath).Equals(sourcePluginPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Another mod is overriding {assignment.SourcePlugin}. Select the active MO2 winner for this replacer.");
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
        var previouslyManagedDisabled = (previousManifest?.DisabledSourcePlugins ?? Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            .Select(NormalizeFormKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var npcChains = assignments.Count == 0
            ? new Dictionary<string, List<PluginNpc>>(StringComparer.OrdinalIgnoreCase)
            : ReadNpcChains(activePluginOrder, pluginPaths, requestedKeys);
        var prepared = new List<SharedPreparedAssignment>();
        var warnings = new List<string>();
        var assetResolver = new FaceGenAssetResolver(modsPath, dataPath, modOrder, pluginPaths);

        foreach (var assignment in assignments)
        {
            var sourceModFolder = Path.GetFullPath(Path.Combine(modsPath, assignment.SourceMod));
            var sourcePluginPath = Path.GetFullPath(Path.Combine(sourceModFolder, assignment.SourcePlugin));
            var sourceChain = GetChain(npcChains, NormalizeFormKey(assignment.SourceFormKey), assignment.SourceName);
            var sourceIndex = sourceChain.FindIndex(r => r.Plugin.Equals(assignment.SourcePlugin, StringComparison.OrdinalIgnoreCase));
            var sourcePluginActive = activePlugins.Contains(assignment.SourcePlugin);
            Npc source;
            Npc beforeSource;
            FaceGenFiles? previousFaceGen = null;
            if (sourcePluginActive)
            {
                if (sourceIndex <= 0)
                    throw new InvalidOperationException($"Could not find both {assignment.SourceName} and the earlier version needed to restore it before {assignment.SourcePlugin}.");
                var laterOverride = sourceChain.Skip(sourceIndex + 1).FirstOrDefault(r => !IsGalathilOutputPlugin(r.Plugin));
                if (laterOverride is not null)
                    throw new InvalidOperationException($"{assignment.SourceName} is overwritten by '{laterOverride.Plugin}' after '{assignment.SourcePlugin}'. Select the active winning replacer.");
                source = sourceChain[sourceIndex].Record;
                beforeSource = sourceChain[sourceIndex - 1].Record;
                previousFaceGen = assetResolver.ReadEarlierFaceGen(assignment.SourceMod, sourceChain[sourceIndex - 1].Plugin, source.FormKey);
            }
            else
            {
                using var sourcePluginMod = SkyrimMod.CreateFromBinaryOverlay(sourcePluginPath, SkyrimRelease.SkyrimSE);
                source = sourcePluginMod.Npcs.FirstOrDefault(n => FormKeyText(n.FormKey).Equals(NormalizeFormKey(assignment.SourceFormKey), StringComparison.OrdinalIgnoreCase))?.DeepCopy()
                    ?? throw new InvalidOperationException($"{assignment.SourceName} is not present in {assignment.SourcePlugin}.");
                beforeSource = sourceChain[^1].Record;
            }
            var targetChain = GetChain(npcChains, NormalizeFormKey(assignment.TargetFormKey), assignment.TargetName);
            var target = targetChain[^1].Record;
            var appearance = source;
            if (!string.IsNullOrWhiteSpace(assignment.AppearanceFormKey))
            {
                var appearancePlugin = assignment.AppearancePlugin ?? assignment.SourcePlugin;
                if (!pluginPaths.TryGetValue(appearancePlugin, out var appearancePath))
                    throw new InvalidOperationException($"The saved appearance for {assignment.SourceName} refers to missing plugin {appearancePlugin}.");
                using var appearancePluginMod = SkyrimMod.CreateFromBinaryOverlay(appearancePath, SkyrimRelease.SkyrimSE);
                appearance = appearancePluginMod.Npcs.FirstOrDefault(n => FormKeyText(n.FormKey).Equals(NormalizeFormKey(assignment.AppearanceFormKey), StringComparison.OrdinalIgnoreCase))?.DeepCopy()
                    ?? throw new InvalidOperationException($"The saved appearance for {assignment.SourceName} is not present in {appearancePlugin}. Restore the source mod's Galathil backup first.");
            }

            var copiedSkin = copySkin && assignment.HasCustomSkin || copyBody && assignment.HasCustomBody;
            var hasNewOutfit = assignment.HasNewOutfit || (appearance.DefaultOutfit is { IsNull: false } &&
                appearance.DefaultOutfit.FormKeyNullable != beforeSource.DefaultOutfit?.FormKeyNullable);
            var appearanceMod = assignment.AppearanceMod ?? assignment.SourceMod;
            var sourceFaceGen = assetResolver.ReadCurrentReplacerFaceGen(appearanceMod, appearance.FormKey);
            if (sourcePluginActive && previousFaceGen is not null && string.IsNullOrWhiteSpace(assignment.AppearanceFormKey) &&
                AppearanceSignature(source, includeSkin: true, includeOutfit: true).Equals(
                    AppearanceSignature(beforeSource, includeSkin: true, includeOutfit: true), StringComparison.Ordinal) &&
                sourceFaceGen.Mesh.AsSpan().SequenceEqual(previousFaceGen.Mesh) && sourceFaceGen.Tint.AsSpan().SequenceEqual(previousFaceGen.Tint))
                throw new InvalidOperationException($"{assignment.SourceName} has already been restored by an older Galathil transfer. Select its existing Galathil mapping or restore the matching backup first.");
            if (!string.IsNullOrWhiteSpace(assignment.AppearanceFormKey) &&
                previousFaceGen is not null &&
                AppearanceSignature(appearance, includeSkin: true, includeOutfit: true).Equals(
                    AppearanceSignature(beforeSource, includeSkin: true, includeOutfit: true), StringComparison.Ordinal) &&
                sourceFaceGen.Mesh.AsSpan().SequenceEqual(previousFaceGen.Mesh) && sourceFaceGen.Tint.AsSpan().SequenceEqual(previousFaceGen.Tint))
                throw new InvalidOperationException($"The saved Galathil appearance for {assignment.SourceName} matches its earlier appearance. Restore its source mod backup before importing it.");

            if (!IsFemale(appearance).Equals(IsFemale(target)))
                warnings.Add($"SkyPatcher will change {assignment.TargetName}'s sex to match {assignment.SourceName}; check voice, animations, and behavior in game.");
            if (appearance.Race.FormKey != target.Race.FormKey)
                warnings.Add($"SkyPatcher will change {assignment.TargetName}'s race to match {assignment.SourceName}; this can affect race-based behavior.");
            if (!copiedSkin && (assignment.HasCustomSkin || assignment.HasCustomBody))
                warnings.Add($"{assignment.TargetName} keeps its current skin/body. Select Skin or Body to transfer the replacer's unique assets.");
            if (assignment.HasCustomSkin && assignment.HasCustomBody && copySkin != copyBody)
                warnings.Add($"{assignment.SourceName}'s skin textures and body mesh use the same skin record, so both may transfer together.");
            if (hasNewOutfit && !copyOutfit)
                warnings.Add($"{assignment.TargetName} keeps its current outfit because New outfit was not selected.");

            prepared.Add(new SharedPreparedAssignment(assignment, source, appearance, beforeSource, target,
                sourceFaceGen, previousFaceGen, sourcePluginActive, copiedSkin, copyOutfit && hasNewOutfit));
        }

        var stageFolder = Path.Combine(modsPath, ".galathil-output-stage-" + Guid.NewGuid().ToString("N"));
        if (!IsChildPath(modsPath, stageFolder)) throw new InvalidOperationException("Could not create Galathil Output staging folder inside the MO2 mods directory.");
        Directory.CreateDirectory(stageFolder);
        var pluginNames = new List<string>();
        var expectedFaceGen = new Dictionary<FormKey, FaceGenFiles>();
        var outputMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cloneRules = new List<(SharedPreparedAssignment Item, FormKey CloneKey, string PluginName)>();
            var shardCapacity = (int)(LastLightObjectId - FirstLightObjectId + 1);
            for (var offset = 0; offset < prepared.Count; offset += shardCapacity)
            {
                var shard = offset / shardCapacity + 1;
                var pluginName = OutputPluginName(shard);
                pluginNames.Add(pluginName);
                var output = new SkyrimMod(ModKey.FromFileName(pluginName), SkyrimRelease.SkyrimSE);
                output.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
                var shardAssignments = prepared.Skip(offset).Take(shardCapacity).ToArray();
                uint localId = FirstLightObjectId;
                foreach (var item in shardAssignments)
                {
                    if (item.RestoreSource && !NormalizeFormKey(item.Assignment.SourceFormKey).Equals(NormalizeFormKey(item.Assignment.TargetFormKey), StringComparison.OrdinalIgnoreCase))
                    {
                        if (item.PreviousFaceGen is null) throw new InvalidDataException($"Earlier FaceGen for {item.Assignment.SourceName} was not available to restore the source NPC.");
                        var restore = output.Npcs.GetOrAddAsOverride(item.Source);
                        CopyAppearance(restore, item.BeforeSource, includeRace: false, includeSkin: true, includeOutfit: true);
                        WriteFaceGen(stageFolder, item.Source.FormKey, item.PreviousFaceGen);
                        expectedFaceGen[item.Source.FormKey] = item.PreviousFaceGen;
                    }

                    var cloneKey = new FormKey(output.ModKey, localId++);
                    var clone = output.Npcs.DuplicateInAsNewRecord(item.Appearance, cloneKey);
                    clone.EditorID = MakeEditorId(item.Appearance, clone.FormKey);
                    WriteFaceGen(stageFolder, clone.FormKey, item.SourceFaceGen);
                    expectedFaceGen[clone.FormKey] = item.SourceFaceGen;
                    cloneRules.Add((item, clone.FormKey, pluginName));
                }

                ValidateLightPlugin(output, shardAssignments.Select((_, i) => new FormKey(output.ModKey, FirstLightObjectId + (uint)i)).ToHashSet());
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
                ValidateWrittenFaceGen(stageFolder, expectedFaceGen.Where(kv => kv.Key.ModKey == output.ModKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value));
            }

            if (prepared.Count > 0)
            {
                var configFolder = Path.Combine(stageFolder, "SKSE", "Plugins", "SkyPatcher", "npc");
                Directory.CreateDirectory(configFolder);
                var lines = new List<string>
                {
                    "; Generated by Galathil. The shared appearance records are in the Galathil Output ESPFE plugin(s).",
                    "; copyVisualStyle also applies the source NPC's race and sex in the installed SkyPatcher implementation."
                };
                foreach (var (item, cloneKey, pluginName) in cloneRules)
                {
                    var targetIdentifier = FormKeyIdentifier(item.Target.FormKey);
                    var cloneIdentifier = FormKeyIdentifier(cloneKey);
                    var rule = new List<string>
                    {
                        "filterByNpcs=" + targetIdentifier,
                        "filterByTemplate=" + targetIdentifier,
                        "restrictToTemplateFlags=traits",
                        "copyVisualStyle=" + cloneIdentifier
                    };
                    if (item.CopyOutfit && item.Appearance.DefaultOutfit is { IsNull: false } outfit)
                        rule.Add("outfitDefault=" + FormKeyIdentifier(outfit.FormKey));
                    lines.Add(string.Join(':', rule));
                    if (item.CopySkinArmor && item.Appearance.WornArmor is { IsNull: false } skin)
                        lines.Add("filterByNpcs=" + targetIdentifier + ":skin=" + FormKeyIdentifier(skin.FormKey));
                    else if (item.Target.WornArmor is { IsNull: false } targetSkin)
                        lines.Add("filterByNpcs=" + targetIdentifier + ":skin=" + FormKeyIdentifier(targetSkin.FormKey));
                }
                File.WriteAllText(Path.Combine(configFolder, "Galathil_Output.ini"), string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
            }

            var assetLinks = assignments.Select(a => new GalathilAssetLink(a.SourceMod, a.SourcePlugin))
                .DistinctBy(link => AssetLinkKey(link.SourceMod, link.SourcePlugin), StringComparer.OrdinalIgnoreCase).ToArray();
            var oldConfigPaths = FindMigratedLegacyConfigs(modsPath, assetLinks, assignments, warnings);
            var legacyConfigBlocks = FindUnmigratedLegacyConfigPlugins(modsPath, assetLinks, assignments);
            var disabledSourcePlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourcePlugin in sourcePlugins)
            {
                var dependent = FindActivePluginDependent(sourcePlugin, activePlugins, pluginPaths, pluginNames);
                var requiredByOutput = outputMasters.Contains(sourcePlugin);
                var hasUnmigratedConfig = legacyConfigBlocks.Contains(sourcePlugin);
                if (!activePlugins.Contains(sourcePlugin))
                {
                    if (previouslyManagedDisabled.Contains(sourcePlugin))
                    {
                        if (!requiredByOutput && dependent is null && !hasUnmigratedConfig)
                            disabledSourcePlugins.Add(sourcePlugin);
                        else
                            warnings.Add($"Re-enabled {sourcePlugin} because {(requiredByOutput ? "Galathil Output uses records from it" : dependent is not null ? $"{dependent} requires it" : "its old Galathil config still has routes")}. Its asset mod remains enabled.");
                    }
                    else if (requiredByOutput || dependent is not null || hasUnmigratedConfig)
                    {
                        var reason = requiredByOutput ? "Galathil Output needs it as a master" : dependent is not null ? $"{dependent} requires it" : "it still contains SkyPatcher routes";
                        throw new InvalidOperationException($"{sourcePlugin} is disabled in MO2, but {reason}. Enable it in this profile and apply again.");
                    }
                    continue;
                }
                if (!requiredByOutput && dependent is null && !hasUnmigratedConfig)
                    disabledSourcePlugins.Add(sourcePlugin);
                else
                {
                    var reason = requiredByOutput ? "Galathil Output uses records from it" : dependent is not null ? $"{dependent} requires it" : "its old Galathil config has routes not yet imported";
                    warnings.Add($"Kept {sourcePlugin} enabled because {reason}. Its mod still provides the linked assets.");
                }
            }
            var restoreSourcePlugins = (previousManifest?.DisabledSourcePlugins ?? Array.Empty<string>())
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
            var inactiveAfterApply = new HashSet<string>(disabledSourcePlugins, StringComparer.OrdinalIgnoreCase);
            foreach (var sourcePlugin in sourcePlugins)
                if (!activePlugins.Contains(sourcePlugin) && !restoreSourcePlugins.Contains(sourcePlugin, StringComparer.OrdinalIgnoreCase))
                    inactiveAfterApply.Add(sourcePlugin);
            var manifest = new GalathilOutputManifest(1, copySkin, copyBody, copyOutfit, assignments.ToArray(),
                disabledSourcePlugins.ToArray(), assetLinks, sourcePluginLoadOrder);
            File.WriteAllText(Path.Combine(stageFolder, OutputManifestName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            var notes = "Generated by Galathil for MO2 profile '" + profileName + "'." + Environment.NewLine +
                string.Join(Environment.NewLine, assignments.Select(a => $"- {a.SourceName} ({a.SourceFormKey}) -> {a.TargetName} ({a.TargetFormKey})"));
            File.WriteAllText(Path.Combine(stageFolder, "README.txt"), notes + Environment.NewLine, new UTF8Encoding(false));
            WriteOutputAssetMarkers(stageFolder, assetLinks, pluginNames, cloneRules, inactiveAfterApply);

            var backupFolder = Path.Combine(AppContext.BaseDirectory, "Backups", "Galathil Output", DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(backupFolder);
            var backupPath = Path.Combine(backupFolder, "previous-output.zip");
            if (Directory.Exists(outputFolder)) ZipFile.CreateFromDirectory(outputFolder, backupPath, CompressionLevel.Optimal, includeBaseDirectory: true);
            else File.WriteAllText(backupPath + ".txt", "No prior Galathil Output mod existed.");

            var markerChanges = UpdateAssetModMarkers(modsPath, assetLinks, previousManifest?.AssetLinks ?? Array.Empty<GalathilAssetLink>(), pluginNames, cloneRules, inactiveAfterApply, backupFolder);
            var removedConfigChanges = new List<AssetMarkerChange>();
            try
            {
                removedConfigChanges = BackupAndRemoveLegacyConfigs(oldConfigPaths, modsPath, backupFolder);
                InstallGalathilOutput(stageFolder, outputFolder, profilePath, pluginNames, pluginPaths, modsPath,
                    disabledSourcePlugins, restoreSourcePlugins, pluginPaths, sourcePluginLoadOrder);
            }
            catch
            {
                RollbackAssetModMarkers(removedConfigChanges);
                RollbackAssetModMarkers(markerChanges);
                throw;
            }
            var finalPluginPath = pluginNames.Count == 0 ? null : Path.Combine(outputFolder, pluginNames[0]);
            var finalSkyPath = prepared.Count == 0 ? null : Path.Combine(outputFolder, "SKSE", "Plugins", "SkyPatcher", "npc", "Galathil_Output.ini");
            return new AppearanceGenerationResult(OutputModName, outputFolder, finalPluginPath, finalSkyPath,
                Directory.Exists(outputFolder) && File.Exists(backupPath) ? backupPath : backupPath + ".txt", warnings.Distinct(StringComparer.Ordinal).ToArray());
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
        IReadOnlyDictionary<string, string> profilePluginPaths,
        IReadOnlyDictionary<string, int> sourcePluginLoadOrder)
    {
        foreach (var name in pluginNames)
            if (pluginPaths.TryGetValue(name, out var path) && !Path.GetFullPath(path).StartsWith(outputFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Another mod already provides {name}. Disable or rename that plugin before using Galathil Output.");

        var oldFolder = outputFolder + ".galathil-old-" + Guid.NewGuid().ToString("N");
        var movedOld = false;
        try
        {
            if (Directory.Exists(outputFolder))
            {
                Directory.Move(outputFolder, oldFolder);
                movedOld = true;
            }
            Directory.Move(stageFolder, outputFolder);
            UpdateGalathilOutputProfile(profilePath, pluginNames, disableSourcePlugins, restoreSourcePlugins, profilePluginPaths, sourcePluginLoadOrder);
        }
        catch
        {
            if (Directory.Exists(outputFolder) && !Directory.Exists(stageFolder)) Directory.Move(outputFolder, stageFolder);
            if (movedOld && Directory.Exists(oldFolder) && !Directory.Exists(outputFolder)) Directory.Move(oldFolder, outputFolder);
            throw;
        }
        if (movedOld)
        {
            try { Directory.Delete(oldFolder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string AssetLinkKey(string sourceMod, string sourcePlugin) => sourceMod + "|" + sourcePlugin;

    private static string SourceAssetMarkerName(GalathilAssetLink link) =>
        "Galathil Link - " + SafeFileStem(link.SourcePlugin) + ".txt";

    private static string OutputAssetMarkerName(GalathilAssetLink link) =>
        "Asset Mod - " + SafeFileStem(link.SourcePlugin) + " - " + SafeFileStem(link.SourceMod) + ".txt";

    private static void WriteOutputAssetMarkers(string stageFolder, IReadOnlyList<GalathilAssetLink> links,
        IReadOnlyList<string> outputPlugins, IReadOnlyList<(SharedPreparedAssignment Item, FormKey CloneKey, string PluginName)> cloneRules,
        IReadOnlySet<string> disabledSourcePlugins)
    {
        var indexLines = new List<string> { "Generated by Galathil. These MO2 mods provide the linked NPC appearance assets." };
        foreach (var link in links)
        {
            var linkedPlugins = cloneRules.Where(rule =>
                    rule.Item.Assignment.SourceMod.Equals(link.SourceMod, StringComparison.OrdinalIgnoreCase) &&
                    rule.Item.Assignment.SourcePlugin.Equals(link.SourcePlugin, StringComparison.OrdinalIgnoreCase))
                .Select(rule => rule.PluginName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var marker = Path.Combine(stageFolder, OutputAssetMarkerName(link));
            var text = "; Generated by Galathil. This file links the asset mod to its Galathil Output records." + Environment.NewLine +
                "Asset mod: " + link.SourceMod + Environment.NewLine +
                "Source ESP: " + link.SourcePlugin + Environment.NewLine +
                "Galathil output ESPFE: " + (linkedPlugins.Length == 0 ? string.Join(", ", outputPlugins) : string.Join(", ", linkedPlugins)) + Environment.NewLine +
                (disabledSourcePlugins.Contains(link.SourcePlugin)
                    ? "The source ESP is disabled in this profile; the asset mod stays enabled." + Environment.NewLine
                    : "The source ESP remains enabled in this profile." + Environment.NewLine) +
                "SkyPatcher config: SKSE/Plugins/SkyPatcher/npc/Galathil_Output.ini" + Environment.NewLine;
            File.WriteAllText(marker, text, new UTF8Encoding(false));
            indexLines.Add($"- {link.SourceMod} — {link.SourcePlugin}");
        }
        File.WriteAllText(Path.Combine(stageFolder, "Galathil Asset Mods.txt"),
            string.Join(Environment.NewLine, indexLines) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static List<AssetMarkerChange> UpdateAssetModMarkers(string modsPath,
        IReadOnlyList<GalathilAssetLink> links, IReadOnlyList<GalathilAssetLink> previousLinks,
        IReadOnlyList<string> outputPlugins,
        IReadOnlyList<(SharedPreparedAssignment Item, FormKey CloneKey, string PluginName)> cloneRules,
        IReadOnlySet<string> disabledSourcePlugins, string backupFolder)
    {
        var changes = new List<AssetMarkerChange>();
        var desired = links.ToDictionary(link => AssetLinkKey(link.SourceMod, link.SourcePlugin), StringComparer.OrdinalIgnoreCase);
        var prior = previousLinks.ToDictionary(link => AssetLinkKey(link.SourceMod, link.SourcePlugin), StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, link) in prior)
        {
            if (desired.ContainsKey(key)) continue;
            var folder = SafeAssetModFolder(modsPath, link.SourceMod);
            var path = Path.Combine(folder, SourceAssetMarkerName(link));
            if (File.Exists(path)) paths.Add(path);
        }
        foreach (var link in links)
        {
            var folder = SafeAssetModFolder(modsPath, link.SourceMod);
            paths.Add(Path.Combine(folder, SourceAssetMarkerName(link)));
        }

        try
        {
            foreach (var path in paths)
            {
                byte[]? oldBytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if (oldBytes is not null && !Encoding.UTF8.GetString(oldBytes).StartsWith("; Generated by Galathil.", StringComparison.Ordinal))
                    throw new IOException($"A file already exists where Galathil needs an asset link note: {path}");
                changes.Add(new AssetMarkerChange(path, oldBytes));
                if (oldBytes is not null)
                {
                    var backupFile = Path.Combine(backupFolder, "asset-markers", SafeFileStem(Path.GetFileName(Path.GetDirectoryName(path)) + "-" + Path.GetFileName(path)) + ".bak");
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.WriteAllBytes(backupFile, oldBytes);
                }
                var markerLink = links.FirstOrDefault(link => Path.GetFullPath(Path.Combine(SafeAssetModFolder(modsPath, link.SourceMod), SourceAssetMarkerName(link)))
                    .Equals(path, StringComparison.OrdinalIgnoreCase));
                if (markerLink is null)
                {
                    File.Delete(path);
                    continue;
                }
                var linkedPlugins = cloneRules.Where(rule =>
                        rule.Item.Assignment.SourceMod.Equals(markerLink.SourceMod, StringComparison.OrdinalIgnoreCase) &&
                        rule.Item.Assignment.SourcePlugin.Equals(markerLink.SourcePlugin, StringComparison.OrdinalIgnoreCase))
                    .Select(rule => rule.PluginName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var state = disabledSourcePlugins.Contains(markerLink.SourcePlugin)
                    ? "The source ESP is disabled in this profile. Keep this mod enabled for its linked face and body assets."
                    : "The source ESP remains enabled in this profile.";
                var text = "; Generated by Galathil. Keep this asset mod enabled while its mapping is in Galathil Output." + Environment.NewLine +
                    "Source ESP: " + markerLink.SourcePlugin + Environment.NewLine +
                    "Galathil output ESPFE: " + string.Join(", ", linkedPlugins) + Environment.NewLine + state + Environment.NewLine;
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            return changes;
        }
        catch
        {
            RollbackAssetModMarkers(changes);
            throw;
        }
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

            var pluginLines = File.ReadAllLines(pluginsPath).Where(line => !outputPlugins.Contains(line.TrimStart('*').Trim())).ToList();
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

            var orderLines = File.ReadAllLines(loadOrderPath).Where(line => !outputPlugins.Contains(line.Trim())).ToList();
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
        if (includeSkin)
            destination.WornArmor = new FormLinkNullable<IArmorGetter>(source.WornArmor?.FormKeyNullable);
        if (includeOutfit)
            destination.DefaultOutfit = new FormLinkNullable<IOutfitGetter>(source.DefaultOutfit?.FormKeyNullable);
        if (includeRace) destination.Race = source.Race.FormKey.ToLink<IRaceGetter>();
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

    private static string BuildSkyPatcherConfig(IReadOnlyList<(PreparedAssignment Assignment, FormKey CloneKey)> assignments, string pluginName)
    {
        var lines = new List<string>
        {
            "; Generated by Galathil. The appearance clone is stored in the source ESP.",
            "; These rules copy the clone's visual style to the chosen NPC."
        };
        foreach (var (item, cloneKey) in assignments)
        {
            var cloneIdentifier = pluginName + "|" + cloneKey.ID.ToString("X8", CultureInfo.InvariantCulture);
            var targetIdentifier = item.Target.FormKey.ModKey.FileName + "|" + item.Target.FormKey.ID.ToString("X8", CultureInfo.InvariantCulture);
            var parts = new List<string> { "filterByNpcs=" + targetIdentifier, "filterByTemplate=" + targetIdentifier, "restrictToTemplateFlags=traits" };
            parts.Add("copyVisualStyle=" + cloneIdentifier);
            if (item.CopyOutfit && item.Source.DefaultOutfit is { IsNull: false } outfit)
                parts.Add("outfitDefault=" + outfit.FormKey.ModKey.FileName + "|" + outfit.FormKey.ID.ToString("X8", CultureInfo.InvariantCulture));
            lines.Add(string.Join(':', parts));
            if (item.CopySkinArmor && item.Source.WornArmor is { IsNull: false } skin)
                lines.Add("filterByNpcs=" + targetIdentifier + ":skin=" + skin.FormKey.ModKey.FileName + "|" + skin.FormKey.ID.ToString("X8", CultureInfo.InvariantCulture));
            if (item.CopyRace)
                lines.Add("filterByNpcs=" + targetIdentifier + ":race=" + item.Source.Race.FormKey.ModKey.FileName + "|" + item.Source.Race.FormKey.ID.ToString("X8", CultureInfo.InvariantCulture));
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
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

    private static FormKey FindFreeLightFormKey(ISkyrimMod output)
    {
        var usedIds = new HashSet<uint>();
        foreach (var property in output.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0 || property.Name == "ModHeader") continue;
            if (property.GetValue(output) is not System.Collections.IEnumerable records) continue;
            foreach (var record in records.Cast<object?>().OfType<IMajorRecordGetter>())
            {
                if (record.FormKey.ModKey == output.ModKey) usedIds.Add(record.FormKey.ID);
            }
        }

        for (uint id = 0x800; id <= LastLightObjectId; id++)
        {
            if (!usedIds.Contains(id)) return new FormKey(output.ModKey, id);
        }

        throw new LightPluginFormIdCapacityException(new FormKey(output.ModKey, output.NextFormID));
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

    private sealed record SupersededMod(string Name, string Folder, string[] Plugins);

    private static bool IsPluginFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".esp" or ".esm" or ".esl";

    private static string MakeEditorId(INpcGetter source, FormKey cloneKey)
    {
        var name = Regex.Replace(source.EditorID ?? "NPC", "[^A-Za-z0-9_]", "_");
        if (name.Length > 30) name = name[..30];
        return $"GALATHIL_{name}_{cloneKey.ID:X3}";
    }

    private static string FormKeyText(FormKey key) => $"{key.ModKey.FileName}|{key.ID:X}";

    private sealed record PluginNpc(string Plugin, Npc Record);
    private sealed record PreparedAssignment(
        AppearanceAssignment Assignment,
        Npc Source,
        Npc BeforeSource,
        Npc Target,
        FaceGenFiles SourceFaceGen,
        FaceGenFiles PreviousFaceGen,
        bool CopyRace,
        bool CopySkinArmor,
        bool CopyOutfit);
    private sealed record SharedPreparedAssignment(
        AppearanceAssignment Assignment,
        Npc Source,
        Npc Appearance,
        Npc BeforeSource,
        Npc Target,
        FaceGenFiles SourceFaceGen,
        FaceGenFiles? PreviousFaceGen,
        bool RestoreSource,
        bool CopySkinArmor,
        bool CopyOutfit);
    private sealed record FaceGenFiles(byte[] Mesh, byte[] Tint);

    private sealed class FaceGenAssetResolver
    {
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

        public FaceGenFiles ReadCurrentReplacerFaceGen(string sourceMod, FormKey key)
        {
            var meshRelative = FaceGenRelative(MeshFolder, key, ".nif");
            var tintRelative = FaceGenRelative(TintFolder, key, ".dds");
            var sourceIndex = _modOrder.ToList().FindIndex(m => m.Name.Equals(sourceMod, StringComparison.OrdinalIgnoreCase));
            if (sourceIndex < 0) throw new InvalidOperationException($"MO2 mod '{sourceMod}' is missing from its mod list.");

            var meshConflict = FindLooseConflictAfter(meshRelative, sourceIndex);
            var tintConflict = FindLooseConflictAfter(tintRelative, sourceIndex);
            if (meshConflict is not null)
                throw new InvalidOperationException($"'{meshConflict}' has a later FaceGen mesh for the same NPC than the selected replacer. Resolve that asset conflict before generating a transfer.");
            if (tintConflict is not null)
                throw new InvalidOperationException($"'{tintConflict}' has a later FaceGen tint for the same NPC than the selected replacer. Resolve that asset conflict before generating a transfer.");
            var meshWinner = FindLooseWinner(meshRelative, sourceIndex);
            var tintWinner = FindLooseWinner(tintRelative, sourceIndex);

            var sourceFolder = Path.Combine(_modsPath, sourceMod);
            var meshBytes = ReadLoose(meshWinner?.Path) ?? ReadFromModArchives(sourceFolder, meshRelative);
            var tintBytes = ReadLoose(tintWinner?.Path) ?? ReadFromModArchives(sourceFolder, tintRelative);
            if (meshBytes is null || tintBytes is null)
                throw new FileNotFoundException($"The selected replacer does not provide both FaceGen files for {FormKeyText(key)}. Galathil needs its face mesh and tint to move the look.");
            ValidateFaceGen(meshBytes, tintBytes, key);
            return new FaceGenFiles(meshBytes, tintBytes);
        }

        public FaceGenFiles ReadEarlierFaceGen(string sourceMod, string previousPlugin, FormKey key)
        {
            var meshRelative = FaceGenRelative(MeshFolder, key, ".nif");
            var tintRelative = FaceGenRelative(TintFolder, key, ".dds");
            var sourceIndex = _modOrder.ToList().FindIndex(m => m.Name.Equals(sourceMod, StringComparison.OrdinalIgnoreCase));
            var meshWinner = FindLooseWinner(meshRelative, sourceIndex - 1);
            var tintWinner = FindLooseWinner(tintRelative, sourceIndex - 1);
            var previousPluginFolder = _pluginPaths.TryGetValue(previousPlugin, out var previousPath)
                ? Path.GetDirectoryName(previousPath)
                : null;
            var meshBytes = ReadLoose(meshWinner?.Path)
                ?? (previousPluginFolder is null || previousPluginFolder.Equals(_dataPath, StringComparison.OrdinalIgnoreCase) ? null : ReadFromModArchives(previousPluginFolder, meshRelative))
                ?? ReadFromGameArchives(key.ModKey, meshRelative);
            var tintBytes = ReadLoose(tintWinner?.Path)
                ?? (previousPluginFolder is null || previousPluginFolder.Equals(_dataPath, StringComparison.OrdinalIgnoreCase) ? null : ReadFromModArchives(previousPluginFolder, tintRelative))
                ?? ReadFromGameArchives(key.ModKey, tintRelative);
            if (meshBytes is null || tintBytes is null)
                throw new FileNotFoundException($"Could not find the earlier FaceGen mesh and tint for {FormKeyText(key)}. Galathil cannot restore the NPC without those files.");
            ValidateFaceGen(meshBytes, tintBytes, key);
            return new FaceGenFiles(meshBytes, tintBytes);
        }

        private (string ModName, int Index, string Path)? FindLooseWinner(string relative, int lastIndex = int.MaxValue)
        {
            (string ModName, int Index, string Path)? winner = null;
            var end = Math.Min(lastIndex, _modOrder.Count - 1);
            for (var index = 0; index <= end; index++)
            {
                var mod = _modOrder[index];
                if (!mod.Enabled) continue;
                var path = Path.Combine(_modsPath, mod.Name, relative);
                if (File.Exists(path)) winner = (mod.Name, index, path);
            }
            return winner;
        }

        private string? FindLooseConflictAfter(string relative, int sourceIndex)
        {
            for (var index = sourceIndex + 1; index < _modOrder.Count; index++)
            {
                var mod = _modOrder[index];
                if (!mod.Enabled || mod.Name.StartsWith("Galathil - ", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(Path.Combine(_modsPath, mod.Name, relative))) return mod.Name;
            }
            return null;
        }

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
            var archiveNames = owner.FileName.ToString().ToLowerInvariant() switch
            {
                "skyrim.esm" when relative.StartsWith(MeshFolder, StringComparison.OrdinalIgnoreCase) => new[] { "Skyrim - Meshes0.bsa", "Skyrim - Meshes1.bsa" },
                "skyrim.esm" => new[] { "Skyrim - Textures0.bsa", "Skyrim - Textures1.bsa", "Skyrim - Textures2.bsa", "Skyrim - Textures3.bsa", "Skyrim - Textures4.bsa", "Skyrim - Textures5.bsa", "Skyrim - Textures6.bsa", "Skyrim - Textures7.bsa", "Skyrim - Textures8.bsa" },
                _ => new[] { Path.GetFileNameWithoutExtension(owner.FileName.ToString()) + ".bsa" }
            };
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

        private static void ValidateFaceGen(byte[] mesh, byte[] tint, FormKey key)
        {
            if (mesh.Length < 64) throw new InvalidDataException($"FaceGen mesh for {FormKeyText(key)} is empty or incomplete.");
            if (tint.Length < 128 || tint[0] != (byte)'D' || tint[1] != (byte)'D' || tint[2] != (byte)'S' || tint[3] != (byte)' ')
                throw new InvalidDataException($"FaceGen tint for {FormKeyText(key)} is not a valid DDS file.");
        }
    }

    private static string FaceGenRelative(string folder, FormKey key, string extension) =>
        Path.Combine(folder, key.ModKey.FileName, key.ID.ToString("X8", CultureInfo.InvariantCulture) + extension);
}
