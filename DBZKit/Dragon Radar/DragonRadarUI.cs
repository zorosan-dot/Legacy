using Bulma;
using DrGero;
using DrGero.IO;
using DrGero.Loader;
using DrGero.Rendering;
using DrGero.Types;
using DrGero.UI;
using System.Drawing.Drawing2D;
using static DrGero.Types.MapEntry;
using FlagUsageScanner_FlagUsage = DrGero.Quests.FlagUsageScanner.FlagUsage;

namespace Dragon_Radar
{
    public partial class DragonRadarUI : Form
    {
        private IGame? _game;
        private ROM? _rom;
        private List<DrGero.Quests.QuestEntry> _quests = new();
        private List<DrGero.Quests.FlagUsageScanner.FlagUsage> _flagUsages = new();

        private int? _selectedTriggerPayloadFileOffset;

        public DragonRadarUI()
        {
            InitializeComponent();

            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1700, 960);
            viewportToolStrip.GripStyle = ToolStripGripStyle.Hidden;

            mapPictureBox.Paint += mapPictureBox_Paint;
            mapPictureBox.MouseDown += mapPictureBox_MouseDown;
            mapPictureBox.MouseMove += mapPictureBox_MouseMove;
            mapPictureBox.MouseUp += mapPictureBox_MouseUp;
            listView1.SelectedIndexChanged += listView1_SelectedIndexChanged;
            KeyPreview = true;
            KeyDown += DragonRadarUI_KeyDown;

            mapTreeView.KeyDown += MapTreeView_KeyDown;
            BuildLevelGatePanel();
            BuildViewportPanel();
            mapPictureBox.Paint += DrawViewportOverlay; 
            BuildEditorTab();
        }

        private CheckBox _paintTilesButton = null!;
        private CheckBox _eraseButton = null!;
        private ComboBox _paintLayerCombo = null!;
        private ComboBox _brushSizeCombo = null!;
        private CheckBox _flipXButton = null!;
        private CheckBox _flipYButton = null!;
        private CheckBox _showGridCheck = null!;
        private CheckBox _snapGridCheck = null!;

        private bool _painting;                 
        private int _brushTileId = -1;          
        private Dictionary<Range, Bitmap> _currentTilesets = new(); 
        private readonly Dictionary<int, TileLayerEditor> _tileEditors = new();
        private int _tileEditorMapOffset = -1;

        private readonly record struct PaintEdit(bool IsCollision, int Layer, int X, int Y, int Old, int New);
        private List<PaintEdit> _currentStroke = new();
        private readonly List<List<PaintEdit>> _undoStack = new();
        private readonly List<List<PaintEdit>> _redoStack = new(); 
        private const int MaxUndoStrokes = 100;
        private Button _undoButton = null!;
        private Button _redoButton = null!;

        private void PushCurrentStroke()
        {
            if (_currentStroke.Count > 0)
            {
                _undoStack.Add(_currentStroke);
                if (_undoStack.Count > MaxUndoStrokes) _undoStack.RemoveAt(0);
                _redoStack.Clear(); 
            }
            _currentStroke = new();
            UpdateUndoButton();
        }

        private void UpdateUndoButton()
        {
            if (_undoButton != null)
            {
                _undoButton.Enabled = _undoStack.Count > 0;
                _undoButton.Text = _undoStack.Count > 0 ? $"Undo ({_undoStack.Count})" : "Undo";
            }
            if (_redoButton != null)
            {
                _redoButton.Enabled = _redoStack.Count > 0;
                _redoButton.Text = _redoStack.Count > 0 ? $"Redo ({_redoStack.Count})" : "Redo";
            }
        }

        private void RedoLastStroke()
        {
            if (_rom == null || _redoStack.Count == 0) return;

            var stroke = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);

            bool tilesTouched = false;
            foreach (var edit in stroke) 
            {
                if (edit.IsCollision)
                    SetCollisionAtWorld(edit.X, edit.Y, edit.New != 0, record: false);
                else if (GetTileEditor(edit.Layer) is { } editor && editor.SetTile(edit.X, edit.Y, (ushort)edit.New))
                    tilesTouched = true;
            }

