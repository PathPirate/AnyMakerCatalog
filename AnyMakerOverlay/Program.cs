using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Numerics;

namespace AnyMakerOverlay;

internal sealed record CatalogEntry(string Id, string Name, string Category, string Kind, string? MeshPath,
    int? TechTier, string Description, int? PaintColor = null, IReadOnlyList<PreviewMeshPart>? DynamicParts = null);

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        DiagnosticLog.Write($"Started on Windows {Environment.OSVersion.Version}");
        Application.ThreadException += (_, e) =>
        {
            DiagnosticLog.Write("UI exception: " + e.Exception);
            MessageBox.Show("Catalog error. See catalog-diagnostic.log beside the program.", "AnyMaker Catalog");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DiagnosticLog.Write("Unhandled exception: " + e.ExceptionObject);
        var gamePath = GamePathResolver.Resolve();
        if (gamePath is null) return;
        DiagnosticLog.Write("Game folder: " + gamePath);
        Application.Run(new CatalogOverlay(gamePath));
    }
}

internal static class DiagnosticLog
{
    public static void Write(string message)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "catalog-diagnostic.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}"); }
        catch { /* Logging must never prevent the catalog from starting. */ }
    }
}

internal static class GamePathResolver
{
    public static string? Resolve()
    {
        var configFile = Path.Combine(AppContext.BaseDirectory, "game-path.txt");
        string? configured;
        try
        {
            configured = File.Exists(configFile) ? File.ReadAllText(configFile).Trim().Trim('"') : null;
        }
        catch (IOException)
        {
            configured = null;
        }
        if (Valid(configured)) return Path.GetFullPath(configured!);
        MessageBox.Show("Set game-path.txt beside the catalog to your AnyMaker game folder (the one containing game.exe), then start the catalog again.",
            "AnyMaker Catalog", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return null;
    }

    private static bool Valid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return File.Exists(Path.Combine(path, "game.exe")) &&
                   File.Exists(Path.Combine(path, "bin", "game.gcl")) &&
                   File.Exists(Path.Combine(path, "rom", "data", "inventory_definitions.json"));
        }
        catch (ArgumentException) { return false; }
    }
}

internal static class CatalogSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AnyMakerCatalog", "settings.json");

    private static (bool ShowTechTierMinusOne, double TooltipDelaySeconds) Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return (false, 1.5);
            using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
            var show = json.RootElement.TryGetProperty("showTechTierMinusOne", out var value) &&
                       value.ValueKind == JsonValueKind.True;
            var delay = json.RootElement.TryGetProperty("tooltipDelaySeconds", out var savedDelay) &&
                        savedDelay.ValueKind == JsonValueKind.Number && savedDelay.TryGetDouble(out var seconds) &&
                        double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 5) : 1.5;
            return (show, delay);
        }
        catch (Exception error)
        {
            DiagnosticLog.Write("Could not read catalog settings: " + error.Message);
            return (false, 1.5);
        }
    }

    public static bool LoadShowTechTierMinusOne() => Load().ShowTechTierMinusOne;
    public static double LoadTooltipDelaySeconds() => Load().TooltipDelaySeconds;

    private static void Save(bool showTechTierMinusOne, double tooltipDelaySeconds)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new { showTechTierMinusOne, tooltipDelaySeconds }));
        }
        catch (Exception error) { DiagnosticLog.Write("Could not save catalog settings: " + error.Message); }
    }

    public static void SaveShowTechTierMinusOne(bool enabled)
    {
        var settings = Load();
        Save(enabled, settings.TooltipDelaySeconds);
    }
    public static void SaveTooltipDelaySeconds(double seconds)
    {
        var settings = Load();
        Save(settings.ShowTechTierMinusOne, Math.Clamp(seconds, 0, 5));
    }
}

internal static class PaintPalette
{
    public static Color[] Load(string gamePath)
    {
        var colors = new Color[86];
        try
        {
            var path = Path.Combine(gamePath, "rom", "textures", "color_palette.txtr");
            var bytes = File.ReadAllBytes(path);
            const int header = 24;
            if (bytes.Length < header + colors.Length * 4 ||
                !bytes.AsSpan(0, 4).SequenceEqual("TXTR"u8) ||
                BitConverter.ToInt32(bytes, 20) < colors.Length * 4)
                throw new InvalidDataException("Unexpected color palette format");
            for (var i = 0; i < colors.Length; i++)
            {
                var offset = header + i * 4;
                colors[i] = Color.FromArgb(bytes[offset], bytes[offset + 1], bytes[offset + 2]);
            }
            // C85 is the game's missing-texture paint: its palette texel is transparent.
            colors[85] = Color.FromArgb(255, 92, 255);
        }
        catch (Exception error) { DiagnosticLog.Write("Could not load paint swatches: " + error.Message); }
        return colors;
    }
}

internal sealed class StartupStatusForm : Form
{
    private readonly Label heading = new();
    private readonly Label detail = new();
    private readonly Label elapsed = new();
    private readonly Button cancel = new();

    public event EventHandler? CancelRequested;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000; // Keep the game focused, but allow the cancel button to receive clicks.
            return parameters;
        }
    }

    public StartupStatusForm()
    {
        Text = "AnyMaker Catalog — starting";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ControlBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(380, 120);
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10);

        heading.SetBounds(16, 12, 306, 28);
        heading.Font = new Font("Segoe UI Semibold", 12);
        heading.Text = "Starting catalog";
        detail.SetBounds(16, 44, 346, 25);
        detail.ForeColor = Color.FromArgb(205, 205, 205);
        detail.AutoEllipsis = true;
        detail.Text = "Looking for AnyMaker";
        elapsed.SetBounds(16, 80, 346, 20);
        elapsed.ForeColor = Color.FromArgb(160, 164, 173);
        cancel.SetBounds(335, 10, 28, 28);
        cancel.Text = "×";
        cancel.AccessibleName = "Cancel catalog startup";
        cancel.FlatStyle = FlatStyle.Flat;
        cancel.FlatAppearance.BorderSize = 0;
        cancel.BackColor = Color.FromArgb(45, 45, 45);
        cancel.ForeColor = Color.WhiteSmoke;
        cancel.Cursor = Cursors.Hand;
        cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        Controls.AddRange([heading, detail, elapsed, cancel]);
        SetStatus("Starting catalog", "Looking for AnyMaker", TimeSpan.Zero);
        PositionNear(IntPtr.Zero);
    }

    public void PositionNear(IntPtr window)
    {
        var area = window == IntPtr.Zero ? Screen.PrimaryScreen!.WorkingArea : Screen.FromHandle(window).WorkingArea;
        var position = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
        if (Location != position) Location = position;
    }

    public void SetStatus(string stage, string message, TimeSpan running)
    {
        var dots = new string('.', (int)(running.TotalSeconds % 4));
        var title = stage == "Catalog ready" || stage == "Could not connect" ? stage : stage + dots;
        if (heading.Text != title) heading.Text = title;
        if (detail.Text != message) detail.Text = message;
        var time = $"Elapsed {running.Minutes:00}:{running.Seconds:00}";
        if (elapsed.Text != time) elapsed.Text = time;
    }
}

