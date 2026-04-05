using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using BinaryCarver.Analysis;
using BinaryCarver.Models;

namespace BinaryCarver;

public partial class MainWindow : Window
{
    private CarveResult? _result;
    private string? _filePath;
    private List<CustomSignature> _customSigs = [];
    private int _inspectedBlockIndex = -1;
    private int _rangeStartBlock = -1;  // Shift-click range selection
    private int _rangeEndBlock = -1;

    // 3D visualization state
    private System.Windows.Point _lastMousePos3D;
    private bool _isDragging3D;
    private double _camTheta = 0.6;   // horizontal angle (radians)
    private double _camPhi = 0.5;     // vertical angle
    private double _camDist = 14.0;   // distance from center
    private int _3dPagesPerRow;       // grid layout cached for hit-testing
    private int _3dPagesPerCol;
    private int _3dLayers;
    private int _3dNumPages;
    private double _3dSpacing = 1.1;
    // Grid is always centered at origin (0,0,0) — camera orbits around origin
    private HashSet<int> _selectedPageIndices = new();  // multi-select cubes in 3D
    private Color[] _pageColors = [];                   // cached per-page colors for color-matching
    private System.Windows.Point _3dMouseDownPos; // to distinguish click from drag
    // Filter state: if non-null, only pages in _filterSet are shown fully — others are ghosted
    private HashSet<int>? _filterSet = null;
    private string _filterLabel = "";


    public MainWindow()
    {
        InitializeComponent();
        _customSigs = CustomSignatureStore.Load();
        CarveEngine.SetCustomSignatures(_customSigs);

        // Wire up click handlers on all entropy canvases for block inspection
        EntropyCanvas.MouseLeftButtonDown += EntropyCanvas_Click;
        NibbleEntropyCanvas.MouseLeftButtonDown += EntropyCanvas_Click;
        BitEntropyCanvas.MouseLeftButtonDown += EntropyCanvas_Click;
        BigramEntropyCanvas.MouseLeftButtonDown += EntropyCanvas_Click;
        DivergenceCanvas.MouseLeftButtonDown += EntropyCanvas_Click;
        DeltaEntropyCanvas.MouseLeftButtonDown += EntropyCanvas_Click;
        ByteMapCanvas.MouseLeftButtonDown += ByteMapCanvas_Click;

        // 3D mouse interaction (distinguish click, double-click, and drag)
        Viewport3D.MouseLeftButtonDown += (s, ev) =>
        {
            if (ev.ClickCount == 2)
            {
                Viewport3D_DoubleClick(ev.GetPosition(Viewport3D));
                return;
            }
            _isDragging3D = true;
            _lastMousePos3D = ev.GetPosition(Viewport3D);
            _3dMouseDownPos = _lastMousePos3D;
            Viewport3D.CaptureMouse();
        };
        Viewport3D.MouseLeftButtonUp += (s, ev) =>
        {
            bool wasDrag = _isDragging3D && (ev.GetPosition(Viewport3D) - _3dMouseDownPos).Length > 4;
            _isDragging3D = false;
            Viewport3D.ReleaseMouseCapture();
            if (!wasDrag) Viewport3D_LeftClick(ev.GetPosition(Viewport3D), ev);
        };
        // Right-click: context menu for color operations
        Viewport3D.MouseRightButtonUp += Viewport3D_RightClick;
        Viewport3D.MouseMove += Viewport3D_MouseMove;
        Viewport3D.MouseWheel += Viewport3D_MouseWheel;

        // Redraw all visuals when window resizes (debounced to avoid UI thrashing)
        System.Windows.Threading.DispatcherTimer? resizeTimer = null;
        SizeChanged += (_, _) =>
        {
            if (_result == null) return;
            resizeTimer?.Stop();
            resizeTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            resizeTimer.Tick += (__, ___) =>
            {
                resizeTimer.Stop();
                RedrawAllVisuals(_result);
            };
            resizeTimer.Start();
        };
    }

    /// <summary>Redraw all canvases (byte map + entropy tracks). Called on window resize.</summary>
    private void RedrawAllVisuals(CarveResult r)
    {
        DrawByteMap(r);
        DrawEntropyHeatmap(r);
        DrawEntropyTrack(NibbleEntropyCanvas, r.NibbleEntropyMap, 4.0, r);
        DrawEntropyTrack(BitEntropyCanvas, r.BitEntropyMap, 1.0, r);
        DrawEntropyTrack(BigramEntropyCanvas, r.BigramEntropyMap, 16.0, r);
        DrawDivergenceTrack(r);
        DrawDeltaEntropyTrack(r);
    }