            _undoStack.Add(stroke); 
            if (tilesTouched) RerenderMapView();
            mapPictureBox.Invalidate();
            UpdateUndoButton();
            statusLabel.Text = $"Redid {stroke.Count} cell change(s).";
        }

        private void UndoLastStroke()
        {
            if (_rom == null || _undoStack.Count == 0) return;

            var stroke = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            _redoStack.Add(stroke); 

            bool tilesTouched = false;
            for (int i = stroke.Count - 1; i >= 0; i--) 
            {
                var edit = stroke[i];
                if (edit.IsCollision)
                    SetCollisionAtWorld(edit.X, edit.Y, edit.Old != 0, record: false);
                else if (GetTileEditor(edit.Layer) is { } editor && editor.SetTile(edit.X, edit.Y, (ushort)edit.Old))
                    tilesTouched = true;
            }

            if (tilesTouched) RerenderMapView(); 
            mapPictureBox.Invalidate();
            UpdateUndoButton();
            statusLabel.Text = $"Undid {stroke.Count} cell change(s).";
        }

        private HashSet<Range> _currentUsedTilesets = new();
        private Point? _lastPaintPoint;         

        private ComboBox _tileSourceCombo = null!; 
        private bool _suppressTileSourceEvents;
        private int _tileSourceMap = -1;                   
        private int[]? _sourceAtlasIndices;                
        private readonly Dictionary<int, int> _atlasToSlot = new(); 

        private CheckBox _paintCollisionButton = null!;
        private ComboBox _collisionModeCombo = null!;
        private CheckBox _tileAlsoCollidesButton = null!;
        private CollisionEditor? _collisionEditor;

        private readonly CheckBox[] _layerItems = new CheckBox[4];
        private CheckBox _isolateLayerButton = null!;
        private bool _suppressLayerEvents;

        private CollisionEditor? GetCollisionEditor()
        {
            if (_rom == null || _currentEntry == null) return null;
            if (_collisionEditor != null && ReferenceEquals(_collisionEditor.Rom, _rom) && _collisionEditor.MapOffset == _currentMapOffset)
                return _collisionEditor;

            _collisionEditor = CollisionEditor.Open(_rom, _currentMapOffset);
            return _collisionEditor;
        }

        private bool SetCollisionAtWorld(int worldX, int worldY, bool allowed, bool record = true)
        {
            if (worldX < 0 || worldY < 0) return false;
            var editor = GetCollisionEditor();
            if (editor == null) return false;

            int tx = worldX / TileSize, ty = worldY / TileSize;
            bool was = editor.IsAllowed(tx, ty);
            if (!editor.SetAllowed(tx, ty, allowed)) return false;
            if (record && was != allowed) _currentStroke.Add(new PaintEdit(true, 0, worldX, worldY, was ? 1 : 0, allowed ? 1 : 0));
            if (_collisionGrid != null && tx < _collisionGrid.GetLength(0) && ty < _collisionGrid.GetLength(1))
                _collisionGrid[tx, ty] = allowed;
            return true;
        }

        private void PaintCollisionAt(Point p)
        {
            if (_rom == null || _currentMapBitmap == null) return;
            if (GetCollisionEditor() == null)
            {
                statusLabel.Text = "This map's collision data couldn't be read, so it can't be edited.";
                return;
            }

            bool block = _collisionModeCombo.SelectedIndex == 0;
            int size = _brushSizeCombo.SelectedIndex + 1;
            var origin = BrushOrigin(p, size, 0, 0); 
            var dirty = Rectangle.Empty;

            for (int dy = 0; dy < size; dy++)
            {
                for (int dx = 0; dx < size; dx++)
                {
                    int wx = origin.X + (dx * TileSize), wy = origin.Y + (dy * TileSize);
                    if (!SetCollisionAtWorld(wx, wy, block)) continue;

                    var cell = new Rectangle((wx / TileSize) * TileSize, (wy / TileSize) * TileSize, TileSize, TileSize);
                    dirty = dirty.IsEmpty ? cell : Rectangle.Union(dirty, cell);
                }
            }

            if (!dirty.IsEmpty)
            {
                _dirty = true;
                mapPictureBox.Invalidate(dirty);
            }
        }

        private void SetCollisionForTileUses(bool block)
        {
            if (_rom == null) return;
            if (_sourceAtlasIndices != null || _brushTileId < 0)
            {
                statusLabel.Text = "Pick a tile from THIS map's own tile list first (set Tiles from map # to this map).";
                return;
            }
            if (!_tileUsageIndex.TryGetValue(_brushTileId, out var uses) || uses.Count == 0)
            {
                statusLabel.Text = $"Tile {_brushTileId} isn't used anywhere on this map.";
                return;
            }

            int changed = 0;
            _currentStroke = new();
            foreach (var pt in uses)
                if (SetCollisionAtWorld(pt.X, pt.Y, block)) changed++;
            PushCurrentStroke(); 

            _dirty = true;
            toolStrip_ShowCollision.Checked = true;
            mapPictureBox.Invalidate();
            statusLabel.Text = block
                ? $"Painted collision on {changed} cell(s) where tile {_brushTileId} is used -- those cells are now ALLOWED space."
                : $"Removed collision from {changed} cell(s) where tile {_brushTileId} is used -- those cells are now barred.";
        }

        private bool _suppressVariantEvents;

        private void SyncVariantBox()
        {
            if (_game == null || _currentEntry == null) return;
            _suppressVariantEvents = true;
            toolStrip_VariantCombo.Items.Clear();
            var siblings = _game.MapEntries
                .Where(e => e.Zone == _currentEntry.Zone && e.Area == _currentEntry.Area)
                .OrderBy(e => e.Variation).ToList();
            foreach (var e in siblings)
                toolStrip_VariantCombo.Items.Add(new VariantItem(e));
            toolStrip_VariantCombo.SelectedIndex = siblings.FindIndex(e => ReferenceEquals(e, _currentEntry));
            toolStrip_VariantCombo.Enabled = siblings.Count > 1;
            _suppressVariantEvents = false;
        }

        private void toolStrip_VariantCombo_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_suppressVariantEvents || toolStrip_VariantCombo.SelectedItem is not VariantItem item) return;
            if (ReferenceEquals(item.Entry, _currentEntry)) return;
            var node = FindMapNode(mapTreeView.Nodes, item.Entry);
            if (node == null) return;
            mapTreeView.SelectedNode = node; 
            node.EnsureVisible();
        }

        private sealed record VariantItem(MapEntry Entry)
        {
            public override string ToString() => $"Variation {Entry.Variation}";
        }

        private int CurrentMapIndex()
        {
            if (_currentEntry == null || _game == null) return -1;
            for (int i = 0; i < _game.MapEntries.Count; i++)
                if (ReferenceEquals(_game.MapEntries[i], _currentEntry)) return i;
            return -1;
        }

        private void SyncTileSourceBox()
        {
            if (_tileSourceCombo == null || _game == null) return;
            _suppressTileSourceEvents = true;

            if (_tileSourceCombo.Items.Count != _game.MapEntries.Count)
            {
                _tileSourceCombo.BeginUpdate();
                _tileSourceCombo.Items.Clear();
                foreach (var entry in _game.MapEntries)
                    _tileSourceCombo.Items.Add(MapLabelFor(entry));
                _tileSourceCombo.EndUpdate();
            }

            int current = CurrentMapIndex();
            _tileSourceCombo.SelectedIndex = current < 0 ? -1 : current;
            _tileSourceMap = -1;
            _suppressTileSourceEvents = false;
        }

        private static string MapLabelFor(MapEntry entry)
        {
            string label = $"Z{entry.Zone} A{entry.Area}";
            if (entry.Variation > 1) label += $" v{entry.Variation}";
            return string.IsNullOrWhiteSpace(entry.Name) ? label : $"{label} - {entry.Name}";
        }

        private void TileSourceCombo_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_suppressTileSourceEvents || _rom == null || _game == null) return;

            int index = _tileSourceCombo.SelectedIndex;
            if (index < 0 || index >= _game.MapEntries.Count) return;
            if (index == CurrentMapIndex())
            {
                _tileSourceMap = -1;
                _sourceAtlasIndices = null;
                PopulateTileList(_currentTilesets, _currentUsedTilesets);
                statusLabel.Text = "Tile list: this map's own tiles.";
                return;
            }

            var entry = _game.MapEntries[index];
            _rom.PushPosition(entry.VariationArray);
            int otherMapOffset = _rom.ReadPointer();
            _rom.PopPosition();

            try
            {
                var all = MapRenderer.DrawTileset(_rom, _game.Config, otherMapOffset);
                var staticOnly = all.Where(kv => kv.Key.Start.Value == 0).ToDictionary(kv => kv.Key, kv => kv.Value);
                _sourceAtlasIndices = TilesetTable.ReadAtlasIndices(_rom, otherMapOffset);
                _tileSourceMap = index;
                PopulateTileList(staticOnly, new HashSet<Range>(staticOnly.Keys));
                statusLabel.Text = $"Tile list: borrowing tiles from {MapLabelFor(entry)}. Pick one and paint -- it's added to this map's tileset as needed.";
            }
            catch (Exception ex)
            {
                _sourceAtlasIndices = null;
                _tileSourceMap = -1;
                statusLabel.Text = $"Couldn't read the tileset for {MapLabelFor(entry)}: {ex.Message}";
            }
        }

        private int ResolveBrushSlot()
        {
            if (_brushTileId < 0 || _rom == null || _game == null) return -1;
            if (_sourceAtlasIndices == null) return _brushTileId;
            if (_brushTileId >= _sourceAtlasIndices.Length) return -1;

            int atlas = _sourceAtlasIndices[_brushTileId];
            if (_atlasToSlot.TryGetValue(atlas, out int known)) return known;

            int slot = TilesetTable.EnsureAtlasTile(_rom, _currentMapOffset, atlas);
            if (slot < 0)
            {
                statusLabel.Text = "This map's tileset can't take another tile (it's full, or the next slot is used by an animated tile).";
                return -1;
            }

            _atlasToSlot[atlas] = slot;
            _currentTilesets = MapRenderer.DrawTileset(_rom, _game.Config, _currentMapOffset); 
            _dirty = true;
            return slot;
        }

        private void PaintStrokeTo(Point p)
        {
            void Paint(Point at)
            {
                if (_paintCollisionButton.Checked) PaintCollisionAt(at);
                else PaintTilesAt(at);
            }

            if (_lastPaintPoint is { } from)
            {
                int dx = p.X - from.X, dy = p.Y - from.Y;
                int steps = Math.Max(Math.Abs(dx), Math.Abs(dy)) / (TileSize / 2);
                for (int i = 1; i < steps; i++)
                    Paint(new Point(from.X + (dx * i / steps), from.Y + (dy * i / steps)));
            }
            Paint(p);
            _lastPaintPoint = p;
        }

        private void BuildEditorTab()
        {
            var tips = new ToolTip { AutoPopDelay = 12000 };

            CheckBox Check(string text, string tip, bool isChecked = false)
            {
                var c = new CheckBox { Text = text, AutoSize = true, Checked = isChecked, Margin = new Padding(3, 2, 3, 2) };
                tips.SetToolTip(c, tip);
                return c;
            }
            ComboBox Combo(int width, string tip, params object[] items)
            {
                var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
                c.Items.AddRange(items);
                c.SelectedIndex = 0;
                tips.SetToolTip(c, tip);
                return c;
            }
            Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
            FlowLayoutPanel Row(params Control[] controls)
            {
                var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
                row.Controls.AddRange(controls);
                return row;
            }
            GroupBox Group(string title, params Control[] rows)
            {
                var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
                flow.Controls.AddRange(rows);
                var box = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 240, Padding = new Padding(6, 4, 6, 6) };
                box.Controls.Add(flow);
                return box;
            }

            _paintTilesButton = Check("Paint tiles", "Click and hold on the map to paint the tile selected in the Tiles tab");
            _paintLayerCombo = Combo(120, "Which background layer to paint into", "BG0", "BG1", "BG2", "BG3");
            _brushSizeCombo = Combo(60, "Brush size in tiles", "1x1", "2x2", "3x3", "4x4");
            _flipXButton = Check("Flip X", "Paint the tile mirrored left-right");
            _flipYButton = Check("Flip Y", "Paint the tile mirrored top-bottom");
            _eraseButton = Check("Erase", "Paint blank (tile 0) instead of the selected tile");
            _tileSourceCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 215, DropDownWidth = 320, MaxDropDownItems = 20 };
            _tileSourceCombo.SelectedIndexChanged += TileSourceCombo_SelectedIndexChanged;
            tips.SetToolTip(_tileSourceCombo, "Pick any map to browse its tileset in the Tiles tab and paint with its tiles. Pick this map again to go back to its own tiles.");

            _paintCollisionButton = Check("Paint collision", "Click and hold on the map to paint collision. Red cells are where you ARE allowed to walk; anything not red is barred.");
            _collisionModeCombo = Combo(70, "Block = paint collision (red = ALLOWED space you can walk on). Clear = remove it (that cell becomes barred).", "Block", "Clear");
            _tileAlsoCollidesButton = Check("Tiles also block", "Painting a tile also paints collision under it (red = ALLOWED space); erasing removes it, making the cell barred");
            
            var blockUses = new Button { Text = "Paint collision where used", AutoSize = true };
            var clearUses = new Button { Text = "Clear collision where used", AutoSize = true };
            tips.SetToolTip(blockUses, "Paint collision on every cell of this map that uses the selected tile (red = ALLOWED space you can walk on)");
            tips.SetToolTip(clearUses, "Remove collision from every cell of this map that uses the selected tile (those cells become barred)");
            _selectedTileInfo = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 34, Text = "No tile selected.", Padding = new Padding(2, 4, 2, 0) };
            blockUses.Click += (_, _) => SetCollisionForTileUses(block: true);
            clearUses.Click += (_, _) => SetCollisionForTileUses(block: false);

            var layerBoxes = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Width = 220, Margin = new Padding(0) };
            for (int i = 0; i < 4; i++)
            {
                var box = Check($"BG{i}", $"Show or hide background layer {i}", isChecked: true);
                box.CheckedChanged += (_, _) => { if (!_suppressLayerEvents) RerenderMapView(); };
                _layerItems[i] = box;
                layerBoxes.Controls.Add(box);
            }
            _isolateLayerButton = Check("Only edited layer", "While painting tiles, show only the layer being painted", isChecked: true);
            _showGridCheck = Check("Show grid", "Draw the 8x8 tile grid over the map (brighter lines mark 256-pixel chunk edges)");
            _snapGridCheck = Check("Snap to grid", "Tile brush: snap to a grid of brush-sized blocks so strokes line up. Dragging things on the map snaps to 8-pixel tiles.");
            _showGridCheck.CheckedChanged += (_, _) => mapPictureBox.Invalidate();

            _undoButton = new Button { Text = "Undo", AutoSize = true, Enabled = false };
            tips.SetToolTip(_undoButton, "Undo the last tile/collision stroke (Ctrl+Z)");
            _undoButton.Click += (_, _) => UndoLastStroke();
            _redoButton = new Button { Text = "Redo", AutoSize = true, Enabled = false };
            tips.SetToolTip(_redoButton, "Redo the stroke you just undid (Ctrl+Y or Ctrl+Shift+Z)");
            _redoButton.Click += (_, _) => RedoLastStroke();

            _paintCollisionButton.CheckedChanged += (_, _) =>
            {
                if (_paintCollisionButton.Checked)
                {
                    _paintTilesButton.Checked = false;
                    toolStrip_ShowCollision.Checked = true;
                }
                UpdateStatusLabel();
            };
            _paintTilesButton.CheckedChanged += (_, _) =>
            {
                if (_paintTilesButton.Checked) _paintCollisionButton.Checked = false;
                if (_paintTilesButton.Checked) _highlightedTileId = null;
                else RefreshTileHighlightFromList();
                if (_isolateLayerButton.Checked) RerenderMapView(); 
                UpdateStatusLabel();
                mapPictureBox.Invalidate();
            };
            _paintLayerCombo.SelectedIndexChanged += (_, _) =>
            {
                if (!_suppressLayerEvents && _isolateLayerButton.Checked && _paintTilesButton.Checked) RerenderMapView();
            };
            _isolateLayerButton.CheckedChanged += (_, _) => { if (_paintTilesButton.Checked) RerenderMapView(); };

            var mapProps = new Button { Text = "Map properties...", AutoSize = true };
            tips.SetToolTip(mapProps, "Change this map's width and height");
            mapProps.Click += (_, _) => ShowMapProperties();

            var tilesGroup = Group("Tiles",
                _paintTilesButton,
                Row(Caption("Layer"), _paintLayerCombo, Caption("Brush"), _brushSizeCombo),
                Row(_flipXButton, _flipYButton, _eraseButton),
                Caption("Tiles from map (Zone / Area)"),
                _tileSourceCombo);

            tilesTabPage.Controls.Remove(listView1);
            listView1.Dock = DockStyle.Fill;
            var uses = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            uses.Controls.AddRange(new Control[] { blockUses, clearUses });
            var palette = new GroupBox { Text = "Tile palette -- click a tile to use it as the brush", Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 6), MinimumSize = new Size(0, 140) };
            palette.Controls.Add(listView1);      
            palette.Controls.Add(uses);
            palette.Controls.Add(_selectedTileInfo);

            var lower = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            lower.Controls.Add(Group("Collision (red = allowed to walk)",
                _paintCollisionButton,
                Row(Caption("Mode"), _collisionModeCombo),
                _tileAlsoCollidesButton));
            lower.Controls.Add(Group("View",
                layerBoxes,
                Row(_isolateLayerButton),
                Row(_showGridCheck, _snapGridCheck)));
            lower.Controls.Add(Group("History / Map", Row(_undoButton, _redoButton, mapProps)));

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(4) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(tilesGroup, 0, 0);
            layout.Controls.Add(palette, 0, 1);
            layout.Controls.Add(lower, 0, 2);

            var tab = new TabPage("Editor") { Padding = new Padding(0) };
            tab.Controls.Add(layout);
            sidebarTabControl.TabPages.Add(tab);
            sidebarTabControl.TabPages.Remove(tilesTabPage); 
            sidebarTabControl.SelectedTab = tab;
            sidebarTabControl.Width = 350;                    
        }

        private Label _selectedTileInfo = null!;

        private void UpdateSelectedTileInfo()
        {
            if (_selectedTileInfo == null) return;
            if (_brushTileId < 0) { _selectedTileInfo.Text = "No tile selected. Click one below."; return; }
            if (_sourceAtlasIndices != null)
            {
                _selectedTileInfo.Text = $"Tile {_brushTileId} from another map. The collision buttons work on this map's own tiles.";
                return;
            }
            int uses = _tileUsageIndex.TryGetValue(_brushTileId, out var list) ? list.Count : 0;
            _selectedTileInfo.Text = $"Selected tile {_brushTileId}: used {uses} time(s) on this map. The buttons below apply to every one of them.";
        }

        private void ShowMapProperties()
        {
            if (_rom == null || _currentEntry == null || _currentMapBitmap == null)
            {
                MessageBox.Show("Open a map first.", "Map properties", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var (width, height) = MapResizer.ReadSize(_rom, _currentMapOffset);
            var (layerW, layerH) = MapResizer.LayerCoverage(_rom, _currentMapOffset);

            using var form = new Form
            {
                Text = $"Map properties -- Zone {_currentEntry.Zone} Area {_currentEntry.Area}",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ClientSize = new Size(400, 230),
                ShowIcon = false
            };

            var widthBox = new NumericUpDown { Minimum = MapResizer.ScreenWidth, Maximum = MapResizer.MaxSize, Value = Math.Clamp(width, MapResizer.ScreenWidth, MapResizer.MaxSize), Width = 90, Location = new Point(110, 18) };
            var heightBox = new NumericUpDown { Minimum = MapResizer.ScreenHeight, Maximum = MapResizer.MaxSize, Value = Math.Clamp(height, MapResizer.ScreenHeight, MapResizer.MaxSize), Width = 90, Location = new Point(110, 50) };
            var grow = new CheckBox { Text = "Also add empty layer chunks so the new area can be painted", Checked = true, AutoSize = false, Size = new Size(370, 36), Location = new Point(16, 120) };
            var info = new Label
            {
                AutoSize = false,
                Size = new Size(370, 40),
                Location = new Point(16, 78),
                Text = $"Currently {width} x {height} pixels. The background layers cover {layerW} x {layerH}. Anything past the layers is empty until you grow them and paint it."
            };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(220, 185), Width = 80 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(310, 185), Width = 80 };
            form.Controls.AddRange(new Control[]
            {
                new Label { Text = "Width (pixels)", AutoSize = true, Location = new Point(16, 21) }, widthBox,
                new Label { Text = "Height (pixels)", AutoSize = true, Location = new Point(16, 53) }, heightBox,
                info, grow, ok, cancel
            });
            form.AcceptButton = ok;
            form.CancelButton = cancel;

            if (form.ShowDialog(this) != DialogResult.OK) return;

            int newWidth = (int)widthBox.Value, newHeight = (int)heightBox.Value;
            if (newWidth == width && newHeight == height && !grow.Checked) return;

            string? error = MapResizer.Resize(_rom, _currentMapOffset, newWidth, newHeight, grow.Checked, out int layersGrown);
            if (error != null)
            {
                MessageBox.Show(error, "Map properties", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _tileEditors.Clear(); 
            _dirty = true;
            RerenderMapView();
            RefreshPaintLayerChoices();
            statusLabel.Text = $"Map is now {newWidth} x {newHeight}" + (layersGrown > 0 ? $"; {layersGrown} layer(s) extended." : ".");
        }

        private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

        private (int X, int Y) GridOffsets()
        {
            if (_paintTilesButton.Checked && GetTileEditor() is { } editor) return (editor.OffX, editor.OffY);
            return (0, 0);
        }

        private Point BrushOrigin(Point p, int size, int offX, int offY)
        {
            int cx = FloorDiv(p.X + offX, TileSize), cy = FloorDiv(p.Y + offY, TileSize);
            if (_snapGridCheck.Checked)
            {
                cx = FloorDiv(cx, size) * size;
                cy = FloorDiv(cy, size) * size;
            }
            else
            {
                int first = -((size - 1) / 2); 
                cx += first;
                cy += first;
            }
            return new Point((cx * TileSize) - offX, (cy * TileSize) - offY);
        }

        private void DrawGrid(Graphics g, Rectangle clip)
        {
            if (_currentMapBitmap == null) return;
            clip = Rectangle.Intersect(clip, new Rectangle(0, 0, _currentMapBitmap.Width, _currentMapBitmap.Height));
            if (clip.Width <= 0 || clip.Height <= 0) return;

            var (offX, offY) = GridOffsets();
            const int chunk = 256;
            using var thin = new Pen(Color.FromArgb(70, 255, 255, 255));
            using var thick = new Pen(Color.FromArgb(170, 255, 220, 0));

            int startX = clip.Left - ((((clip.Left + offX) % TileSize) + TileSize) % TileSize);
            for (int x = startX; x <= clip.Right; x += TileSize)
                g.DrawLine(((x + offX) % chunk) == 0 ? thick : thin, x, clip.Top, x, clip.Bottom);

            int startY = clip.Top - ((((clip.Top + offY) % TileSize) + TileSize) % TileSize);
            for (int y = startY; y <= clip.Bottom; y += TileSize)
                g.DrawLine(((y + offY) % chunk) == 0 ? thick : thin, clip.Left, y, clip.Right, y);
        }

        private void RefreshTileHighlightFromList()
        {
            _highlightedTileId = listView1.SelectedItems.Count > 0 && listView1.SelectedItems[0].Tag is int tileId ? tileId : null;
        }

        private TileLayerEditor? GetTileEditor(int? layerOverride = null)
        {
            if (_rom == null || _currentEntry == null) return null;
            int layer = layerOverride ?? _paintLayerCombo.SelectedIndex;
            if (layer < 0) return null;

            if (_tileEditorMapOffset != _currentMapOffset) { _tileEditors.Clear(); _tileEditorMapOffset = _currentMapOffset; }
            if (_tileEditors.TryGetValue(layer, out var existing))
            {
                if (ReferenceEquals(existing.Rom, _rom)) return existing;
                _tileEditors.Clear(); 
            }

            _rom.PushPosition(_currentMapOffset + 0x14 + (layer * 4));
            int layerAddress = _rom.ReadPointer();
            _rom.PopPosition();

            var opened = TileLayerEditor.Open(_rom, layerAddress);
            if (opened != null) _tileEditors[layer] = opened;
            return opened;
        }

        private void PaintTilesAt(Point p)
        {
            if (_rom == null || _currentMapBitmap == null) return;

            var editor = GetTileEditor();
            if (editor == null)
            {
                statusLabel.Text = $"{_paintLayerCombo.SelectedItem} isn't an editable layer on this map (empty, or a format that isn't supported yet).";
                return;
            }

            bool erase = _eraseButton.Checked;
            int tileId = erase ? 0 : ResolveBrushSlot();
            if (tileId < 0)
            {
                if (!erase && _brushTileId < 0) statusLabel.Text = "Pick a tile in the Tiles tab first -- it becomes the brush.";
                return;
            }

            bool flipX = !erase && _flipXButton.Checked, flipY = !erase && _flipYButton.Checked;
            ushort entry = (ushort)((tileId & 0x3FF) | (flipX ? 1 << 10 : 0) | (flipY ? 1 << 11 : 0));

            int size = _brushSizeCombo.SelectedIndex + 1;
            var origin = BrushOrigin(p, size, editor.OffX, editor.OffY);

            using var g = Graphics.FromImage(_currentMapBitmap);
            var dirty = Rectangle.Empty;

            for (int dy = 0; dy < size; dy++)
            {
                for (int dx = 0; dx < size; dx++)
                {
                    int wx = origin.X + (dx * TileSize), wy = origin.Y + (dy * TileSize);
                    ushort? before = editor.GetTile(wx, wy);
                    if (!editor.SetTile(wx, wy, entry)) continue;
                    if (before.HasValue && before.Value != entry)
                        _currentStroke.Add(new PaintEdit(false, _paintLayerCombo.SelectedIndex, wx, wy, before.Value, entry));

                    var cell = editor.CellOrigin(wx, wy);
                    if (_tileAlsoCollidesButton.Checked) SetCollisionAtWorld(cell.X, cell.Y, allowed: !erase);
                    if (!erase)
                    {
                        MapRenderer.DrawTileInto(g, _currentTilesets, tileId, flipX, flipY, cell.X, cell.Y);
                    }
                    dirty = dirty.IsEmpty ? new Rectangle(cell, new Size(TileSize, TileSize)) : Rectangle.Union(dirty, new Rectangle(cell, new Size(TileSize, TileSize)));
                }
            }

            if (!dirty.IsEmpty)
            {
                _dirty = true;
                mapPictureBox.Invalidate(dirty);
            }
        }

        private void FinishTileStroke()
        {
            _painting = false;
            PushCurrentStroke(); 
            if (_rom == null || _game == null || _currentEntry == null) return;

            if (_paintCollisionButton.Checked)
            {
                mapPictureBox.Invalidate();
                UpdateStatusLabel();
                return;
            }

            RerenderMapView();
            UpdateStatusLabel();
        }

        private MapRenderOptions BuildRenderOptions()
        {
            bool isolate = _isolateLayerButton is { Checked: true } && _paintTilesButton is { Checked: true };
            int edited = _paintLayerCombo?.SelectedIndex ?? -1;
            bool Show(int i) => isolate ? i == edited : (_layerItems[i]?.Checked ?? true);
            return new MapRenderOptions { ShowBG0 = Show(0), ShowBG1 = Show(1), ShowBG2 = Show(2), ShowBG3 = Show(3) };
        }

        private void RerenderMapView()
        {
            if (_rom == null || _game == null || _currentEntry == null || _currentMapBitmap == null) return;

            MapRenderer.ResetCache(_game.Config);
            var (bitmap, tilesets, _, hasUnsupportedLayer, collisionGrid) =
                MapRenderer.RenderMap(_rom, _game.Config, _currentMapOffset, BuildRenderOptions());

            _currentMapBitmap?.Dispose();
            _currentMapBitmap = bitmap;
            mapPictureBox.Size = bitmap.Size; 
            _currentTilesets = tilesets;
            _hasUnsupportedLayer = hasUnsupportedLayer;
            _collisionGrid = collisionGrid;
            _tileUsageIndex = MapRenderer.BuildTileUsageIndex(_rom, _currentMapOffset);
            mapPictureBox.Invalidate();
            UpdateSelectedTileInfo(); 
        }

        private void RefreshPaintLayerChoices()
        {
            if (_paintLayerCombo == null || _rom == null || _currentEntry == null) return;

            var labels = new string[4];
            int firstEditable = -1;
            var editable = new bool[4];
            for (int i = 0; i < 4; i++)
            {
                _rom.PushPosition(_currentMapOffset + 0x14 + (i * 4));
                int address = _rom.ReadPointer();
                _rom.PopPosition();

                editable[i] = TileLayerEditor.Open(_rom, address) != null;
                labels[i] = editable[i] ? $"BG{i}" : (address == 0 ? $"BG{i} (empty)" : $"BG{i} (unsupported)");
                if (editable[i] && firstEditable < 0) firstEditable = i;
            }

            int keep = _paintLayerCombo.SelectedIndex;
            _suppressLayerEvents = true;
            _paintLayerCombo.Items.Clear();
            _paintLayerCombo.Items.AddRange(labels);
            _paintLayerCombo.SelectedIndex = keep >= 0 && editable[keep] ? keep : Math.Max(0, firstEditable);
            _suppressLayerEvents = false;
        }

        private void MapTreeView_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) return;

            var maps = new List<TreeNode>();
            CollectMapNodes(mapTreeView.Nodes, maps);
            if (maps.Count == 0) return;

            int current = mapTreeView.SelectedNode == null ? -1 : maps.IndexOf(mapTreeView.SelectedNode);
            int next = current < 0 ? 0 : Math.Clamp(current + (e.KeyCode == Keys.Down ? 1 : -1), 0, maps.Count - 1);

            mapTreeView.SelectedNode = maps[next];
            maps[next].EnsureVisible(); 
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private static void CollectMapNodes(TreeNodeCollection nodes, List<TreeNode> into)
        {
            foreach (TreeNode node in nodes)
            {
                if (node.Tag is MapEntry) into.Add(node);
                CollectMapNodes(node.Nodes, into);
            }
        }

        private Panel _levelGatePanel = null!;
        private NumericUpDown _levelGateNud = null!;
        private bool _suppressLevelGateEvents;

        private void BuildLevelGatePanel()
        {
            _levelGateNud = new NumericUpDown { Minimum = 0, Maximum = 255, Width = 70, Location = new Point(110, 8) };
            var label = new Label { Text = "Required level:", AutoSize = true, Location = new Point(8, 11) };
            _levelGatePanel = new Panel { Dock = DockStyle.Top, Height = 40, Visible = false };
            _levelGatePanel.Controls.Add(label);
            _levelGatePanel.Controls.Add(_levelGateNud);
            propertiesTabPage.Controls.Add(_levelGatePanel);
            _levelGateNud.ValueChanged += LevelGateNud_ValueChanged;
        }

        private void LevelGateNud_ValueChanged(object? sender, EventArgs e)
        {
            if (_suppressLevelGateEvents || _rom == null) return;
            if (_selectedIndex < 0 || _selectedIndex >= _currentEntities.Count) return;

            var entity = _currentEntities[_selectedIndex];
            if (entity.Kind != EntityKind.LevelGate || entity.SourceAddress == 0) return;

            byte level = (byte)_levelGateNud.Value;
            _rom.PatchByte(entity.SourceAddress + 6, level); 
            _currentEntities[_selectedIndex] = entity with { TypeId = level };
            _dirty = true;

            UpdatePropertiesPanel();
            mapPictureBox.Invalidate();
        }

        private const int GbaViewWidth = 240, GbaViewHeight = 160, ViewportGrabMargin = 6;
        private CheckBox _showViewportCheck = null!;
        private Panel _viewportPanel = null!;
        private Label _viewportTopLeftLabel = null!, _viewportCentreLabel = null!, _viewportCallLabel = null!;
        private Point _viewportPos;
        private bool _draggingViewport;
        private Point _viewportGrabOffset;

        private void BuildViewportPanel()
        {
            _showViewportCheck = new CheckBox { Text = "Show GBA viewport", AutoSize = true, Location = new Point(8, 8) };
            _viewportTopLeftLabel = new Label { AutoSize = true, Location = new Point(8, 34), Enabled = false };
            _viewportCentreLabel = new Label { AutoSize = true, Location = new Point(8, 54), Enabled = false };
            _viewportCallLabel = new Label { AutoSize = false, Size = new Size(190, 34), Location = new Point(8, 74), Enabled = false };
            _viewportPanel = new Panel { Dock = DockStyle.Bottom, Height = 116 };
            _viewportPanel.Controls.AddRange(new Control[] { _showViewportCheck, _viewportTopLeftLabel, _viewportCentreLabel, _viewportCallLabel });
            propertiesTabPage.Controls.Add(_viewportPanel);
            new ToolTip().SetToolTip(_showViewportCheck, "Draws the 240x160 GBA screen on the map. Drag its outline to move it; the labels give the top-left (x, y) that PanCameraToPosition wants.");
            _showViewportCheck.CheckedChanged += (_, _) =>
            {
                _draggingViewport = false;
                UpdateViewportLabels();
                mapPictureBox.Invalidate();
            };
            UpdateViewportLabels();
        }

        private void UpdateViewportLabels()
        {
            bool on = _showViewportCheck.Checked;
            _viewportTopLeftLabel.Enabled = _viewportCentreLabel.Enabled = _viewportCallLabel.Enabled = on;
            _viewportTopLeftLabel.Text = $"Top-left: ({_viewportPos.X}, {_viewportPos.Y})";
            _viewportCentreLabel.Text = $"Centre: ({_viewportPos.X + GbaViewWidth / 2}, {_viewportPos.Y + GbaViewHeight / 2})";
            _viewportCallLabel.Text = $"PanCameraToPosition({_viewportPos.X}, {_viewportPos.Y}, speed)";
        }

        private Rectangle ViewportRect => new(_viewportPos.X, _viewportPos.Y, GbaViewWidth, GbaViewHeight);

        private bool IsOnViewportEdge(Point p)
        {
            if (!_showViewportCheck.Checked) return false;
            var outer = Rectangle.Inflate(ViewportRect, ViewportGrabMargin, ViewportGrabMargin);
            var inner = Rectangle.Inflate(ViewportRect, -ViewportGrabMargin, -ViewportGrabMargin);
            return outer.Contains(p) && !inner.Contains(p);
        }

        private void MoveViewportTo(Point topLeft)
        {
            int maxX = Math.Max(0, (_currentMapBitmap?.Width ?? GbaViewWidth) - GbaViewWidth);
            int maxY = Math.Max(0, (_currentMapBitmap?.Height ?? GbaViewHeight) - GbaViewHeight);
            _viewportPos = new Point(Math.Clamp(topLeft.X, 0, maxX), Math.Clamp(topLeft.Y, 0, maxY));
            UpdateViewportLabels();
            mapPictureBox.Invalidate();
        }

        private void DrawViewportOverlay(object? sender, PaintEventArgs e)
        {
            if (!_showViewportCheck.Checked || _currentMapBitmap == null) return;
            var rect = ViewportRect;
            using var fill = new SolidBrush(Color.FromArgb(28, 0, 200, 255));
            using var pen = new Pen(Color.FromArgb(230, 0, 200, 255), 2);
            e.Graphics.FillRectangle(fill, rect);
            e.Graphics.DrawRectangle(pen, rect);
            using var thin = new Pen(Color.FromArgb(160, 0, 200, 255)) { DashStyle = DashStyle.Dot };
            int cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
            e.Graphics.DrawLine(thin, cx - 6, cy, cx + 6, cy);
            e.Graphics.DrawLine(thin, cx, cy - 6, cx, cy + 6);
        }

        private void DragonRadarUI_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Control && ActiveControl is not (TextBoxBase or NumericUpDown))
            {
                if (e.KeyCode == Keys.Z && !e.Shift) { UndoLastStroke(); e.Handled = e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Y || (e.KeyCode == Keys.Z && e.Shift)) { RedoLastStroke(); e.Handled = e.SuppressKeyPress = true; return; }
            }

            if (e.KeyCode == Keys.Delete)
            {
                bool typing = ActiveControl is TextBoxBase or NumericUpDown or ComboBox;
                if (!typing && mainViewTabControl.SelectedTab == mapViewerTabPage
                    && _selectedIndex >= 0 && _selectedIndex < _currentEntities.Count)
                {
                    DeleteEntityAt(_selectedIndex);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                return;
            }

            if (e.KeyCode != Keys.Escape) return;

            if (_placingItemId.HasValue)
            {
                _placingItemId = null;
                _placingCustomPickup = false;
                UpdateStatusLabel();
            }

            if (_placingCharacterSpriteId.HasValue)
            {
                _placingCharacterSpriteId = null;
                UpdateStatusLabel();
            }

            if (_placingEnemy.HasValue)
            {
                _placingEnemy = null;
                UpdateStatusLabel();
            }
        }

        private void PopulateMapTree()
        {
            if (_game == null) return;

            mapTreeView.BeginUpdate();
            mapTreeView.Nodes.Clear();

            foreach (var zoneNode in MapTreeBuilder.Build(_game.MapEntries))
            {
                mapTreeView.Nodes.Add(BuildTreeNode(zoneNode));
            }

            mapTreeView.EndUpdate();
        }

        private static TreeNode BuildTreeNode(MapTreeNode node)
        {
            var tvNode = new TreeNode(node.Text) { Tag = node.Entry };
            foreach (var child in node.Children)
                tvNode.Nodes.Add(BuildTreeNode(child));
            return tvNode;
        }

        private const int TileSize = 8;
        private const int DotRadius = 3;
        private const int HitTestPadding = 4; 

        private Bitmap? _currentMapBitmap;
        private readonly ImageList _tileThumbnails = new ImageList
        {
            ImageSize = new Size(32, 32),
            ColorDepth = ColorDepth.Depth32Bit
        };
        private readonly ImageList _itemThumbnails = new ImageList
        {
            ImageSize = new Size(32, 32),
            ColorDepth = ColorDepth.Depth32Bit
        };
        private readonly ImageList _npcThumbnails = new ImageList
        {
            ImageSize = new Size(32, 32),
            ColorDepth = ColorDepth.Depth32Bit
        };

        private int? _placingItemId;
        private (int OnPickup, int CollectionMsg) _placingTemplate;

        private int? _placingCharacterSpriteId;

        private (int StatIndex, int SpriteId)? _placingEnemy;
        private int _placingNpcVariant; 

        private static bool IsTalkableNpc(Entity entity) =>
            entity.Kind == EntityKind.Character &&
            (entity.Handler == EntityReader.SpriteSpawnHandlerOffset || (entity.SourceAddress == 0 && entity.Variant != 2));
        private List<EnemyRecords.Template>? _enemyTemplates;

        private void PopulateItemList()
        {
            itemsListView.Items.Clear();
            _itemThumbnails.Images.Clear();
            itemsListView.LargeImageList = _itemThumbnails;
            itemsListView.View = View.LargeIcon;

            if (_rom == null || _game == null) return;

            var images = new List<Image>();
            var items = new List<ListViewItem>();

            foreach (int itemId in ItemIconReader.EnumerateValidItemIds(_rom))
            {
                var icon = ItemIconReader.GetIcon(_rom, _game.Config, itemId) ?? new Bitmap(_itemThumbnails.ImageSize.Width, _itemThumbnails.ImageSize.Height);
                images.Add(icon);
                items.Add(new ListViewItem($"Item {itemId}", images.Count - 1) { Tag = itemId });
            }

            _itemThumbnails.Images.AddRange(images.ToArray());

            itemsListView.BeginUpdate();
            itemsListView.Items.AddRange(items.ToArray());
            itemsListView.EndUpdate();
        }

        private void itemsListView_MouseDown(object? sender, MouseEventArgs e)
        {
            var item = itemsListView.GetItemAt(e.X, e.Y);
            if (item?.Tag is not int itemId) return;

            ArmPlacementWithDisambiguation(itemId, itemsListView, e.Location);
        }

        private void ArmPlacementWithDisambiguation(int itemId, Control menuOwner, Point menuAnchor)
        {
            if (_rom == null || _game == null)
            {
                ArmPlacement(itemId, 0, 0);
                return;
            }

            var templates = EntityReader.FindPickupTemplates(_rom, _game.MapEntries, itemId);

            var menu = new ContextMenuStrip();
            if (templates.Count == 0)
                menu.Items.Add("Default (no pickup script)", null, (_, _) => ArmPlacement(itemId, 0, 0));

            foreach (var t in templates)
            {
                string label = string.IsNullOrEmpty(t.MapName) ? $"onPickup=0x{t.OnPickup:X} msg=0x{t.CollectionMsg:X}" : $"{t.MapName} (onPickup=0x{t.OnPickup:X} msg=0x{t.CollectionMsg:X})";
                var onPickup = t.OnPickup;
                var collectionMsg = t.CollectionMsg;
                menu.Items.Add(label, null, (_, _) => ArmPlacement(itemId, onPickup, collectionMsg));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Custom...", null, (_, _) =>
            {
                ArmPlacement(itemId, GenericPickupHandler, 0);
                _placingCustomPickup = true;
                statusLabel.Text = $"Placing item {itemId} with a custom pickup script — click the map to place it (Legacy opens right after), Esc to cancel";
            });
            menu.Show(menuOwner, menuAnchor);
        }

        private bool _placingCustomPickup;

        private const int GenericPickupHandler = 0x0800FFE7;

        private bool MaterializeSelected()
        {
            if (_rom == null || _game == null || _currentEntry == null) return false;
            if (_selectedIndex < 0 || _selectedIndex >= _currentEntities.Count) return false;

            var want = _currentEntities[_selectedIndex];
            if (want.SourceAddress != 0) return true;

            int? mapEntryAddress = CurrentMapEntryAddress();
            if (mapEntryAddress == null) return false;

            try
            {
                foreach (var e2 in _currentEntities)
                    WriteEntityPositionLive(e2);

                EntityWriter.PersistNewObjects(_rom, _currentEntry, mapEntryAddress.Value, _currentEntities);
                EntityWriter.PersistNewCharacters(_rom, _currentEntry, mapEntryAddress.Value, _currentEntities);
                EnemyRecords.PersistNewEnemies(_rom, _game.MapEntries, _currentEntry, mapEntryAddress.Value, _currentEntities);
                _currentEntry.RefreshFrom(_rom, mapEntryAddress.Value);
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(ex.Message, "Couldn't write it into the ROM yet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            RescanQuestsAndFlags();
            if (mapTreeView.SelectedNode != null)
                mapTreeView_AfterSelect(this, new TreeViewEventArgs(mapTreeView.SelectedNode));
            _dirty = true;

            int idx = _currentEntities.FindLastIndex(e => e.Kind == want.Kind && e.SourceAddress != 0 && e.X == want.X && e.Y == want.Y
                && e.TypeId == want.TypeId && (want.Kind != EntityKind.Enemy || e.StatIndex == want.StatIndex));
            if (idx < 0)
            {
                MessageBox.Show("It was written to the ROM but couldn't be found on the reloaded map.", "Edit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            _selectedIndex = idx;
            UpdateStatusLabel();
            UpdatePropertiesPanel();
            mapPictureBox.Invalidate();
            return true;
        }

        private void BeginCustomPickupEditing(int entityIndex)
        {
            if (_rom == null || _game == null || _currentEntry == null) return;
            if (entityIndex < 0 || entityIndex >= _currentEntities.Count) return;

            int entryIndex = -1;
            for (int i = 0; i < _game.MapEntries.Count; i++)
            {
                if (ReferenceEquals(_game.MapEntries[i], _currentEntry)) { entryIndex = i; break; }
            }
            if (entryIndex < 0) return;
            int mapEntryAddress = _game.Config.MapEntriesOffset + (entryIndex * MapEntry.RecordSize);

            var placed = _currentEntities[entityIndex];
            int itemId = placed.TypeId;

            int sequenceAddress = DrGero.Quests.DialogWriter.WriteSequence(_rom, new[] { DrGero.Quests.DialogLine.Script(new byte[] { 0x11 }) });
            _currentEntities[entityIndex] = placed with { OnPickup = GenericPickupHandler, CollectionMsg = sequenceAddress };

            try
            {
                foreach (var e2 in _currentEntities)
                    WriteEntityPositionLive(e2);

                EntityWriter.PersistNewObjects(_rom, _currentEntry, mapEntryAddress, _currentEntities);
                EntityWriter.PersistNewCharacters(_rom, _currentEntry, mapEntryAddress, _currentEntities);
                EnemyRecords.PersistNewEnemies(_rom, _game!.MapEntries, _currentEntry, mapEntryAddress, _currentEntities);
                _currentEntry.RefreshFrom(_rom, mapEntryAddress);
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(ex.Message, "Couldn't place the item", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _dirty = true;
            RescanQuestsAndFlags();
            if (mapTreeView.SelectedNode != null)
                mapTreeView_AfterSelect(this, new TreeViewEventArgs(mapTreeView.SelectedNode));

            int idx = _currentEntities.FindIndex(e => e.Kind == EntityKind.Object && e.TypeId == itemId && e.CollectionMsg == sequenceAddress);
            if (idx < 0)
            {
                MessageBox.Show("The item was written to the ROM but couldn't be found on the reloaded map, so Legacy wasn't opened.",
                    "Custom pickup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _selectedIndex = idx;
            UpdateStatusLabel();
            UpdatePropertiesPanel(); 
            mapPictureBox.Invalidate();

            EditSelectedPayload();
        }

        private void ArmPlacement(int itemId, int onPickup, int collectionMsg)
        {
            _placingItemId = itemId;
            _placingCustomPickup = false; 
            _placingTemplate = (onPickup, collectionMsg);
            statusLabel.Text = $"Placing item {itemId} — click the map to place it, Esc to cancel";
        }

        private void mapTreeView_BeforeSelect(object? sender, TreeViewCancelEventArgs e)
        {
            if (e.Node?.Tag is not MapEntry)
            {
                e.Cancel = true;
                e.Node?.Toggle();
            }
        }

        private void toolStrip_RefreshMap_Click(object? sender, EventArgs e)
        {
            if (mapTreeView.SelectedNode != null)
                mapTreeView_AfterSelect(this, new TreeViewEventArgs(mapTreeView.SelectedNode));
        }

        private void mapTreeView_AfterSelect(object? sender, TreeViewEventArgs e)
        {
            _currentMapBitmap?.Dispose();
            _currentMapBitmap = null;
            _selectedIndex = -1;
            _dragging = false;
            _dirty = false;

            _highlightedTileId = null;

            if (_rom == null || _game == null || e.Node?.Tag is not MapEntry entry)
            {
                listView1.Items.Clear();
                _tileThumbnails.Images.Clear();
                _currentEntities.Clear();
                _hasUnsupportedLayer = false;
                _collisionGrid = null;
                _tileUsageIndex = new();
                DisposeDecorationSprites();
                mapPictureBox.Invalidate();
                UpdateStatusLabel();
                UpdatePropertiesPanel();
                return;
            }

            _currentEntry = entry;
            SyncVariantBox();

            _rom.PushPosition(entry.VariationArray);
            int mapOffset = _rom.ReadPointer();
            _rom.PopPosition();
            _currentMapOffset = mapOffset;

            RefreshPaintLayerChoices(); 
            var (bitmap, tilesets, usedTilesets, hasUnsupportedLayer, collisionGrid) = MapRenderer.RenderMap(_rom, _game.Config, mapOffset, BuildRenderOptions());
            _currentMapBitmap = bitmap;
            _currentTilesets = tilesets;           
            _currentUsedTilesets = usedTilesets;   
            _tileEditors.Clear();                  
            _undoStack.Clear();                    
            _redoStack.Clear();
            _currentStroke = new();
            UpdateUndoButton();
            _collisionEditor = null;
            _atlasToSlot.Clear();
            _sourceAtlasIndices = null;            
            SyncTileSourceBox();
            _hasUnsupportedLayer = hasUnsupportedLayer;
            _collisionGrid = collisionGrid;
            _tileUsageIndex = MapRenderer.BuildTileUsageIndex(_rom, mapOffset);

            mapPictureBox.Size = bitmap.Size;

            _currentEntities = EntityReader.ReadLevelGates(_rom, entry);
            _currentEntities.AddRange(EntityReader.ReadMapTriggers(_rom, entry));
            _currentEntities.AddRange(EntityReader.ReadVariationTriggers(_rom, mapOffset));
            _currentEntities.AddRange(EntityReader.ReadObjectArray(_rom, entry));
            _currentEntities.AddRange(EntityReader.ReadMapScripts(_rom, entry));
            _currentEntities.AddRange(EntityReader.ReadMapItems(_rom, entry, mapOffset));
            _currentEntities.AddRange(EntityReader.ReadGraphicObjects(_rom, mapOffset));

            DisposeDecorationSprites();
            _decorationSprites = MapRenderer.RenderGraphicObjectSprites(_rom, _game.Config, mapOffset);
            PopulateTileList(tilesets, usedTilesets);

            mapPictureBox.Invalidate();
            UpdateStatusLabel();
            UpdatePropertiesPanel();
        }

        private void DisposeDecorationSprites()
        {
            foreach (var sprite in _decorationSprites.Values)
                sprite.Dispose();
            _decorationSprites = new();
        }

        private void PopulateTileList(Dictionary<Range, Bitmap> tilesets, HashSet<Range> usedTilesets)
        {
            listView1.Items.Clear();
            _tileThumbnails.Images.Clear();
            listView1.LargeImageList = _tileThumbnails;
            listView1.View = View.LargeIcon;

            int outputSize = _tileThumbnails.ImageSize.Width;

            var images = new List<Image>();
            var items = new List<ListViewItem>();

            foreach (var range in usedTilesets.OrderBy(r => r.Start.Value))
            {
                if (!tilesets.TryGetValue(range, out var sheet))
                    continue;

                int columns = sheet.Width / TileSize;
                int rows = sheet.Height / TileSize;
                int rangeStart = range.Start.Value;

                for (int row = 0; row < rows; row++)
                {
                    for (int col = 0; col < columns; col++)
                    {
                        int tileId = rangeStart + (row * columns) + col;
                        if (tileId > range.End.Value)
                            break;

                        var tileBmp = new Bitmap(outputSize, outputSize);
                        using (var g = Graphics.FromImage(tileBmp))
                        {
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(sheet,
                                new Rectangle(0, 0, outputSize, outputSize),
                                new Rectangle(col * TileSize, row * TileSize, TileSize, TileSize),
                                GraphicsUnit.Pixel);
                        }

                        images.Add(tileBmp);
                        items.Add(new ListViewItem($"Tile {tileId}", images.Count - 1) { Tag = tileId });
                    }
                }
            }

            _tileThumbnails.Images.AddRange(images.ToArray());

            listView1.BeginUpdate();
            listView1.Items.AddRange(items.ToArray());
            listView1.EndUpdate();

            foreach (var img in images)
                img.Dispose();

            UpdateSelectedTileInfo(); 
        }

        private void listView1_SelectedIndexChanged(object? sender, EventArgs e)
        {
            int? selected = listView1.SelectedItems.Count > 0 && listView1.SelectedItems[0].Tag is int tileId ? tileId : null;

            if (selected.HasValue) _brushTileId = selected.Value;
            _highlightedTileId = _paintTilesButton is { Checked: true } || _tileSourceMap >= 0 && _sourceAtlasIndices != null ? null : selected;
            UpdateSelectedTileInfo();
            mapPictureBox.Invalidate();
        }

        private List<Entity> _currentEntities = new();
        private MapEntry? _currentEntry;
        private int _currentMapOffset;
        private int _selectedIndex = -1;
        private bool _dragging = false;
        private bool _resizing = false;
        private ResizeHandle _resizeHandle = ResizeHandle.None;
        private Point _dragGrabOffset; 
        private bool _dirty = false;
        private bool _hasUnsupportedLayer = false; 

        private Dictionary<int, List<Point>> _tileUsageIndex = new();
        private int? _highlightedTileId;

        private Dictionary<int, Bitmap> _decorationSprites = new();

        private bool[,]? _collisionGrid;

        private static Color ColorFor(EntityKind kind) => kind switch
        {
            EntityKind.LevelGate => Color.Cyan,
            EntityKind.Object => Color.Yellow,
            EntityKind.SpawnScript => Color.Red,
            EntityKind.Trigger => Color.Lime,
            EntityKind.Character => Color.Orange,
            EntityKind.Enemy => Color.OrangeRed,
            EntityKind.Item => Color.Magenta,
            EntityKind.Decoration => Color.SpringGreen,
            _ => Color.White
        };

        private void mapPictureBox_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            if (_currentMapBitmap == null)
            {
                g.Clear(Color.Black);
                return;
            }

            g.DrawImage(_currentMapBitmap, 0, 0);

            if (toolStrip_ShowCollision.Checked && _collisionGrid != null)
            {
                var bounds = new Rectangle(0, 0, _currentMapBitmap.Width, _currentMapBitmap.Height);
                var clip = Rectangle.Intersect(bounds, Rectangle.FromLTRB(
                    (e.ClipRectangle.Left / TileSize) * TileSize, (e.ClipRectangle.Top / TileSize) * TileSize,
                    ((e.ClipRectangle.Right + TileSize - 1) / TileSize) * TileSize, ((e.ClipRectangle.Bottom + TileSize - 1) / TileSize) * TileSize));
                if (clip.Width > 0 && clip.Height > 0)
                {
                    using var overlay = new Bitmap(clip.Width, clip.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    MapRenderer.DrawCollisionOverlay(_collisionGrid, overlay, clip.X / TileSize, clip.Y / TileSize);
                    g.DrawImage(overlay, clip.X, clip.Y);
                }
            }

            if (_showGridCheck.Checked) DrawGrid(g, e.ClipRectangle);

            for (int i = 0; i < _currentEntities.Count; i++)
            {
                var entity = _currentEntities[i];
                if (entity.Kind != EntityKind.Decoration) continue;

                if (_decorationSprites.TryGetValue(entity.SourceAddress, out var sprite))
                    g.DrawImage(sprite, entity.X, entity.Y);
                else
                    using (var pen = new Pen(ColorFor(entity.Kind), 1))
                        g.DrawRectangle(pen, entity.X, entity.Y, entity.Width, entity.Height);

                if (i == _selectedIndex)
                {
                    using var selectPen = new Pen(Color.White, 1) { DashStyle = DashStyle.Dash };
                    int w = entity.Width + 4;
                    int h = entity.Height + 4;
                    g.DrawRectangle(selectPen, entity.X - 2, entity.Y - 2, w, h);
                }
            }

            for (int i = 0; i < _currentEntities.Count; i++)
            {
                var entity = _currentEntities[i];
                if (entity.Kind == EntityKind.Decoration) continue;

                var color = ColorFor(entity.Kind);

                using var pen = new Pen(color, 1);

                if (entity.Width > 0 || entity.Height > 0)
                {
                    g.DrawRectangle(pen, entity.X, entity.Y, entity.Width, entity.Height);
                }
                else if (entity.Kind == EntityKind.LevelGate)
                {
                    string label = entity.TypeId.ToString();
                    using var font = new Font(FontFamily.GenericSansSerif, 7f, FontStyle.Bold);
                    var textSize = g.MeasureString(label, font);
                    var badgeRect = new RectangleF(entity.X - textSize.Width / 2 - 1, entity.Y - textSize.Height / 2, textSize.Width + 2, textSize.Height);

                    using (var bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                        g.FillRectangle(bg, badgeRect);
                    g.DrawRectangle(pen, badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height);
                    using (var textBrush = new SolidBrush(color))
                        g.DrawString(label, font, textBrush, badgeRect.X + 1, badgeRect.Y);
                }
                else
                {
                    Bitmap? icon = _rom == null || _game == null ? null : entity.Kind switch
                    {
                        EntityKind.Object => ItemIconReader.GetIcon(_rom, _game.Config, entity.TypeId),
                        EntityKind.Character or EntityKind.Enemy => CharacterIconReader.GetIcon(_rom, _game.Config, entity.TypeId),
                        _ => null
                    };

                    if (icon != null)
                    {
                        g.DrawImage(icon, entity.X - icon.Width / 2, entity.Y - icon.Height / 2, icon.Width, icon.Height);
                    }
                    else
                    {
                        using var brush = new SolidBrush(color);
                        g.FillEllipse(brush, entity.X - DotRadius, entity.Y - DotRadius, DotRadius * 2, DotRadius * 2);
                    }
                }

                if (i == _selectedIndex)
                {
                    using var selectPen = new Pen(Color.White, 1) { DashStyle = DashStyle.Dash };

                    if (entity.Width > 0 || entity.Height > 0)
                    {
                        g.DrawRectangle(selectPen, entity.X - 2, entity.Y - 2, entity.Width + 4, entity.Height + 4);
                    }
                    else
                    {
                        int w = (DotRadius * 2) + 4;
                        int h = (DotRadius * 2) + 4;
                        g.DrawRectangle(selectPen, entity.X - w / 2, entity.Y - h / 2, w, h);
                    }
                }
            }

            if (_highlightedTileId.HasValue && _tileUsageIndex.TryGetValue(_highlightedTileId.Value, out var highlightPositions))
            {
                using var highlightPen = new Pen(Color.Cyan, 1);
                foreach (var p in highlightPositions)
                {
                    g.DrawRectangle(highlightPen, p.X, p.Y, TileSize - 1, TileSize - 1);
                }
            }

            if (_placingTriggerStart != null && _placingTriggerCurrent != null)
            {
                var start = _placingTriggerStart.Value;
                var end = _placingTriggerCurrent.Value;
                int left = Math.Min(start.X, end.X);
                int top = Math.Min(start.Y, end.Y);
                int width = Math.Abs(end.X - start.X);
                int height = Math.Abs(end.Y - start.Y);

                using var previewPen = new Pen(Color.Yellow, 1) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(previewPen, left, top, width, height);
            }
        }

        private const int EdgeGrabMargin = 4;

        private enum ResizeHandle { None, Top, Bottom, Left, Right, TopLeft, TopRight, BottomLeft, BottomRight }

        private static ResizeHandle GetResizeHandleAt(Entity entity, Point p)
        {
            var rect = new Rectangle(entity.X, entity.Y, entity.Width, entity.Height);

            bool nearLeft = Math.Abs(p.X - rect.Left) <= EdgeGrabMargin && p.Y >= rect.Top - EdgeGrabMargin && p.Y <= rect.Bottom + EdgeGrabMargin;
            bool nearRight = Math.Abs(p.X - rect.Right) <= EdgeGrabMargin && p.Y >= rect.Top - EdgeGrabMargin && p.Y <= rect.Bottom + EdgeGrabMargin;
            bool nearTop = Math.Abs(p.Y - rect.Top) <= EdgeGrabMargin && p.X >= rect.Left - EdgeGrabMargin && p.X <= rect.Right + EdgeGrabMargin;
            bool nearBottom = Math.Abs(p.Y - rect.Bottom) <= EdgeGrabMargin && p.X >= rect.Left - EdgeGrabMargin && p.X <= rect.Right + EdgeGrabMargin;

            if (nearTop && nearLeft) return ResizeHandle.TopLeft;
            if (nearTop && nearRight) return ResizeHandle.TopRight;
            if (nearBottom && nearLeft) return ResizeHandle.BottomLeft;
            if (nearBottom && nearRight) return ResizeHandle.BottomRight;
            if (nearLeft) return ResizeHandle.Left;
            if (nearRight) return ResizeHandle.Right;
            if (nearTop) return ResizeHandle.Top;
            if (nearBottom) return ResizeHandle.Bottom;
            return ResizeHandle.None;
        }

        private static Cursor CursorFor(ResizeHandle handle) => handle switch
        {
            ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
            ResizeHandle.Left or ResizeHandle.Right => Cursors.SizeWE,
            ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
            ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
            _ => Cursors.Default
        };

        private int HitTest(Point clickLocation, bool skipTriggers = false)
        {
            for (int i = _currentEntities.Count - 1; i >= 0; i--)
            {
                var entity = _currentEntities[i];
                if (skipTriggers && entity.Kind == EntityKind.Trigger) continue; 

                if (entity.Width > 0 || entity.Height > 0)
                {
                    var rect = new Rectangle(entity.X, entity.Y, entity.Width, entity.Height);
                    if (rect.Contains(clickLocation))
                        return i;
                }
                else
                {
                    int dx = clickLocation.X - entity.X;
                    int dy = clickLocation.Y - entity.Y;
                    if (dx * dx + dy * dy <= (DotRadius + HitTestPadding) * (DotRadius + HitTestPadding))
                        return i;
                }
            }

            return -1;
        }

        private List<int> HitTestAll(Point p)
        {
            var results = new List<int>();
            for (int i = _currentEntities.Count - 1; i >= 0; i--)
            {
                var entity = _currentEntities[i];
                bool hit = entity.Width > 0 || entity.Height > 0
                    ? new Rectangle(entity.X, entity.Y, entity.Width, entity.Height).Contains(p)
                    : Math.Pow(p.X - entity.X, 2) + Math.Pow(p.Y - entity.Y, 2) <= Math.Pow(DotRadius + HitTestPadding, 2);

                if (hit) results.Add(i);
            }
            return results;
        }

        private (int Ordinal, int Total) OrdinalOf(int index)
        {
            var kind = _currentEntities[index].Kind;
            int ordinal = 1 + _currentEntities.Take(index).Count(e => e.Kind == kind);
            int total = _currentEntities.Count(e => e.Kind == kind);
            return (ordinal, total);
        }

        private string DescribeEntityAt(int index)
        {
            var entity = _currentEntities[index];
            int n = OrdinalOf(index).Ordinal;
            return entity.Kind switch
            {
                EntityKind.Trigger => $"Trigger #{n} ({entity.Width}x{entity.Height}) vtable=0x{entity.TypeId:X}",
                EntityKind.Object => $"Object #{n} — item id {entity.TypeId}",
                EntityKind.Item => $"Item #{n} — id {entity.TypeId} (position approximate)",
                EntityKind.Decoration => $"Decoration #{n} — frame {entity.TypeId}",
                EntityKind.LevelGate => $"Level Gate #{n} — requires level {entity.TypeId}",
                EntityKind.Character => $"{(IsTalkableNpc(entity) || entity.SourceAddress == 0 && entity.Variant != 2 ? "NPC" : "Actor")} #{n} — sprite {entity.TypeId}",
                EntityKind.Enemy => $"Enemy #{n} — sprite {entity.TypeId}, stat {entity.StatIndex}",
                EntityKind.SpawnScript => $"Spawn Script #{n}",
                _ => entity.