internal sealed class CatalogOverlay : Form
{
    private readonly StartupStatusForm startup = new();
    private readonly Stopwatch startupTimer = Stopwatch.StartNew();
    private bool startupDismissed;
    private DateTime startupReadyAt;
    private readonly List<CatalogEntry> entries = [];
    private readonly Color[] paintPalette;
    private readonly PrivateFontCollection gameFonts = new();
    private readonly MeshPreview previews;
    private readonly Label title = new();
    private readonly TextBox search = new();
    private readonly Panel searchArea = new();
    private readonly RoundedButton clearSearch = new();
    private readonly RoundedButton itemsTab = new();
    private readonly RoundedButton componentsTab = new();
    private readonly RoundedButton settingsTab = new();
    private readonly Button back = new RoundedButton();
    private readonly FlowLayoutPanel content = new();
    private readonly Panel scrollCover = new();
    private readonly Panel scrollRail = new RoundedPanel();
    private readonly Panel scrollThumb = new RoundedPanel();
    private readonly Label status = new();
    private readonly Dictionary<int, Font> statusFontSizes = new();
    private bool statusFitPending;
    private readonly NotifyIcon tray = new();
    private NativeTrackingTooltip? descriptionTip;
    private bool descriptionTipUnavailable;
    private readonly int bridgePort = AllocateBridgePort();
    private readonly System.Windows.Forms.Timer tracker = new() { Interval = 80 };
    private readonly System.Windows.Forms.Timer searchDebounce = new() { Interval = 600 };
    private readonly System.Windows.Forms.Timer descriptionDelay = new();
    private double tooltipDelaySeconds = CatalogSettings.LoadTooltipDelaySeconds();
    private Control? hoveredCard;
    private string? hoveredDescription;
    private readonly string gamePath;
    private Process? ownedBridge;
    private Task<(bool Ok, string? Error, bool? Visible, string? Location)>? bridgeWarmup;
    private bool warmupAttemptedForWindow;
    private DateTime nextWarmupCheck;
    private DateTime nextBridgeStatePoll;
    private bool bridgeStatePollPending;
    private bool hookVisibilityKnown;
    private bool hookInventoryVisible;
    private bool addInProgress;
    private bool trackerFaultLogged;
    private IntPtr gameWindow;
    private Process? connectedGameProcess;
    private DateTime nextGameExitCheck;
    private DateTime? missingGameWindowSince;
    private bool forcedVisible;
    private bool inventoryLatched;
    private int visibleFrames;
    private int hiddenFrames;
    private DateTime suppressUntil;
    private bool closeHeldByKey;
    private readonly Native.LowLevelKeyboardProc keyboardProc;
    private IntPtr keyboardHook;
    private bool keyboardTabDown;
    private bool keyboardEscapeDown;
    private string kind = "Item";
    private string? category;
    private string appliedSearch = "";
    private bool showTechTierMinusOne = CatalogSettings.LoadShowTechTierMinusOne();
    private Font? entryTitleFont;
    private Font? compactTabFont;
    private int scrollDragStartY;
    private int scrollDragStartValue;
    private readonly Color background = Color.FromArgb(30, 30, 30);
    private readonly Color row = Color.FromArgb(36, 36, 36);
    private readonly Color pane = Color.FromArgb(46, 46, 46);

    public CatalogOverlay(string gamePath)
    {
        this.gamePath = gamePath;
        previews = new MeshPreview(gamePath);
        paintPalette = PaintPalette.Load(gamePath);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        Text = "AnyMaker Catalog Overlay";
        BackColor = background;
        ForeColor = Color.WhiteSmoke;
        FontFamily? gameFont = null;
        try
        {
            var fontPath = Path.Combine(gamePath, "rom", "fonts", "noto_sans_regular.ttf");
            gameFonts.AddFontFile(fontPath);
            gameFont = gameFonts.Families.FirstOrDefault();
        }
        catch { /* Keep a system font if the game font is unavailable. */ }
        Font = gameFont is null ? new Font("Segoe UI", 16f, FontStyle.Regular, GraphicsUnit.Pixel) : new Font(gameFont, 16f, FontStyle.Regular, GraphicsUnit.Pixel);
        entryTitleFont = new Font(Font.FontFamily, 14f, FontStyle.Regular, GraphicsUnit.Pixel);
        compactTabFont = new Font(Font.FontFamily, 13.33f, FontStyle.Regular, GraphicsUnit.Pixel);
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(400, 700);
        Round(this, RoundedPaint.Radius);

        var header = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = background };
        title.Text = "Catalog >";
        title.AutoEllipsis = true;
        title.Font = gameFont is null ? new Font("Segoe UI Semibold", 17.33f, FontStyle.Regular, GraphicsUnit.Pixel) : new Font(gameFont, 17.33f, FontStyle.Regular, GraphicsUnit.Pixel);
        title.Location = new Point(14, 8);
        title.Size = new Size(360, 26);
        title.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        header.Controls.Add(title);