    // ── File loading ────────────────────────────────────────────────────

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            LoadFile(files[0]);
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "All files|*.*|Executables|*.exe;*.dll;*.sys|Archives|*.zip;*.rar;*.7z|Firmware|*.bin;*.rom;*.img",
        };
        if (dlg.ShowDialog() == true)
            LoadFile(dlg.FileName);
    }

    private void LoadFile(string filePath)
    {
        _filePath = filePath;
        TxtStatus.Text = "Analyzing...";

        try
        {
            bool recursive = ChkRecursive.IsChecked == true;
            CarveEngine.SetCustomSignatures(_customSigs);
            _result = CarveEngine.Analyze(filePath, recursive);
            PopulateAll(_result);

            TxtStatus.Text = $"Done — {_result.Regions.Count} region(s) found"
                + (recursive ? " (recursive)" : "");
            TxtFileInfo.Text = $"{_result.FileName}  ·  {_result.FileSizeHuman}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error analyzing file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Error";
        }
    }

    // ── Populate UI ─────────────────────────────────────────────────────

    private void PopulateAll(CarveResult r)
    {
        // Show/hide panels
        DropZone.Visibility     = Visibility.Collapsed;
        ByteMapPanel.Visibility = Visibility.Visible;
        ResultsPanel.Visibility = Visibility.Visible;
        DetailPanel.Visibility  = Visibility.Collapsed;
        RangePanel.Visibility   = Visibility.Visible;

        // File name
        TxtFileName.Text = $"{r.FileName}  ({r.FileSizeHuman})";
        TxtFileName.Foreground = (Brush)FindResource("TextPrimaryBrush");

        // Region count
        int nested = r.Regions.Count(x => x.Depth > 0);
        TxtRegionCount.Text = nested > 0
            ? $"{r.Regions.Count} region(s) ({nested} nested)"
            : $"{r.Regions.Count} region(s)";

        // Overlay info
        TxtOverlay.Text = r.Overlay != null
            ? $"Overlay: {r.Overlay.SizeDisplay} at {r.Overlay.OffsetHex} — {r.Overlay.Description}"
            : "";

        // Grid
        GridRegions.ItemsSource = r.Regions;

        // Buttons
        BtnExtractAll.IsEnabled = r.Regions.Count > 0;
        BtnExtractSelected.IsEnabled = false;
        BtnOpenInLens.IsEnabled = false;
        BtnExport.IsEnabled = r.Regions.Count > 0;
        BtnImportMap.IsEnabled = true;

        // Byte map (with gap visualization) + multi-base entropy heatmaps
        // Defer drawing to after layout so ActualWidth is valid
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                RedrawAllVisuals(r);
                if (Panel3D.Visibility == Visibility.Visible)
                {
                    _selectedPageIndices.Clear();
                    _filterSet = null;
                    _filterLabel = "";
                    Build3DView();
                }
            }));

        // Errors
        if (r.Errors.Count > 0)
            TxtStatus.Text = $"Done with {r.Errors.Count} warning(s): {r.Errors[0]}";
    }

    // ── Visual byte map ─────────────────────────────────────────────────

    private void DrawByteMap(CarveResult r)
    {
        ByteMapCanvas.Children.Clear();
        if (r.FileSize <= 0) return;

        double canvasWidth = ByteMapCanvas.ActualWidth;
        if (canvasWidth <= 0) canvasWidth = 1100;
        double canvasHeight = 40;

        var bg = new Rectangle
        {
            Width = canvasWidth, Height = canvasHeight,
            Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
        };
        Canvas.SetLeft(bg, 0);
        Canvas.SetTop(bg, 0);
        ByteMapCanvas.Children.Add(bg);

        // Draw gap regions first (background layer)
        foreach (var gap in r.Gaps)
        {
            double x = (double)gap.Offset / r.FileSize * canvasWidth;
            double w = Math.Max(1, (double)gap.Size / r.FileSize * canvasWidth);

            var rect = new Rectangle
            {
                Width = w, Height = canvasHeight,
                Fill = new SolidColorBrush(GetGapColor(gap.Classification)),
                ToolTip = $"Gap [{gap.Classification}]: {gap.OffsetHex} ({gap.SizeDisplay}) entropy={gap.Entropy:F2}",
                Opacity = 0.4,
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 0);
            ByteMapCanvas.Children.Add(rect);
        }

        // Draw carved regions on top
        foreach (var region in r.Regions.Where(x => x.Depth == 0))
        {
            double x = (double)region.Offset / r.FileSize * canvasWidth;
            double w = Math.Max(2, (double)region.Size / r.FileSize * canvasWidth);

            var rect = new Rectangle
            {
                Width = w, Height = canvasHeight,
                Fill = new SolidColorBrush(GetRegionColor(region.FileType)),
                ToolTip = $"{region.FileType}: {region.OffsetHex} ({region.SizeDisplay}) [{region.SizingMethod}]",
                Opacity = 0.85,
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 0);
            ByteMapCanvas.Children.Add(rect);
        }
    }

    private static Color GetRegionColor(string fileType) => fileType switch
    {
        "PE/EXE" or "PE/DLL" or "ELF" or "Mach-O" => Color.FromRgb(0x4C, 0xAF, 0x50),
        "ZIP" or "GZIP" or "7Z" or "RAR" or "CAB"
            or "XZ" or "BZIP2" or "ZSTD" or "TAR"  => Color.FromRgb(0x21, 0x96, 0xF3),
        "PNG" or "JPEG" or "GIF" or "BMP" or "ICO"
            or "TIFF" or "WEBP"                     => Color.FromRgb(0xFF, 0x98, 0x00),
        "PDF" or "XML" or "HTML" or "RTF"
            or "OLE/DOC"                            => Color.FromRgb(0x9C, 0x27, 0xB0),
        "Overlay"                                   => Color.FromRgb(0xF4, 0x43, 0x36),
        _                                           => Color.FromRgb(0x60, 0x7D, 0x8B),
    };

    // ── Multi-base entropy heatmaps ────────────────────────────────────

    /// <summary>Draw the byte entropy track (same gradient as before).</summary>
    private void DrawEntropyHeatmap(CarveResult r)
    {
        DrawEntropyTrack(EntropyCanvas, r.EntropyMap, 8.0, r);
    }

    /// <summary>Generic entropy track renderer — normalizes to maxEntropy then applies gradient.</summary>
    private void DrawEntropyTrack(System.Windows.Controls.Canvas canvas, double[] map,
        double maxEntropy, CarveResult r)
    {
        canvas.Children.Clear();
        if (map == null || map.Length == 0) return;

        double canvasWidth = canvas.ActualWidth;
        if (canvasWidth <= 0) canvasWidth = 1100;
        double canvasHeight = 16;

        double blockWidth = canvasWidth / map.Length;
        if (blockWidth < 0.5) blockWidth = 0.5;

        for (int i = 0; i < map.Length; i++)
        {
            double normalized = Math.Clamp(map[i] / maxEntropy, 0, 1);
            Color color = NormalizedEntropyToColor(normalized);

            double x = (double)i / map.Length * canvasWidth;

            var rect = new Rectangle
            {
                Width = Math.Max(blockWidth, 1),
                Height = canvasHeight,
                Fill = new SolidColorBrush(color),
                ToolTip = $"Block {i}: {map[i]:F3} / {maxEntropy:F1} (offset 0x{(long)i * r.EntropyBlockSize:X8})",
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 0);
            canvas.Children.Add(rect);
        }
    }

    /// <summary>Draw the divergence track — dark=agreement, bright=boundary.</summary>
    private void DrawDivergenceTrack(CarveResult r)
    {
        DivergenceCanvas.Children.Clear();
        if (r.DivergenceMap == null || r.DivergenceMap.Length == 0) return;

        double canvasWidth = DivergenceCanvas.ActualWidth;
        if (canvasWidth <= 0) canvasWidth = 1100;
        double canvasHeight = 16;

        // Find max divergence for normalization
        double maxDiv = 0;
        foreach (double d in r.DivergenceMap)
            if (d > maxDiv) maxDiv = d;
        if (maxDiv < 0.001) maxDiv = 0.5; // avoid div by zero

        double blockWidth = canvasWidth / r.DivergenceMap.Length;
        if (blockWidth < 0.5) blockWidth = 0.5;

        for (int i = 0; i < r.DivergenceMap.Length; i++)
        {
            double t = Math.Clamp(r.DivergenceMap[i] / maxDiv, 0, 1);

            // Dark → amber → white gradient for divergence
            Color color;
            if (t < 0.5)
            {
                double s = t / 0.5;
                color = Lerp(Color.FromRgb(0x1A, 0x1A, 0x1A), Color.FromRgb(0xFF, 0x6F, 0x00), s);
            }
            else
            {
                double s = (t - 0.5) / 0.5;
                color = Lerp(Color.FromRgb(0xFF, 0x6F, 0x00), Color.FromRgb(0xFF, 0xFF, 0xFF), s);
            }

            double x = (double)i / r.DivergenceMap.Length * canvasWidth;

            var rect = new Rectangle
            {
                Width = Math.Max(blockWidth, 1),
                Height = canvasHeight,
                Fill = new SolidColorBrush(color),
                ToolTip = $"Block {i}: divergence {r.DivergenceMap[i]:F4} (offset 0x{(long)i * r.EntropyBlockSize:X8})",
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 0);
            DivergenceCanvas.Children.Add(rect);
        }
    }

    /// <summary>Map normalized 0–1 value to blue→green→yellow→orange→red gradient.</summary>
    private static Color NormalizedEntropyToColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t < 0.25)
        {
            double s = t / 0.25;
            return Lerp(Color.FromRgb(0x21, 0x96, 0xF3), Color.FromRgb(0x4C, 0xAF, 0x50), s);
        }
        if (t < 0.5)
        {
            double s = (t - 0.25) / 0.25;
            return Lerp(Color.FromRgb(0x4C, 0xAF, 0x50), Color.FromRgb(0xFF, 0xEB, 0x3B), s);
        }
        if (t < 0.75)
        {
            double s = (t - 0.5) / 0.25;
            return Lerp(Color.FromRgb(0xFF, 0xEB, 0x3B), Color.FromRgb(0xFF, 0x98, 0x00), s);
        }
        {
            double s = (t - 0.75) / 0.25;
            return Lerp(Color.FromRgb(0xFF, 0x98, 0x00), Color.FromRgb(0xF4, 0x43, 0x36), s);
        }
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static Color GetGapColor(GapClassification cls) => cls switch
    {
        GapClassification.Padding    => Color.FromRgb(0x33, 0x33, 0x33),
        GapClassification.Text       => Color.FromRgb(0x5C, 0x6B, 0xC0),
        GapClassification.Code       => Color.FromRgb(0x26, 0xA6, 0x9A),
        GapClassification.Compressed => Color.FromRgb(0xEF, 0x53, 0x50),
        GapClassification.Structured => Color.FromRgb(0xAB, 0x47, 0xBC),
        _                            => Color.FromRgb(0x42, 0x42, 0x42),
    };

    /// <summary>Draw delta entropy track — same gradient as divergence (dark→amber→white).</summary>
    private void DrawDeltaEntropyTrack(CarveResult r)
    {
        DeltaEntropyCanvas.Children.Clear();
        if (r.DeltaEntropyMap == null || r.DeltaEntropyMap.Length == 0) return;

        double canvasWidth = DeltaEntropyCanvas.ActualWidth;
        if (canvasWidth <= 0) canvasWidth = 1100;
        double canvasHeight = 16;

        double maxDelta = 0;
        foreach (double d in r.DeltaEntropyMap)
            if (d > maxDelta) maxDelta = d;
        if (maxDelta < 0.01) maxDelta = 1.0;

        double blockWidth = canvasWidth / r.DeltaEntropyMap.Length;
        if (blockWidth < 0.5) blockWidth = 0.5;

        for (int i = 0; i < r.DeltaEntropyMap.Length; i++)
        {
            double t = Math.Clamp(r.DeltaEntropyMap[i] / maxDelta, 0, 1);
            Color color;
            if (t < 0.5)
            {
                double s = t / 0.5;
                color = Lerp(Color.FromRgb(0x1A, 0x1A, 0x1A), Color.FromRgb(0xFF, 0x6F, 0x00), s);
            }
            else
            {
                double s = (t - 0.5) / 0.5;
                color = Lerp(Color.FromRgb(0xFF, 0x6F, 0x00), Color.FromRgb(0xFF, 0xFF, 0xFF), s);
            }

            double x = (double)i / r.DeltaEntropyMap.Length * canvasWidth;
            var rect = new Rectangle
            {
                Width = Math.Max(blockWidth, 1),
                Height = canvasHeight,
                Fill = new SolidColorBrush(color),
                ToolTip = $"Block {i}: Δentropy {r.DeltaEntropyMap[i]:F4} (offset 0x{(long)i * r.EntropyBlockSize:X8})",
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 0);
            DeltaEntropyCanvas.Children.Add(rect);
        }
    }

    // ── Interactive: click entropy track → inspect block ────────────────

    private void EntropyCanvas_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_result == null || _result.EntropyMap.Length == 0) return;

        var canvas = sender as System.Windows.Controls.Canvas;
        if (canvas == null) return;

        double canvasWidth = canvas.ActualWidth;
        if (canvasWidth <= 0) return;

        double clickX = e.GetPosition(canvas).X;
        int blockIndex = (int)(clickX / canvasWidth * _result.EntropyMap.Length);
        blockIndex = Math.Clamp(blockIndex, 0, _result.EntropyMap.Length - 1);

        // Shift-click = set range end
        bool isShift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        if (isShift && _inspectedBlockIndex >= 0)
        {
            _rangeStartBlock = Math.Min(_inspectedBlockIndex, blockIndex);
            _rangeEndBlock = Math.Max(_inspectedBlockIndex, blockIndex);
            UpdateRangeDisplay();
            return;
        }

        // Normal click = inspect single block, reset range
        _rangeStartBlock = -1;
        _rangeEndBlock = -1;
        ShowBlockInspector(blockIndex);
    }

    private void ByteMapCanvas_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_result == null || _result.FileSize <= 0) return;

        double canvasWidth = ByteMapCanvas.ActualWidth;
        if (canvasWidth <= 0) return;

        double clickX = e.GetPosition(ByteMapCanvas).X;
        long clickOffset = (long)(clickX / canvasWidth * _result.FileSize);

        // Find the region at this offset
        var region = _result.Regions
            .Where(r => r.Depth == 0 && clickOffset >= r.Offset && clickOffset < r.Offset + r.Size)
            .FirstOrDefault();

        if (region != null)
        {
            GridRegions.SelectedItem = region;
            GridRegions.ScrollIntoView(region);

            // Highlight the region's pages in 3D
            if (Panel3D.Visibility == Visibility.Visible && _result.EntropyBlockSize > 0)
            {
                bool isCtrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
                if (!isCtrl) _selectedPageIndices.Clear();

                int startPage = (int)(region.Offset / _result.EntropyBlockSize);
                int endPage = (int)((region.Offset + region.Size - 1) / _result.EntropyBlockSize);
                for (int p = startPage; p <= endPage && p < _3dNumPages; p++)
                    _selectedPageIndices.Add(p);
                UpdateHighlight3D();
            }
        }
        else
        {
            // Clicked on a gap — show the entropy block inspector
            int blockIndex = (int)(clickOffset / _result.EntropyBlockSize);
            blockIndex = Math.Clamp(blockIndex, 0, Math.Max(0, _result.EntropyMap.Length - 1));
            ShowBlockInspector(blockIndex);
        }
    }

    private void ShowBlockInspector(int blockIndex, bool updateFrom3D = true)
    {
        if (_result == null) return;
        _inspectedBlockIndex = blockIndex;

        long blockOffset = (long)blockIndex * _result.EntropyBlockSize;
        long blockEnd = Math.Min(blockOffset + _result.EntropyBlockSize, _result.FileSize);
        long blockSize = blockEnd - blockOffset;

        TxtBlockInfo.Text = $"Block {blockIndex}  |  Offset: 0x{blockOffset:X8}  |  Size: {blockSize} B  |  Range: 0x{blockOffset:X8} – 0x{blockEnd:X8}";

        string byteE = blockIndex < _result.EntropyMap.Length ? $"{_result.EntropyMap[blockIndex]:F3}" : "—";
        string nibE  = blockIndex < _result.NibbleEntropyMap.Length ? $"{_result.NibbleEntropyMap[blockIndex]:F3}" : "—";
        string bitE  = blockIndex < _result.BitEntropyMap.Length ? $"{_result.BitEntropyMap[blockIndex]:F3}" : "—";
        string bigE  = blockIndex < _result.BigramEntropyMap.Length ? $"{_result.BigramEntropyMap[blockIndex]:F3}" : "—";
        string divE  = blockIndex < _result.DivergenceMap.Length ? $"{_result.DivergenceMap[blockIndex]:F4}" : "—";
        string dltE  = blockIndex < _result.DeltaEntropyMap.Length ? $"{_result.DeltaEntropyMap[blockIndex]:F4}" : "—";

        TxtBlockEntropy.Text = $"Byte: {byteE}/8.0  |  Nibble: {nibE}/4.0  |  Bit: {bitE}/1.0  |  Bigram: {bigE}/16.0  |  Divergence: {divE}  |  Delta: {dltE}";

        var gap = _result.Gaps.FirstOrDefault(g => blockOffset >= g.Offset && blockOffset < g.Offset + g.Size);
        if (gap != null)
            TxtBlockEntropy.Text += $"  |  Gap: {gap.Classification}";

        // Check if block falls inside a carved region
        var region = _result.Regions
            .FirstOrDefault(r => r.Depth == 0 && blockOffset >= r.Offset && blockOffset < r.Offset + r.Size);
        if (region != null)
            TxtBlockEntropy.Text += $"  |  In: {region.FileType} @ {region.OffsetHex}";

        // Hex preview of the block
        TxtBlockHexPreview.Text = "";
        if (_filePath != null && File.Exists(_filePath))
        {
            try
            {
                using var fs = File.OpenRead(_filePath);
                if (blockOffset < fs.Length)
                {
                    fs.Seek(blockOffset, SeekOrigin.Begin);
                    int toRead = (int)Math.Min(256, Math.Min(blockSize, fs.Length - blockOffset));
                    byte[] hexData = new byte[toRead];
                    fs.Read(hexData, 0, toRead);
                    TxtBlockHexPreview.Text = FormatHexDump(hexData, blockOffset);
                }
            }
            catch { TxtBlockHexPreview.Text = "(could not read block data)"; }
        }

        // Range display
        TxtBlockRange.Visibility = Visibility.Collapsed;
        BtnDrillInRange.IsEnabled = false;

        BlockInspectorPanel.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
        GridRegions.SelectedItem = null;

        // Update 3D highlight if the panel is visible (skip if called from 3D click to avoid loop)
        if (updateFrom3D && Panel3D.Visibility == Visibility.Visible && _result.EntropyBlockSize > 0)
        {
            int pageIdx = (int)(blockOffset / _result.EntropyBlockSize);
            bool isCtrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
            if (isCtrl)
            {
                if (!_selectedPageIndices.Remove(pageIdx))
                    _selectedPageIndices.Add(pageIdx);
            }
            else
            {
                _selectedPageIndices.Clear();
                _selectedPageIndices.Add(pageIdx);
            }
            UpdateHighlight3D();
        }
    }

    private void UpdateRangeDisplay()
    {
        if (_result == null || _rangeStartBlock < 0 || _rangeEndBlock < 0) return;

        long rangeStart = (long)_rangeStartBlock * _result.EntropyBlockSize;
        long rangeEnd = Math.Min((long)(_rangeEndBlock + 1) * _result.EntropyBlockSize, _result.FileSize);
        long rangeSize = rangeEnd - rangeStart;
        int blockCount = _rangeEndBlock - _rangeStartBlock + 1;

        TxtBlockRange.Text = $"RANGE: Blocks {_rangeStartBlock}–{_rangeEndBlock} ({blockCount} blocks)  |  0x{rangeStart:X8} – 0x{rangeEnd:X8}  |  {rangeSize} bytes ({rangeSize / 1024.0:F1} KB)";
        TxtBlockRange.Visibility = Visibility.Visible;
        BtnDrillInRange.IsEnabled = true;
    }

    private void BtnExtractBlock_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null || _result == null || _inspectedBlockIndex < 0) return;

        long offset = (long)_inspectedBlockIndex * _result.EntropyBlockSize;
        long size = Math.Min(_result.EntropyBlockSize, _result.FileSize - offset);

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"block_{_inspectedBlockIndex:D4}_0x{offset:X}.bin",
            Filter = "Raw binary (*.bin)|*.bin|All files|*.*",
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                CarveEngine.ExtractRange(_filePath, offset, size, dlg.FileName);
                TxtStatus.Text = $"Extracted block {_inspectedBlockIndex} ({size} bytes from 0x{offset:X8})";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Block extraction failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnExtractToBoundary_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null || _result == null || _inspectedBlockIndex < 0) return;
        if (_result.DivergenceMap.Length == 0) return;

        long startOffset = (long)_inspectedBlockIndex * _result.EntropyBlockSize;

        // Find mean + 2*stddev threshold for divergence peaks
        double sum = 0, sumSq = 0;
        for (int i = 0; i < _result.DivergenceMap.Length; i++)
        {
            sum += _result.DivergenceMap[i];
            sumSq += _result.DivergenceMap[i] * _result.DivergenceMap[i];
        }
        double mean = sum / _result.DivergenceMap.Length;
        double variance = (sumSq / _result.DivergenceMap.Length) - (mean * mean);
        double threshold = mean + 2.0 * Math.Sqrt(Math.Max(0, variance));
        if (threshold < 0.05) threshold = 0.05;

        long endOffset = _result.FileSize;
        for (int i = _inspectedBlockIndex + 1; i < _result.DivergenceMap.Length; i++)
        {
            if (_result.DivergenceMap[i] >= threshold)
            {
                endOffset = (long)i * _result.EntropyBlockSize;
                break;
            }
        }

        long size = endOffset - startOffset;
        if (size <= 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"boundary_0x{startOffset:X}_0x{endOffset:X}.bin",
            Filter = "Raw binary (*.bin)|*.bin|All files|*.*",
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                CarveEngine.ExtractRange(_filePath, startOffset, size, dlg.FileName);
                TxtStatus.Text = $"Extracted {size} bytes from 0x{startOffset:X8} to boundary at 0x{endOffset:X8}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Boundary extraction failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>Drill into a single entropy block — extract it and analyze with full engine.</summary>
    private void BtnDrillInBlock_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null || _result == null || _inspectedBlockIndex < 0) return;

        long offset = (long)_inspectedBlockIndex * _result.EntropyBlockSize;
        long size = Math.Min(_result.EntropyBlockSize, _result.FileSize - offset);

        DrillIntoRange(offset, size, $"block_{_inspectedBlockIndex:D4}");
    }

    /// <summary>Drill into a shift-click selected range of entropy blocks.</summary>
    private void BtnDrillInRange_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null || _result == null || _rangeStartBlock < 0 || _rangeEndBlock < 0) return;

        long offset = (long)_rangeStartBlock * _result.EntropyBlockSize;
        long end = Math.Min((long)(_rangeEndBlock + 1) * _result.EntropyBlockSize, _result.FileSize);
        long size = end - offset;

        DrillIntoRange(offset, size, $"range_{_rangeStartBlock}-{_rangeEndBlock}");
    }

    /// <summary>
    /// Extract a byte range from the current file, save to temp, and run the full
    /// carving engine on it — same drill-in behavior as carved regions.
    /// </summary>
    private void DrillIntoRange(long offset, long size, string label)
    {
        if (_filePath == null || _result == null || size <= 0) return;

        try
        {
            // Unique temp name avoids collisions when drilling into the same offset
            // from different parent files or re-drilling the same block
            string uid = Guid.NewGuid().ToString("N")[..8];
            string tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"BinaryCarver_drill_{label}_0x{offset:X}_{uid}.bin");
            CarveEngine.ExtractRange(_filePath, offset, size, tempPath);

            // Save parent state
            _drillStack.Push((_filePath, _result));

            // Analyze the extracted range
            LoadFile(tempPath);

            string parentName = _drillStack.Peek().Result.FileName;
            TxtStatus.Text = $"Drill-in: analyzing 0x{offset:X8}–0x{offset + size:X8} ({size} bytes) from {parentName}";
            TxtFileName.Text += $"  [drilled from {parentName} @ 0x{offset:X8}]";
            BtnBack.Visibility = Visibility.Visible;

            // Close inspector
            BlockInspectorPanel.Visibility = Visibility.Collapsed;
            _inspectedBlockIndex = -1;
            _rangeStartBlock = -1;
            _rangeEndBlock = -1;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Drill-in failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnCloseInspector_Click(object sender, RoutedEventArgs e)
    {
        BlockInspectorPanel.Visibility = Visibility.Collapsed;
        _inspectedBlockIndex = -1;
        _rangeStartBlock = -1;
        _rangeEndBlock = -1;
    }

    // ── Region selection ────────────────────────────────────────────────

    private void GridRegions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridRegions.SelectedItem is not CarvedRegion region)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            BtnExtractSelected.IsEnabled = false;
            BtnOpenInLens.IsEnabled = false;
            return;
        }

        BtnExtractSelected.IsEnabled = true;
        DetailPanel.Visibility = Visibility.Visible;
        BlockInspectorPanel.Visibility = Visibility.Collapsed; // close inspector when viewing a region

        // BinaryLens button — only for PE regions
        bool isPe = region.FileType is "PE/EXE" or "PE/DLL";
        BtnOpenInLens.IsEnabled = isPe;

        // Detail properties
        TxtDetailHeader.Text = $"{region.Icon}  {region.FileType} — {region.Signature}";

        var props = new List<KeyValuePair<string, string>>
        {
            new("File Type",   region.FileType),
            new("Signature",   region.Signature),
            new("Offset",      region.OffsetHex),
            new("Size",        region.SizeDisplay),
            new("Entropy",     $"{region.Entropy:F4}"),
            new("Confidence",  region.ConfidenceLevel),
            new("Sized By",    region.SizingMethod),
            new("Depth",       $"{region.Depth}"),
            new("Parent",      region.ParentIndex >= 0 ? $"#{region.ParentIndex}" : "—"),
            new("Description", region.Description),
        };
        GridDetail.ItemsSource = props;

        // Hex preview — read up to 256 bytes from the file for a better view
        byte[] hexData = region.Preview;
        if (_filePath != null && File.Exists(_filePath))
        {
            try
            {
                using var fs = File.OpenRead(_filePath);
                if (region.Offset < fs.Length)
                {
                    fs.Seek(region.Offset, SeekOrigin.Begin);
                    int toRead = (int)Math.Min(256, Math.Min(region.Size, fs.Length - region.Offset));
                    hexData = new byte[toRead];
                    fs.Read(hexData, 0, toRead);
                }
            }
            catch { /* fall back to Preview bytes */ }
        }
        TxtHexPreview.Text = FormatHexDump(hexData, region.Offset);

        // Highlight region's pages in 3D
        if (Panel3D.Visibility == Visibility.Visible && _result.EntropyBlockSize > 0)
        {
            _selectedPageIndices.Clear();
            int startPage = (int)(region.Offset / _result.EntropyBlockSize);
            int endPage = (int)((region.Offset + region.Size - 1) / _result.EntropyBlockSize);
            for (int p = startPage; p <= endPage && p < _3dNumPages; p++)
                _selectedPageIndices.Add(p);
            UpdateHighlight3D();
        }
    }

    // ── Hex dump formatting ─────────────────────────────────────────────

    private static string FormatHexDump(byte[] data, long baseOffset)
    {
        if (data.Length == 0) return "(no data)";

        var sb = new System.Text.StringBuilder();
        int bytesPerLine = 16;

        for (int i = 0; i < data.Length; i += bytesPerLine)
        {
            sb.Append($"{baseOffset + i:X8}  ");
            for (int j = 0; j < bytesPerLine; j++)
            {
                if (i + j < data.Length)
                    sb.Append($"{data[i + j]:X2} ");
                else
                    sb.Append("   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int j = 0; j < bytesPerLine && i + j < data.Length; j++)
            {
                byte b = data[i + j];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── Extraction: selected region ─────────────────────────────────────

    private void BtnExtractSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null || GridRegions.SelectedItem is not CarvedRegion region) return;

        string defaultName = $"carved_{region.Index:D3}_{region.FileType.ToLowerInvariant().Replace("/", "_")}";
        string defaultExt = GetDefaultExtension(region.FileType);

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{defaultName}{defaultExt}",
            Filter = $"Detected type (*{defaultExt})|*{defaultExt}|Raw binary (*.bin)|*.bin|All files|*.*",
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                CarveEngine.ExtractRegion(_filePath, region, dlg.FileName);
                TxtStatus.Text = $"Extracted {region.FileType} ({region.SizeDisplay}) to {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Extraction failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ── Extraction: all regions ─────────────────────────────────────────

    private void BtnExtractAll_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null || _result == null || _result.Regions.Count == 0) return;

        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder to extract all carved regions into",
            UseDescriptionForTitle = true,
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            try
            {
                int count = 0;
                foreach (var region in _result.Regions)
                {
                    string ext = GetDefaultExtension(region.FileType);
                    string depthTag = region.Depth > 0 ? $"_d{region.Depth}" : "";
                    string name = $"carved_{region.Index:D3}_{region.FileType.ToLowerInvariant().Replace("/", "_")}{depthTag}{ext}";
                    string outPath = System.IO.Path.Combine(dlg.SelectedPath, name);
                    CarveEngine.ExtractRegion(_filePath, region, outPath);
                    count++;
                }
                TxtStatus.Text = $"Extracted {count} region(s) to {dlg.SelectedPath}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Extraction failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ── Extraction: custom range ────────────────────────────────────────

    private void BtnExtractRange_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null) return;

        if (!TryParseNumber(TxtRangeOffset.Text.Trim(), out long offset))
        {
            MessageBox.Show("Invalid offset. Use hex (0x...) or decimal.", "Invalid Input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryParseNumber(TxtRangeSize.Text.Trim(), out long size) || size <= 0)
        {
            MessageBox.Show("Invalid size. Use hex (0x...) or decimal, must be > 0.", "Invalid Input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"range_0x{offset:X}_0x{size:X}.bin",
            Filter = "Raw binary (*.bin)|*.bin|All files|*.*",
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                CarveEngine.ExtractRange(_filePath, offset, size, dlg.FileName);
                TxtStatus.Text = $"Extracted {size} bytes from offset 0x{offset:X8} to {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Range extraction failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>Parse a string as hex (0x prefix) or decimal.</summary>
    private static bool TryParseNumber(string text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return long.TryParse(text, out value);
    }

    // ── BinaryLens integration ──────────────────────────────────────────

    private void BtnOpenInLens_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null || GridRegions.SelectedItem is not CarvedRegion region) return;

        // Find BinaryLens.exe relative to this app
        string? appDir = System.IO.Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location);
        if (appDir == null) return;

        // Check sibling directory, then same directory
        string[] searchPaths =
        [
            System.IO.Path.Combine(appDir, "..", "BinaryLens", "BinaryLens.exe"),
            System.IO.Path.Combine(appDir, "..", "BinaryLens", "bin", "Release", "net9.0-windows", "win-x64", "BinaryLens.exe"),
            System.IO.Path.Combine(appDir, "BinaryLens.exe"),
        ];

        string? lensPath = null;
        foreach (var p in searchPaths)
        {
            string full = System.IO.Path.GetFullPath(p);
            if (File.Exists(full)) { lensPath = full; break; }
        }

        if (lensPath == null)
        {
            // Ask the user to locate it
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Locate BinaryLens.exe",
                Filter = "BinaryLens|BinaryLens.exe|All executables|*.exe",
            };
            if (dlg.ShowDialog() == true)
                lensPath = dlg.FileName;
            else
                return;
        }

        // If the region isn't the whole file, extract to a temp file first
        string targetPath = _filePath;
        if (region.Offset > 0 || region.Size < new FileInfo(_filePath).Length)
        {
            try
            {
                string ext = GetDefaultExtension(region.FileType);
                string tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"BinaryCarver_temp_{region.Index:D3}_{Guid.NewGuid().ToString("N")[..8]}{ext}");
                CarveEngine.ExtractRegion(_filePath, region, tempPath);
                targetPath = tempPath;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to extract region for BinaryLens:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = lensPath,
                Arguments = $"\"{targetPath}\"",
                UseShellExecute = true,
            });
            TxtStatus.Text = $"Launched BinaryLens on {System.IO.Path.GetFileName(targetPath)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch BinaryLens:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Drill-in: analyze a carved region as its own file ─────────────

    private readonly Stack<(string FilePath, CarveResult Result)> _drillStack = new();

    private void BtnDrillIn_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null || _result == null || GridRegions.SelectedItem is not CarvedRegion region) return;

        // Extract region to a temp file
        try
        {
            string ext = GetDefaultExtension(region.FileType);
            string uid = Guid.NewGuid().ToString("N")[..8];
            string tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"BinaryCarver_drill_{region.Index:D3}_{region.FileType.Replace("/", "_")}_{uid}{ext}");
            CarveEngine.ExtractRegion(_filePath, region, tempPath);

            // Save current state for "Back" navigation
            _drillStack.Push((_filePath, _result));

            // Analyze the extracted region as its own file
            LoadFile(tempPath);

            string parentName = _drillStack.Peek().Result.FileName;
            TxtStatus.Text = $"Drill-in: analyzing {region.FileType} at {region.OffsetHex} ({region.SizeDisplay}) from {parentName}";
            TxtFileName.Text += $"  [drilled from {parentName} @ {region.OffsetHex}]";
            BtnBack.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Drill-in failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Back navigation: pop drill-in stack ────────────────────────────

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_drillStack.Count == 0) return;

        var (parentPath, parentResult) = _drillStack.Pop();

        _filePath = parentPath;
        _result = parentResult;

        // Refresh UI with parent data
        GridRegions.ItemsSource = _result.Regions;
        TxtFileName.Text = _result.FileName;
        TxtStatus.Text = $"Returned to {_result.FileName}  ({_result.Regions.Count} regions)";
        Title = $"BinaryCarver — {_result.FileName}";

        if (_drillStack.Count > 0)
            TxtFileName.Text += $"  [drilled from {_drillStack.Peek().Result.FileName}]";

        BtnBack.Visibility = _drillStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Redraw visuals
        RedrawAllVisuals(_result);
        if (Panel3D.Visibility == Visibility.Visible) Build3DView();

        // Reset inspector
        BlockInspectorPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Collapsed;
        _inspectedBlockIndex = -1;
        _rangeStartBlock = -1;
        _rangeEndBlock = -1;
        _selectedPageIndices.Clear();
        _filterSet = null;
        _filterLabel = "";
    }

    // ── Signature editor ────────────────────────────────────────────────

    private void BtnSignatures_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SignatureEditorWindow(_customSigs);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            _customSigs = dialog.Signatures;
            CustomSignatureStore.Save(_customSigs);
            CarveEngine.SetCustomSignatures(_customSigs);
            TxtStatus.Text = $"Custom signatures updated ({_customSigs.Count} loaded)";

            // Re-analyze if a file is loaded
            if (_filePath != null)
                LoadFile(_filePath);
        }
    }

    // ── Export (JSON / CSV / RE tools) ───────────────────────────────────

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{_result.FileName}_report",
            Filter = string.Join("|",
                "JSON report (*.json)|*.json",
                "CSV report (*.csv)|*.csv",
                "IDA MAP file (*.map)|*.map",
                "IDAPython script (*.py)|*.py",
                "Ghidra script (*.py)|*.py",
                "radare2 script (*.r2)|*.r2",
                "Binary Ninja script (*.py)|*.py",
                "All files|*.*"),
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                string path = dlg.FileName;
                switch (dlg.FilterIndex)
                {
                    case 1: // JSON
                        CarveEngine.ExportJson(_result, path);
                        break;
                    case 2: // CSV
                        CarveEngine.ExportCsv(_result, path);
                        break;
                    case 3: // IDA MAP
                        CarveEngine.ExportIdaMap(_result, path, _filePath);
                        break;
                    case 4: // IDAPython
                        CarveEngine.ExportIdaPython(_result, path, _filePath);
                        break;
                    case 5: // Ghidra
                        CarveEngine.ExportGhidraScript(_result, path, _filePath);
                        break;
                    case 6: // radare2
                        CarveEngine.ExportR2Script(_result, path, _filePath);
                        break;
                    case 7: // Binary Ninja
                        CarveEngine.ExportBinaryNinja(_result, path, _filePath);
                        break;
                    default:
                        CarveEngine.ExportJson(_result, path);
                        break;
                }

                TxtStatus.Text = $"Exported report to {System.IO.Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ── Import region map ──────────────────────────────────────────────

    private void BtnImportMap_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null || _filePath == null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = string.Join("|",
                "IDA MAP file (*.map)|*.map",
                "BinaryCarver JSON (*.json)|*.json",
                "All files|*.*"),
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            string text = File.ReadAllText(dlg.FileName);
            int imported = 0;

            if (dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                imported = ImportFromJson(text);
            else
                imported = ImportFromIdaMap(text);

            if (imported > 0)
            {
                GridRegions.ItemsSource = null;
                GridRegions.ItemsSource = _result.Regions;
                RedrawAllVisuals(_result);
                if (Panel3D.Visibility == Visibility.Visible)
                {
                    _filterSet = null;
                    _filterLabel = "";
                    _selectedPageIndices.Clear();
                    Build3DView();
                }
            }

            TxtStatus.Text = $"Imported {imported} regions from {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Parse IDA MAP format — extracts segment start/length/name/class lines.</summary>
    private int ImportFromIdaMap(string text)
    {
        if (_result == null) return 0;
        int count = 0;
        int nextIdx = _result.Regions.Count > 0 ? _result.Regions.Max(r => r.Index) + 1 : 0;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            // Look for lines like: 0001:00001000 00001000H .text     CODE
            // or:                  0001:00001000 00001000H .text     DATA
            if (line.Length < 20 || line.StartsWith(";") || line.StartsWith("Address")
                || line.StartsWith("Start") || line.StartsWith("Publics")) continue;

            // Try to parse segment definition: "SSSS:OOOOOOOO LLLLLLLLH Name Class"
            var parts = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            // Parse address (segment:offset format)
            string addrPart = parts[0];
            long offset;
            if (addrPart.Contains(':'))
            {
                string[] addrParts = addrPart.Split(':');
                if (!long.TryParse(addrParts[1], System.Globalization.NumberStyles.HexNumber, null, out offset))
                    continue;
            }
            else if (!long.TryParse(addrPart.TrimStart('0', 'x', 'X'),
                System.Globalization.NumberStyles.HexNumber, null, out offset))
                continue;

            // Parse length (with trailing H)
            string lenPart = parts[1].TrimEnd('H', 'h');
            if (!long.TryParse(lenPart, System.Globalization.NumberStyles.HexNumber, null, out long size))
                continue;
            if (size <= 0) continue;

            string name = parts[2];
            string sclass = parts.Length > 3 ? parts[3] : "DATA";

            // Skip if this region overlaps existing ones at the exact same offset
            if (_result.Regions.Any(r => r.Offset == offset && r.Size == size)) continue;

            _result.Regions.Add(new Models.CarvedRegion
            {
                Index = nextIdx++,
                Offset = offset,
                Size = size,
                FileType = $"MAP/{sclass}",
                Signature = $"Imported: {name}",
                Description = $"Imported from MAP file: {name} ({sclass})",
                ConfidenceLevel = "Imported",
                SizingMethod = "MAP",
            });
            count++;
        }

        return count;
    }

    /// <summary>Parse BinaryCarver JSON export and merge regions.</summary>
    private int ImportFromJson(string text)
    {
        if (_result == null) return 0;
        int count = 0;
        int nextIdx = _result.Regions.Count > 0 ? _result.Regions.Max(r => r.Index) + 1 : 0;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (!root.TryGetProperty("regions", out var regions)) return 0;

            foreach (var r in regions.EnumerateArray())
            {
                long offset = r.GetProperty("offset").GetInt64();
                long size = r.GetProperty("size").GetInt64();
                string fileType = r.TryGetProperty("fileType", out var ft) ? ft.GetString() ?? "Unknown" : "Unknown";
                string sig = r.TryGetProperty("signature", out var s) ? s.GetString() ?? "" : "";
                string desc = r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                string conf = r.TryGetProperty("confidenceLevel", out var cl) ? cl.GetString() ?? "Imported" : "Imported";
                string sizing = r.TryGetProperty("sizingMethod", out var sm) ? sm.GetString() ?? "Imported" : "Imported";

                if (_result.Regions.Any(rr => rr.Offset == offset && rr.Size == size)) continue;

                _result.Regions.Add(new Models.CarvedRegion
                {
                    Index = nextIdx++,
                    Offset = offset,
                    Size = size,
                    FileType = fileType,
                    Signature = $"Imported: {sig}",
                    Description = desc,
                    ConfidenceLevel = conf,
                    SizingMethod = sizing,
                });
                count++;
            }
        }
        catch (System.Text.Json.JsonException) { }

        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // VISUALIZATION SETTINGS + 3D VIEW
    // ═══════════════════════════════════════════════════════════════════════

    private void CboBlockSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_result == null || _filePath == null || CboBlockSize.SelectedItem == null) return;
        if (CboBlockSize.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out int blockSize))
        {
            // Re-analyze with new block size
            try
            {
                byte[] data;
                if (_result.FileSize > 64 * 1024 * 1024)
                    data = ReadFileBytes(_filePath, (int)_result.FileSize);
                else
                    data = File.ReadAllBytes(_filePath);

                CarveEngine.RecomputeEntropy(data, _result, blockSize);
                _filterSet = null;
                _filterLabel = "";
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() =>
                    {
                        RedrawAllVisuals(_result);
                        if (Panel3D.Visibility == Visibility.Visible) Build3DView();
                    }));
                TxtStatus.Text = $"Entropy recomputed with block size {blockSize}";
            }
            catch (Exception ex) { TxtStatus.Text = $"Error: {ex.Message}"; }
        }
    }

    private void CboColorMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_result == null) return;
        if (Panel3D.Visibility == Visibility.Visible)
        {
            // Save and restore camera — only cube colors change
            double savedTheta = _camTheta, savedPhi = _camPhi, savedDist = _camDist;
            Build3DView();
            _camTheta = savedTheta; _camPhi = savedPhi; _camDist = savedDist;
            UpdateCamera(0, 0, 0);
        }
    }

    private void Cbo3DShape_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_result == null) return;
        if (Panel3D.Visibility == Visibility.Visible)
        {
            // Shape change needs full geometry rebuild, but preserve camera
            double savedTheta = _camTheta, savedPhi = _camPhi, savedDist = _camDist;
            Build3DView();
            _camTheta = savedTheta; _camPhi = savedPhi; _camDist = savedDist;
            UpdateCamera(0, 0, 0);
        }
    }

    private bool _3dColumnVisible = true;

    private void Btn3DToggle_Click(object sender, RoutedEventArgs e)
    {
        _3dColumnVisible = !_3dColumnVisible;
        if (_3dColumnVisible)
        {
            Col3D.Width = new GridLength(1, GridUnitType.Star);
            Col3D.MinWidth = 200;
            Panel3D.Visibility = Visibility.Visible;
            Btn3DToggle.Content = "Hide 3D";
            if (_result != null) Build3DView();
        }
        else
        {
            Col3D.Width = new GridLength(0);
            Col3D.MinWidth = 0;
            Panel3D.Visibility = Visibility.Collapsed;
            Btn3DToggle.Content = "Show 3D";
        }
    }

    private static byte[] ReadFileBytes(string path, int length)
    {
        var data = new byte[length];
        using var fs = File.OpenRead(path);
        fs.Read(data, 0, length);
        return data;
    }

    /// <summary>Get the selected color mode index (0=Byte, 1=Nibble, etc.).</summary>
    private int GetColorModeIndex()
    {
        if (CboColorMode.SelectedIndex >= 0) return CboColorMode.SelectedIndex;
        return 0;
    }

    /// <summary>Map a page's data to a color based on the selected color mode.</summary>
    private Color GetPageColor(int pageIndex)
    {
        if (_result == null) return Colors.Gray;

        int colorMode = GetColorModeIndex();

        // Map page index to entropy block index
        int blockSize = _result.EntropyBlockSize;
        long pageOffset = (long)pageIndex * _result.EntropyBlockSize;
        int entropyBlock = blockSize > 0 ? (int)(pageOffset / blockSize) : 0;

        switch (colorMode)
        {
            case 0: // Byte Entropy
                if (entropyBlock < _result.EntropyMap.Length)
                    return NormalizedEntropyToColor(_result.EntropyMap[entropyBlock] / 8.0);
                break;
            case 1: // Nibble
                if (entropyBlock < _result.NibbleEntropyMap.Length)
                    return NormalizedEntropyToColor(_result.NibbleEntropyMap[entropyBlock] / 4.0);
                break;
            case 2: // Bit
                if (entropyBlock < _result.BitEntropyMap.Length)
                    return NormalizedEntropyToColor(_result.BitEntropyMap[entropyBlock] / 1.0);
                break;
            case 3: // Bigram
                if (entropyBlock < _result.BigramEntropyMap.Length)
                    return NormalizedEntropyToColor(_result.BigramEntropyMap[entropyBlock] / 16.0);
                break;
            case 4: // Divergence
                if (entropyBlock < _result.DivergenceMap.Length)
                {
                    double maxDiv = 0;
                    foreach (double d in _result.DivergenceMap) if (d > maxDiv) maxDiv = d;
                    if (maxDiv < 0.001) maxDiv = 0.5;
                    double t = Math.Clamp(_result.DivergenceMap[entropyBlock] / maxDiv, 0, 1);
                    if (t < 0.5) return Lerp(Color.FromRgb(0x1A, 0x1A, 0x1A), Color.FromRgb(0xFF, 0x6F, 0x00), t / 0.5);
                    return Lerp(Color.FromRgb(0xFF, 0x6F, 0x00), Colors.White, (t - 0.5) / 0.5);
                }
                break;
            case 5: // Delta
                if (entropyBlock < _result.DeltaEntropyMap.Length)
                {
                    double maxD = 0;
                    foreach (double d in _result.DeltaEntropyMap) if (d > maxD) maxD = d;
                    if (maxD < 0.01) maxD = 1.0;
                    double t = Math.Clamp(_result.DeltaEntropyMap[entropyBlock] / maxD, 0, 1);
                    if (t < 0.5) return Lerp(Color.FromRgb(0x1A, 0x1A, 0x1A), Color.FromRgb(0xFF, 0x6F, 0x00), t / 0.5);
                    return Lerp(Color.FromRgb(0xFF, 0x6F, 0x00), Colors.White, (t - 0.5) / 0.5);
                }
                break;
            case 6: // Region Type
            {
                var region = _result.Regions.FirstOrDefault(r =>
                    r.Depth == 0 && pageOffset >= r.Offset && pageOffset < r.Offset + r.Size);
                if (region != null) return GetRegionColor(region.FileType);
                return Color.FromRgb(0x30, 0x30, 0x30);
            }
            case 7: // Gap Class
            {
                var gap = _result.Gaps.FirstOrDefault(g =>
                    pageOffset >= g.Offset && pageOffset < g.Offset + g.Size);
                if (gap != null) return GetGapColor(gap.Classification);
                // Check if it's in a region instead
                var reg = _result.Regions.FirstOrDefault(r =>
                    r.Depth == 0 && pageOffset >= r.Offset && pageOffset < r.Offset + r.Size);
                if (reg != null) return GetRegionColor(reg.FileType);
                return Color.FromRgb(0x30, 0x30, 0x30);
            }
        }
        return Colors.Gray;
    }

    /// <summary>Get the region type string for a page, or null if the page is in a gap/unrecognized area.</summary>
    private string? GetPageRegionType(int pageIndex)
    {
        if (_result == null) return null;
        long pageOffset = (long)pageIndex * _result.EntropyBlockSize;
        var region = _result.Regions.FirstOrDefault(r =>
            r.Depth == 0 && pageOffset >= r.Offset && pageOffset < r.Offset + r.Size);
        return region?.FileType;
    }

    /// <summary>Get a normalized 0–1 value for a page based on the selected color/entropy mode.</summary>
    private double GetPageValue(int pageIndex)
    {
        if (_result == null) return 0;

        int blockSize = _result.EntropyBlockSize;
        long pageOffset = (long)pageIndex * _result.EntropyBlockSize;
        int entropyBlock = blockSize > 0 ? (int)(pageOffset / blockSize) : 0;
        int colorMode = GetColorModeIndex();

        switch (colorMode)
        {
            case 0: return entropyBlock < _result.EntropyMap.Length
                ? Math.Clamp(_result.EntropyMap[entropyBlock] / 8.0, 0, 1) : 0;
            case 1: return entropyBlock < _result.NibbleEntropyMap.Length
                ? Math.Clamp(_result.NibbleEntropyMap[entropyBlock] / 4.0, 0, 1) : 0;
            case 2: return entropyBlock < _result.BitEntropyMap.Length
                ? Math.Clamp(_result.BitEntropyMap[entropyBlock], 0, 1) : 0;
            case 3: return entropyBlock < _result.BigramEntropyMap.Length
                ? Math.Clamp(_result.BigramEntropyMap[entropyBlock] / 16.0, 0, 1) : 0;
            case 4: // Divergence — normalize against max
                if (entropyBlock < _result.DivergenceMap.Length)
                {
                    double maxDiv = 0;
                    foreach (double d in _result.DivergenceMap) if (d > maxDiv) maxDiv = d;
                    return maxDiv > 0.001 ? Math.Clamp(_result.DivergenceMap[entropyBlock] / maxDiv, 0, 1) : 0;
                }
                return 0;
            case 5: // Delta — normalize against max
                if (entropyBlock < _result.DeltaEntropyMap.Length)
                {
                    double maxD = 0;
                    foreach (double d in _result.DeltaEntropyMap) if (d > maxD) maxD = d;
                    return maxD > 0.01 ? Math.Clamp(_result.DeltaEntropyMap[entropyBlock] / maxD, 0, 1) : 0;
                }
                return 0;
            case 6: // Region Type — 1.0 if in a region, 0.0 if not
            {
                var region = _result.Regions.FirstOrDefault(r =>
                    r.Depth == 0 && pageOffset >= r.Offset && pageOffset < r.Offset + r.Size);
                return region != null ? 1.0 : 0.1;
            }
            case 7: // Gap Class — 1.0 if in a gap, 0.5 if region, 0.1 if neither
            {
                var gap = _result.Gaps.FirstOrDefault(g =>
                    pageOffset >= g.Offset && pageOffset < g.Offset + g.Size);
                if (gap != null) return 1.0;
                var reg = _result.Regions.FirstOrDefault(r =>
                    r.Depth == 0 && pageOffset >= r.Offset && pageOffset < r.Offset + r.Size);
                return reg != null ? 0.5 : 0.1;
            }
        }
        return 0;
    }

    private int Get3DShapeMode()
    {
        if (Cbo3DShape.SelectedIndex >= 0) return Cbo3DShape.SelectedIndex;
        return 0; // Cubes
    }

    // ── 3D View Builder ─────────────────────────────────────────────────

    private void Build3DView()
    {
        if (_result == null || _result.FileSize <= 0) return;

        int numPages = (int)Math.Ceiling((double)_result.FileSize / _result.EntropyBlockSize);
        // Cap at 4096 pages for performance
        if (numPages > 4096) numPages = 4096;

        // Compute grid dimensions for a roughly cubic layout
        int pagesPerRow = (int)Math.Ceiling(Math.Pow(numPages, 1.0 / 3.0));
        int pagesPerCol = pagesPerRow;
        int layers = (int)Math.Ceiling((double)numPages / (pagesPerRow * pagesPerCol));

        // Cache grid layout for hit-testing and camera
        _3dPagesPerRow = pagesPerRow;
        _3dPagesPerCol = pagesPerCol;
        _3dLayers = layers;
        _3dNumPages = numPages;
        _3dSpacing = 1.1;

        double spacing = _3dSpacing;

        // Offsets to center the grid at origin (0,0,0)
        double offX = (pagesPerRow - 1) * spacing / 2.0;
        double offY = (pagesPerCol - 1) * spacing / 2.0;
        double offZ = (layers - 1) * spacing / 2.0;

        // Build a single mesh with all cube faces for performance
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        var colors = new List<Color>();

        int pageIdx = 0;
        int shapeMode = Get3DShapeMode(); // 0=Cubes, 1=Bars, 2=Spheres

        // Track per-shape vertex/index ranges for color batching
        var shapeRanges = new List<(int VertStart, int VertCount, int IdxStart, int IdxCount)>();

        for (int z = 0; z < layers && pageIdx < numPages; z++)
        {
            for (int y = 0; y < pagesPerCol && pageIdx < numPages; y++)
            {
                for (int x = 0; x < pagesPerRow && pageIdx < numPages; x++)
                {
                    bool passesFilter = _filterSet == null || _filterSet.Contains(pageIdx);
                    Color c = passesFilter ? GetPageColor(pageIdx) : Color.FromRgb(30, 30, 30);
                    double px = x * spacing - offX;
                    double py = y * spacing - offY;
                    double pz = -(z * spacing - offZ);

                    int vStart = positions.Count;
                    int iStart = indices.Count;

                    double scale = passesFilter ? 1.0 : 0.25; // ghosted pages are tiny
                    switch (shapeMode)
                    {
                        case 1: // Bars — height proportional to value
                        {
                            double val = passesFilter ? GetPageValue(pageIdx) : 0.02;
                            double barH = Math.Max(0.05, val) * spacing * 2.0;
                            AddBar(positions, indices, colors, px, py, pz, 0.85 * scale, barH, c);
                            break;
                        }
                        case 2: // Spheres — radius proportional to value
                        {
                            double val = passesFilter ? GetPageValue(pageIdx) : 0.05;
                            double radius = Math.Max(0.05, val * 0.48) * scale;
                            AddSphere(positions, indices, colors, px, py, pz, radius, 8, c);
                            break;
                        }
                        default: // Cubes
                            AddCube(positions, indices, colors, px, py, pz, 0.95 * scale, c);
                            break;
                    }

                    shapeRanges.Add((vStart, positions.Count - vStart, iStart, indices.Count - iStart));
                    pageIdx++;
                }
            }
        }

        // Cache per-page colors for right-click color matching
        _pageColors = colors.ToArray();

        // Batch shapes by color for rendering performance
        var group = new Model3DGroup();
        var colorBatches = new Dictionary<uint, (Point3DCollection Pos, Int32Collection Idx)>();

        for (int i = 0; i < colors.Count; i++)
        {
            Color c = colors[i];
            uint key = ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

            if (!colorBatches.ContainsKey(key))
                colorBatches[key] = (new Point3DCollection(), new Int32Collection());

            var (bPos, bIdx) = colorBatches[key];
            var (srcVertStart, vertCount, srcIdxStart, idxCount) = shapeRanges[i];
            int dstVertStart = bPos.Count;

            for (int v = 0; v < vertCount; v++)
                bPos.Add(positions[srcVertStart + v]);

            for (int t = 0; t < idxCount; t++)
                bIdx.Add(indices[srcIdxStart + t] - srcVertStart + dstVertStart);
        }

        foreach (var kvp in colorBatches)
        {
            uint key = kvp.Key;
            var (bPos, bIdx) = kvp.Value;
            byte r = (byte)((key >> 16) & 0xFF);
            byte g = (byte)((key >> 8) & 0xFF);
            byte b = (byte)(key & 0xFF);

            var batchMesh = new MeshGeometry3D { Positions = bPos, TriangleIndices = bIdx };
            var mat = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(r, g, b)));
            group.Children.Add(new GeometryModel3D(batchMesh, mat) { BackMaterial = mat });
        }

        DataModel3D.Content = group;

        // Update selection highlight on the separate overlay
        UpdateHighlight3D();

        // Zoom extents: fit entire prism with margin
        double extX = pagesPerRow * spacing / 2.0;
        double extY = pagesPerCol * spacing / 2.0;
        double extZ = layers * spacing / 2.0;
        double boundRadius = Math.Sqrt(extX * extX + extY * extY + extZ * extZ);
        double fovRad = 60.0 * Math.PI / 180.0;
        _camDist = boundRadius / Math.Sin(fovRad / 2.0) * 1.4; // 40% margin for comfortable framing
        UpdateCamera(0, 0, 0);

        string filterInfo = _filterSet != null ? $"  |  FILTER: {_filterLabel} ({_filterSet.Count} match)" : "";
        Txt3DInfo.Text = $"{numPages} pages ({_result.EntropyBlockSize}B each)  |  {pagesPerRow}×{pagesPerCol}×{layers} grid  |  Drag to rotate, scroll to zoom{filterInfo}";
    }

    private static void AddCube(Point3DCollection positions, Int32Collection indices,
        List<Color> colors, double cx, double cy, double cz, double size, Color color)
    {
        double h = size / 2.0;
        int baseIdx = positions.Count;

        // 6 faces × 4 vertices = 24 vertices per cube
        // Front face
        positions.Add(new Point3D(cx - h, cy - h, cz + h));
        positions.Add(new Point3D(cx + h, cy - h, cz + h));
        positions.Add(new Point3D(cx + h, cy + h, cz + h));
        positions.Add(new Point3D(cx - h, cy + h, cz + h));
        // Back face
        positions.Add(new Point3D(cx + h, cy - h, cz - h));
        positions.Add(new Point3D(cx - h, cy - h, cz - h));
        positions.Add(new Point3D(cx - h, cy + h, cz - h));
        positions.Add(new Point3D(cx + h, cy + h, cz - h));
        // Top face
        positions.Add(new Point3D(cx - h, cy + h, cz + h));
        positions.Add(new Point3D(cx + h, cy + h, cz + h));
        positions.Add(new Point3D(cx + h, cy + h, cz - h));
        positions.Add(new Point3D(cx - h, cy + h, cz - h));
        // Bottom face
        positions.Add(new Point3D(cx - h, cy - h, cz - h));
        positions.Add(new Point3D(cx + h, cy - h, cz - h));
        positions.Add(new Point3D(cx + h, cy - h, cz + h));
        positions.Add(new Point3D(cx - h, cy - h, cz + h));
        // Right face
        positions.Add(new Point3D(cx + h, cy - h, cz + h));
        positions.Add(new Point3D(cx + h, cy - h, cz - h));
        positions.Add(new Point3D(cx + h, cy + h, cz - h));
        positions.Add(new Point3D(cx + h, cy + h, cz + h));
        // Left face
        positions.Add(new Point3D(cx - h, cy - h, cz - h));
        positions.Add(new Point3D(cx - h, cy - h, cz + h));
        positions.Add(new Point3D(cx - h, cy + h, cz + h));
        positions.Add(new Point3D(cx - h, cy + h, cz - h));

        // 6 faces × 2 triangles × 3 indices = 36 indices
        for (int face = 0; face < 6; face++)
        {
            int fi = baseIdx + face * 4;
            indices.Add(fi); indices.Add(fi + 1); indices.Add(fi + 2);
            indices.Add(fi); indices.Add(fi + 2); indices.Add(fi + 3);
        }

        colors.Add(color);
    }

    /// <summary>Add a vertical bar (box with variable height) centered at (cx, cy, cz).</summary>
    private static void AddBar(Point3DCollection positions, Int32Collection indices,
        List<Color> colors, double cx, double cy, double cz, double baseSize, double height, Color color)
    {
        double hw = baseSize / 2.0;
        double hh = height / 2.0;
        int baseIdx = positions.Count;

        // Same 6-face structure as AddCube but with asymmetric Y extent
        // Front
        positions.Add(new Point3D(cx - hw, cy - hh, cz + hw));
        positions.Add(new Point3D(cx + hw, cy - hh, cz + hw));
        positions.Add(new Point3D(cx + hw, cy + hh, cz + hw));
        positions.Add(new Point3D(cx - hw, cy + hh, cz + hw));
        // Back
        positions.Add(new Point3D(cx + hw, cy - hh, cz - hw));
        positions.Add(new Point3D(cx - hw, cy - hh, cz - hw));
        positions.Add(new Point3D(cx - hw, cy + hh, cz - hw));
        positions.Add(new Point3D(cx + hw, cy + hh, cz - hw));
        // Top
        positions.Add(new Point3D(cx - hw, cy + hh, cz + hw));
        positions.Add(new Point3D(cx + hw, cy + hh, cz + hw));
        positions.Add(new Point3D(cx + hw, cy + hh, cz - hw));
        positions.Add(new Point3D(cx - hw, cy + hh, cz - hw));
        // Bottom
        positions.Add(new Point3D(cx - hw, cy - hh, cz - hw));
        positions.Add(new Point3D(cx + hw, cy - hh, cz - hw));
        positions.Add(new Point3D(cx + hw, cy - hh, cz + hw));
        positions.Add(new Point3D(cx - hw, cy - hh, cz + hw));
        // Right
        positions.Add(new Point3D(cx + hw, cy - hh, cz + hw));
        positions.Add(new Point3D(cx + hw, cy - hh, cz - hw));
        positions.Add(new Point3D(cx + hw, cy + hh, cz - hw));
        positions.Add(new Point3D(cx + hw, cy + hh, cz + hw));
        // Left
        positions.Add(new Point3D(cx - hw, cy - hh, cz - hw));
        positions.Add(new Point3D(cx - hw, cy - hh, cz + hw));
        positions.Add(new Point3D(cx - hw, cy + hh, cz + hw));
        positions.Add(new Point3D(cx - hw, cy + hh, cz - hw));

        for (int face = 0; face < 6; face++)
        {
            int fi = baseIdx + face * 4;
            indices.Add(fi); indices.Add(fi + 1); indices.Add(fi + 2);
            indices.Add(fi); indices.Add(fi + 2); indices.Add(fi + 3);
        }
        colors.Add(color);
    }

    /// <summary>Add a UV sphere at (cx, cy, cz) with the given radius and segment count.</summary>
    private static void AddSphere(Point3DCollection positions, Int32Collection indices,
        List<Color> colors, double cx, double cy, double cz, double radius, int segments, Color color)
    {
        int baseIdx = positions.Count;
        int rings = segments;
        int sectors = segments * 2;

        // Generate vertices
        for (int r = 0; r <= rings; r++)
        {
            double phi = Math.PI * r / rings;
            double sinPhi = Math.Sin(phi);
            double cosPhi = Math.Cos(phi);

            for (int s = 0; s <= sectors; s++)
            {
                double theta = 2.0 * Math.PI * s / sectors;
                double x = cx + radius * sinPhi * Math.Cos(theta);
                double y = cy + radius * cosPhi;
                double z = cz + radius * sinPhi * Math.Sin(theta);
                positions.Add(new Point3D(x, y, z));
            }
        }

        // Generate triangle indices
        int vertsPerRing = sectors + 1;
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < sectors; s++)
            {
                int a = baseIdx + r * vertsPerRing + s;
                int b = a + vertsPerRing;
                int c2 = a + 1;
                int d = b + 1;

                indices.Add(a); indices.Add(b); indices.Add(c2);
                indices.Add(c2); indices.Add(b); indices.Add(d);
            }
        }
        colors.Add(color);
    }

    // ── 3D Selection highlight (lightweight — doesn't rebuild data cubes) ──

    private void UpdateHighlight3D()
    {
        if (_result == null || _3dNumPages <= 0)
        {
            HighlightModel3D.Content = null;
            return;
        }

        if (_selectedPageIndices.Count == 0)
        {
            HighlightModel3D.Content = null;
            return;
        }

        var hlPos = new Point3DCollection();
        var hlIdx = new Int32Collection();
        var hlColors = new List<Color>();

        double offX = (_3dPagesPerRow - 1) * _3dSpacing / 2.0;
        double offY = (_3dPagesPerCol - 1) * _3dSpacing / 2.0;
        double offZ = (_3dLayers - 1) * _3dSpacing / 2.0;

        foreach (int hi in _selectedPageIndices)
        {
            if (hi < 0 || hi >= _3dNumPages) continue;
            int hz = hi / (_3dPagesPerRow * _3dPagesPerCol);
            int hRem = hi % (_3dPagesPerRow * _3dPagesPerCol);
            int hy = hRem / _3dPagesPerRow;
            int hx = hRem % _3dPagesPerRow;
            AddCube(hlPos, hlIdx, hlColors,
                hx * _3dSpacing - offX, hy * _3dSpacing - offY, -(hz * _3dSpacing - offZ), 1.08, Colors.White);
        }

        if (hlPos.Count == 0)
        {
            HighlightModel3D.Content = null;
            return;
        }

        var hlMesh = new MeshGeometry3D { Positions = hlPos, TriangleIndices = hlIdx };
        var hlMatGroup = new MaterialGroup();
        hlMatGroup.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255))));
        hlMatGroup.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(80, 100, 200, 255))));
        var group = new Model3DGroup();
        group.Children.Add(new GeometryModel3D(hlMesh, hlMatGroup) { BackMaterial = hlMatGroup });
        HighlightModel3D.Content = group;
    }

    // ── 3D Camera control ───────────────────────────────────────────────

    private void UpdateCamera(double cx, double cy, double cz)
    {
        double x = cx + _camDist * Math.Cos(_camPhi) * Math.Sin(_camTheta);
        double y = cy + _camDist * Math.Sin(_camPhi);
        double z = cz + _camDist * Math.Cos(_camPhi) * Math.Cos(_camTheta);

        Camera3D.Position = new Point3D(x, y, z);
        Camera3D.LookDirection = new Vector3D(cx - x, cy - y, cz - z);
    }

    private void Viewport3D_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging3D || _result == null) return;

        System.Windows.Point pos = e.GetPosition(Viewport3D);
        double dx = pos.X - _lastMousePos3D.X;
        double dy = pos.Y - _lastMousePos3D.Y;
        _lastMousePos3D = pos;

        _camTheta += dx * 0.01;
        _camPhi = Math.Clamp(_camPhi + dy * 0.01, -1.4, 1.4);

        UpdateCamera(0, 0, 0);
    }

    private void Viewport3D_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _camDist = Math.Clamp(_camDist - e.Delta * 0.01, 2, 200);
        if (_result == null) return;
        UpdateCamera(0, 0, 0);
    }

    // ── 3D Click / Right-Click / Double-Click ─────────────────────────

    /// <summary>Ray-cast hit-test: returns page index at screen point, or -1 if nothing hit.</summary>
    private int HitTest3DPageIndex(System.Windows.Point screenPos)
    {
        if (_result == null || _3dNumPages <= 0) return -1;

        var hitParams = new PointHitTestParameters(screenPos);
        int hitPageIndex = -1;

        VisualTreeHelper.HitTest(Viewport3D, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult rayResult)
            {
                Point3D hitPt = rayResult.PointHit;
                double s = _3dSpacing;
                double offX = (_3dPagesPerRow - 1) * s / 2.0;
                double offY = (_3dPagesPerCol - 1) * s / 2.0;
                double offZ = (_3dLayers - 1) * s / 2.0;

                int gx = (int)Math.Round((hitPt.X + offX) / s);
                int gy = (int)Math.Round((hitPt.Y + offY) / s);
                int gz = (int)Math.Round((-hitPt.Z + offZ) / s);

                gx = Math.Clamp(gx, 0, _3dPagesPerRow - 1);
                gy = Math.Clamp(gy, 0, _3dPagesPerCol - 1);
                gz = Math.Clamp(gz, 0, Math.Max(0, _3dLayers - 1));

                int pageIdx = gz * (_3dPagesPerRow * _3dPagesPerCol) + gy * _3dPagesPerRow + gx;
                if (pageIdx >= 0 && pageIdx < _3dNumPages)
                    hitPageIndex = pageIdx;

                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, hitParams);

        return hitPageIndex;
    }

    /// <summary>Single left-click: select page, or click empty to deselect all.</summary>
    private void Viewport3D_LeftClick(System.Windows.Point clickPos, System.Windows.Input.MouseButtonEventArgs ev)
    {
        int hitPageIndex = HitTest3DPageIndex(clickPos);

        if (hitPageIndex < 0)
        {
            // Clicked empty space — clear selection
            if (_selectedPageIndices.Count > 0)
            {
                _selectedPageIndices.Clear();
                UpdateHighlight3D();
            }
            return;
        }

        bool isCtrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        bool isShift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

        if (isCtrl)
        {
            if (!_selectedPageIndices.Remove(hitPageIndex))
                _selectedPageIndices.Add(hitPageIndex);
        }
        else if (isShift && _selectedPageIndices.Count > 0)
        {
            int anchor = _selectedPageIndices.Max();
            int lo = Math.Min(anchor, hitPageIndex);
            int hi = Math.Max(anchor, hitPageIndex);
            for (int p = lo; p <= hi; p++)
                _selectedPageIndices.Add(p);
        }
        else
        {
            _selectedPageIndices.Clear();
            _selectedPageIndices.Add(hitPageIndex);
        }

        UpdateHighlight3D();

        // Show inspector for the clicked page's entropy block
        if (_result != null)
        {
            long pageOffset = (long)hitPageIndex * _result.EntropyBlockSize;
            int blockSize = _result.EntropyBlockSize;
            int blockIndex = blockSize > 0 ? (int)(pageOffset / blockSize) : 0;
            blockIndex = Math.Clamp(blockIndex, 0, Math.Max(0, _result.EntropyMap.Length - 1));
            ShowBlockInspector(blockIndex, updateFrom3D: false);
        }
    }

    /// <summary>Double-click: flood-select adjacent pages with the same color.</summary>
    private void Viewport3D_DoubleClick(System.Windows.Point clickPos)
    {
        int hitPageIndex = HitTest3DPageIndex(clickPos);
        if (hitPageIndex < 0 || hitPageIndex >= _pageColors.Length) return;

        Color targetColor = _pageColors[hitPageIndex];
        int perLayer = _3dPagesPerRow * _3dPagesPerCol;

        // BFS flood fill on the 3D grid — only spread to same-color neighbors
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(hitPageIndex);
        visited.Add(hitPageIndex);

        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();

            // Decompose to grid coords
            int iz = idx / perLayer;
            int rem = idx % perLayer;
            int iy = rem / _3dPagesPerRow;
            int ix = rem % _3dPagesPerRow;

            // 6 neighbors (±x, ±y, ±z)
            int[] dx = { -1, 1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, -1, 1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, -1, 1 };

            for (int n = 0; n < 6; n++)
            {
                int nx = ix + dx[n], ny = iy + dy[n], nz = iz + dz[n];
                if (nx < 0 || nx >= _3dPagesPerRow || ny < 0 || ny >= _3dPagesPerCol || nz < 0 || nz >= _3dLayers)
                    continue;
                int ni = nz * perLayer + ny * _3dPagesPerRow + nx;
                if (ni >= _3dNumPages || ni >= _pageColors.Length) continue;
                if (visited.Contains(ni)) continue;
                if (_pageColors[ni] == targetColor)
                {
                    visited.Add(ni);
                    queue.Enqueue(ni);
                }
            }
        }

        bool isCtrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        if (!isCtrl) _selectedPageIndices.Clear();
        foreach (int idx in visited)
            _selectedPageIndices.Add(idx);

        UpdateHighlight3D();
    }

    /// <summary>Right-click: show context menu with color operations.</summary>
    private void Viewport3D_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        int hitPageIndex = HitTest3DPageIndex(e.GetPosition(Viewport3D));
        if (hitPageIndex < 0 || hitPageIndex >= _pageColors.Length) return;

        Color targetColor = _pageColors[hitPageIndex];
        double targetValue = GetPageValue(hitPageIndex);
        const double valueTolerance = 0.02; // for size matching on continuous 0–1 scale

        var menu = new System.Windows.Controls.ContextMenu();

        var selectColor = new System.Windows.Controls.MenuItem
        {
            Header = $"Select All This Color",
        };
        selectColor.Click += (s, ev) =>
        {
            _selectedPageIndices.Clear();
            for (int i = 0; i < _pageColors.Length && i < _3dNumPages; i++)
                if (_pageColors[i] == targetColor) _selectedPageIndices.Add(i);
            UpdateHighlight3D();
        };
        menu.Items.Add(selectColor);

        var selectSize = new System.Windows.Controls.MenuItem
        {
            Header = "Select All This Size",
        };
        selectSize.Click += (s, ev) =>
        {
            _selectedPageIndices.Clear();
            for (int i = 0; i < _3dNumPages; i++)
                if (Math.Abs(GetPageValue(i) - targetValue) <= valueTolerance)
                    _selectedPageIndices.Add(i);
            UpdateHighlight3D();
        };
        menu.Items.Add(selectSize);

        var selectSizeColor = new System.Windows.Controls.MenuItem
        {
            Header = "Select All This Size && Color",
        };
        selectSizeColor.Click += (s, ev) =>
        {
            _selectedPageIndices.Clear();
            for (int i = 0; i < _pageColors.Length && i < _3dNumPages; i++)
                if (_pageColors[i] == targetColor && Math.Abs(GetPageValue(i) - targetValue) <= valueTolerance)
                    _selectedPageIndices.Add(i);
            UpdateHighlight3D();
        };
        menu.Items.Add(selectSizeColor);

        var isolateColor = new System.Windows.Controls.MenuItem
        {
            Header = "Isolate This Color (hide others)",
        };
        isolateColor.Click += (s, ev) =>
        {
            // Mark pages with this color as selected, rebuild 3D dimming the rest
            _selectedPageIndices.Clear();
            for (int i = 0; i < _pageColors.Length && i < _3dNumPages; i++)
                if (_pageColors[i] == targetColor) _selectedPageIndices.Add(i);
            Rebuild3DIsolated(targetColor);
        };
        menu.Items.Add(isolateColor);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // ── Filter submenu ──────────────────────────────────────────────
        var filterColor = new System.Windows.Controls.MenuItem { Header = "Filter by This Color" };
        filterColor.Click += (s, ev) => Apply3DFilter(
            i => _pageColors.Length > i && _pageColors[i] == targetColor,
            $"Color #{targetColor.R:X2}{targetColor.G:X2}{targetColor.B:X2}");
        menu.Items.Add(filterColor);

        var filterSize = new System.Windows.Controls.MenuItem { Header = "Filter by This Size" };
        filterSize.Click += (s, ev) => Apply3DFilter(
            i => Math.Abs(GetPageValue(i) - targetValue) <= valueTolerance,
            $"Size ≈{targetValue:F2}");
        menu.Items.Add(filterSize);

        var filterSizeColor = new System.Windows.Controls.MenuItem { Header = "Filter by Size && Color" };
        filterSizeColor.Click += (s, ev) => Apply3DFilter(
            i => _pageColors.Length > i && _pageColors[i] == targetColor
                 && Math.Abs(GetPageValue(i) - targetValue) <= valueTolerance,
            $"Size+Color");
        menu.Items.Add(filterSizeColor);

        // Filter by region type (PE, ZIP, PNG, etc.)
        string? hitType = GetPageRegionType(hitPageIndex);
        if (hitType != null)
        {
            var filterType = new System.Windows.Controls.MenuItem { Header = $"Filter by Type: {hitType}" };
            filterType.Click += (s, ev) => Apply3DFilter(
                i => GetPageRegionType(i) == hitType,
                $"Type: {hitType}");
            menu.Items.Add(filterType);
        }

        if (_filterSet != null)
        {
            var clearFilter = new System.Windows.Controls.MenuItem
            {
                Header = "Clear Filter",
                FontWeight = System.Windows.FontWeights.Bold,
            };
            clearFilter.Click += (s, ev) => ClearFilter3D();
            menu.Items.Add(clearFilter);
        }

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Drill Into Selection — uses current selection OR the color just clicked
        var drillSel = new System.Windows.Controls.MenuItem { Header = "Drill Into Selection" };
        drillSel.Click += (s, ev) => DrillInto3DSelection();
        menu.Items.Add(drillSel);

        // Also offer drill into just this specific color
        var drillColor = new System.Windows.Controls.MenuItem { Header = "Drill Into This Color" };
        drillColor.Click += (s, ev) =>
        {
            _selectedPageIndices.Clear();
            for (int i = 0; i < _pageColors.Length && i < _3dNumPages; i++)
                if (_pageColors[i] == targetColor) _selectedPageIndices.Add(i);
            UpdateHighlight3D();
            DrillInto3DSelection();
        };
        menu.Items.Add(drillColor);

        // Extract Selection — save the selected pages' byte range to a file
        var extractSel = new System.Windows.Controls.MenuItem { Header = "Extract Selection" };
        extractSel.Click += (s, ev) => Extract3DSelection();
        menu.Items.Add(extractSel);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var clearSel = new System.Windows.Controls.MenuItem { Header = "Clear Selection" };
        clearSel.Click += (s, ev) =>
        {
            _selectedPageIndices.Clear();
            if (_filterSet != null) ClearFilter3D();
            else
            {
                double savedTheta = _camTheta, savedPhi = _camPhi, savedDist = _camDist;
                Build3DView();
                _camTheta = savedTheta; _camPhi = savedPhi; _camDist = savedDist;
                UpdateCamera(0, 0, 0);
            }
        };
        menu.Items.Add(clearSel);

        menu.IsOpen = true;
    }

    /// <summary>Rebuild 3D with non-matching pages dimmed to near-transparent dark.</summary>
    /// <summary>Drill into the bounding range of the current 3D page selection.</summary>
    private void DrillInto3DSelection()
    {
        if (_selectedPageIndices.Count == 0 || _filePath == null || _result == null)
        {
            MessageBox.Show("No pages selected. Click, double-click, or use Select Color first.",
                "Drill In", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int minPage = _selectedPageIndices.Min();
        int maxPage = _selectedPageIndices.Max();
        long offset = (long)minPage * _result.EntropyBlockSize;
        long endOffset = Math.Min((long)(maxPage + 1) * _result.EntropyBlockSize, _result.FileSize);
        long size = endOffset - offset;

        if (size <= 0) return;

        string label = _selectedPageIndices.Count == 1
            ? $"3d_page_{minPage}"
            : $"3d_pages_{minPage}-{maxPage}";

        DrillIntoRange(offset, size, label);
    }

    /// <summary>Extract the bounding byte range of the current 3D page selection to a file.</summary>
    private void Extract3DSelection()
    {
        if (_selectedPageIndices.Count == 0 || _filePath == null || _result == null)
        {
            MessageBox.Show("No pages selected. Click, double-click, or use Select Color first.",
                "Extract", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int minPage = _selectedPageIndices.Min();
        int maxPage = _selectedPageIndices.Max();
        long offset = (long)minPage * _result.EntropyBlockSize;
        long endOffset = Math.Min((long)(maxPage + 1) * _result.EntropyBlockSize, _result.FileSize);
        long size = endOffset - offset;

        if (size <= 0) return;

        string defaultName = _selectedPageIndices.Count == 1
            ? $"3d_page_{minPage}_0x{offset:X}.bin"
            : $"3d_pages_{minPage}-{maxPage}_0x{offset:X}_{size}B.bin";

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = defaultName,
            Filter = "Raw binary (*.bin)|*.bin|All files|*.*",
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                CarveEngine.ExtractRange(_filePath, offset, size, dlg.FileName);
                TxtStatus.Text = $"Extracted 3D selection: pages {minPage}–{maxPage} ({size:N0} bytes from 0x{offset:X8})";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"3D selection extraction failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>Apply a filter: only pages matching the predicate show at full size/color; others ghost.</summary>
    private void Apply3DFilter(Func<int, bool> predicate, string label)
    {
        if (_result == null || _3dNumPages <= 0) return;

        // Compute page colors first if needed (filter may be applied before colors are cached)
        if (_pageColors.Length < _3dNumPages)
        {
            _pageColors = new Color[_3dNumPages];
            for (int i = 0; i < _3dNumPages; i++) _pageColors[i] = GetPageColor(i);
        }

        _filterSet = new HashSet<int>();
        _filterLabel = label;
        for (int i = 0; i < _3dNumPages; i++)
            if (predicate(i)) _filterSet.Add(i);

        // Rebuild with filter applied — preserves camera
        double savedTheta = _camTheta, savedPhi = _camPhi, savedDist = _camDist;
        Build3DView();
        _camTheta = savedTheta; _camPhi = savedPhi; _camDist = savedDist;
        UpdateCamera(0, 0, 0);

        TxtStatus.Text = $"3D Filter: {label}  ({_filterSet.Count} of {_3dNumPages} pages)";
    }

    /// <summary>Clear the 3D filter and restore all pages to full visibility.</summary>
    private void ClearFilter3D()
    {
        _filterSet = null;
        _filterLabel = "";
        _selectedPageIndices.Clear();

        double savedTheta = _camTheta, savedPhi = _camPhi, savedDist = _camDist;
        Build3DView();
        _camTheta = savedTheta; _camPhi = savedPhi; _camDist = savedDist;
        UpdateCamera(0, 0, 0);

        TxtStatus.Text = "3D filter cleared";
    }

    /// <summary>Isolate a specific color — now delegates to the unified filter system.</summary>
    private void Rebuild3DIsolated(Color isolateColor)
    {
        Apply3DFilter(
            i => _pageColors.Length > i && _pageColors[i] == isolateColor,
            $"Isolate #{isolateColor.R:X2}{isolateColor.G:X2}{isolateColor.B:X2}");
    }

    // ── Utilities ───────────────────────────────────────────────────────

    private static string GetDefaultExtension(string fileType) => fileType switch
    {
        "PE/EXE"  => ".exe",
        "PE/DLL"  => ".dll",
        "ELF"     => ".elf",
        "Mach-O"  => ".macho",
        "ZIP"     => ".zip",
        "GZIP"    => ".gz",
        "7Z"      => ".7z",
        "RAR"     => ".rar",
        "CAB"     => ".cab",
        "XZ"      => ".xz",
        "BZIP2"   => ".bz2",
        "ZSTD"    => ".zst",
        "TAR"     => ".tar",
        "PNG"     => ".png",
        "JPEG"    => ".jpg",
        "GIF"     => ".gif",
        "BMP"     => ".bmp",
        "ICO"     => ".ico",
        "TIFF"    => ".tiff",
        "WEBP"    => ".webp",
        "PDF"     => ".pdf",
        "XML"     => ".xml",
        "HTML"    => ".html",
        "RTF"     => ".rtf",
        "OGG"     => ".ogg",
        "FLAC"    => ".flac",
        "MP3"     => ".mp3",
        "SQLite"  => ".sqlite",
        "OLE/DOC" => ".doc",
        "DER/CER" => ".cer",
        "CLASS"   => ".class",
        "DEX"     => ".dex",
        "PEM"     => ".pem",
        _         => ".bin",
    };
}
