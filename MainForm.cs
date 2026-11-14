using System.Diagnostics;
using System.Drawing;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

internal sealed class MainForm : Form
{
    private readonly TextBox _instancePath = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _sourceSearch = new() { PlaceholderText = "Search characters changed by this mod...", Dock = DockStyle.Fill };
    private readonly TextBox _modSearch = new() { PlaceholderText = "Search mods...", Dock = DockStyle.Fill };
    private readonly ListBox _mods = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 23 };
    private readonly ComboBox _plugins = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly DataGridView _sourceGrid = CreateGrid();
    private readonly TextBox _targetSearch = new() { PlaceholderText = "Search characters by name or ID...", Dock = DockStyle.Fill };
    private readonly ListView _targetList = new BufferedListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, VirtualMode = true, ShowItemToolTips = true };
    private readonly DataGridView _mappingGrid = CreateGrid();
    private readonly ComboBox _outputMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly CheckBox _copySkin = new() { Text = "", AutoSize = true, Checked = false, Enabled = false, Margin = new Padding(0, 3, 2, 0) };
    private readonly CheckBox _copyBody = new() { Text = "", AutoSize = true, Checked = false, Enabled = false, Margin = new Padding(0, 3, 2, 0) };
    private readonly CheckBox _copyOutfit = new() { Text = "", AutoSize = true, Checked = false, Enabled = false, Margin = new Padding(0, 3, 2, 0) };
    private readonly Label _skinOptionText = new() { Text = "Skin", AutoSize = true, ForeColor = Color.White };
    private readonly Label _bodyOptionText = new() { Text = "Body", AutoSize = true, ForeColor = Color.White };
    private readonly Label _outfitOptionText = new() { Text = "New outfit", AutoSize = true, ForeColor = Color.White };
    private readonly ToolTip _optionsToolTip = new();
    private readonly ToolStripStatusLabel _statusText = new() { Text = "Choose a MO2 profile and source mod.", AutoSize = false, Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Visible = false, Style = ProgressBarStyle.Marquee, Width = 120 };
    private readonly List<ModListEntry> _allMods = [];
    private readonly List<TargetNpc> _allTargets = [];
    private TargetNpc[] _visibleTargets = [];
    private readonly List<NpcReplacement> _sourceNpcs = [];
    private NpcReplacement[] _visibleSourceNpcs = [];
    private readonly List<MappingRow> _mappings = [];
    private readonly Dictionary<string, AppearanceAssignment> _savedOutputAssignmentsBySource = new(StringComparer.OrdinalIgnoreCase);
    private ProfileContext? _context;
    private bool _binding;
    private int _loadGeneration;

    public MainForm(string? initialInstance = null)
    {
        Text = "Galathil";
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new System.Drawing.Size(1320, 620);
        Size = new System.Drawing.Size(1320, 680);
        StartPosition = FormStartPosition.CenterScreen;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        if (!string.IsNullOrWhiteSpace(initialInstance)) _instancePath.Text = initialInstance;
        else _instancePath.Text = Mo2Paths.TryFindDefaultInstance() ?? "";

        BuildLayout();
        WireEvents();
        Shown += async (_, _) => await LoadProfilesAsync();
    }

    private void BuildLayout()
    {
        ConfigureGrid(_sourceGrid,
            ("Source NPC", "Npc"),
            ("Plugin ID", "EditorID"),
            ("Replaces", "CurrentTarget"));
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        Controls.Add(root);

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "MO2 instance", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        top.Controls.Add(_instancePath, 1, 0);
        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) => BrowseInstance();
        top.Controls.Add(browse, 2, 0);
        top.Controls.Add(new Label { Text = "Profile", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(8, 0, 6, 0) }, 3, 0);
        _profiles.Margin = new Padding(0, 0, 4, 0);
        top.Controls.Add(_profiles, 4, 0);
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += async (_, _) => await RefreshProfileAsync();
        top.Controls.Add(refresh, 5, 0);
        root.Controls.Add(top, 0, 0);

        var mainSplit = new SplitContainer { Size = new System.Drawing.Size(1220, 500), Dock = DockStyle.Fill, Orientation = Orientation.Vertical, Panel1MinSize = 210, Panel2MinSize = 610, SplitterWidth = 5, SplitterDistance = 370 };
        mainSplit.Panel1.Controls.Add(BuildModPanel());
        var detailSplit = new SplitContainer { Size = new System.Drawing.Size(840, 500), Dock = DockStyle.Fill, Orientation = Orientation.Vertical, Panel1MinSize = 290, Panel2MinSize = 290, SplitterWidth = 5, SplitterDistance = 400 };
        detailSplit.Panel1.Controls.Add(BuildSourcePanel());
        detailSplit.Panel2.Controls.Add(BuildTargetPanel());
        mainSplit.Panel2.Controls.Add(detailSplit);
        MakeResponsive(mainSplit, 0.30);
        MakeResponsive(detailSplit, 0.46);
        root.Controls.Add(mainSplit, 0, 1);

        root.Controls.Add(BuildMappingPanel(), 0, 2);
        var strip = new StatusStrip();
        strip.Items.Add(_statusText);
        strip.Items.Add(_progress);
        Controls.Add(strip);
        strip.Dock = DockStyle.Bottom;
    }

    private Control BuildSourcePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(4, 0, 6, 0) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = "Characters changed by this mod", AutoSize = true, ForeColor = SystemColors.GrayText }, 0, 0);
        panel.Controls.Add(_plugins, 0, 1);
        panel.Controls.Add(_sourceSearch, 0, 2);

        var records = new GroupBox { Text = "Characters changed in this plugin", Dock = DockStyle.Fill };
        records.Controls.Add(_sourceGrid);
        panel.Controls.Add(records, 0, 3);
        return panel;
    }

    private Control BuildModPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0, 0, 6, 0) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = "MO2 mods", AutoSize = true, ForeColor = SystemColors.GrayText }, 0, 0);
        panel.Controls.Add(_modSearch, 0, 1);
        panel.Controls.Add(_mods, 0, 2);
        return panel;
    }

    private Control BuildTargetPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = "Character who receives the look", AutoSize = true, ForeColor = SystemColors.GrayText }, 0, 0);
        panel.Controls.Add(_targetSearch, 0, 1);
        _targetList.Columns.Add("NPC", 150);
        _targetList.Columns.Add("ID", 100);
        _targetList.Columns.Add("FormID", 95);
        _targetList.Columns.Add("Plugin", 130);
        _targetList.Resize += (_, _) => ResizeTargetColumns();
        ResizeTargetColumns();
        panel.Controls.Add(_targetList, 0, 2);
        return panel;
    }

    private Control BuildMappingPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 6, 0, 0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
        header.Controls.Add(new Label { Text = "Replacer look -> character receiving it", AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left }, 0, 0);
        var outputControls = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0), Padding = new Padding(2, 3, 0, 0) };
        outputControls.Controls.Add(new Label { Text = "Output:", AutoSize = true, ForeColor = Color.White, Margin = new Padding(0, 5, 5, 0) });
        _outputMode.Width = 205;
        outputControls.Controls.Add(_outputMode);
        var create = new Button { Text = "Apply look", Width = 132, Height = 28, Margin = new Padding(6, 0, 0, 0) };
        create.Click += async (_, _) => await CreateModAsync();
        outputControls.Controls.Add(create);
        header.Controls.Add(outputControls, 1, 0);
        panel.Controls.Add(header, 0, 0);
        panel.SetColumnSpan(header, 2);
        ConfigureGrid(_mappingGrid, ("Look from", "Source"), ("Apply to", "Target"));
        panel.Controls.Add(_mappingGrid, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), Padding = new Padding(4, 0, 0, 0) };
        var add = new Button { Text = "Add mapping", Width = 200, Height = 28 };
        add.Click += (_, _) => AddMapping();
        var remove = new Button { Text = "Remove mapping", Width = 200, Height = 28 };
        remove.Click += (_, _) => RemoveMapping();
        _outputMode.Items.AddRange(["Galathil Output + SkyPatcher", "Replace source ESP", "Same ESP + SkyPatcher"]);
        _outputMode.SelectedIndex = 0;
        var optional = new GroupBox { Text = "Optional", Width = 210, Height = 80, ForeColor = Color.White };
        var optionalGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(3) };
        optionalGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optionalGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optionalGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        optionalGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        optionalGrid.Controls.Add(CreateOptionalOption(_copySkin, _skinOptionText), 0, 0);
        optionalGrid.Controls.Add(CreateOptionalOption(_copyBody, _bodyOptionText), 1, 0);
        var outfitOption = CreateOptionalOption(_copyOutfit, _outfitOptionText);
        optionalGrid.Controls.Add(outfitOption, 0, 1);
        optionalGrid.SetColumnSpan(outfitOption, 2);
        optional.Controls.Add(optionalGrid);
        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);
        buttons.Controls.Add(optional);
        panel.Controls.Add(buttons, 1, 1);
        return panel;
    }

    private static Control CreateOptionalOption(CheckBox checkBox, Label text)
    {
        var option = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
        option.Controls.Add(checkBox);
        option.Controls.Add(text);
        text.Click += (_, _) =>
        {
            if (checkBox.Enabled) checkBox.Checked = !checkBox.Checked;
        };
        return option;
    }

    private void WireEvents()
    {
        _profiles.SelectedIndexChanged += async (_, _) => { if (!_binding && _profiles.SelectedItem is string) await RefreshProfileAsync(); };
        _modSearch.TextChanged += (_, _) => FilterMods();
        _sourceSearch.TextChanged += (_, _) => FilterSourceNpcs();
        _mods.DrawItem += DrawModItem;
        _mods.SelectedIndexChanged += (_, _) => SelectMod();
        _plugins.SelectedIndexChanged += async (_, _) => await LoadSourcePluginAsync();
        _targetSearch.TextChanged += (_, _) => FilterTargets();
        _targetList.RetrieveVirtualItem += (_, e) =>
        {
            var npc = _visibleTargets[e.ItemIndex];
            e.Item = new ListViewItem([npc.Name, npc.EditorId, npc.FormKeyText, npc.WinningPlugin])
            {
                Tag = npc,
                ToolTipText = $"{npc.Name}\nID: {npc.EditorId}\nFormID: {npc.FormKeyText}\nPlugin: {npc.WinningPlugin}"
            };
        };
        _targetList.DoubleClick += (_, _) => AddMapping();
    }

    private async Task LoadProfilesAsync()
    {
        var instance = _instancePath.Text.Trim();
        if (!Directory.Exists(instance)) { SetStatus("Select the MO2 instance folder."); return; }
        try
        {
            var profileDir = Path.Combine(instance, "profiles");
            var profiles = Directory.GetDirectories(profileDir).Select(Path.GetFileName).Where(x => x is not null).Cast<string>().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var selected = Mo2Paths.ReadSelectedProfile(Path.Combine(instance, "ModOrganizer.ini"));
            _binding = true;
            _profiles.Items.Clear();
            _profiles.Items.AddRange(profiles.Cast<object>().ToArray());
            _profiles.SelectedItem = profiles.FirstOrDefault(x => x.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? profiles.FirstOrDefault();
            _binding = false;
            await RefreshProfileAsync();
        }
        catch (Exception ex) { _binding = false; SetStatus($"Could not read MO2 profiles: {ex.Message}"); }
    }

    private async Task RefreshProfileAsync()
    {
        var profileName = _profiles.SelectedItem as string;
        if (profileName is null) return;
        var thisGeneration = ++_loadGeneration;
        SetBusy(true, "Reading MO2 profile and scanning active NPC records...");
        try
        {
            var instance = Path.GetFullPath(_instancePath.Text.Trim());
            var context = await Task.Run(() => ProfileContext.Read(instance, profileName));
            var scan = await Task.Run(() => PluginScanner.ReadActiveNpcIndex(context));
            if (thisGeneration != _loadGeneration) return;
            _context = context;
            _allMods.Clear();
            _allMods.AddRange(context.Mods);
            _allTargets.Clear();
            _allTargets.AddRange(scan.Npcs);
            _mappings.Clear();
            _savedOutputAssignmentsBySource.Clear();
            var planIssue = LoadGalathilOutputPlan(context);
            _mods.SelectedIndex = -1;
            FilterMods();
            FilterTargets();
            RefreshMappingGrid();
            _sourceGrid.Rows.Clear();
            _plugins.Items.Clear();
            var warning = scan.Errors.Count == 0 ? "" : $" {scan.Errors.Count} plugins could not be scanned; see details in the status tooltip.";
            SetBusy(false, $"Loaded profile '{profileName}': {_allMods.Count(m => !m.IsSeparator)} mods, {_allTargets.Count:N0} unique active NPCs, {context.ActivePlugins.Count} active plugins.{warning}");
            var details = scan.Errors.Take(30).ToList();
            if (!string.IsNullOrWhiteSpace(planIssue)) details.Insert(0, planIssue);
            _statusText.ToolTipText = details.Count == 0 ? "" : string.Join(Environment.NewLine, details);
        }
        catch (Exception ex)
        {
            if (thisGeneration == _loadGeneration) SetBusy(false, $"Profile scan failed: {ex.Message}");
        }
    }

    private void FilterMods()
    {
        var needle = _modSearch.Text.Trim();
        var selectedName = (_mods.SelectedItem as ModListEntry)?.Name;
        _mods.BeginUpdate();
        _mods.Items.Clear();
        foreach (var mod in _allMods)
        {
            if (mod.IsSeparator)
            {
                if (needle.Length == 0 || mod.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) _mods.Items.Add(mod);
            }
            else if (needle.Length == 0 || mod.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) _mods.Items.Add(mod);
        }
        if (selectedName is not null)
            for (var i = 0; i < _mods.Items.Count; i++) if (_mods.Items[i] is ModListEntry m && m.Name == selectedName) { _mods.SelectedIndex = i; break; }
        _mods.EndUpdate();
    }

    private string? LoadGalathilOutputPlan(ProfileContext context)
    {
        var path = Path.Combine(context.ModsPath, NpcAppearanceGenerator.OutputModName, NpcAppearanceGenerator.OutputManifestName);
        if (!File.Exists(path)) return null;
        try
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize<GalathilOutputManifest>(File.ReadAllText(path));
            if (manifest is null || manifest.Version != 1) return "Galathil Output has an unsupported or empty saved plan.";
            _copySkin.Checked = manifest.CopySkin;
            _copyBody.Checked = manifest.CopyBody;
            _copyOutfit.Checked = manifest.CopyOutfit;
            foreach (var assignment in manifest.Assignments)
            {
                _savedOutputAssignmentsBySource[SavedAssignmentKey(assignment.SourceMod, assignment.SourcePlugin, assignment.SourceFormKey)] = assignment;
                var source = new NpcReplacement(assignment.SourceName, "", assignment.SourceFormKey, assignment.SourceName,
                    true, assignment.SourceRaceKey ?? "", assignment.SourceIsFemale, assignment.HasCustomSkin,
                    assignment.HasCustomBody, assignment.HasNewOutfit, assignment.AppearancePlugin, assignment.AppearanceFormKey,
                    null, null, assignment.AppearanceMod);
                var target = _allTargets.FirstOrDefault(n => n.FormKeyText.Equals(assignment.TargetFormKey, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    var separator = assignment.TargetFormKey.LastIndexOf('|');
                    var plugin = separator < 0 ? "" : assignment.TargetFormKey[..separator];
                    target = new TargetNpc(assignment.TargetName, "", assignment.TargetFormKey, plugin, "", false);
                }
                _mappings.Add(new MappingRow(assignment.SourceMod, assignment.SourcePlugin, source,
                    target with { Name = assignment.TargetName }));
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not load Galathil Output plan: {ex.Message}";
        }
    }

    private void DrawModItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _mods.Items.Count) return;
        var item = (ModListEntry)_mods.Items[e.Index]!;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var bg = selected ? SystemColors.Highlight : _mods.BackColor;
        var fg = selected ? SystemColors.HighlightText : item.IsSeparator ? SystemColors.GrayText : item.Enabled ? SystemColors.ControlText : SystemColors.GrayText;
        using var back = new SolidBrush(bg);
        using var fore = new SolidBrush(fg);
        e.Graphics.FillRectangle(back, e.Bounds);
        var label = item.IsSeparator ? $"---- {item.Name.Replace('_', ' ')} ----" : $"{(item.Enabled ? "[+]" : "[-]")}  {item.Name}";
        var baseFont = e.Font ?? _mods.Font;
        var font = item.IsSeparator ? new Font(baseFont, FontStyle.Italic) : baseFont;
        e.Graphics.DrawString(label, font, fore, e.Bounds.Left + 4, e.Bounds.Top + 3);
        if (item.IsSeparator) font.Dispose();
        e.DrawFocusRectangle();
    }

    private void SelectMod()
    {
        if (_mods.SelectedItem is not ModListEntry mod || mod.IsSeparator || _context is null) return;
        _plugins.Items.Clear();
        _sourceGrid.Rows.Clear();
        var modPath = Path.Combine(_context.ModsPath, mod.Name);
        if (!Directory.Exists(modPath)) { SetStatus($"MO2 mod folder not found: {mod.Name}"); return; }
        foreach (var file in Directory.GetFiles(modPath, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(IsPluginFile).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            var isActive = _context.ActivePlugins.Contains(name);
            _plugins.Items.Add(new PluginChoice(name, file, isActive));
        }
        if (_plugins.Items.Count > 0) _plugins.SelectedIndex = 0;
        else SetStatus($"'{mod.Name}' has no root-level ESP/ESM/ESL plugins.");
    }

    private async Task LoadSourcePluginAsync()
    {
        if (_plugins.SelectedItem is not PluginChoice plugin || _context is null) return;
        SetBusy(true, $"Scanning NPC records in {plugin.Name}...");
        try
        {
            var context = _context;
            var sourceModName = (_mods.SelectedItem as ModListEntry)?.Name ?? Path.GetFileName(Path.GetDirectoryName(plugin.Path)) ?? string.Empty;
            var npcs = await Task.Run(() => PluginScanner.ReadNpcOverrides(plugin.Path, plugin.Name, context, _allTargets)
                .Select(n => n.IsDirectOverride && _allTargets.FirstOrDefault(t => t.FormKeyText.Equals(n.OriginalFormKey, StringComparison.OrdinalIgnoreCase)) is { } baseNpc
                    ? n with { OverriddenNpcName = baseNpc.Name }
                    : n)
                .Select(n => _savedOutputAssignmentsBySource.TryGetValue(SavedAssignmentKey(sourceModName, plugin.Name, n.OriginalFormKey), out var saved)
                    ? n with
                    {
                        RaceKey = saved.SourceRaceKey ?? n.RaceKey,
                        IsFemale = saved.SourceIsFemale,
                        HasCustomSkin = saved.HasCustomSkin,
                        HasCustomBody = saved.HasCustomBody,
                        HasNewOutfit = saved.HasNewOutfit,
                        AppearancePlugin = saved.AppearancePlugin,
                        AppearanceFormKey = saved.AppearanceFormKey,
                        ExistingTargetFormKey = saved.TargetFormKey,
                        AppearanceMod = saved.AppearanceMod
                    }
                    : n).ToList());
            _sourceNpcs.Clear();
            _sourceNpcs.AddRange(npcs);
            FilterSourceNpcs();
            SetBusy(false, npcs.Count == 0
                ? $"No NPC records found in {plugin.Name}."
                : $"Found {npcs.Count} NPC record(s), including {npcs.Count(n => n.IsDirectOverride)} direct overrides. Select one source and one target to add a mapping.");
        }
        catch (Exception ex) { SetBusy(false, $"Could not parse {plugin.Name}: {ex.Message}"); }
    }

    private void FilterTargets()
    {
        var needle = _targetSearch.Text.Trim();
        _visibleTargets = _allTargets.Where(n => needle.Length == 0 ||
            n.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            n.EditorId.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            n.FormKeyText.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToArray();
        _targetList.BeginUpdate();
        _targetList.VirtualListSize = _visibleTargets.Length;
        _targetList.EndUpdate();
        _targetList.Invalidate();
        ResizeTargetColumns();
    }

    private void ResizeTargetColumns()
    {
        if (_targetList.Columns.Count != 4) return;
        var available = Math.Max(300, _targetList.ClientSize.Width - 8);
        var name = (int)(available * 0.34);
        var editor = (int)(available * 0.20);
        var formId = (int)(available * 0.20);
        _targetList.Columns[0].Width = name;
        _targetList.Columns[1].Width = editor;
        _targetList.Columns[2].Width = formId;
        _targetList.Columns[3].Width = Math.Max(65, available - name - editor - formId);
    }

    private void FilterSourceNpcs()
    {
        var needle = _sourceSearch.Text.Trim();
        _visibleSourceNpcs = _sourceNpcs.Where(n => needle.Length == 0 ||
            n.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            n.EditorId.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            n.OverriddenNpcName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            n.OriginalFormKey.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToArray();
        _sourceGrid.Rows.Clear();
        foreach (var npc in _visibleSourceNpcs)
        {
            var row = _sourceGrid.Rows[_sourceGrid.Rows.Add(npc.Name, npc.EditorId, npc.OverriddenNpcName)];
            row.Tag = npc;
        }
    }

    private void AddMapping()
    {
        if (_sourceGrid.CurrentRow?.Tag is not NpcReplacement source)
        {
            SetStatus("Select a source NPC override first.");
            return;
        }
        if (!source.IsDirectOverride)
        {
            SetStatus("That record is newly added by the mod, not an override of an existing NPC. Choose an override record.");
            return;
        }

        TargetNpc? target;
        if (_targetList.SelectedIndices.Count == 0)
        {
            _outputMode.SelectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(source.ExistingRouteProblem))
            {
                SetStatus(source.ExistingRouteProblem);
                return;
            }
            var targetKey = source.ExistingTargetFormKey ?? source.OriginalFormKey;
            target = _allTargets.FirstOrDefault(n => n.FormKeyText.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                SetStatus($"Could not find the default recipient {targetKey} in this profile. Select a recipient from the list.");
                return;
            }
        }
        else target = _visibleTargets[_targetList.SelectedIndices[0]];

        if (source.OriginalFormKey.Equals(target.FormKeyText, StringComparison.OrdinalIgnoreCase) && _outputMode.SelectedIndex != 0)
        {
            SetStatus("The target is the NPC this replacer already overrides. Choose a different target to redirect it.");
            return;
        }
        if (_mappings.Any(m => m.Target.FormKeyText.Equals(target.FormKeyText, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"{target.Name} already has an appearance assigned. Remove that assignment first.");
            return;
        }
        var modName = (_mods.SelectedItem as ModListEntry)?.Name ?? "";
        if (_plugins.SelectedItem is not PluginChoice plugin)
        {
            SetStatus("Select the replacer's plugin first.");
            return;
        }
        if (_mappings.Any(m => m.ModName.Equals(modName, StringComparison.OrdinalIgnoreCase) && m.Plugin.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase) && m.Source.OriginalFormKey == source.OriginalFormKey))
        {
            SetStatus("A mapping for this source record is already in the plan.");
            return;
        }
        _mappings.Add(new MappingRow(modName, plugin.Name, source, target));
        RefreshMappingGrid();
        SetStatus($"Added {source.OverriddenNpcName} → {target.Name} to the Galathil Output plan. Click Apply look to update the shared SkyPatcher output.");
    }

    private void RemoveMapping()
    {
        var row = _mappingGrid.CurrentRow?.Index ?? -1;
        if (row >= 0 && row < _mappings.Count)
        {
            _mappings.RemoveAt(row);
            RefreshMappingGrid();
        }
    }

    private void RefreshMappingGrid()
    {
        _mappingGrid.Rows.Clear();
        foreach (var mapping in _mappings)
            _mappingGrid.Rows.Add(mapping.Source.OverriddenNpcName, mapping.Target.Name);
        UpdateOptionalOptions();
    }

    private void UpdateOptionalOptions()
    {
        var canCopySkin = _mappings.Any(m => m.Source.HasCustomSkin);
        var canCopyBody = _mappings.Any(m => m.Source.HasCustomBody);
        var canCopyOutfit = _mappings.Any(m => m.Source.HasNewOutfit);
        _copySkin.Enabled = canCopySkin;
        _copyBody.Enabled = canCopyBody;
        _copyOutfit.Enabled = canCopyOutfit;
        if (!canCopySkin) _copySkin.Checked = false;
        if (!canCopyBody) _copyBody.Checked = false;
        if (!canCopyOutfit) _copyOutfit.Checked = false;
        _optionsToolTip.SetToolTip(_copySkin, canCopySkin ? "Use the replacer's own body skin textures on the target." : "Enabled when the mod contains loose skin textures linked to this NPC.");
        _optionsToolTip.SetToolTip(_skinOptionText, _optionsToolTip.GetToolTip(_copySkin));
        _optionsToolTip.SetToolTip(_copyBody, canCopyBody ? "Use the replacer's own body mesh on the target." : "Enabled when the mod contains a loose, NPC-specific body mesh.");
        _optionsToolTip.SetToolTip(_bodyOptionText, _optionsToolTip.GetToolTip(_copyBody));
        _optionsToolTip.SetToolTip(_copyOutfit, canCopyOutfit ? "Use the replacer's new outfit on the target." : "Enabled when a replacer changes the source NPC's default outfit.");
        _optionsToolTip.SetToolTip(_outfitOptionText, _optionsToolTip.GetToolTip(_copyOutfit));
    }

    private async Task CreateModAsync()
    {
        var mode = _outputMode.SelectedIndex switch
        {
            0 => AppearanceOutputMode.GalathilOutput,
            2 => AppearanceOutputMode.SkyPatcher,
            _ => AppearanceOutputMode.EspfePatch
        };
        if (_context is null || (_mappings.Count == 0 && mode != AppearanceOutputMode.GalathilOutput))
        {
            SetStatus("Add at least one mapping first.");
            return;
        }
        var raceMismatches = _mappings.Where(m => !m.Source.RaceKey.Equals(m.Target.RaceKey, StringComparison.OrdinalIgnoreCase)).ToList();
        var genderMismatches = _mappings.Where(m => m.Source.IsFemale != m.Target.IsFemale).ToList();
        if (mode == AppearanceOutputMode.EspfePatch && genderMismatches.Count > 0)
        {
            SetStatus("Different-sex appearance transfers require SkyPatcher. Choose Galathil Output + SkyPatcher or Same ESP + SkyPatcher.");
            return;
        }
        var changeTargetRace = mode != AppearanceOutputMode.EspfePatch;
        if ((raceMismatches.Count > 0 || genderMismatches.Count > 0) && mode != AppearanceOutputMode.EspfePatch)
        {
            var examples = string.Join(Environment.NewLine, _mappings.Where(m =>
                !m.Source.RaceKey.Equals(m.Target.RaceKey, StringComparison.OrdinalIgnoreCase) || m.Source.IsFemale != m.Target.IsFemale)
                .Take(5).Select(m => $"{m.Source.OverriddenNpcName} → {m.Target.Name}"));
            var choice = MessageBox.Show(this,
                $"SkyPatcher applies the source character's race and sex along with the appearance.{Environment.NewLine}{examples}{Environment.NewLine}{Environment.NewLine}This can affect race behavior, voice compatibility, and animations. Continue?",
                "SkyPatcher changes race and sex", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes) return;
        }

        var context = _context;
        var copySkin = _copySkin.Checked;
        var copyBody = _copyBody.Checked;
        var copyOutfit = _copyOutfit.Checked;
        var assignments = _mappings.Select(m => new AppearanceAssignment(
            m.ModName, m.Plugin, m.Source.OverriddenNpcName, m.Source.OriginalFormKey, m.Target.Name, m.Target.FormKeyText,
            m.Source.HasCustomSkin, m.Source.HasCustomBody, m.Source.HasNewOutfit,
            m.Source.AppearancePlugin, m.Source.AppearanceFormKey, m.Source.RaceKey, m.Source.IsFemale,
            m.Target.RaceKey, m.Target.IsFemale, m.Source.AppearanceMod)).ToList();
        SetBusy(true, mode == AppearanceOutputMode.GalathilOutput
            ? "Updating the shared Galathil Output mod and checking its files..."
            : "Replacing the source ESP and checking its files...");
        try
        {
            var result = await Task.Run(() => NpcAppearanceGenerator.Generate(
                context.InstancePath,
                context.ProfileName,
                context.ProfilePath,
                context.ModsPath,
                context.DataPath,
                context.ActivePluginOrder,
                context.ActivePlugins,
                context.PluginPaths,
                context.Mods.Select(m => new GalathilModEntry(m.Name, m.Enabled)).ToList(),
                assignments,
                mode,
                copySkin,
                copyBody,
                copyOutfit,
                changeTargetRace));
            await RefreshProfileAsync();
            var completion = mode == AppearanceOutputMode.GalathilOutput
                ? assignments.Count == 0
                    ? "Galathil Output was cleared. The output mod remains available for new mappings."
                    : $"Updated Galathil Output with {assignments.Count} appearance mapping(s), {CountOutputPlugins(result.ModFolder)} ESPFE plugin(s), and one combined SkyPatcher config."
                : $"Replaced {Path.GetFileName(result.PluginPath)} in {result.ModName} ({(result.SkyPatcherPath is null ? "one plugin" : "one plugin + SkyPatcher rule")}; no extra mod added).";
            SetBusy(false, completion);
            _statusText.ToolTipText = string.Join(Environment.NewLine, new[] { result.BackupPath, result.ModFolder }.Concat(result.Warnings));
            var confirmation = $"Galathil finished successfully.{Environment.NewLine}{Environment.NewLine}{completion}{Environment.NewLine}{Environment.NewLine}Backup saved to:{Environment.NewLine}{result.BackupPath}";
            if (result.Warnings.Count > 0)
                confirmation += $"{Environment.NewLine}{Environment.NewLine}Notes:{Environment.NewLine}{string.Join(Environment.NewLine, result.Warnings)}";
            MessageBox.Show(this, confirmation, "Galathil complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Could not create the mod: {ex.Message}");
        }
    }

    private void BrowseInstance()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select the MO2 instance folder containing ModOrganizer.ini and profiles." };
        if (Directory.Exists(_instancePath.Text)) dialog.SelectedPath = _instancePath.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _instancePath.Text = dialog.SelectedPath;
            _ = LoadProfilesAsync();
        }
    }

    private static int CountOutputPlugins(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "Galathil Output*.esp", SearchOption.TopDirectoryOnly).Count()
        : 0;

    private void SetBusy(bool busy, string message)
    {
        _progress.Visible = busy;
        _statusText.Text = message;
        if (busy) UseWaitCursor = true;
        else UseWaitCursor = false;
    }

    private void SetStatus(string message) => _statusText.Text = message;

    private static DataGridView CreateGrid()
    {
        var grid = new BufferedDataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize };
        grid.BackgroundColor = SystemColors.Window;
        grid.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        return grid;
    }

    private static void ConfigureGrid(DataGridView grid, params (string Header, string Name)[] columns)
    {
        grid.Columns.Clear();
        for (var i = 0; i < columns.Length; i++)
        {
            var fill = columns.Length == 3 ? new[] { 40f, 25f, 35f }[i] : new[] { 55f, 45f }[i];
            var minWidth = columns.Length == 3 ? new[] { 76, 68, 78 }[i] : 100;
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = columns[i].Header,
                Name = columns[i].Name,
                SortMode = DataGridViewColumnSortMode.Automatic,
                FillWeight = fill,
                MinimumWidth = minWidth
            });
        }
    }

    private static void MakeResponsive(SplitContainer split, double initialRatio)
    {
        var ratio = initialRatio;
        var applyingDistance = false;

        void ApplyRatio()
        {
            var available = split.ClientSize.Width;
            var minimum = split.Panel1MinSize;
            var maximum = available - split.Panel2MinSize - split.SplitterWidth;
            if (maximum < minimum) return;
            var distance = Math.Clamp((int)Math.Round(available * ratio), minimum, maximum);
            if (split.SplitterDistance == distance) return;
            applyingDistance = true;
            try { split.SplitterDistance = distance; }
            catch (InvalidOperationException) { }
            finally { applyingDistance = false; }
        }

        split.SizeChanged += (_, _) => ApplyRatio();
        split.SplitterMoved += (_, _) =>
        {
            if (applyingDistance || split.ClientSize.Width <= 0) return;
            ratio = Math.Clamp((double)split.SplitterDistance / split.ClientSize.Width, 0.15, 0.85);
        };
        split.HandleCreated += (_, _) => split.BeginInvoke((Action)ApplyRatio);
    }

    private sealed class BufferedDataGridView : DataGridView
    {
        public BufferedDataGridView() => DoubleBuffered = true;
    }

    private sealed class BufferedListView : ListView
    {
        public BufferedListView() => DoubleBuffered = true;
    }

    private static bool IsPluginFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".esp" or ".esm" or ".esl";

    private sealed record PluginChoice(string Name, string Path, bool Active)
    {
        public override string ToString() => Active ? $"{Name}  (active)" : $"{Name}  (inactive)";
    }

    private sealed record NpcReplacement(string Name, string EditorId, string OriginalFormKey, string OverriddenNpcName, bool IsDirectOverride, string RaceKey, bool IsFemale, bool HasCustomSkin, bool HasCustomBody, bool HasNewOutfit,
        string? AppearancePlugin = null, string? AppearanceFormKey = null, string? ExistingTargetFormKey = null, string? ExistingRouteProblem = null, string? AppearanceMod = null);
    private sealed record TargetNpc(string Name, string EditorId, string FormKeyText, string WinningPlugin, string RaceKey, bool IsFemale);
    private sealed record MappingRow(string ModName, string Plugin, NpcReplacement Source, TargetNpc Target);

    private static string SavedAssignmentKey(string modName, string pluginName, string formKey) =>
        modName + "|" + pluginName + "|" + formKey;
    private sealed record ModListEntry(string Name, bool Enabled, bool IsSeparator)
    {
        public override string ToString() => Name;
    }

    private sealed class ProfileContext
    {
        public required string InstancePath { get; init; }
        public required string ProfileName { get; init; }
        public required string ProfilePath { get; init; }
        public required string ModsPath { get; init; }
        public required string DataPath { get; init; }
        public required List<ModListEntry> Mods { get; init; }
        public required List<string> ActivePluginOrder { get; init; }
        public required HashSet<string> ActivePlugins { get; init; }
        public required Dictionary<string, string> PluginPaths { get; init; }

        public static ProfileContext Read(string instance, string profile)
        {
            var iniPath = Path.Combine(instance, "ModOrganizer.ini");
            var profilePath = Path.Combine(instance, "profiles", profile);
            var modsPath = Path.Combine(instance, "mods");
            if (!File.Exists(iniPath) || !File.Exists(Path.Combine(profilePath, "modlist.txt")) || !File.Exists(Path.Combine(profilePath, "plugins.txt")))
                throw new DirectoryNotFoundException("This folder does not look like an MO2 instance/profile.");
            var ini = File.ReadAllLines(iniPath);
            var gamePath = Mo2Paths.FindGamePath(ini);
            var dataPath = Directory.Exists(Path.Combine(gamePath, "Data")) ? Path.Combine(gamePath, "Data") : throw new DirectoryNotFoundException($"Game Data folder not found: {Path.Combine(gamePath, "Data")}");
            var mods = new List<ModListEntry>();
            foreach (var line in File.ReadLines(Path.Combine(profilePath, "modlist.txt")))
            {
                if (line.Length < 2 || line[0] is not ('+' or '-') || line.StartsWith("#")) continue;
                var name = line[1..].Trim();
                var separator = name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase);
                mods.Add(new ModListEntry(name, line[0] == '+', separator));
            }
            var active = new HashSet<string>(File.ReadLines(Path.Combine(profilePath, "plugins.txt"))
                .Where(l => l.StartsWith('*')).Select(l => l[1..].Trim()), StringComparer.OrdinalIgnoreCase);
            var pluginPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddPluginFiles(dataPath, pluginPaths);
            foreach (var mod in mods.Where(m => m.Enabled && !m.IsSeparator))
                AddPluginFiles(Path.Combine(modsPath, mod.Name), pluginPaths);
            AddPluginFiles(Path.Combine(instance, "overwrite"), pluginPaths);
            var implicitMasters = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "_ResourcePack.esl" };
            var loadOrder = implicitMasters.Where(pluginPaths.ContainsKey).ToList();
            foreach (var plugin in File.ReadLines(Path.Combine(profilePath, "loadorder.txt")).Select(l => l.Trim()))
                if (plugin.Length > 0 && !plugin.StartsWith('#') && active.Contains(plugin) && !loadOrder.Contains(plugin, StringComparer.OrdinalIgnoreCase)) loadOrder.Add(plugin);
            foreach (var missing in active.Where(a => !loadOrder.Contains(a, StringComparer.OrdinalIgnoreCase))) loadOrder.Add(missing);
            return new ProfileContext
            {
                InstancePath = Path.GetFullPath(instance), ProfileName = profile, ProfilePath = profilePath,
                ModsPath = modsPath, DataPath = dataPath, Mods = mods, ActivePluginOrder = loadOrder,
                ActivePlugins = active, PluginPaths = pluginPaths
            };
        }

        private static void AddPluginFiles(string directory, Dictionary<string, string> index)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly).Where(IsPluginFile))
                index[Path.GetFileName(path)] = path;
        }
    }

    private static class PluginScanner
    {
        public static NpcScanResult ReadActiveNpcIndex(ProfileContext context)
        {
            var indexed = new Dictionary<string, TargetNpc>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (var plugin in context.ActivePluginOrder)
            {
                if (!context.PluginPaths.TryGetValue(plugin, out var path))
                {
                    errors.Add($"Plugin file not found in enabled mods, Overwrite, or game Data: {plugin}");
                    continue;
                }
                try
                {
                    using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                    foreach (var npc in mod.Npcs)
                    {
                        if (npc.IsDeleted) { indexed.Remove(FormKeyText(npc.FormKey)); continue; }
                        var key = FormKeyText(npc.FormKey);
                        indexed[key] = new TargetNpc(DisplayName(npc.Name?.String, npc.EditorID, key), npc.EditorID ?? "", key, plugin,
                            FormKeyText(npc.Race.FormKey), IsFemale(npc));
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{plugin}: {ex.Message}");
                    Debug.WriteLine($"NPC scan skipped {plugin}: {ex.Message}");
                }
            }
            return new NpcScanResult(indexed.Values.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList(), errors);
        }

        public static List<NpcReplacement> ReadNpcOverrides(string path, string pluginName, ProfileContext context, IReadOnlyList<TargetNpc> activeTargets)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var sourceNpcs = mod.Npcs.ToArray();
            var sourceModFolder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var linkedMods = new Dictionary<string, ISkyrimModDisposableGetter>(StringComparer.OrdinalIgnoreCase);

            ISkyrimModDisposableGetter? FindRecordMod(FormKey formKey)
            {
                if (mod.ModKey == formKey.ModKey) return mod;
                var fileName = formKey.ModKey.FileName.ToString();
                if (linkedMods.TryGetValue(fileName, out var cached)) return cached;
                if (!context.PluginPaths.TryGetValue(fileName, out var linkedPath)) return null;
                try
                {
                    var linkedMod = SkyrimMod.CreateFromBinaryOverlay(linkedPath, SkyrimRelease.SkyrimSE);
                    linkedMods[fileName] = linkedMod;
                    return linkedMod;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Could not inspect linked appearance records in {fileName}: {ex.Message}");
                    return null;
                }
            }

            T? FindLinkedRecord<T>(FormKey key, Func<ISkyrimModDisposableGetter, T?> selector) where T : class
            {
                var sourceRecord = selector(mod);
                if (sourceRecord is not null) return sourceRecord;
                var linkedMod = FindRecordMod(key);
                return linkedMod is null || linkedMod == mod ? null : selector(linkedMod);
            }

            string? LooseAssetPath(string? relativePath)
            {
                if (string.IsNullOrWhiteSpace(relativePath)) return null;
                try
                {
                    var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(sourceModFolder, normalized));
                    var root = Path.GetFullPath(sourceModFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath) ? fullPath : null;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
                {
                    return null;
                }
            }

            bool HasUniqueBodyModel(string? relativePath)
            {
                if (LooseAssetPath(relativePath) is null) return false;
                var normalized = relativePath!.Replace('/', '\\').TrimStart('\\');
                if (!normalized.StartsWith("meshes\\actors\\character\\", StringComparison.OrdinalIgnoreCase)) return false;
                var sharedBodyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "femalebody_0.nif", "femalebody_1.nif", "femalehands_0.nif", "femalehands_1.nif",
                    "femalefeet_0.nif", "femalefeet_1.nif", "malebody_0.nif", "malebody_1.nif",
                    "malehands_0.nif", "malehands_1.nif", "malefeet_0.nif", "malefeet_1.nif"
                };
                if (!sharedBodyFiles.Contains(Path.GetFileName(normalized))) return true;
                var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                var commonBodyFolder = Array.FindLastIndex(parts, p => p.Equals("character assets", StringComparison.OrdinalIgnoreCase));
                if (commonBodyFolder < 0) return true;
                var assetFolders = parts.Skip(commonBodyFolder + 1).Take(parts.Length - commonBodyFolder - 2).ToArray();
                if (assetFolders.Length == 0) return false;
                var sharedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "female", "male", "human", "humanoid", "beast", "argonian", "khajiit" };
                return assetFolders.Length > 1 || !sharedFolderNames.Contains(assetFolders[0]);
            }

            bool HasUniqueSkinTexture(ITextureSetGetter textureSet)
            {
                var texturePaths = new[]
                {
                    textureSet.Diffuse?.GivenPath,
                    textureSet.NormalOrGloss?.GivenPath,
                    textureSet.EnvironmentMaskOrSubsurfaceTint?.GivenPath,
                    textureSet.GlowOrDetailMap?.GivenPath,
                    textureSet.Height?.GivenPath,
                    textureSet.Environment?.GivenPath,
                    textureSet.Multilayer?.GivenPath,
                    textureSet.BacklightMaskOrSpecular?.GivenPath
                };
                var sharedCharacterTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "female.dds", "femalebody_1.dds", "femalehands_1.dds", "femalefeet_1.dds",
                    "male.dds", "malebody_1.dds", "malehands_1.dds", "malefeet_1.dds"
                };
                var sharedCharacterFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "female", "male", "femalevampire", "malevampire", "argonianfemale", "argonianmale", "khajiitfemale", "khajiitmale" };
                return texturePaths.Any(assetPath =>
                {
                    if (string.IsNullOrWhiteSpace(assetPath)) return false;
                    if (LooseAssetPath(assetPath) is null) return false;
                    var normalized = assetPath.Replace('/', '\\').TrimStart('\\');
                    if (normalized.Contains("\\facegendata\\", StringComparison.OrdinalIgnoreCase)) return false;
                    if (!sharedCharacterTextures.Contains(Path.GetFileName(normalized))) return true;
                    var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length != 5 || !parts[0].Equals("textures", StringComparison.OrdinalIgnoreCase) ||
                        !parts[1].Equals("actors", StringComparison.OrdinalIgnoreCase) ||
                        !parts[2].Equals("character", StringComparison.OrdinalIgnoreCase) ||
                        !sharedCharacterFolders.Contains(parts[3]);
                });
            }

            (bool Skin, bool Body) DetectNpcAssets(INpcGetter npc)
            {
                if (npc.WornArmor is not { IsNull: false } wornArmor) return (false, false);
                var armor = FindLinkedRecord(wornArmor.FormKey, candidate =>
                    candidate.Armors.FirstOrDefault(record => record.FormKey == wornArmor.FormKey));
                if (armor is null) return (false, false);
                var hasSkin = false;
                var hasBody = false;
                foreach (var addonLink in armor.Armature)
                {
                    var addon = FindLinkedRecord(addonLink.FormKey, candidate =>
                        candidate.ArmorAddons.FirstOrDefault(record => record.FormKey == addonLink.FormKey));
                    if (addon is null) continue;
                    var bodyModel = IsFemale(npc) ? addon.WorldModel?.Female : addon.WorldModel?.Male;
                    hasBody |= HasUniqueBodyModel(bodyModel?.File.GivenPath);
                    var skinLink = IsFemale(npc) ? addon.SkinTexture?.Female : addon.SkinTexture?.Male;
                    if (skinLink is not { IsNull: false }) continue;
                    var skinKey = skinLink!.FormKey;
                    var textureSet = FindLinkedRecord(skinKey, candidate =>
                        candidate.TextureSets.FirstOrDefault(record => record.FormKey == skinKey));
                    hasSkin |= textureSet is not null && HasUniqueSkinTexture(textureSet);
                }
                return (hasSkin, hasBody);
            }

            (string? TargetKey, string? AppearancePlugin, string? AppearanceKey, string? Problem, string? AppearanceMod, string? RaceKey, bool? IsFemale) ExistingGalathilRoute(INpcGetter sourceNpc)
            {
                var configFolder = Path.Combine(sourceModFolder, "SKSE", "Plugins", "SkyPatcher", "npc");
                if (!Directory.Exists(configFolder)) return (null, null, null, null, null, null, null);
                var editorStem = System.Text.RegularExpressions.Regex.Replace(sourceNpc.EditorID ?? "NPC", "[^A-Za-z0-9_]", "_");
                if (editorStem.Length > 30) editorStem = editorStem[..30];
                var expectedPrefix = "GALATHIL_" + editorStem + "_";
                string? missingCloneProblem = null;

                foreach (var iniPath in Directory.EnumerateFiles(configFolder, "*.ini", SearchOption.TopDirectoryOnly))
                foreach (var line in File.ReadLines(iniPath))
                {
                    var targetMatch = System.Text.RegularExpressions.Regex.Match(line, @"(?:^|:)filterByNpcs=(?<key>[^:]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var cloneMatch = System.Text.RegularExpressions.Regex.Match(line, @"(?:^|:)copyVisualStyle=(?<key>[^:]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!targetMatch.Success || !cloneMatch.Success) continue;
                    var targetKey = NormalizeRecordKey(targetMatch.Groups["key"].Value);
                    var cloneKey = NormalizeRecordKey(cloneMatch.Groups["key"].Value);
                    var split = cloneKey.LastIndexOf('|');
                    if (split < 1) continue;
                    var appearancePlugin = cloneKey[..split];
                    if (!context.PluginPaths.TryGetValue(appearancePlugin, out var appearancePath))
                    {
                        missingCloneProblem = $"This mod already has a Galathil SkyPatcher rule, but its appearance plugin '{appearancePlugin}' is missing from the selected profile.";
                        continue;
                    }
                    Npc? cloneNpc = null;
                    if (mod.ModKey.FileName.ToString().Equals(appearancePlugin, StringComparison.OrdinalIgnoreCase))
                        cloneNpc = mod.Npcs.FirstOrDefault(n => FormKeyText(n.FormKey).Equals(cloneKey, StringComparison.OrdinalIgnoreCase))?.DeepCopy();
                    else
                    {
                        try
                        {
                            using var appearancePluginMod = SkyrimMod.CreateFromBinaryOverlay(appearancePath, SkyrimRelease.SkyrimSE);
                            cloneNpc = appearancePluginMod.Npcs.FirstOrDefault(n => FormKeyText(n.FormKey).Equals(cloneKey, StringComparison.OrdinalIgnoreCase))?.DeepCopy();
                        }
                        catch (Exception ex) { Debug.WriteLine($"Could not inspect existing Galathil appearance clone: {ex.Message}"); }
                    }
                    if (cloneNpc is null)
                    {
                        missingCloneProblem = $"This mod's Galathil rule points to {cloneKey}, but that appearance record is missing. Restore the source mod's Galathil backup before importing this mapping.";
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(cloneNpc.EditorID) || !cloneNpc.EditorID.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!activeTargets.Any(n => n.FormKeyText.Equals(targetKey, StringComparison.OrdinalIgnoreCase)))
                        return (null, null, null, $"The existing Galathil recipient {targetKey} is not active in this profile. Choose an active recipient manually.", null, null, null);
                    var pluginPathFull = Path.GetFullPath(appearancePath);
                    var appearanceMod = context.Mods.Where(m => m.Enabled && !m.IsSeparator).FirstOrDefault(m =>
                    {
                        var folder = Path.GetFullPath(Path.Combine(context.ModsPath, m.Name)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        return pluginPathFull.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
                    })?.Name;
                    return (targetKey, appearancePlugin, cloneKey, null, appearanceMod,
                        FormKeyText(cloneNpc.Race.FormKey), IsFemale(cloneNpc));
                }
                return (null, null, null, missingCloneProblem, null, null, null);
            }

            static string NormalizeRecordKey(string value)
            {
                var split = value.LastIndexOf('|');
                if (split < 1 || !uint.TryParse(value[(split + 1)..].Trim(), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var id)) return value.Trim();
                return value[..split].Trim() + "|" + id.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
            }

            var overrideKeys = sourceNpcs.Where(n => n.FormKey.ModKey != mod.ModKey).Select(n => FormKeyText(n.FormKey))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var earlierNpcs = new Dictionary<string, Npc>(StringComparer.OrdinalIgnoreCase);
            var sourceOrder = context.ActivePluginOrder.FindIndex(p => p.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
            if (sourceOrder > 0)
            {
                foreach (var priorPlugin in context.ActivePluginOrder.Take(sourceOrder))
                {
                    if (!context.PluginPaths.TryGetValue(priorPlugin, out var priorPath)) continue;
                    using var priorMod = SkyrimMod.CreateFromBinaryOverlay(priorPath, SkyrimRelease.SkyrimSE);
                    foreach (var priorNpc in priorMod.Npcs)
                    {
                        var key = FormKeyText(priorNpc.FormKey);
                        if (!overrideKeys.Contains(key)) continue;
                        if (priorNpc.IsDeleted) earlierNpcs.Remove(key);
                        else earlierNpcs[key] = priorNpc.DeepCopy();
                    }
                }
            }
            var output = new List<NpcReplacement>();
            try
            {
                foreach (var npc in sourceNpcs)
                {
                    var isOverride = npc.FormKey.ModKey != mod.ModKey;
                    var originalKey = FormKeyText(npc.FormKey);
                    earlierNpcs.TryGetValue(originalKey, out var earlier);
                    var assets = DetectNpcAssets(npc);
                    var hasNewOutfit = earlier is not null && npc.DefaultOutfit is { IsNull: false } &&
                        npc.DefaultOutfit.FormKeyNullable != earlier.DefaultOutfit?.FormKeyNullable;
                    var route = ExistingGalathilRoute(npc);
                    output.Add(new NpcReplacement(
                        DisplayName(npc.Name?.String, npc.EditorID, originalKey),
                        npc.EditorID ?? "",
                        originalKey,
                        isOverride ? DisplayName(npc.Name?.String, npc.EditorID, originalKey) : "New NPC record",
                        isOverride,
                        route.RaceKey ?? FormKeyText(npc.Race.FormKey),
                        route.IsFemale ?? IsFemale(npc), assets.Skin, assets.Body, hasNewOutfit,
                        route.AppearancePlugin, route.AppearanceKey, route.TargetKey, route.Problem, route.AppearanceMod));
                }
            }
            finally
            {
                foreach (var linkedMod in linkedMods.Values) linkedMod.Dispose();
            }
            return output.OrderByDescending(n => n.IsDirectOverride).ThenBy(n => n.OverriddenNpcName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string FormKeyText(FormKey key) => $"{key.ModKey.FileName}|{key.ID:X}";
        private static bool IsFemale(INpcGetter npc) => npc.Configuration.Flags.ToString().Split(',')
            .Any(flag => flag.Trim().Equals("Female", StringComparison.OrdinalIgnoreCase));
        private static string DisplayName(string? name, string? editorId, string formKey) =>
            !string.IsNullOrWhiteSpace(name) ? name : !string.IsNullOrWhiteSpace(editorId) ? editorId! : formKey;

        public sealed record NpcScanResult(List<TargetNpc> Npcs, List<string> Errors);
    }

    private static class Mo2Paths
    {
        public static string? TryFindDefaultInstance()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer", "Skyrim Special Edition");
            return File.Exists(Path.Combine(path, "ModOrganizer.ini")) ? path : null;
        }

        public static string ReadSelectedProfile(string iniPath)
        {
            if (!File.Exists(iniPath)) return "Default";
            var line = File.ReadLines(iniPath).FirstOrDefault(l => l.StartsWith("selected_profile=", StringComparison.OrdinalIgnoreCase));
            if (line is null) return "Default";
            var value = line[(line.IndexOf('=') + 1)..].Trim();
            if (value.StartsWith("@ByteArray(") && value.EndsWith(')')) value = value[11..^1];
            return value.Replace("\\\\", "\\");
        }

        public static string FindGamePath(string[] lines)
        {
            var line = lines.FirstOrDefault(l => l.StartsWith("gamePath=", StringComparison.OrdinalIgnoreCase));
            if (line is null) throw new InvalidDataException("MO2's gamePath is missing from ModOrganizer.ini.");
            var value = line[(line.IndexOf('=') + 1)..].Trim();
            if (value.StartsWith("@ByteArray(") && value.EndsWith(')')) value = value[11..^1];
            value = value.Replace("\\\\", "\\");
            return value;
        }
    }
}