        searchArea.Dock = DockStyle.Top;
        searchArea.Height = 42;
        searchArea.Padding = new Padding(12, 3, 12, 3);
        searchArea.BackColor = background;
        var searchHolder = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 7, 10, 7), BackColor = Color.FromArgb(40, 40, 40) };
        var searchLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = searchHolder.BackColor, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        search.Dock = DockStyle.Fill;
        search.Margin = Padding.Empty;
        search.PlaceholderText = "Search...";
        search.BackColor = searchHolder.BackColor;
        search.ForeColor = Color.White;
        search.BorderStyle = BorderStyle.None;
        search.TabStop = false;
        search.TextChanged += (_, _) => { clearSearch.Visible = search.TextLength > 0; searchDebounce.Stop(); searchDebounce.Start(); };
        clearSearch.Text = "×";
        clearSearch.AccessibleName = "Clear search";
        clearSearch.Dock = DockStyle.Fill;
        clearSearch.Margin = Padding.Empty;
        clearSearch.BackColor = searchHolder.BackColor;
        clearSearch.ForeColor = Color.WhiteSmoke;
        clearSearch.FlatStyle = FlatStyle.Flat;
        clearSearch.FlatAppearance.BorderSize = 0;
        clearSearch.Visible = false;
        clearSearch.Click += (_, _) =>
        {
            search.Clear();
            searchDebounce.Stop();
            appliedSearch = "";
            RenderContent();
        };
        searchDebounce.Tick += (_, _) =>
        {
            searchDebounce.Stop();
            appliedSearch = search.Text.Trim();
            RenderContent();
        };
        searchLayout.Controls.Add(search, 0, 0);
        searchLayout.Controls.Add(clearSearch, 1, 0);
        searchHolder.Controls.Add(searchLayout);
        searchArea.Controls.Add(searchHolder);

        var tabs = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(12, 4, 12, 4), BackColor = background };
        ConfigureTab(itemsTab, "Items", 70);
        itemsTab.Location = new Point(12, 4);
        itemsTab.Click += (_, _) => { kind = "Item"; category = null; RenderContent(); };
        ConfigureTab(componentsTab, "Components", 112);
        componentsTab.Location = new Point(86, 4);
        componentsTab.Click += (_, _) => { kind = "Component"; category = null; RenderContent(); };
        ConfigureTab(settingsTab, "Settings", 82);
        settingsTab.Location = new Point(202, 4);
        settingsTab.Click += (_, _) => { kind = "Settings"; category = null; RenderContent(); };
        back.Text = "< Back";
        back.Size = new Size(70, 30);
        back.AccessibleName = "Back";
        back.TextAlign = ContentAlignment.MiddleCenter;
        back.BackColor = row;
        back.ForeColor = Color.WhiteSmoke;
        back.FlatStyle = FlatStyle.Flat;
        back.FlatAppearance.BorderSize = 0;
        back.Padding = Padding.Empty;
        back.Click += (_, _) =>
        {
            category = null;
            search.Clear();
            searchDebounce.Stop();
            appliedSearch = "";
            RenderContent();
        };
        tabs.Controls.AddRange([itemsTab, componentsTab, settingsTab, back]);
        tabs.SizeChanged += (_, _) => LayoutTabs(tabs);
        LayoutTabs(tabs);

        status.Dock = DockStyle.Bottom;
        status.Height = 28;
        status.AutoSize = false;
        status.AutoEllipsis = true;
        status.UseMnemonic = false;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.ForeColor = Color.FromArgb(160, 164, 173);
        status.Padding = new Padding(12, 4, 0, 0);
        status.Font = Font;
        status.Text = "Press Tab in AnyMaker to show the catalog";
        status.TextChanged += (_, _) => QueueFitStatusText();
        status.SizeChanged += (_, _) => QueueFitStatusText();

        content.Dock = DockStyle.Fill;
        content.AutoScroll = true;
        content.FlowDirection = FlowDirection.TopDown;
        content.WrapContents = false;
        content.Padding = new Padding(12, 8, 12, 12);
        content.BackColor = pane;
        content.SizeChanged += (_, _) => ResizeRows();
        Controls.Add(content);
        Controls.Add(status);
        Controls.Add(tabs);
        Controls.Add(searchArea);
        Controls.Add(header);
        SetupScrollbar();
        descriptionDelay.Interval = Math.Max(1, (int)Math.Round(tooltipDelaySeconds * 1000));
        descriptionDelay.Tick += (_, _) =>
        {
            descriptionDelay.Stop();
            ShowDescription();
        };

        LoadCatalog(gamePath);
        RenderContent();
        DiagnosticLog.Write("Catalog loaded; game UI helper will prepare after the game is ready");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show catalog", null, (_, _) => { forcedVisible = true; UpdateOverlay(); });
        menu.Items.Add("Hide catalog", null, (_, _) => { forcedVisible = false; Hide(); });
        menu.Items.Add("Exit", null, (_, _) => Close());
        tray.Icon = SystemIcons.Application;
        tray.Text = "AnyMaker Catalog Overlay";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += (_, _) => { forcedVisible = true; UpdateOverlay(); };

        startup.CancelRequested += (_, _) => Close();
        Shown += (_, _) => { Hide(); startup.Show(); QueueFitStatusText(); };
        keyboardProc = KeyboardEvent;
        keyboardHook = Native.SetWindowsHookEx(13, keyboardProc, IntPtr.Zero, 0);
        if (keyboardHook == IntPtr.Zero) DiagnosticLog.Write("Keyboard listener could not start");
        tracker.Tick += (_, _) =>
        {
            try
            {
                UpdateOverlay();
                if (IsDisposed || Disposing) return;
                UpdateDescriptionHover();
                UpdateStartupStatus();
                UpdateScrollbar();
            }
            catch (Exception ex)
            {
                if (!trackerFaultLogged) DiagnosticLog.Write("Overlay tracking error: " + ex);
                trackerFaultLogged = true;
                Hide();
            }
        };
        tracker.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tracker.Stop();
        HideDescription();
        startup.Close();
        startup.Dispose();
        if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
        searchDebounce.Stop();
        searchDebounce.Dispose();
        try { if (ownedBridge is { HasExited: false }) ownedBridge.Kill(entireProcessTree: true); } catch { }
        ownedBridge?.Dispose();
        connectedGameProcess?.Dispose();
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosed(e);
        descriptionDelay.Dispose();
        descriptionTip?.Dispose();
        entryTitleFont?.Dispose();
        compactTabFont?.Dispose();
        gameFonts.Dispose();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) HideDescription();
    }

    private void QueueFitStatusText()
    {
        if (!IsHandleCreated || IsDisposed || statusFitPending) return;
        statusFitPending = true;
        BeginInvoke(() =>
        {
            statusFitPending = false;
            if (!IsDisposed) FitStatusText();
        });
    }

    private void FitStatusText()
    {
        if (status.Width != ClientSize.Width) status.Width = ClientSize.Width;
        var width = Math.Max(1, status.ClientSize.Width - status.Padding.Horizontal);
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var selected = Font;
        if (TextRenderer.MeasureText(status.Text, selected, Size.Empty, flags).Width > width)
        {
            for (var quarterPixels = (int)(Font.Size * 4) - 1; quarterPixels >= 32; quarterPixels--)
            {
                if (!statusFontSizes.TryGetValue(quarterPixels, out var candidate))
                {
                    candidate = new Font(Font.FontFamily, quarterPixels / 4f, Font.Style, Font.Unit);
                    statusFontSizes.Add(quarterPixels, candidate);
                }
                selected = candidate;
                if (TextRenderer.MeasureText(status.Text, candidate, Size.Empty, flags).Width <= width) break;
            }
        }
        if (!ReferenceEquals(status.Font, selected)) status.Font = selected;
    }

    private void BeginDescription(CatalogEntry entry, Control card)
    {
        if (string.IsNullOrWhiteSpace(entry.Description))
        {
            if (hoveredCard is not null) HideDescription();
            return;
        }
        if (hoveredCard == card) return;
        HideDescription();
        hoveredCard = card;
        hoveredDescription = entry.Description;
        if (tooltipDelaySeconds == 0) ShowDescription();
        else descriptionDelay.Start();
    }

    private void ShowDescription()
    {
        if (descriptionTipUnavailable) return;
        if (!Visible || hoveredCard is null || hoveredCard.IsDisposed || string.IsNullOrWhiteSpace(hoveredDescription) ||
            !hoveredCard.RectangleToScreen(hoveredCard.ClientRectangle).Contains(Cursor.Position)) return;
        var bounds = hoveredCard.RectangleToScreen(hoveredCard.ClientRectangle);
        var area = Screen.FromRectangle(bounds).WorkingArea;
        var width = Math.Min(360, Math.Max(180, TextRenderer.MeasureText(hoveredDescription, Font).Width + 24));
        var x = bounds.Left - width - 10 >= area.Left ? bounds.Left - width - 10 : bounds.Right + 10;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        try
        {
            descriptionTip ??= new NativeTrackingTooltip(Handle, Font);
            if (!descriptionTip.Show(hoveredDescription, new Point(x, bounds.Top)))
                DiagnosticLog.Write("Description tooltip did not become visible");
        }
        catch (Exception error)
        {
            descriptionTipUnavailable = true;
            DiagnosticLog.Write("Description tooltip unavailable: " + error);
        }
    }

    private void UpdateDescriptionHover()
    {
        if (Visible)
        {
            var pointer = Cursor.Position;
            var point = content.PointToClient(pointer);
            var row = content.ClientRectangle.Contains(point)
                ? content.Controls.Cast<Control>().FirstOrDefault(control => control.Bounds.Contains(point)) : null;
            var insideRow = row is null ? Point.Empty : new Point(point.X - row.Left, point.Y - row.Top);
            var card = row?.Controls.Cast<Control>().FirstOrDefault(control => control.Bounds.Contains(insideRow));
            if (card?.Tag is CatalogEntry entry)
            {
                BeginDescription(entry, card);
                return;
            }
        }
        if (hoveredCard is not null) HideDescription();
    }

    private void HideDescription()
    {
        descriptionDelay.Stop();
        descriptionTip?.Hide();
        hoveredCard = null;
        hoveredDescription = null;
    }

    private void LoadCatalog(string gamePath)
    {
        try
        {
            foreach (var (file, type) in new[] { ("inventory_definitions.json", "Item"), ("vehicle_component_definitions.json", "Component") })
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(gamePath, "rom", "data", file)));
                foreach (var definition in doc.RootElement.GetProperty("definitions").EnumerateArray())
                {
                    string Read(string key) => definition.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                    var id = Read("id");
                    var mesh = type == "Item" ? Read("mesh_file") :
                        definition.TryGetProperty("mesh_static", out var staticMesh) && staticMesh.ValueKind == JsonValueKind.Object && staticMesh.TryGetProperty("mesh_path", out var meshPath) ? meshPath.GetString() : null;
                    List<PreviewMeshPart>? parts = null;
                    if (type == "Component" && definition.TryGetProperty("meshes_dynamic", out var dynamicMeshes) && dynamicMeshes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in dynamicMeshes.EnumerateArray())
                        {
                            if (!part.TryGetProperty("path", out var partPath) || partPath.ValueKind != JsonValueKind.String) continue;
                            var position = Vector3.Zero;
                            if (part.TryGetProperty("pos", out var pos) && pos.ValueKind == JsonValueKind.Array && pos.GetArrayLength() == 3)
                                position = new Vector3(pos[0].GetSingle(), pos[1].GetSingle(), pos[2].GetSingle());
                            float[]? rotation = null;
                            if (part.TryGetProperty("preview_rot", out var rot) && rot.ValueKind == JsonValueKind.Array && rot.GetArrayLength() == 9)
                                rotation = rot.EnumerateArray().Select(value => value.GetSingle()).ToArray();
                            (parts ??= []).Add(new PreviewMeshPart(partPath.GetString()!, position, rotation));
                        }
                    }
                    int? techTier = definition.TryGetProperty("tech_tier", out var tier) && tier.ValueKind == JsonValueKind.Number && tier.TryGetInt32(out var number) ? number : null;
                    if (id == "vehicle_editor_paint" && type == "Item")
                    {
                        for (var color = 0; color <= 85; color++)
                            entries.Add(new(id, $"Paint Tool C{color:00}", "paint", type, mesh, techTier, Read("description"), color));
                    }
                    else if (id.Length > 0)
                        entries.Add(new(id, Read("name") is { Length: > 0 } name ? name : id, Read("category"), type, mesh, techTier, Read("description"), DynamicParts: parts));
                }
            }
        }
        catch (Exception ex) { status.Text = "Could not load game catalog: " + ex.Message; }
    }

    private void RenderContent()
    {
        HideDescription();
        content.SuspendLayout();
        content.AutoScrollPosition = Point.Empty;
        foreach (Control previous in content.Controls.Cast<Control>().ToArray()) previous.Dispose();
        content.Controls.Clear();
        var scope = kind == "Item" ? "Items" : kind == "Component" ? "Components" : "Settings";
        title.Text = category == null ? $"Catalog > {scope}" : $"Catalog > {scope} > {FormatCategory(category)}";
        SelectTab(itemsTab, kind == "Item");
        SelectTab(componentsTab, kind == "Component");
        SelectTab(settingsTab, kind == "Settings");
        search.Enabled = kind != "Settings";
        clearSearch.Enabled = kind != "Settings";
        back.Enabled = category != null && kind != "Settings";
        back.ForeColor = back.Enabled ? Color.WhiteSmoke : Color.Gray;
        if (kind == "Settings")
        {
            content.FlowDirection = FlowDirection.TopDown;
            content.WrapContents = false;
            var heading = new Label { Text = "Catalog display", Height = 32, ForeColor = Color.WhiteSmoke,
                Font = Font, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 4, 0, 6) };
            var toggle = Button($"Show Tech Tier -1 Items: {(showTechTierMinusOne ? "On" : "Off")}", 200);
            toggle.Height = 38;
            toggle.TextAlign = ContentAlignment.MiddleLeft;
            toggle.Padding = new Padding(12, 0, 0, 0);
            toggle.BackColor = showTechTierMinusOne ? Color.FromArgb(55, 66, 60) : row;
            toggle.Margin = new Padding(0, 0, 0, 8);
            toggle.AccessibleRole = AccessibleRole.CheckButton;
            toggle.Click += (_, _) =>
            {
                showTechTierMinusOne = !showTechTierMinusOne;
                CatalogSettings.SaveShowTechTierMinusOne(showTechTierMinusOne);
                RenderContent();
            };
            var description = new Label { Text = "Include items and components marked Tech Tier -1 in categories and search results.",
                Height = 58, ForeColor = Color.FromArgb(180, 180, 180), Margin = Padding.Empty };
            var delayRow = new RoundedPanel { Height = 42, BackColor = row, Margin = new Padding(0, 8, 0, 0) };
            var delayLabel = new Label { Text = "Tooltip delay (seconds)", AutoSize = true,
                ForeColor = Color.WhiteSmoke, Location = new Point(12, 11) };
            var delayInput = new NumericUpDown { Minimum = 0, Maximum = 5, DecimalPlaces = 1,
                Increment = 0.1m, Value = (decimal)tooltipDelaySeconds, Width = 68,
                BackColor = pane, ForeColor = Color.WhiteSmoke, BorderStyle = BorderStyle.None };
            delayRow.Controls.Add(delayLabel);
            delayRow.Controls.Add(delayInput);
            delayRow.SizeChanged += (_, _) => delayInput.Location = new Point(delayRow.ClientSize.Width - delayInput.Width - 12, 11);
            delayInput.ValueChanged += (_, _) =>
            {
                tooltipDelaySeconds = (double)delayInput.Value;
                descriptionDelay.Stop();
                descriptionDelay.Interval = Math.Max(1, (int)Math.Round(tooltipDelaySeconds * 1000));
                CatalogSettings.SaveTooltipDelaySeconds(tooltipDelaySeconds);
            };
            content.Controls.AddRange([heading, toggle, description, delayRow]);
            status.Text = "Settings";
            ResizeRows();
            content.ResumeLayout(true);
            UpdateScrollbar();
            return;
        }
        var query = appliedSearch;
        var showingCategories = category == null && query.Length == 0;
        content.FlowDirection = FlowDirection.TopDown;
        content.WrapContents = false;
        var filtered = entries.Where(e => e.Kind == kind && (showTechTierMinusOne || e.TechTier != -1) &&
            (query.Length == 0 || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Id.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (showingCategories)
        {
            var groups = filtered.GroupBy(e => e.Category).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                var name = group.Key;
                var button = Button(FormatCategory(name), 0);
                button.Height = 29;
                button.BackColor = row;
                button.TextAlign = ContentAlignment.MiddleLeft;
                button.Padding = new Padding(24, 0, 0, 0);
                button.Margin = new Padding(0, 2, 0, 3);
                button.Click += (_, _) => { category = name; RenderContent(); };
                content.Controls.Add(button);
            }
            status.Text = $"{filtered.Length} {kind.ToLowerInvariant()}s · select a category";
        }
        else
        {
            var found = filtered.Where(e => category == null || e.Category == category).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id).ToArray();
            Panel? cardRow = null;
            for (var index = 0; index < found.Length; index++)
            {
                var entry = found[index];
                if (index % 3 == 0)
                {
                    cardRow = new Panel { Height = 161, Width = 100, BackColor = pane, Margin = Padding.Empty };
                    content.Controls.Add(cardRow);
                }
                var card = new RoundedPanel { Height = 154, BackColor = row, Margin = Padding.Empty, Tag = entry };
                var preview = new RoundedImageButton { BackColor = Color.FromArgb(35, 37, 42), Location = new Point(8, 8), Size = new Size(92, 90),
                    Cursor = Cursors.Hand, AccessibleName = $"Add {entry.Name}",
                    PaintBackground = entry.PaintColor is int color && !paintPalette[color].IsEmpty ? paintPalette[color] : null };
                preview.Click += async (_, _) => await AddAsync(entry, preview);
                var name = new FittingTitleLabel { Text = entry.Name, Font = entryTitleFont, ForeColor = Color.WhiteSmoke,
                    TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Location = new Point(8, 102), Size = new Size(92, 46) };
                card.Controls.AddRange([preview, name]);
                cardRow!.Controls.Add(card);
                var current = previews.Request(entry.MeshPath, entry.Kind == "Component", entry.PaintColor.HasValue,
                    entry.Kind == "Item" && entry.Category == "vehicle_editor",
                    entry.DynamicParts,
                    entry.Kind == "Component" && entry.Name.Equals("Engine", StringComparison.OrdinalIgnoreCase), bitmap =>
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(() => { if (!preview.IsDisposed) preview.Image = bitmap; });
                });
                if (current != null) preview.Image = current;
            }
            status.Text = $"{found.Length} {kind.ToLowerInvariant()}s";
        }
        ResizeRows();
        content.ResumeLayout(true);
        UpdateScrollbar();
    }

    private static string FormatCategory(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Uncategorized";
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(raw.Replace('_', ' ').ToLower(CultureInfo.CurrentCulture));
    }

    private void LayoutTabs(Panel tabs)
    {
        if (tabs.ClientSize.Width <= 0) return;
        var compact = tabs.ClientSize.Width < 370;
        itemsTab.Width = compact ? 58 : 70;
        componentsTab.Width = compact ? 96 : 112;
        settingsTab.Width = compact ? 72 : 82;
        back.Width = compact ? 34 : 70;
        back.Text = compact ? "<" : "< Back";
        var tabFont = compact ? compactTabFont! : Font;
        itemsTab.Font = tabFont;
        componentsTab.Font = tabFont;
        settingsTab.Font = tabFont;
        back.Font = tabFont;
        itemsTab.Location = new Point(12, 4);
        componentsTab.Location = new Point(itemsTab.Right + 4, 4);
        settingsTab.Location = new Point(componentsTab.Right + 4, 4);
        back.Location = new Point(tabs.ClientSize.Width - back.Width - 12, 4);
    }

    private void StartBridge()
    {
        if (ownedBridge is { HasExited: false }) return;
        ownedBridge?.Dispose();
        var exe = Path.Combine(AppContext.BaseDirectory, "AnyMakerBridge.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Add helper is missing", exe);
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            info.ArgumentList.Add(Environment.ProcessId.ToString());
            info.ArgumentList.Add(bridgePort.ToString());
            ownedBridge = Process.Start(info);
            DiagnosticLog.Write("Add helper started");
        }
        catch (Exception ex) { DiagnosticLog.Write("Add helper start failed: " + ex); throw; }
    }

    private void BeginBridgeWarmup()
    {
        if (warmupAttemptedForWindow) return;
        warmupAttemptedForWindow = true;
        try
        {
            StartBridge();
            bridgeWarmup = WaitForBridgeAsync();
            _ = ObserveBridgeWarmupAsync(bridgeWarmup);
        }
        catch (Exception ex) { DiagnosticLog.Write("Add helper warmup failed: " + ex); }
    }

    private async Task ObserveBridgeWarmupAsync(Task<(bool Ok, string? Error, bool? Visible, string? Location)> warmup)
    {
        try
        {
            var result = await warmup;
            DiagnosticLog.Write(result.Ok ? "Add helper ready before first Add" : "Add helper unavailable: " + result.Error);
        }
        catch (Exception ex) { DiagnosticLog.Write("Add helper warmup failed: " + ex); }
    }

    private void UpdateStartupStatus()
    {
        if (startupDismissed) return;
        if (inventoryLatched)
        {
            startup.Hide();
            startupDismissed = true;
            return;
        }
        string stage;
        string detail;
        if (gameWindow == IntPtr.Zero)
        {
            stage = "Waiting for AnyMaker";
            detail = "Start the game to connect the catalog";
        }
        else if (bridgeWarmup is { IsCompletedSuccessfully: true } ready)
        {
            if (ready.Result.Ok)
            {
                startupReadyAt = startupReadyAt == default ? DateTime.UtcNow : startupReadyAt;
                stage = "Catalog ready";
                detail = "Press Tab in AnyMaker to open inventory";
                if (DateTime.UtcNow - startupReadyAt > TimeSpan.FromSeconds(5))
                {
                    startup.Hide();
                    startupDismissed = true;
                    return;
                }
            }
            else
            {
                stage = "Could not connect";
                detail = ready.Result.Error ?? "Check catalog-diagnostic.log";
            }
        }
        else if (bridgeWarmup is { IsFaulted: true })
        {
            stage = "Could not connect";
            detail = "Check catalog-diagnostic.log";
        }
        else if (bridgeWarmup is not null)
        {
            stage = "Connecting to AnyMaker";
            detail = "Add helper loading. Please wait ~1-2 minutes.";
        }
        else if (Native.GetForegroundWindow() != gameWindow)
        {
            stage = "Switch to AnyMaker";
            detail = "Keep the game in view while the catalog connects";
        }
        else
        {
            stage = "Waiting for the game";
            detail = "The game is settling before connection";
        }
        startup.SetStatus(stage, detail, startupTimer.Elapsed);
        startup.PositionNear(gameWindow);
        if (!startup.Visible) startup.Show();
    }

    private async Task PollBridgeStateAsync()
    {
        bridgeStatePollPending = true;
        try
        {
            var result = await SendBridgeAsync(new { op = "inventory" }, TimeSpan.FromSeconds(2));
            hookVisibilityKnown = result.Ok && result.Visible.HasValue;
            hookInventoryVisible = result.Visible == true;
        }
        catch
        {
            hookVisibilityKnown = false;
            hookInventoryVisible = false;
        }
        finally { bridgeStatePollPending = false; }
    }

    private async Task AddAsync(CatalogEntry entry, Button button)
    {
        if (addInProgress) return;
        addInProgress = true;
        status.Text = $"Adding {entry.Name}...";
        try
        {
            DiagnosticLog.Write("Add clicked: " + entry.Id);
            var timer = Stopwatch.StartNew();
            if (bridgeWarmup is null || ownedBridge is null || ownedBridge.HasExited)
            {
                StartBridge();
                bridgeWarmup = WaitForBridgeAsync();
            }
            var ready = await bridgeWarmup;
            if (!ready.Ok) throw new IOException(ready.Error ?? "The live bridge is unavailable.");
            DiagnosticLog.Write($"Add helper ready after {timer.Elapsed.TotalSeconds:F1}s");
            var result = await SendBridgeAsync(new { kind = entry.Kind, id = entry.Id, color = entry.PaintColor }, TimeSpan.FromSeconds(90));
            if (!result.Ok) throw new IOException(result.Error ?? "Unknown bridge error");
            status.Text = result.Location switch
            {
                "ground" => $"Dropped {entry.Name} nearby (inventory full)",
                "hand" => $"Placed {entry.Name} in hand (inventory full)",
                "secondary" => $"Added {entry.Name} to carried storage",
                _ => $"Added {entry.Name} to inventory"
            };
            DiagnosticLog.Write($"Add completed in {timer.Elapsed.TotalSeconds:F1}s");
        }
        catch (Exception ex) { DiagnosticLog.Write("Add failed: " + ex); status.Text = "Add failed: " + ex.Message; }
        finally
        {
            addInProgress = false;
        }
    }

    private static int AllocateBridgePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private async Task<(bool Ok, string? Error, bool? Visible, string? Location)> WaitForBridgeAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try { return await SendBridgeAsync(new { op = "status" }, TimeSpan.FromSeconds(90)); }
            catch (SocketException) when (attempt < 99) { await Task.Delay(150); }
        }
        throw new IOException("The Add helper did not open its local connection.");
    }

    private async Task<(bool Ok, string? Error, bool? Visible, string? Location)> SendBridgeAsync(object message, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, bridgePort, cancel.Token);
        using var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await stream.WriteAsync(bytes, cancel.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var line = await reader.ReadLineAsync(cancel.Token);
        if (line == null) throw new IOException("The live bridge closed the connection.");
        using var result = JsonDocument.Parse(line);
        var root = result.RootElement;
        return (root.GetProperty("ok").GetBoolean(),
            root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null,
            root.TryGetProperty("visible", out var visible) && visible.ValueKind is JsonValueKind.True or JsonValueKind.False ? visible.GetBoolean() : null,
            root.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.String ? location.GetString() : null);
    }

    private void ResizeRows()
    {
        if (kind == "Settings")
        {
            var settingsWidth = Math.Max(150, content.Width - content.Padding.Horizontal - 24);
            foreach (Control control in content.Controls) control.Width = settingsWidth;
            return;
        }
        var categoryRows = category == null && appliedSearch.Length == 0;
        var rows = content.Controls.Count;
        var rowHeight = categoryRows ? 34 : 161;
        var needsScroll = rows * rowHeight + content.Padding.Vertical > content.ClientSize.Height;
        // Our scrollbar rail begins 20px from the content edge. This leaves
        // the same 12px gap on both sides of each row, with or without a rail.
        var width = Math.Max(150, content.Width - content.Padding.Horizontal - (needsScroll ? 20 : 0));
        if (categoryRows)
        {
            foreach (Control control in content.Controls) control.Width = width;
            return;
        }
        foreach (Control rowControl in content.Controls)
        {
            rowControl.Width = width;
            var cards = rowControl.Controls.Cast<Control>().ToArray();
            var baseWidth = (width - 10) / 3;
            var remainder = (width - 10) % 3;
            var left = 0;
            for (var index = 0; index < cards.Length; index++)
            {
                var card = cards[index];
                var cardWidth = baseWidth + (index < remainder ? 1 : 0);
                card.Bounds = new Rectangle(left, 3, cardWidth, 154);
                left += cardWidth + 5;
                if (card.Controls.OfType<RoundedImageButton>().FirstOrDefault() is { } picture) picture.Width = cardWidth - 16;
                if (card.Controls.OfType<FittingTitleLabel>().FirstOrDefault() is { } label)
                {
                    label.Width = cardWidth - 16;
                    label.FitText(entryTitleFont!);
                }
            }
        }
    }

    private static Button Button(string text, int width)
    {
        var button = new RoundedButton
        {
            Text = text, Width = width, Height = 30, BackColor = Color.FromArgb(43, 43, 43),
            ForeColor = Color.WhiteSmoke, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(53, 53, 53), MouseDownBackColor = Color.FromArgb(62, 62, 62) }
        };
        return button;
    }

    private static void ConfigureTab(RoundedButton tab, string text, int width)
    {
        tab.Text = text;
        tab.Size = new Size(width, 30);
        tab.FlatStyle = FlatStyle.Flat;
        tab.FlatAppearance.BorderSize = 0;
        tab.UseVisualStyleBackColor = false;
        tab.TextAlign = ContentAlignment.MiddleCenter;
    }

    private static void SelectTab(RoundedButton tab, bool selected)
    {
        tab.IsSelectedTab = selected;
        tab.Height = selected ? 38 : 30;
        tab.BackColor = selected ? Color.FromArgb(46, 46, 46) : Color.FromArgb(31, 31, 31);
        tab.ForeColor = selected ? Color.White : Color.FromArgb(170, 170, 170);
        tab.FlatAppearance.MouseOverBackColor = selected ? Color.FromArgb(46, 46, 46) : Color.FromArgb(40, 40, 40);
        tab.FlatAppearance.MouseDownBackColor = selected ? Color.FromArgb(46, 46, 46) : Color.FromArgb(46, 46, 46);
        tab.Invalidate();
    }

    private static void Round(Control control, int radius)
    {
        void Apply(object? sender, EventArgs e)
        {
            if (control.Width < 2 || control.Height < 2) return;
            var curve = Math.Min(radius, Math.Min(control.Width, control.Height) / 2);
            using var path = new GraphicsPath(FillMode.Winding);
            if (control.Height > curve * 2)
                path.AddRectangle(new Rectangle(0, curve, control.Width, control.Height - curve * 2));
            for (var y = 0; y < curve; y++)
            {
                var distance = curve - y - .5;
                var inset = (int)Math.Round(curve - Math.Sqrt(curve * curve - distance * distance));
                var span = control.Width - inset * 2;
                if (span <= 0) continue;
                path.AddRectangle(new Rectangle(inset, y, span, 1));
                path.AddRectangle(new Rectangle(inset, control.Height - 1 - y, span, 1));
            }
            var previous = control.Region;
            control.Region = new Region(path);
            previous?.Dispose();
            control.Invalidate();
        }
        control.SizeChanged += Apply;
        Apply(control, EventArgs.Empty);
    }

    private void SetupScrollbar()
    {
        scrollCover.BackColor = pane;
        scrollRail.BackColor = Color.FromArgb(38, 38, 38);
        scrollThumb.BackColor = Color.FromArgb(60, 60, 60);
        scrollRail.Controls.Add(scrollThumb);
        scrollCover.Controls.Add(scrollRail);
        Controls.Add(scrollCover);
        scrollCover.BringToFront();
        scrollCover.Visible = false;
        scrollRail.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) ScrollToRailPosition(e.Y); };
        scrollThumb.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            scrollDragStartY = Cursor.Position.Y;
            scrollDragStartValue = -content.AutoScrollPosition.Y;
        };
        scrollThumb.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var maximum = ScrollRange();
            var travel = Math.Max(1, scrollRail.Height - scrollThumb.Height);
            var target = scrollDragStartValue + (Cursor.Position.Y - scrollDragStartY) * maximum / travel;
            content.AutoScrollPosition = new Point(0, Math.Clamp(target, 0, maximum));
            UpdateScrollbar();
        };
        scrollRail.MouseWheel += (_, e) => ScrollByWheel(e.Delta);
        scrollThumb.MouseWheel += (_, e) => ScrollByWheel(e.Delta);
        content.Scroll += (_, _) => UpdateScrollbar();
        content.SizeChanged += (_, _) => UpdateScrollbar();
    }

    private void ScrollByWheel(int delta)
    {
        var target = -content.AutoScrollPosition.Y - Math.Sign(delta) * 48;
        var maximum = ScrollRange();
        content.AutoScrollPosition = new Point(0, Math.Clamp(target, 0, maximum));
        UpdateScrollbar();
    }

    private void ScrollToRailPosition(int y)
    {
        var maximum = ScrollRange();
        var travel = Math.Max(1, scrollRail.Height - scrollThumb.Height);
        var target = (int)Math.Round(Math.Clamp(y - scrollThumb.Height / 2, 0, travel) * (double)maximum / travel);
        content.AutoScrollPosition = new Point(0, target);
        UpdateScrollbar();
    }

    private void UpdateScrollbar()
    {
        if (!Visible || !content.IsHandleCreated || content.IsDisposed) return;
        var coverBounds = new Rectangle(content.Right - 24, content.Top, 24, content.Height);
        if (scrollCover.Bounds != coverBounds) scrollCover.Bounds = coverBounds;
        var railBounds = new Rectangle(4, 8, 16, Math.Max(1, scrollCover.Height - 16));
        if (scrollRail.Bounds != railBounds) scrollRail.Bounds = railBounds;
        var visible = content.VerticalScroll.Visible;
        if (scrollCover.Visible != visible) scrollCover.Visible = visible;
        if (!visible) return;
        var maximum = ScrollRange();
        var viewport = Math.Max(1, content.ClientSize.Height);
        var thumbHeight = Math.Clamp((int)(scrollRail.Height * (double)viewport / (viewport + maximum)), 34, scrollRail.Height);
        var travel = Math.Max(0, scrollRail.Height - thumbHeight);
        var value = Math.Clamp(-content.AutoScrollPosition.Y, 0, maximum);
        var top = maximum == 0 ? 0 : (int)Math.Round(travel * (double)value / maximum);
        var thumbBounds = new Rectangle(2, top, 12, thumbHeight);
        if (scrollThumb.Bounds != thumbBounds) scrollThumb.Bounds = thumbBounds;
    }

    private int ScrollRange() => Math.Max(0, content.DisplayRectangle.Height - content.ClientSize.Height);

    private IntPtr KeyboardEvent(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message == (IntPtr)0x100 || message == (IntPtr)0x101 || message == (IntPtr)0x104 || message == (IntPtr)0x105))
        {
            var key = Marshal.PtrToStructure<Native.KeyboardData>(data);
            if (key.VirtualKey == 0x09 || key.VirtualKey == 0x1B)
            {
                var down = message == (IntPtr)0x100 || message == (IntPtr)0x104;
                ref var wasDown = ref (key.VirtualKey == 0x09 ? ref keyboardTabDown : ref keyboardEscapeDown);
                var pressed = down && !wasDown;
                wasDown = down;
                if (pressed && (key.Flags & 0x30) == 0 && Native.GetForegroundWindow() == gameWindow && IsHandleCreated)
                    BeginInvoke(() => InventoryKeyPressed((int)key.VirtualKey));
            }
        }
        return Native.CallNextHookEx(keyboardHook, code, message, data);
    }

    private void InventoryKeyPressed(int key)
    {
        if (key == 0x09 && closeHeldByKey)
        {
            closeHeldByKey = false;
            suppressUntil = DateTime.UtcNow.AddMilliseconds(200);
            DiagnosticLog.Write("Inventory reopening by key");
        }
        else if (!closeHeldByKey && (inventoryLatched || Visible))
        {
            closeHeldByKey = true;
            inventoryLatched = false;
            forcedVisible = false;
            Hide();
            DiagnosticLog.Write("Inventory closed by key");
        }
    }

    private void UpdateOverlay()
    {
        if (connectedGameProcess is not null && DateTime.UtcNow >= nextGameExitCheck)
        {
            nextGameExitCheck = DateTime.UtcNow.AddSeconds(1);
            try
            {
                if (connectedGameProcess.HasExited)
                {
                    DiagnosticLog.Write("AnyMaker exited; closing catalog and Add helper");
                    Close();
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                DiagnosticLog.Write("Could not check game process: " + error.Message);
            }
        }
        if (gameWindow == IntPtr.Zero || !Native.IsWindow(gameWindow))
        {
            gameWindow = Native.FindGameWindow();
            if (gameWindow != IntPtr.Zero)
            {
                missingGameWindowSince = null;
                DiagnosticLog.Write("Game window detected");
                if (connectedGameProcess is null)
                {
                    Native.GetWindowThreadProcessId(gameWindow, out var processId);
                    if (processId != 0)
                    {
                        try
                        {
                            connectedGameProcess = Process.GetProcessById((int)processId);
                            nextGameExitCheck = DateTime.UtcNow.AddSeconds(1);
                        }
                        catch (ArgumentException) { /* The window's process exited while it was found. */ }
                    }
                }
            }
            warmupAttemptedForWindow = false;
            bridgeWarmup = null;
            hookVisibilityKnown = false;
            hookInventoryVisible = false;
            nextWarmupCheck = DateTime.UtcNow.AddSeconds(2);
            inventoryLatched = false;
            closeHeldByKey = false;
            visibleFrames = 0;
            hiddenFrames = 0;
        }
        if (gameWindow == IntPtr.Zero && connectedGameProcess is not null)
        {
            missingGameWindowSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - missingGameWindowSince >= TimeSpan.FromSeconds(10))
            {
                DiagnosticLog.Write("AnyMaker window disappeared; closing catalog and Add helper");
                Close();
                return;
            }
        }
        if (gameWindow == IntPtr.Zero || Native.IsIconic(gameWindow) || !Native.GetGameClientBounds(gameWindow, out var rect)) { Hide(); return; }
        var foreground = Native.GetForegroundWindow();
        if (!warmupAttemptedForWindow && foreground == gameWindow && DateTime.UtcNow >= nextWarmupCheck)
        {
            if (GameProcessSettled()) BeginBridgeWarmup();
            else nextWarmupCheck = DateTime.UtcNow.AddSeconds(2);
        }
        if (bridgeWarmup is { IsCompletedSuccessfully: true } && bridgeWarmup.Result.Ok &&
            !bridgeStatePollPending && DateTime.UtcNow >= nextBridgeStatePoll)
        {
            nextBridgeStatePoll = DateTime.UtcNow.AddMilliseconds(100);
            _ = PollBridgeStateAsync();
        }
        var detected = !closeHeldByKey && DateTime.UtcNow >= suppressUntil && hookVisibilityKnown && hookInventoryVisible;
        if (detected)
        {
            visibleFrames++;
            hiddenFrames = 0;
            if (visibleFrames >= 1 && !inventoryLatched)
            {
                inventoryLatched = true;
                DiagnosticLog.Write("Inventory detected");
            }
        }
        else
        {
            hiddenFrames++;
            visibleFrames = 0;
            if (hiddenFrames >= 1 && inventoryLatched)
            {
                inventoryLatched = false;
                DiagnosticLog.Write("Inventory closed");
            }
        }
        if (DateTime.UtcNow < suppressUntil || (foreground != gameWindow && foreground != Handle) || (!forcedVisible && !inventoryLatched)) { Hide(); return; }
        var gameWidth = rect.Right - rect.Left;
        var gameHeight = rect.Bottom - rect.Top;
        var width = Math.Clamp((int)(gameWidth * .159), 300, 418);
        var top = rect.Top + (int)(gameHeight * .116) + 20;
        var bottom = rect.Bottom - 25 - 117;
        var height = Math.Max(320, bottom - top);
        var bounds = new Rectangle(rect.Right - width - 13, top, width, height);
        if (Bounds != bounds) Bounds = bounds;
        if (!Visible) Show();
    }

    private static bool GameProcessSettled()
    {
        foreach (var process in Process.GetProcessesByName("game"))
        {
            try
            {
                if (DateTime.Now - process.StartTime >= TimeSpan.FromSeconds(30)) return true;
            }
            catch { /* A process can exit while being inspected. */ }
            finally { process.Dispose(); }
        }
        return false;
    }

    private void ActivateGame()
    {
        if (gameWindow != IntPtr.Zero && Native.IsWindow(gameWindow)) Native.SetForegroundWindow(gameWindow);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (Visible && (keyData == Keys.Tab || keyData == Keys.Escape))
        {
            forcedVisible = false;
            closeHeldByKey = true;
            suppressUntil = DateTime.UtcNow.AddMilliseconds(300);
            Hide();
            ForwardKeyToGame(keyData == Keys.Tab ? 0x09 : 0x1B);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async void ForwardKeyToGame(int key)
    {
        ActivateGame();
        await Task.Delay(60);
        if (gameWindow == IntPtr.Zero || !Native.IsWindow(gameWindow)) return;
        ActivateGame();
        Native.SendKey(key);
    }
}

internal static class RoundedPaint
{
    internal const int Radius = 6;

    internal static GraphicsPath Path(Rectangle bounds)
    {
        var path = new GraphicsPath();
        var x = bounds.Left + .5f;
        var y = bounds.Top + .5f;
        var width = Math.Max(1f, bounds.Width - 1f);
        var height = Math.Max(1f, bounds.Height - 1f);
        var diameter = Math.Min(Radius * 2f, Math.Min(width, height));
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void Fill(Control control, PaintEventArgs e, Color color)
    {
        e.Graphics.Clear(control.Parent?.BackColor ?? Color.FromArgb(30, 30, 30));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var path = Path(control.ClientRectangle);
        using var brush = new SolidBrush(color);
        e.Graphics.FillPath(brush, path);
    }
}

internal sealed class RoundedPanel : Panel
{
    internal RoundedPanel() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    protected override void OnPaintBackground(PaintEventArgs e) => RoundedPaint.Fill(this, e, BackColor);
}

internal sealed class FittingTitleLabel : Label
{
    private Font? fittedFont;

    internal void FitText(Font preferred)
    {
        Font = preferred;
        fittedFont?.Dispose();
        fittedFont = null;
        var width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
        var height = Math.Max(1, ClientSize.Height - Padding.Vertical);
        var words = Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var size = preferred.Size; size >= 7f; size -= .25f)
        {
            var candidate = size == preferred.Size ? preferred : new Font(preferred.FontFamily, size, preferred.Style, preferred.Unit);
            var flags = TextFormatFlags.NoPrefix;
            var longestWord = words.Length == 0 ? 0 : words.Max(word =>
                TextRenderer.MeasureText(word, candidate, Size.Empty, flags | TextFormatFlags.SingleLine).Width);
            var wrapped = TextRenderer.MeasureText(Text, candidate, new Size(width, int.MaxValue), flags | TextFormatFlags.WordBreak);
            var lineHeight = TextRenderer.MeasureText("Ag", candidate, Size.Empty, flags | TextFormatFlags.SingleLine).Height;
            if (longestWord <= width && wrapped.Height <= Math.Min(height, lineHeight * 2))
            {
                if (candidate != preferred) fittedFont = candidate;
                Font = candidate;
                return;
            }
            if (candidate != preferred) candidate.Dispose();
        }
        fittedFont = new Font(preferred.FontFamily, 7f, preferred.Style, preferred.Unit);
        Font = fittedFont;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { fittedFont?.Dispose(); fittedFont = null; }
    }
}

internal sealed class RoundedImageButton : Button
{
    private bool hover;
    private bool pressed;
    internal Color? PaintBackground { get; set; }

    internal RoundedImageButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e)
    {
        var color = PaintBackground is Color paint
            ? pressed ? ControlPaint.Dark(paint) : hover ? ControlPaint.Light(paint) : paint
            : !Enabled ? Color.FromArgb(31, 32, 35) : pressed ? Color.FromArgb(58, 61, 67) :
                hover ? Color.FromArgb(47, 50, 56) : BackColor;
        RoundedPaint.Fill(this, e, color);
        if (Image is not null)
        {
            using var path = RoundedPaint.Path(ClientRectangle);
            var state = e.Graphics.Save();
            e.Graphics.SetClip(path);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var scale = Math.Min((Width - 12f) / Image.Width, (Height - 12f) / Image.Height);
            var size = new SizeF(Image.Width * scale, Image.Height * scale);
            e.Graphics.DrawImage(Image, (Width - size.Width) / 2, (Height - size.Height) / 2, size.Width, size.Height);
            e.Graphics.Restore(state);
        }
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = e.Button == MouseButtons.Left; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

internal sealed class RoundedButton : Button
{
    private bool hover;
    private bool pressed;
    internal bool IsSelectedTab { get; set; }

    internal RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e)
    {
        var color = !Enabled ? Color.FromArgb(38, 38, 38) :
            pressed ? FlatAppearance.MouseDownBackColor :
            hover ? FlatAppearance.MouseOverBackColor : BackColor;
        RoundedPaint.Fill(this, e, color);
        if (IsSelectedTab)
        {
            using var join = new SolidBrush(color);
            e.Graphics.FillRectangle(join, 0, Height - 6, Width, 6);
        }
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        flags |= TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft
            ? TextFormatFlags.Left : TextAlign is ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight
            ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter;
        var textBounds = new Rectangle(Padding.Left, Padding.Top,
            Math.Max(1, Width - Padding.Horizontal), Math.Max(1, Height - Padding.Vertical));
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Enabled ? ForeColor : Color.Gray, flags);
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = e.Button == MouseButtons.Left; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    internal delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData { internal uint VirtualKey; internal uint ScanCode; internal uint Flags; internal uint Time; internal nuint ExtraInfo; }
    internal delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref Point point);

    internal static bool GetGameClientBounds(IntPtr window, out Rect bounds)
    {
        bounds = default;
        if (!GetClientRect(window, out var client)) return false;
        var topLeft = new Point { X = client.Left, Y = client.Top };
        var bottomRight = new Point { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(window, ref topLeft) || !ClientToScreen(window, ref bottomRight)) return false;
        bounds = new Rect { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y };
        return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

    internal static void SendKey(int key)
    {
        var scanCode = (byte)(key == 0x09 ? 0x0F : 0x01);
        keybd_event((byte)key, scanCode, 0, 0);
        keybd_event((byte)key, scanCode, 2, 0);
    }

    internal static IntPtr FindGameWindow()
    {
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (!title.ToString().StartsWith("Anymaker v", StringComparison.OrdinalIgnoreCase)) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
