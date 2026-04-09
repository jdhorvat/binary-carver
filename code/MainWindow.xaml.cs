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
using IOPath = System.IO.Path;

namespace BinaryCarver;

public partial class MainWindow : Window
{
    private CarveResult? _result;
    private string? _filePath;
    private byte[]? _fileData;   // raw bytes cached for dot plot + entropy recompute
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

    // Manual region state — user-defined regions built from selected blocks
    private List<ManualRegion> _manualRegions = [];
    private static readonly Color[] _regionPalette =
    [
        Color.FromRgb(0xFF, 0x6F, 0x00), // Orange
        Color.FromRgb(0x00, 0xBF, 0xA5), // Teal
        Color.FromRgb(0xE9, 0x1E, 0x63), // Pink
        Color.FromRgb(0x29, 0x79, 0xFF), // Blue
        Color.FromRgb(0xAA, 0x00, 0xFF), // Purple
        Color.FromRgb(0xFF, 0xD6, 0x00), // Yellow
        Color.FromRgb(0x00, 0xE6, 0x76), // Green
        Color.FromRgb(0xFF, 0x17, 0x44), // Red
    ];

    // Right-panel tile-overview mode.  True = show all 6 visualizations as a tiled
    // dashboard; false = show a single full-panel visualization (zoom/pan enabled).
    // Default is true so the overview loads on startup.
    private bool _tileMode = true;

    // Right-panel zoom/pan state (applies to the PlotContainer image)
    private double _plotZoom   = 1.0;
    private double _plotPanX   = 0.0;
    private double _plotPanY   = 0.0;
    private bool   _plotDragging;
    private System.Windows.Point _plotDragStart;
    private System.Windows.Point _plotDragOrigin;

    // Right-panel visualization parameters (controlled by the parameter strip)
    private int  _vizWrapWidth = 256;  // fold/wrap width for heatmap, byte-pair, autocorrelation
    private int  _vizStride    = 1;    // byte-pair plot: gap between the two paired bytes
    private int  _vizMaxLag    = 256;  // autocorrelation: maximum lag to compute
    private bool _vizLogScale  = true; // histogram/waveform: log vs linear

    // Right-panel rubber-band selection state (waveform + heat-map modes)
    private bool   _plotSelecting;
    private bool   _plotSelDeselect;   // Shift → deselect mode
    private System.Windows.Point _plotSelStart;


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

        // 3D mouse interaction — wired on Panel3D (the Border) so you can click
        // anywhere in the 3D area to start rotating, not just on geometry
        Panel3D.MouseLeftButtonDown += (s, ev) =>
        {
            if (ev.ClickCount == 2)
            {
                Viewport3D_DoubleClick(ev.GetPosition(Viewport3D));
                return;
            }
            _isDragging3D = true;
            _lastMousePos3D = ev.GetPosition(Viewport3D);
            _3dMouseDownPos = _lastMousePos3D;
            Panel3D.CaptureMouse();
        };
        Panel3D.MouseLeftButtonUp += (s, ev) =>
        {
            bool wasDrag = _isDragging3D && (ev.GetPosition(Viewport3D) - _3dMouseDownPos).Length > 4;
            _isDragging3D = false;
            Panel3D.ReleaseMouseCapture();
            if (!wasDrag) Viewport3D_LeftClick(ev.GetPosition(Viewport3D), ev);
        };
        // Right-click: context menu for color operations
        Panel3D.MouseRightButtonUp += (s, ev) => Viewport3D_RightClick(s, (System.Windows.Input.MouseButtonEventArgs)ev);
        Panel3D.MouseMove += Viewport3D_MouseMove;
        Panel3D.MouseWheel += Viewport3D_MouseWheel;

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

    /// <summary>Redraw all canvases (byte map + entropy tracks + dot plot). Called on window resize.</summary>
    private void RedrawAllVisuals(CarveResult r)
    {
        DrawByteMap(r);
        DrawEntropyHeatmap(r);
        DrawEntropyTrack(NibbleEntropyCanvas, r.NibbleEntropyMap, 4.0, r);
        DrawEntropyTrack(BitEntropyCanvas, r.BitEntropyMap, 1.0, r);
        DrawEntropyTrack(BigramEntropyCanvas, r.BigramEntropyMap, 16.0, r);
        DrawDivergenceTrack(r);
        DrawDeltaEntropyTrack(r);
        UpdateRightPanel();
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

    /// <summary>Global Escape key: clears selection from anywhere in the app.</summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (_selectedPageIndices.Count > 0)
            {
                _selectedPageIndices.Clear();
                UpdateSelectionVisuals();
                UpdateRightPanel();
                if (TxtStatus != null) TxtStatus.Text = "Selection cleared";
                e.Handled = true;
            }
            else if (!_tileMode)
            {
                // No selection → return to tile overview
                EnterTileMode();
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Global middle-click: zoom extents (reset zoom/pan to fit) in whichever view
    /// is under the cursor — 2D plot panel or 3D viewport.
    /// </summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        // Check if the click is over the 2D plot container
        if (PlotContainer.Visibility == Visibility.Visible &&
            PlotContainer.IsMouseOver)
        {
            ResetPlotTransform();
            e.Handled = true;
            return;
        }

        // Check if the click is over the 3D viewport
        if (Panel3D != null && Panel3D.Visibility == Visibility.Visible &&
            Panel3D.IsMouseOver)
        {
            Reset3DCamera();
            e.Handled = true;
        }
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

        // Clear manual regions from the previous file
        _manualRegions.Clear();
        RefreshManualRegionsUI();

        try
        {
            bool recursive = ChkRecursive.IsChecked == true;
            CarveEngine.SetCustomSignatures(_customSigs);
            _result = CarveEngine.Analyze(filePath, recursive);

            // Cache raw bytes for dot plot and live entropy recompute
            try
            {
                _fileData = _result.FileSize > 64 * 1024 * 1024
                    ? ReadFileBytes(filePath, (int)_result.FileSize)
                    : File.ReadAllBytes(filePath);
            }
            catch { _fileData = null; }
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
        BtnExportReport.IsEnabled = true;

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

    /// <summary>
    /// Byte-pair dot plot: builds a 256×256 WriteableBitmap where pixel (x,y) represents
    /// the log-normalized frequency of the consecutive byte pair (data[i]=y, data[i+1]=x).
    /// Color scale: dark=zero, deep blue=rare, cyan=moderate, yellow=common, white=dominant.
    /// Typical fingerprints:
    ///   Text/ASCII  → bright L-shape along left column and top row (null-terminated runs + printable chars)
    ///   Compressed  → uniform scatter across all 256×256
    ///   Code (x86)  → structured clusters around instruction byte ranges
    ///   Null fill   → single bright dot at (0,0)
    /// </summary>
    /// <summary>
    /// Render the 256×256 byte-pair dot plot.
    /// When highlightBlocks is non-empty, the global file data is dimmed to a dark background
    /// and the selected blocks' byte pairs are overlaid in a bright orange→yellow→white scale,
    /// making the fingerprint of the selection immediately visible against the dim context.
    /// </summary>
    private void DrawDotPlot(byte[]? data, HashSet<int>? highlightBlocks = null)
    {
        if (data == null || data.Length < 2)
        {
            DotPlotImage.Source = null;
            if (TxtDotPlotInfo != null) TxtDotPlotInfo.Text = "no data";
            return;
        }

        const int size = 256;
        int stride = Math.Max(1, _vizStride);   // gap between paired bytes
        int wrapW  = Math.Max(1, _vizWrapWidth); // fold period — pairs don't cross wrap boundaries

        // ── Global frequency (whole file) ──────────────────────────────
        var freq = new long[size * size];
        int pairCount = data.Length - stride;
        for (int i = 0; i < pairCount; i++)
        {
            // Skip pairs whose second byte falls in the next wrap window
            if ((i % wrapW) + stride >= wrapW) continue;
            freq[(data[i] << 8) | data[i + stride]]++;
        }

        long maxFreq = 1;
        foreach (long f in freq) if (f > maxFreq) maxFreq = f;
        double logMax = Math.Log(maxFreq + 1);

        // ── Highlight frequency (selected blocks only) ──────────────────
        long[]? hlFreq = null;
        int hlPairCount = 0;
        if (highlightBlocks != null && highlightBlocks.Count > 0 && _result != null)
        {
            hlFreq = new long[size * size];
            int bs = _result.EntropyBlockSize;
            foreach (int bi in highlightBlocks)
            {
                long start = (long)bi * bs;
                long end   = Math.Min(start + bs, data.Length - stride);
                for (long i = start; i < end; i++)
                {
                    if ((i % wrapW) + stride >= wrapW) continue;
                    hlFreq[(data[i] << 8) | data[i + stride]]++;
                    hlPairCount++;
                }
            }
        }

        long hlMax = 1;
        if (hlFreq != null)
            foreach (long f in hlFreq) if (f > hlMax) hlMax = f;
        double hlLogMax = Math.Log(hlMax + 1);

        bool hasHighlight = hlFreq != null;

        // ── Build pixel buffer ─────────────────────────────────────────
        var pixels = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int cell = (y << 8) | x;
                double t = freq[cell] > 0 ? Math.Log(freq[cell] + 1) / logMax : 0;

                byte r, g, b;

                if (hasHighlight)
                {
                    // Dim the global background to ~20% of normal brightness
                    double dim = t * 0.20;
                    if (dim < 0.001)
                    {
                        r = 0x08; g = 0x08; b = 0x14;
                    }
                    else
                    {
                        r = (byte)(dim * 0x30);
                        g = (byte)(dim * 0x40);
                        b = (byte)(0x18 + dim * 0x80);
                    }

                    // Overlay selected blocks in orange → yellow → white
                    double ht = hlFreq![cell] > 0 ? Math.Log(hlFreq[cell] + 1) / hlLogMax : 0;
                    if (ht > 0.001)
                    {
                        if (ht < 0.33)
                        {
                            double s = ht / 0.33;
                            r = (byte)(0xC0 + s * 0x3F);
                            g = (byte)(s * 0x80);
                            b = 0;
                        }
                        else if (ht < 0.66)
                        {
                            double s = (ht - 0.33) / 0.33;
                            r = 0xFF;
                            g = (byte)(0x80 + s * 0x7F);
                            b = 0;
                        }
                        else
                        {
                            double s = (ht - 0.66) / 0.34;
                            r = 0xFF;
                            g = 0xFF;
                            b = (byte)(s * 0xFF);
                        }
                    }
                }
                else
                {
                    // Normal full-color rendering (no selection)
                    if (t < 0.001)
                    {
                        r = 0x14; g = 0x14; b = 0x1E;
                    }
                    else if (t < 0.25)
                    {
                        double s = t / 0.25;
                        r = 0;
                        g = (byte)(s * 0x40);
                        b = (byte)(0x60 + s * 0x9F);
                    }
                    else if (t < 0.50)
                    {
                        double s = (t - 0.25) / 0.25;
                        r = 0;
                        g = (byte)(0x40 + s * 0xBF);
                        b = (byte)(0xFF - s * 0x80);
                    }
                    else if (t < 0.75)
                    {
                        double s = (t - 0.50) / 0.25;
                        r = (byte)(s * 0xFF);
                        g = 0xFF;
                        b = (byte)(0x7F - s * 0x7F);
                    }
                    else
                    {
                        double s = (t - 0.75) / 0.25;
                        r = 0xFF;
                        g = 0xFF;
                        b = (byte)(s * 0xFF);
                    }
                }

                int idx = (y * size + x) * 4;
                pixels[idx + 0] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = 255;
            }
        }

        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
            size, size, 96, 96,
            System.Windows.Media.PixelFormats.Bgr32, null);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        SetPlotBitmap(bmp);

        // ── Info label ─────────────────────────────────────────────────
        int uniquePairs = 0;
        foreach (long f in freq) if (f > 0) uniquePairs++;

        if (TxtDotPlotInfo != null)
        {
            if (hasHighlight)
            {
                int hlUnique = 0;
                foreach (long f in hlFreq!) if (f > 0) hlUnique++;
                TxtDotPlotInfo.Text =
                    $"Selection: {hlUnique:N0} unique pairs  ·  {hlPairCount:N0} pairs  ·  {highlightBlocks!.Count} blocks";
            }
            else
            {
                TxtDotPlotInfo.Text = $"{uniquePairs:N0} / 65536 pairs  ·  {pairCount:N0} total  ·  stride {stride}";
            }
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
        int totalBlocks = _result.EntropyMap.Length;
        int blockIndex = (int)(clickX / canvasWidth * totalBlocks);
        blockIndex = Math.Clamp(blockIndex, 0, totalBlocks - 1);

        bool isShift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        bool isCtrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;

        // Double-click = select all blocks with same/similar color on this canvas
        if (e.ClickCount == 2)
        {
            EntropyCanvas_SelectByColor(canvas, blockIndex);
            return;
        }

        // Shift-click = range selection from last inspected block
        if (isShift && _inspectedBlockIndex >= 0)
        {
            int lo = Math.Min(_inspectedBlockIndex, blockIndex);
            int hi = Math.Max(_inspectedBlockIndex, blockIndex);
            _rangeStartBlock = lo;
            _rangeEndBlock = hi;

            // Also populate _selectedPageIndices for the range
            if (!isCtrl) _selectedPageIndices.Clear();
            for (int p = lo; p <= hi; p++)
                _selectedPageIndices.Add(p);

            UpdateRangeDisplay();
            if (_selectedPageIndices.Count > 1)
            {
                ShowMultiBlockInspector(_selectedPageIndices);
                if (Panel3D.Visibility == Visibility.Visible) UpdateHighlight3D();
                else UpdateDotPlotHighlight();
            }
            UpdateExtractSelectedState();
            return;
        }

        // Ctrl-click = toggle block in multi-selection
        if (isCtrl)
        {
            if (_selectedPageIndices.Contains(blockIndex))
                _selectedPageIndices.Remove(blockIndex);
            else
                _selectedPageIndices.Add(blockIndex);

            if (_selectedPageIndices.Count > 1)
            {
                ShowMultiBlockInspector(_selectedPageIndices);
            }
            else if (_selectedPageIndices.Count == 1)
            {
                ShowBlockInspector(_selectedPageIndices.First());
            }
            if (Panel3D.Visibility == Visibility.Visible) UpdateHighlight3D();
            else UpdateDotPlotHighlight();
            UpdateExtractSelectedState();
            return;
        }

        // Normal click = inspect single block, reset range & multi-select
        _rangeStartBlock = -1;
        _rangeEndBlock = -1;
        _selectedPageIndices.Clear();
        _selectedPageIndices.Add(blockIndex);
        ShowBlockInspector(blockIndex);
        if (Panel3D.Visibility == Visibility.Visible) UpdateHighlight3D();
        else UpdateDotPlotHighlight();
        UpdateExtractSelectedState();
    }

    /// <summary>Get the entropy data array and max entropy value for a given canvas.</summary>
    private (double[]? map, double maxEntropy) GetEntropyDataForCanvas(System.Windows.Controls.Canvas canvas)
    {
        if (_result == null) return (null, 1.0);

        if (canvas == EntropyCanvas)        return (_result.EntropyMap, 8.0);
        if (canvas == NibbleEntropyCanvas)  return (_result.NibbleEntropyMap, 4.0);
        if (canvas == BitEntropyCanvas)     return (_result.BitEntropyMap, 1.0);
        if (canvas == BigramEntropyCanvas)  return (_result.BigramEntropyMap, 16.0);
        if (canvas == DivergenceCanvas)     return (_result.DivergenceMap, _result.DivergenceMap is { Length: > 0 } d ? d.Max() : 1.0);
        if (canvas == DeltaEntropyCanvas)   return (_result.DeltaEntropyMap, _result.DeltaEntropyMap is { Length: > 0 } dt ? dt.Max() : 1.0);

        return (null, 1.0);
    }

    /// <summary>Select all blocks whose entropy color closely matches the clicked block's color.</summary>
    private void EntropyCanvas_SelectByColor(System.Windows.Controls.Canvas canvas, int clickedBlock)
    {
        if (_result == null) return;

        var (map, maxEntropy) = GetEntropyDataForCanvas(canvas);
        if (map == null || map.Length == 0) return;

        // Get the clicked block's normalized value
        double clickedNorm = Math.Clamp(map[clickedBlock] / maxEntropy, 0, 1);

        // Select all blocks within a tolerance band (±0.05 normalized)
        const double tolerance = 0.05;
        _selectedPageIndices.Clear();

        for (int i = 0; i < map.Length; i++)
        {
            double norm = Math.Clamp(map[i] / maxEntropy, 0, 1);
            if (Math.Abs(norm - clickedNorm) <= tolerance)
                _selectedPageIndices.Add(i);
        }

        if (_selectedPageIndices.Count > 1)
        {
            ShowMultiBlockInspector(_selectedPageIndices);
        }
        else if (_selectedPageIndices.Count == 1)
        {
            ShowBlockInspector(_selectedPageIndices.First());
        }

        if (Panel3D.Visibility == Visibility.Visible) UpdateHighlight3D();
        UpdateExtractSelectedState();
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

    private void ShowBlockInspector(int blockIndex, bool updateFrom3D = true, bool keepDetailPanel = false)
    {
        if (_result == null) return;

        // If there are multiple pages selected, show multi-block view instead
        if (_selectedPageIndices.Count > 1 && !updateFrom3D)
        {
            ShowMultiBlockInspector(_selectedPageIndices, keepDetailPanel);
            return;
        }

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
        if (!keepDetailPanel)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            GridRegions.SelectedItem = null;
        }
        UpdateExtractSelectedState();

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

    /// <summary>Show the block inspector for multiple selected blocks — aggregated entropy + concatenated hex.</summary>
    private void ShowMultiBlockInspector(HashSet<int> pageIndices, bool keepDetailPanel = false)
    {
        if (_result == null || pageIndices.Count == 0) return;

        var sorted = pageIndices.OrderBy(p => p).ToList();
        int blockSize = _result.EntropyBlockSize;
        int blockCount = sorted.Count;
        long totalBytes = (long)blockCount * blockSize;

        // Clamp last block to file size
        long lastBlockEnd = Math.Min((long)(sorted[^1] + 1) * blockSize, _result.FileSize);
        long firstOffset = (long)sorted[0] * blockSize;
        long spanBytes = lastBlockEnd - firstOffset;

        _inspectedBlockIndex = sorted[0]; // track first block for extract/drill

        // Count contiguous spans
        int spanCount = 1;
        for (int i = 1; i < sorted.Count; i++)
            if (sorted[i] != sorted[i - 1] + 1) spanCount++;

        string contiguity = spanCount == 1 ? "contiguous" : $"{spanCount} spans";
        TxtBlockInfo.Text = $"{blockCount} blocks selected  |  {totalBytes:N0} bytes ({contiguity})  |  Range: 0x{firstOffset:X8} – 0x{lastBlockEnd:X8}";

        // Aggregate entropy: min / avg / max across selected blocks
        double byteMin = double.MaxValue, byteMax = 0, byteSum = 0;
        double nibMin = double.MaxValue, nibMax = 0, nibSum = 0;
        double bitMin = double.MaxValue, bitMax = 0, bitSum = 0;
        double bigMin = double.MaxValue, bigMax = 0, bigSum = 0;
        double divMin = double.MaxValue, divMax = 0, divSum = 0;
        double dltMin = double.MaxValue, dltMax = 0, dltSum = 0;
        int validCount = 0;

        foreach (int bi in sorted)
        {
            if (bi >= _result.EntropyMap.Length) continue;
            validCount++;
            double bv = _result.EntropyMap[bi];
            byteMin = Math.Min(byteMin, bv); byteMax = Math.Max(byteMax, bv); byteSum += bv;

            if (bi < _result.NibbleEntropyMap.Length)
            { double v = _result.NibbleEntropyMap[bi]; nibMin = Math.Min(nibMin, v); nibMax = Math.Max(nibMax, v); nibSum += v; }
            if (bi < _result.BitEntropyMap.Length)
            { double v = _result.BitEntropyMap[bi]; bitMin = Math.Min(bitMin, v); bitMax = Math.Max(bitMax, v); bitSum += v; }
            if (bi < _result.BigramEntropyMap.Length)
            { double v = _result.BigramEntropyMap[bi]; bigMin = Math.Min(bigMin, v); bigMax = Math.Max(bigMax, v); bigSum += v; }
            if (bi < _result.DivergenceMap.Length)
            { double v = _result.DivergenceMap[bi]; divMin = Math.Min(divMin, v); divMax = Math.Max(divMax, v); divSum += v; }
            if (bi < _result.DeltaEntropyMap.Length)
            { double v = _result.DeltaEntropyMap[bi]; dltMin = Math.Min(dltMin, v); dltMax = Math.Max(dltMax, v); dltSum += v; }
        }

        if (validCount > 0)
        {
            string fmtRange(double min, double avg, double max) => $"{min:F2}–{max:F2} (avg {avg:F2})";
            TxtBlockEntropy.Text =
                $"Byte: {fmtRange(byteMin, byteSum / validCount, byteMax)}/8.0  |  " +
                $"Nibble: {fmtRange(nibMin, nibSum / validCount, nibMax)}/4.0  |  " +
                $"Bit: {fmtRange(bitMin, bitSum / validCount, bitMax)}/1.0\n" +
                $"Bigram: {fmtRange(bigMin, bigSum / validCount, bigMax)}/16.0  |  " +
                $"Divergence: {fmtRange(divMin, divSum / validCount, divMax)}  |  " +
                $"Delta: {fmtRange(dltMin, dltSum / validCount, dltMax)}";
        }
        else
        {
            TxtBlockEntropy.Text = "(no entropy data for selected blocks)";
        }

        // Classify what's in the selection: regions + gaps
        var regionTypes = new HashSet<string>();
        var gapTypes = new HashSet<string>();
        foreach (int bi in sorted)
        {
            long off = (long)bi * blockSize;
            var region = _result.Regions.FirstOrDefault(r => r.Depth == 0 && off >= r.Offset && off < r.Offset + r.Size);
            if (region != null) regionTypes.Add(region.FileType);
            var gap = _result.Gaps.FirstOrDefault(g => off >= g.Offset && off < g.Offset + g.Size);
            if (gap != null) gapTypes.Add(gap.Classification.ToString());
        }
        if (regionTypes.Count > 0)
            TxtBlockEntropy.Text += $"\nRegions: {string.Join(", ", regionTypes)}";
        if (gapTypes.Count > 0)
            TxtBlockEntropy.Text += $"  |  Gaps: {string.Join(", ", gapTypes)}";

        // Check if any belong to manual regions
        var manualNames = _manualRegions
            .Where(mr => sorted.Any(bi => mr.PageIndices.Contains(bi)))
            .Select(mr => mr.Name)
            .ToList();
        if (manualNames.Count > 0)
            TxtBlockEntropy.Text += $"\nManual: {string.Join(", ", manualNames)}";

        // Concatenated hex preview — show first bytes of each block, up to ~512 bytes total
        TxtBlockHexPreview.Text = "";
        if (_filePath != null && File.Exists(_filePath))
        {
            try
            {
                using var fs = File.OpenRead(_filePath);
                var sb = new System.Text.StringBuilder();
                int totalHexBytes = 0;
                const int maxHexBytes = 512;
                const int bytesPerBlock = 64; // show first 64 bytes of each block

                foreach (int bi in sorted)
                {
                    if (totalHexBytes >= maxHexBytes) { sb.AppendLine($"... ({blockCount - sorted.IndexOf(bi)} more blocks)"); break; }

                    long off = (long)bi * blockSize;
                    if (off >= fs.Length) continue;

                    fs.Seek(off, SeekOrigin.Begin);
                    int toRead = (int)Math.Min(bytesPerBlock, Math.Min(blockSize, fs.Length - off));
                    byte[] hexData = new byte[toRead];
                    fs.Read(hexData, 0, toRead);

                    sb.AppendLine($"── Block {bi} @ 0x{off:X8} ──");
                    sb.Append(FormatHexDump(hexData, off));
                    totalHexBytes += toRead;
                }

                TxtBlockHexPreview.Text = sb.ToString();
            }
            catch { TxtBlockHexPreview.Text = "(could not read block data)"; }
        }

        // Range display for contiguous selections
        if (spanCount == 1)
        {
            _rangeStartBlock = sorted[0];
            _rangeEndBlock = sorted[^1];
            TxtBlockRange.Text = $"RANGE: Blocks {_rangeStartBlock}–{_rangeEndBlock} ({blockCount} blocks)  |  0x{firstOffset:X8} – 0x{lastBlockEnd:X8}  |  {spanBytes:N0} bytes";
            TxtBlockRange.Visibility = Visibility.Visible;
            BtnDrillInRange.IsEnabled = true;
        }
        else
        {
            TxtBlockRange.Visibility = Visibility.Collapsed;
            BtnDrillInRange.IsEnabled = false;
        }

        BlockInspectorPanel.Visibility = Visibility.Visible;
        if (!keepDetailPanel)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            GridRegions.SelectedItem = null;
        }
        UpdateExtractSelectedState();
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
        UpdateExtractSelectedState();
    }

    // ── Region selection ────────────────────────────────────────────────

    private void GridRegions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridRegions.SelectedItem is not CarvedRegion region)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            BtnOpenInLens.IsEnabled = false;
            UpdateExtractSelectedState();
            return;
        }

        DetailPanel.Visibility = Visibility.Visible;

        // Also update block inspector for the first block of this region
        if (_result != null && region.Size > 0)
        {
            int blockSize = _result.EntropyBlockSize;
            int blockIndex = blockSize > 0 ? (int)(region.Offset / blockSize) : 0;
            blockIndex = Math.Clamp(blockIndex, 0, Math.Max(0, _result.EntropyMap.Length - 1));
            ShowBlockInspector(blockIndex, updateFrom3D: true, keepDetailPanel: true);
        }

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

    /// <summary>Universal extract: works for carved regions, 3D page selection, block inspector, or manual regions.</summary>
    private void BtnExtractSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null || _result == null) return;

        // Priority 1: Carved region selected in grid
        if (GridRegions.SelectedItem is CarvedRegion region)
        {
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
            return;
        }

        // Priority 2: 3D page selection (covers manual region highlights too)
        if (_selectedPageIndices.Count > 0)
        {
            Extract3DSelection();
            return;
        }

        // Priority 3: Block inspector has an active block
        if (_inspectedBlockIndex >= 0)
        {
            long offset = (long)_inspectedBlockIndex * _result.EntropyBlockSize;
            long size = _rangeEndBlock >= 0
                ? (long)(_rangeEndBlock + 1) * _result.EntropyBlockSize - offset
                : _result.EntropyBlockSize;
            size = Math.Min(size, _result.FileSize - offset);
            if (size <= 0) return;

            string label = _rangeEndBlock >= 0
                ? $"blocks_{_rangeStartBlock}-{_rangeEndBlock}_0x{offset:X}"
                : $"block_{_inspectedBlockIndex}_0x{offset:X}";

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"{label}.bin",
                Filter = "Raw binary (*.bin)|*.bin|All files|*.*",
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    CarveEngine.ExtractRange(_filePath, offset, size, dlg.FileName);
                    TxtStatus.Text = $"Extracted {label} ({size:N0} bytes)";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Extraction failed:\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            return;
        }
    }

    /// <summary>Enable/disable the Extract Selected button based on whether anything is selected anywhere.</summary>
    private void UpdateExtractSelectedState()
    {
        if (_result == null) { BtnExtractSelected.IsEnabled = false; return; }

        bool hasSelection =
            GridRegions.SelectedItem is CarvedRegion ||
            _selectedPageIndices.Count > 0 ||
            _inspectedBlockIndex >= 0;

        BtnExtractSelected.IsEnabled = hasSelection;
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
        if (_result == null || CboBlockSize.SelectedItem == null) return;
        if (CboBlockSize.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out int blockSize))
        {
            try
            {
                // Use cached bytes; fall back to re-reading if not yet cached
                byte[] data = _fileData
                    ?? (_filePath != null
                        ? (_result.FileSize > 64 * 1024 * 1024
                            ? ReadFileBytes(_filePath, (int)_result.FileSize)
                            : File.ReadAllBytes(_filePath))
                        : throw new InvalidOperationException("No file data available"));

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

    private void SliderSpacing_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_result == null || TxtSpacingValue == null) return;
        _3dSpacing = e.NewValue;
        TxtSpacingValue.Text = _3dSpacing.ToString("F1");
        if (Panel3D.Visibility == Visibility.Visible)
            Rebuild3DPreservingCamera();
    }

    private bool _3dColumnVisible = true;

    private void Btn3DToggle_Click(object sender, RoutedEventArgs e)
    {
        _3dColumnVisible = !_3dColumnVisible;
        if (_3dColumnVisible)
        {
            Col3D.Width = new GridLength(1, GridUnitType.Star);
            Col3D.MinWidth = 180;
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

    /// <summary>Check if a page is assigned to any manual region.</summary>
    private bool IsPageInManualRegion(int pageIndex)
    {
        return _manualRegions.Any(mr => mr.PageIndices.Contains(pageIndex));
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

        // Compute base color from entropy/mode, then blend with manual region color if assigned
        Color baseColor = Colors.Gray;
        bool hasBase = false;

        switch (colorMode)
        {
            case 0: // Byte Entropy
                if (entropyBlock < _result.EntropyMap.Length)
                { baseColor = NormalizedEntropyToColor(_result.EntropyMap[entropyBlock] / 8.0); hasBase = true; }
                break;
            case 1: // Nibble
                if (entropyBlock < _result.NibbleEntropyMap.Length)
                { baseColor = NormalizedEntropyToColor(_result.NibbleEntropyMap[entropyBlock] / 4.0); hasBase = true; }
                break;
            case 2: // Bit
                if (entropyBlock < _result.BitEntropyMap.Length)
                { baseColor = NormalizedEntropyToColor(_result.BitEntropyMap[entropyBlock] / 1.0); hasBase = true; }
                break;
            case 3: // Bigram
                if (entropyBlock < _result.BigramEntropyMap.Length)
                { baseColor = NormalizedEntropyToColor(_result.BigramEntropyMap[entropyBlock] / 16.0); hasBase = true; }
                break;
            case 4: // Divergence
                if (entropyBlock < _result.DivergenceMap.Length)
                {
                    double maxDiv = 0;
                    foreach (double d in _result.DivergenceMap) if (d > maxDiv) maxDiv = d;
                    if (maxDiv < 0.001) maxDiv = 0.5;
                    double t = Math.Clamp(_result.DivergenceMap[entropyBlock] / maxDiv, 0, 1);
                    baseColor = t < 0.5
                        ? Lerp(Color.FromRgb(0x1A, 0x1A, 0x1A), Color.FromRgb(0xFF, 0x6F, 0x00), t / 0.5)
                        : Lerp(Color.FromRgb(0xFF, 0x6F, 0x00), Colors.White, (t - 0.5) / 0.5);
                    hasBase = true;
                }
                break;
            case 5: // Delta
                if (entropyBlock < _result.DeltaEntropyMap.Length)
                {
                    double maxD = 0;
                    foreach (double d in _result.DeltaEntropyMap) if (d > maxD) maxD = d;
                    if (maxD < 0.01) maxD = 1.0;
                    double t = Math.Clamp(_result.DeltaEntropyMap[entropyBlock] / maxD, 0, 1);
                    baseColor = t < 0.5
                        ? Lerp(Color.FromRgb(0x1A, 0x1A, 0x1A), Color.FromRgb(0xFF, 0x6F, 0x00), t / 0.5)
                        : Lerp(Color.FromRgb(0xFF, 0x6F, 0x00), Colors.White, (t - 0.5) / 0.5);
                    hasBase = true;
                }
                break;
            case 6: // Region Type
            {
                var region = _result.Regions.FirstOrDefault(r =>
                    r.Depth == 0 && pageOffset >= r.Offset && pageOffset < r.Offset + r.Size);
                baseColor = region != null ? GetRegionColor(region.FileType) : Color.FromRgb(0x30, 0x30, 0x30);
                hasBase = true;
                break;
            }
            case 7: // Gap Class
            {
                var gap = _result.Gaps.FirstOrDefault(g =>
                    pageOffset >= g.Offset && pageOffset < g.Offset + g.Size);
                if (gap != null)
                    baseColor = GetGapColor(gap.Classification);
                else
                {
                    var reg = _result.Regions.FirstOrDefault(r =>
                        r.Depth == 0 && pageOffset >= r.Offset && pageOffset < r.Offset + r.Size);
                    baseColor = reg != null ? GetRegionColor(reg.FileType) : Color.FromRgb(0x30, 0x30, 0x30);
                }
                hasBase = true;
                break;
            }
        }

        return hasBase ? baseColor : Colors.Gray;
    }

    /// <summary>Parse the hex color string from a ManualRegion.</summary>
    private static Color ParseRegionColor(ManualRegion region)
    {
        try
        {
            string hex = region.ColorHex.TrimStart('#');
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return Color.FromRgb(r, g, b);
        }
        catch
        {
            return Color.FromRgb(0xFF, 0x6F, 0x00);
        }
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
        // _3dSpacing is set by the slider (default 1.1)

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
        var isRegionAssigned = new bool[numPages]; // track which pages go transparent

        for (int z = 0; z < layers && pageIdx < numPages; z++)
        {
            for (int y = 0; y < pagesPerCol && pageIdx < numPages; y++)
            {
                for (int x = 0; x < pagesPerRow && pageIdx < numPages; x++)
                {
                    bool passesFilter = _filterSet == null || _filterSet.Contains(pageIdx);
                    bool inRegion = passesFilter && IsPageInManualRegion(pageIdx);
                    isRegionAssigned[pageIdx] = inRegion;

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
        // Opaque batch (normal blocks) and transparent batch (region-assigned blocks) kept separate
        var group = new Model3DGroup();
        var opaqueBatches = new Dictionary<uint, (Point3DCollection Pos, Int32Collection Idx)>();
        var transpBatches = new Dictionary<uint, (Point3DCollection Pos, Int32Collection Idx)>();

        for (int i = 0; i < colors.Count; i++)
        {
            Color c = colors[i];
            uint key = ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            var batches = (i < isRegionAssigned.Length && isRegionAssigned[i]) ? transpBatches : opaqueBatches;

            if (!batches.ContainsKey(key))
                batches[key] = (new Point3DCollection(), new Int32Collection());

            var (bPos, bIdx) = batches[key];
            var (srcVertStart, vertCount, srcIdxStart, idxCount) = shapeRanges[i];
            int dstVertStart = bPos.Count;

            for (int v = 0; v < vertCount; v++)
                bPos.Add(positions[srcVertStart + v]);

            for (int t = 0; t < idxCount; t++)
                bIdx.Add(indices[srcIdxStart + t] - srcVertStart + dstVertStart);
        }

        // Add opaque batches (normal blocks)
        foreach (var kvp in opaqueBatches)
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

        // Add transparent batches (region-assigned blocks — mostly transparent so they fade out)
        foreach (var kvp in transpBatches)
        {
            uint key = kvp.Key;
            var (bPos, bIdx) = kvp.Value;
            byte r = (byte)((key >> 16) & 0xFF);
            byte g = (byte)((key >> 8) & 0xFF);
            byte b = (byte)(key & 0xFF);

            var batchMesh = new MeshGeometry3D { Positions = bPos, TriangleIndices = bIdx };
            var mat = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(45, r, g, b))); // ~18% opacity
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
        _camDist = boundRadius / Math.Sin(fovRad / 2.0) * 1.15; // 15% margin — tight default zoom
        UpdateCamera(0, 0, 0);

        string filterInfo = _filterSet != null ? $"\nFILTER: {_filterLabel} ({_filterSet.Count} match)" : "";
        int regionAssignedCount = 0;
        for (int i = 0; i < numPages; i++)
            if (isRegionAssigned[i]) regionAssignedCount++;
        string regionInfo = regionAssignedCount > 0 ? $"\n{regionAssignedCount} block{(regionAssignedCount == 1 ? "" : "s")} assigned to regions" : "";
        Txt3DInfo.Text = $"{numPages} pages ({_result.EntropyBlockSize}B)  {pagesPerRow}×{pagesPerCol}×{layers}\nDrag to rotate, scroll to zoom{regionInfo}{filterInfo}";
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

    /// <summary>
    /// Refresh all selection-dependent visuals: 3D highlight cubes, 2D right panel,
    /// entropy track overlays.  Called from global Escape handler and anywhere else
    /// that clears or modifies the selection set.
    /// </summary>
    private void UpdateSelectionVisuals()
    {
        if (Panel3D != null && Panel3D.Visibility == Visibility.Visible)
            UpdateHighlight3D();
        UpdateRightPanel();
    }

    /// <summary>
    /// Reset the 3D camera to the computed zoom-extents position (same calculation
    /// as the initial camera setup in Build3DView).
    /// </summary>
    private void Reset3DCamera()
    {
        if (_result == null || _3dNumPages <= 0) return;
        double extX = _3dPagesPerRow * _3dSpacing / 2.0;
        double extY = _3dPagesPerCol * _3dSpacing / 2.0;
        double extZ = _3dLayers      * _3dSpacing / 2.0;
        double boundRadius = Math.Sqrt(extX * extX + extY * extY + extZ * extZ);
        double fovRad = 60.0 * Math.PI / 180.0;
        _camDist  = boundRadius / Math.Sin(fovRad / 2.0) * 1.15;
        _camTheta = 0.6;
        _camPhi   = 0.5;
        UpdateCamera(0, 0, 0);
    }

    // ── 3D Selection highlight (lightweight — doesn't rebuild data cubes) ──

    private void UpdateHighlight3D()
    {
        if (_result == null || _3dNumPages <= 0)
        {
            HighlightModel3D.Content = null;
            Update3DSelectionInfo();
            return;
        }

        if (_selectedPageIndices.Count == 0)
        {
            HighlightModel3D.Content = null;
            Update3DSelectionInfo();
            UpdateDotPlotHighlight();   // reset dot plot to full-file view
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

        Update3DSelectionInfo();
        UpdateDotPlotHighlight();
    }

    /// <summary>Refresh the right panel reflecting the current selection (or full file if none).</summary>
    private void UpdateDotPlotHighlight() => UpdateRightPanel();

    /// <summary>Update the 3D info overlay and status bar with selection count.</summary>
    private void Update3DSelectionInfo()
    {
        if (_result == null || _3dNumPages <= 0) return;

        int selCount = _selectedPageIndices.Count;
        string filterInfo = _filterSet != null ? $"\nFILTER: {_filterLabel} ({_filterSet.Count} match)" : "";

        // Count how many selected blocks are already assigned to manual regions
        int assignedCount = 0;
        foreach (int pi in _selectedPageIndices)
            if (_manualRegions.Any(mr => mr.PageIndices.Contains(pi)))
                assignedCount++;

        // Count total region-assigned blocks
        int totalAssigned = 0;
        for (int i = 0; i < _3dNumPages; i++)
            if (IsPageInManualRegion(i)) totalAssigned++;

        string line1 = $"{_3dNumPages} pages ({_result.EntropyBlockSize}B)  {_3dPagesPerRow}×{_3dPagesPerCol}×{_3dLayers}";
        string line2 = "Drag to rotate, scroll to zoom";
        string regionLine = totalAssigned > 0 ? $"\n{totalAssigned} block{(totalAssigned == 1 ? "" : "s")} assigned to regions" : "";

        if (selCount > 0)
        {
            string assignedNote = assignedCount > 0 ? $" ({assignedCount} assigned)" : "";
            line2 = $"{selCount} block{(selCount == 1 ? "" : "s")} selected ({(long)selCount * _result.EntropyBlockSize:N0} bytes){assignedNote}";
            TxtStatus.Text = $"Selected {selCount} block{(selCount == 1 ? "" : "s")} — {(long)selCount * _result.EntropyBlockSize:N0} bytes";
        }

        Txt3DInfo.Text = $"{line1}\n{line2}{regionLine}{filterInfo}";

        UpdateExtractSelectedState();
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

        _camTheta -= dx * 0.01;
        _camPhi = Math.Clamp(_camPhi + dy * 0.01, -1.4, 1.4);

        UpdateCamera(0, 0, 0);
    }

    private void Viewport3D_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _camDist = Math.Clamp(_camDist - e.Delta * 0.01, 2, 400);
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

        // Show multi-block inspector for the flood-filled selection
        if (_selectedPageIndices.Count > 0)
            ShowMultiBlockInspector(_selectedPageIndices);
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
            if (_selectedPageIndices.Count > 0) ShowMultiBlockInspector(_selectedPageIndices);
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
            if (_selectedPageIndices.Count > 0) ShowMultiBlockInspector(_selectedPageIndices);
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
            if (_selectedPageIndices.Count > 0) ShowMultiBlockInspector(_selectedPageIndices);
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

        // ── Manual Region operations ────────────────────────────────
        AddRegionMenuItems(menu);

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

    // ── Manual Regions ─────────────────────────────────────────────────

    /// <summary>Add "Add to Region" submenu items to the 3D right-click context menu.</summary>
    private void AddRegionMenuItems(System.Windows.Controls.ContextMenu menu)
    {
        if (_selectedPageIndices.Count == 0) return;

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Check if any selected blocks are already assigned
        bool anyAssigned = _selectedPageIndices.Any(pi => _manualRegions.Any(mr => mr.PageIndices.Contains(pi)));

        var addSub = new System.Windows.Controls.MenuItem
        {
            Header = $"Add {_selectedPageIndices.Count} Block{(_selectedPageIndices.Count == 1 ? "" : "s")} to Region",
        };

        // Option: New Region
        var newRegion = new System.Windows.Controls.MenuItem { Header = "➕ New Region..." };
        newRegion.Click += (s, ev) =>
        {
            string name = PromptForRegionName($"Region {_manualRegions.Count + 1}");
            if (name == null!) return;

            var mr = new ManualRegion
            {
                Name = name,
                ColorHex = $"#{_regionPalette[_manualRegions.Count % _regionPalette.Length].R:X2}" +
                           $"{_regionPalette[_manualRegions.Count % _regionPalette.Length].G:X2}" +
                           $"{_regionPalette[_manualRegions.Count % _regionPalette.Length].B:X2}",
            };

            // Remove from any existing region first (prevent double-assignment)
            foreach (int pi in _selectedPageIndices)
            {
                foreach (var existing in _manualRegions)
                    existing.PageIndices.Remove(pi);
                mr.PageIndices.Add(pi);
            }

            _manualRegions.Add(mr);
            RefreshManualRegionsUI();
            Rebuild3DPreservingCamera();
        };
        addSub.Items.Add(newRegion);

        // Options for each existing region
        foreach (var existing in _manualRegions)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = $"▶ {existing.Name}  ({existing.BlockCount} blocks)",
            };
            var capturedRegion = existing;
            item.Click += (s, ev) =>
            {
                // Remove from any other region first
                foreach (int pi in _selectedPageIndices)
                {
                    foreach (var other in _manualRegions)
                        if (other != capturedRegion) other.PageIndices.Remove(pi);
                    capturedRegion.PageIndices.Add(pi);
                }
                RefreshManualRegionsUI();
                Rebuild3DPreservingCamera();
            };
            addSub.Items.Add(item);
        }

        menu.Items.Add(addSub);

        // If blocks are assigned, offer quick remove
        if (anyAssigned)
        {
            var removeSub = new System.Windows.Controls.MenuItem
            {
                Header = "Remove Selected Blocks from Regions",
            };
            removeSub.Click += (s, ev) =>
            {
                foreach (int pi in _selectedPageIndices)
                    foreach (var mr in _manualRegions)
                        mr.PageIndices.Remove(pi);

                // Clean up empty regions
                _manualRegions.RemoveAll(mr => mr.PageIndices.Count == 0);
                RefreshManualRegionsUI();
                Rebuild3DPreservingCamera();
            };
            menu.Items.Add(removeSub);
        }
    }

    /// <summary>Simple input prompt using WPF InputBox pattern (MessageBox + clipboard trick isn't great — use a tiny window).</summary>
    private static string? PromptForRegionName(string defaultName)
    {
        var win = new Window
        {
            Title = "New Manual Region",
            Width = 340,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        var label = new System.Windows.Controls.TextBlock
        {
            Text = "Region name:",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var textBox = new System.Windows.Controls.TextBox
        {
            Text = defaultName,
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3A)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x66)),
        };
        textBox.SelectAll();

        var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var okBtn = new System.Windows.Controls.Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 70, IsCancel = true };

        string? result = null;
        okBtn.Click += (s, ev) => { result = textBox.Text; win.Close(); };
        cancelBtn.Click += (s, ev) => win.Close();

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(label);
        panel.Children.Add(textBox);
        panel.Children.Add(btnPanel);
        win.Content = panel;
        win.ShowDialog();

        return result;
    }

    /// <summary>Refresh the manual regions DataGrid and enable/disable export.</summary>
    private void RefreshManualRegionsUI()
    {
        GridManualRegions.ItemsSource = null;
        GridManualRegions.ItemsSource = _manualRegions;
        BtnExportManualRegions.IsEnabled = _manualRegions.Count > 0;
    }

    /// <summary>Rebuild 3D while preserving camera angle and distance.</summary>
    private void Rebuild3DPreservingCamera()
    {
        double savedTheta = _camTheta, savedPhi = _camPhi, savedDist = _camDist;
        Build3DView();
        _camTheta = savedTheta; _camPhi = savedPhi; _camDist = savedDist;
        UpdateCamera(0, 0, 0);
    }

    /// <summary>When a manual region is selected in the grid, highlight its blocks in 3D and show inspector.</summary>
    private void GridManualRegions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridManualRegions.SelectedItem is ManualRegion mr && mr.PageIndices.Count > 0)
        {
            _selectedPageIndices.Clear();
            foreach (int pi in mr.PageIndices)
                _selectedPageIndices.Add(pi);
            UpdateHighlight3D();

            // Show inspector for the first block in this region
            int firstPage = mr.PageIndices.Min();
            ShowBlockInspectorForPage(firstPage);
        }
    }

    /// <summary>Highlight a manual region's blocks in 3D (context menu).</summary>
    private void ManualRegion_Highlight_Click(object sender, RoutedEventArgs e)
    {
        if (GridManualRegions.SelectedItem is ManualRegion mr && mr.PageIndices.Count > 0)
        {
            _selectedPageIndices.Clear();
            foreach (int pi in mr.PageIndices)
                _selectedPageIndices.Add(pi);
            UpdateHighlight3D();

            int firstPage = mr.PageIndices.Min();
            ShowBlockInspectorForPage(firstPage);
        }
    }

    /// <summary>Open the block inspector for a given page index (converts page→block and shows inspector).</summary>
    private void ShowBlockInspectorForPage(int pageIndex)
    {
        if (_result == null) return;
        long pageOffset = (long)pageIndex * _result.EntropyBlockSize;
        int blockSize = _result.EntropyBlockSize;
        int blockIndex = blockSize > 0 ? (int)(pageOffset / blockSize) : 0;
        blockIndex = Math.Clamp(blockIndex, 0, Math.Max(0, _result.EntropyMap.Length - 1));
        ShowBlockInspector(blockIndex, updateFrom3D: false);
    }

    /// <summary>Remove currently selected 3D blocks from the selected manual region.</summary>
    private void ManualRegion_RemoveBlocks_Click(object sender, RoutedEventArgs e)
    {
        if (GridManualRegions.SelectedItem is not ManualRegion mr) return;

        int removed = 0;
        foreach (int pi in _selectedPageIndices)
            if (mr.PageIndices.Remove(pi)) removed++;

        if (removed > 0)
        {
            // Remove empty regions
            if (mr.PageIndices.Count == 0)
                _manualRegions.Remove(mr);

            RefreshManualRegionsUI();
            Rebuild3DPreservingCamera();
            TxtStatus.Text = $"Removed {removed} block{(removed == 1 ? "" : "s")} from \"{mr.Name}\"";
        }
    }

    /// <summary>Rename the selected manual region.</summary>
    private void ManualRegion_Rename_Click(object sender, RoutedEventArgs e)
    {
        if (GridManualRegions.SelectedItem is not ManualRegion mr) return;

        string? newName = PromptForRegionName(mr.Name);
        if (newName != null)
        {
            mr.Name = newName;
            RefreshManualRegionsUI();
        }
    }

    /// <summary>Delete the selected manual region entirely (blocks become unassigned).</summary>
    private void ManualRegion_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (GridManualRegions.SelectedItem is not ManualRegion mr) return;

        var confirm = MessageBox.Show(
            $"Delete region \"{mr.Name}\" ({mr.BlockCount} blocks)?\nBlocks will become unassigned.",
            "Delete Region", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            _manualRegions.Remove(mr);
            RefreshManualRegionsUI();
            Rebuild3DPreservingCamera();
        }
    }

    /// <summary>Export manual regions to JSON, CSV, or RE tool scripts.</summary>
    private void BtnExportManualRegions_Click(object sender, RoutedEventArgs e)
    {
        if (_manualRegions.Count == 0 || _result == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{_result.FileName}_manual_regions",
            Filter = string.Join("|",
                "JSON (*.json)|*.json",
                "CSV (*.csv)|*.csv",
                "IDA MAP (*.map)|*.map",
                "radare2 script (*.r2)|*.r2",
                "All files|*.*"),
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            int blockSize = _result.EntropyBlockSize;
            switch (dlg.FilterIndex)
            {
                case 1: // JSON
                    File.WriteAllText(dlg.FileName, ManualRegionStore.Serialize(_manualRegions));
                    break;

                case 2: // CSV
                    ExportManualRegionsCsv(dlg.FileName, blockSize);
                    break;

                case 3: // IDA MAP
                    ExportManualRegionsIdaMap(dlg.FileName, blockSize);
                    break;

                case 4: // r2
                    ExportManualRegionsR2(dlg.FileName, blockSize);
                    break;

                default:
                    File.WriteAllText(dlg.FileName, ManualRegionStore.Serialize(_manualRegions));
                    break;
            }

            TxtStatus.Text = $"Exported {_manualRegions.Count} manual region{(_manualRegions.Count == 1 ? "" : "s")} to {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Import manual regions from a JSON file.</summary>
    private void BtnImportManualRegions_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json|All files|*.*",
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            string json = File.ReadAllText(dlg.FileName);
            var imported = ManualRegionStore.Deserialize(json);
            if (imported.Count == 0)
            {
                MessageBox.Show("No regions found in file.", "Import",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Merge: add imported regions, skip duplicates by name
            int added = 0;
            foreach (var mr in imported)
            {
                if (_manualRegions.Any(existing => existing.Name == mr.Name)) continue;
                _manualRegions.Add(mr);
                added++;
            }

            RefreshManualRegionsUI();
            Rebuild3DPreservingCamera();
            TxtStatus.Text = $"Imported {added} manual region{(added == 1 ? "" : "s")} from {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportManualRegionsCsv(string path, int blockSize)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("RegionName,SpanOffset,SpanOffsetHex,SpanSize,SpanSizeHex,TotalBlocks,TotalBytes,Notes");
        foreach (var mr in _manualRegions)
        {
            var spans = mr.GetSpans(blockSize);
            foreach (var (offset, size) in spans)
            {
                writer.WriteLine($"\"{mr.Name}\",{offset},0x{offset:X8},{size},0x{size:X8},{mr.BlockCount},{mr.GetByteSize(blockSize)},\"{mr.Notes}\"");
            }
        }
    }

    private void ExportManualRegionsIdaMap(string path, int blockSize)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("; BinaryCarver Manual Regions — IDA MAP format");
        foreach (var mr in _manualRegions)
        {
            var spans = mr.GetSpans(blockSize);
            int spanIdx = 0;
            foreach (var (offset, size) in spans)
            {
                string label = spans.Count == 1 ? mr.Name : $"{mr.Name}_span{spanIdx}";
                // IDA MAP: <segment>:<offset> <name>
                writer.WriteLine($" 0001:{offset:X8} {label.Replace(' ', '_')}");
                spanIdx++;
            }
        }
    }

    private void ExportManualRegionsR2(string path, int blockSize)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("# BinaryCarver Manual Regions — radare2 script");
        foreach (var mr in _manualRegions)
        {
            var spans = mr.GetSpans(blockSize);
            int spanIdx = 0;
            foreach (var (offset, size) in spans)
            {
                string label = spans.Count == 1 ? mr.Name : $"{mr.Name}_span{spanIdx}";
                writer.WriteLine($"f {label.Replace(' ', '_')} {size} @ 0x{offset:X}");
                writer.WriteLine($"CC {mr.Name} (manual region, {mr.BlockCount} blocks) @ 0x{offset:X}");
                spanIdx++;
            }
        }
    }

    // ── Right panel: zoom/pan ──────────────────────────────────────────

    /// <summary>Apply current _plotZoom / _plotPanX / _plotPanY to DotPlotImage.</summary>
    private void UpdatePlotTransform()
    {
        var tg = new System.Windows.Media.TransformGroup();
        tg.Children.Add(new System.Windows.Media.ScaleTransform(_plotZoom, _plotZoom));
        tg.Children.Add(new System.Windows.Media.TranslateTransform(_plotPanX, _plotPanY));
        DotPlotImage.RenderTransform = tg;
    }

    private void PlotContainer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor   = e.Delta > 0 ? 1.25 : 1.0 / 1.25;
        double newZoom  = Math.Clamp(_plotZoom * factor, 0.5, 64.0);
        var    pos      = e.GetPosition(PlotContainer);
        // Zoom toward cursor: keep the image-pixel under the mouse fixed
        _plotPanX = pos.X - (pos.X - _plotPanX) * (newZoom / _plotZoom);
        _plotPanY = pos.Y - (pos.Y - _plotPanY) * (newZoom / _plotZoom);
        _plotZoom = newZoom;
        UpdatePlotTransform();
        e.Handled = true;
    }

    private void PlotContainer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos     = e.GetPosition(PlotContainer);
        int mode    = CboPlotMode?.SelectedIndex ?? 0;
        bool selectable = mode == 3 || mode == 4 || mode == 7; // Heat Map (3), Waveform (4), Autocorrelation (7)
        bool ctrl   = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (selectable && !ctrl)
        {
            // Enter rubber-band selection mode.
            // Shift held = deselect; no modifier = clear-and-select.
            _plotSelecting   = true;
            _plotDragging    = false;
            _plotSelDeselect = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            _plotSelStart    = pos;
            UpdateRubberBand(pos, pos);
        }
        else
        {
            // Pan mode: Ctrl+drag in selectable modes, always in non-selectable.
            _plotSelecting  = false;
            _plotDragging   = true;
            _plotDragStart  = pos;
            _plotDragOrigin = new System.Windows.Point(_plotPanX, _plotPanY);
        }

        PlotContainer.CaptureMouse();
        e.Handled = true;
    }

    private void PlotContainer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        PlotContainer.ReleaseMouseCapture();

        if (_plotSelecting)
        {
            _plotSelecting = false;
            PlotRubberBand.Visibility = Visibility.Collapsed;

            var pos   = e.GetPosition(PlotContainer);
            double dx = Math.Abs(pos.X - _plotSelStart.X);
            double dy = Math.Abs(pos.Y - _plotSelStart.Y);
            bool isDrag = dx > 4 || dy > 4;

            double x1 = isDrag ? Math.Min(_plotSelStart.X, pos.X) : pos.X - 1;
            double x2 = isDrag ? Math.Max(_plotSelStart.X, pos.X) : pos.X + 1;

            var (bStart, bEnd) = PlotContainerXToBlockRange(x1, x2);
            if (_result != null && bEnd >= bStart)
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                ApplyPlotSelection(bStart, bEnd, _plotSelDeselect, additive: ctrl || _plotSelDeselect);
            }
        }
        else
        {
            _plotDragging = false;
        }
    }

    private void PlotContainer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pos = e.GetPosition(PlotContainer);

        if (_plotSelecting)
        {
            UpdateRubberBand(_plotSelStart, pos);
        }
        else if (_plotDragging)
        {
            _plotPanX = _plotDragOrigin.X + (pos.X - _plotDragStart.X);
            _plotPanY = _plotDragOrigin.Y + (pos.Y - _plotDragStart.Y);
            UpdatePlotTransform();
        }
    }

    private void PlotContainer_RightClick(object sender, MouseButtonEventArgs e)
    {
        // Right-click is now free for future context menu use.
        // Zoom extents moved to middle-click (handled by Window_PreviewMouseDown).
    }

    /// <summary>Position and show the rubber-band rectangle in PlotContainer coords.</summary>
    private void UpdateRubberBand(System.Windows.Point a, System.Windows.Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Max(1, Math.Abs(b.X - a.X));
        double h = Math.Max(1, Math.Abs(b.Y - a.Y));
        Canvas.SetLeft(PlotRubberBand, x);
        Canvas.SetTop (PlotRubberBand, y);
        PlotRubberBand.Width      = w;
        PlotRubberBand.Height     = h;
        PlotRubberBand.Visibility = Visibility.Visible;
        // Tint red when deselecting, white when selecting
        PlotRubberBand.Stroke = _plotSelDeselect
            ? new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x60, 0x60))
            : new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
        PlotRubberBand.Fill = _plotSelDeselect
            ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x00, 0x00))
            : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>
    /// Convert two x-positions in PlotContainer pixels to a block-index range,
    /// accounting for current zoom/pan.  Returns (-1,-1) if mode doesn't support selection.
    /// </summary>
    private (int bStart, int bEnd) PlotContainerXToBlockRange(double cx1, double cx2)
    {
        if (_result == null || _fileData == null) return (-1, -1);

        double cw = PlotContainer.ActualWidth;
        if (cw <= 0) return (-1, -1);

        // Container pixel → normalised image coordinate [0..1]:
        // Image layout rect = (0, 0, cw, ch) before transform.
        // Transform: Scale(z) then Translate(px, py).
        // Container x  =  image_natural_x * z  +  panX
        // → image_natural_x = (container_x - panX) / (cw * z)
        double norm1 = (cx1 - _plotPanX) / (cw * _plotZoom);
        double norm2 = (cx2 - _plotPanX) / (cw * _plotZoom);
        if (norm1 > norm2) (norm1, norm2) = (norm2, norm1);
        norm1 = Math.Clamp(norm1, 0, 1);
        norm2 = Math.Clamp(norm2, 0, 1);

        int numBlocks = _result.EntropyMap.Length;
        if (numBlocks == 0) return (-1, -1);

        int mode = CboPlotMode?.SelectedIndex ?? 0;
        int bStart, bEnd;

        if (mode == 4 || mode == 7) // Entropy Waveform / Autocorrelation: x axis IS block index
        {
            bStart = (int)(norm1 * numBlocks);
            bEnd   = (int)(norm2 * numBlocks);
        }
        else if (mode == 3) // Position Heat Map: x = file-position bucket → blocks
        {
            int bs       = _result.EntropyBlockSize;
            long fileLen = _fileData.Length;
            long byte1   = (long)(norm1 * fileLen);
            long byte2   = (long)(norm2 * fileLen);
            bStart = (int)(byte1 / bs);
            bEnd   = (int)(byte2 / bs);
        }
        else return (-1, -1);

        bStart = Math.Clamp(bStart, 0, numBlocks - 1);
        bEnd   = Math.Clamp(bEnd,   0, numBlocks - 1);
        if (bStart > bEnd) (bStart, bEnd) = (bEnd, bStart);
        return (bStart, bEnd);
    }

    /// <summary>
    /// Apply a block range selection to _selectedPageIndices and refresh all dependent views.
    /// additive = true  → keep existing selection (Ctrl or Shift-deselect).
    /// additive = false → clear existing first (plain drag).
    /// </summary>
    private void ApplyPlotSelection(int bStart, int bEnd, bool deselect, bool additive)
    {
        if (_result == null) return;

        if (!additive)
            _selectedPageIndices.Clear();

        for (int bi = bStart; bi <= bEnd; bi++)
        {
            if (deselect)
                _selectedPageIndices.Remove(bi);
            else
                _selectedPageIndices.Add(bi);
        }

        // Sync 3D highlight + inspector
        UpdateHighlight3D();
        if (_selectedPageIndices.Count == 1)
            ShowBlockInspector(_selectedPageIndices.First());
        else if (_selectedPageIndices.Count > 1)
            ShowMultiBlockInspector(_selectedPageIndices);

        UpdateExtractSelectedState();
    }

    // ── Right panel: mode dispatcher ──────────────────────────────────

    private bool _suppressModeChange;
    private void CboPlotMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModeChange || TileScrollViewer == null) return;  // not yet fully initialized
        int idx = CboPlotMode?.SelectedIndex ?? 0;
        if (idx == 0)          // "⊞ All Views"
            EnterTileMode();
        else if (!_tileMode)   // Already in single mode, just refresh
        { UpdateParamStripVisibility(); UpdateRightPanel(); }
        else                   // Was in tile mode, switch to single for this viz
            EnterSingleMode(idx);
    }

    /// <summary>
    /// Central dispatcher: redraws whichever visualization is currently selected
    /// in the right panel, using the current selection (or full file if no selection).
    /// </summary>
    private void UpdateRightPanel()
    {
        if (_fileData == null) return;
        if (_tileMode) { UpdateAllTiles(); return; }
        var sel  = _selectedPageIndices.Count > 0 ? _selectedPageIndices : null;
        // ComboBox index 0 = "All Views" (tile mode), 1–6 = individual viz modes 0–5
        int mode = (CboPlotMode?.SelectedIndex ?? 1) - 1;
        if (mode < 0) mode = 0;
        RenderForMode(mode, _fileData, sel);
    }

    // ── Tile-overview helpers ─────────────────────────────────────────

    /// <summary>
    /// Render all 6 visualization modes and push their bitmaps into the tile Images.
    /// Uses DotPlotImage as the scratch render target, then snapshots Source to each tile.
    /// </summary>
    private void UpdateAllTiles()
    {
        if (_fileData == null) return;
        var sel = _selectedPageIndices.Count > 0 ? _selectedPageIndices : null;
        var tileImgs = new System.Windows.Controls.Image[] { TileImg0, TileImg1, TileImg2, TileImg3, TileImg4, TileImg5, TileImg6 };
        for (int m = 0; m < 7; m++)
        {
            RenderForMode(m, _fileData, sel);
            tileImgs[m].Source = DotPlotImage.Source;
        }
        DotPlotImage.Source = null;   // clear scratch; PlotContainer is Collapsed anyway
    }

    /// <summary>
    /// Render a single mode using <paramref name="data"/> into DotPlotImage.Source.
    /// </summary>
    private void RenderForMode(int mode, byte[] data, HashSet<int>? sel)
    {
        switch (mode)
        {
            case 0: DrawDotPlot        (data, sel); break;
            case 1: DrawHistogram      (data, sel); break;
            case 2: DrawPositionHeatMap(data, sel); break;
            case 3: DrawEntropyWaveform(data, sel); break;
            case 4: DrawParallelCoords (data, sel); break;
            case 5: DrawRadViz         (data, sel); break;
            case 6: DrawAutocorrelation(data, sel); break;
        }
    }

    /// <summary>
    /// Enter the full-panel single-mode view for <paramref name="modeIndex"/>.
    /// Shows PlotContainer, hides tile grid, updates header controls.
    /// </summary>
    private void EnterSingleMode(int comboIndex)
    {
        _tileMode = false;
        TileScrollViewer.Visibility = Visibility.Collapsed;
        PlotContainer.Visibility    = Visibility.Visible;
        TxtPlotHint.Visibility      = Visibility.Visible;
        ResetPlotTransform();
        _suppressModeChange = true;
        CboPlotMode.SelectedIndex = comboIndex;   // 1–7
        _suppressModeChange = false;
        UpdateParamStripVisibility();
        UpdateRightPanel();
    }

    /// <summary>
    /// Return to the tile-overview dashboard.
    /// </summary>
    private void EnterTileMode()
    {
        _tileMode = true;
        TileScrollViewer.Visibility = Visibility.Visible;
        PlotContainer.Visibility    = Visibility.Collapsed;
        TxtPlotHint.Visibility      = Visibility.Collapsed;
        _suppressModeChange = true;
        if (CboPlotMode != null) CboPlotMode.SelectedIndex = 0;   // "⊞ All Views"
        _suppressModeChange = false;
        UpdateParamStripVisibility();
        UpdateAllTiles();
    }

    private void ResetPlotTransform()
    {
        _plotZoom = 1.0; _plotPanX = 0.0; _plotPanY = 0.0;
        UpdatePlotTransform();
    }

    // ── Tile click + overview toggle ──────────────────────────────────

    private void Tile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tagStr && int.TryParse(tagStr, out int idx))
            EnterSingleMode(idx + 1);   // Tag 0–6 → ComboBox index 1–7
    }

    // ── Parameter strip: visibility + event handlers ──────────────────

    /// <summary>
    /// Show/hide mode-specific controls in the parameter strip based on the active
    /// visualization mode (ComboBox index 1–7, where 0 = tile overview).
    /// </summary>
    private void UpdateParamStripVisibility()
    {
        if (ParamStrip == null) return;
        int idx = CboPlotMode?.SelectedIndex ?? 0;
        if (_tileMode || idx == 0)
        {
            ParamStrip.Visibility = Visibility.Collapsed;
            return;
        }
        ParamStrip.Visibility = Visibility.Visible;

        // Mode indices (ComboBox): 1=BytePair, 2=Histogram, 3=HeatMap,
        //   4=Waveform, 5=ParallelCoords, 6=RadViz, 7=Autocorrelation
        PanelStride.Visibility  = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelMaxLag.Visibility  = idx == 7 ? Visibility.Visible : Visibility.Collapsed;
        PanelScope.Visibility   = idx == 7 ? Visibility.Visible : Visibility.Collapsed;
        PanelScale.Visibility   = (idx == 2 || idx == 4) ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _suppressSliderChange;

    private void SliderWrapWidth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderChange || TxtWrapWidth == null) return;
        _vizWrapWidth = Math.Max(1, (int)SliderWrapWidth.Value);
        TxtWrapWidth.Text = _vizWrapWidth.ToString();
        if (!_tileMode) UpdateRightPanel();
    }

    private void SliderStride_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderChange || TxtStride == null) return;
        _vizStride = Math.Max(1, (int)SliderStride.Value);
        TxtStride.Text = _vizStride.ToString();
        if (!_tileMode) UpdateRightPanel();
    }

    private void SliderMaxLag_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderChange || TxtMaxLag == null) return;
        _vizMaxLag = Math.Max(16, (int)SliderMaxLag.Value);
        TxtMaxLag.Text = _vizMaxLag.ToString();
        if (!_tileMode) UpdateRightPanel();
    }

    private void CboScale_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSliderChange || CboScale == null) return;
        _vizLogScale = (CboScale.SelectedIndex == 0);
        if (!_tileMode) UpdateRightPanel();
    }

    private void CboScope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSliderChange) return;
        if (!_tileMode) UpdateRightPanel();
    }

    // ── Right panel: Byte Histogram ────────────────────────────────────

    /// <summary>
    /// 256×256 bar chart of byte-value frequency.
    /// X = byte value (0–255), Y = log-normalized frequency.
    /// With selection: global bars dimmed, selected-block bars in orange→yellow.
    /// </summary>
    private void DrawHistogram(byte[]? data, HashSet<int>? highlightBlocks = null)
    {
        if (data == null || data.Length == 0)
        {
            DotPlotImage.Source = null;
            if (TxtDotPlotInfo != null) TxtDotPlotInfo.Text = "no data";
            return;
        }

        const int size = 256;
        int wrapW  = Math.Max(1, _vizWrapWidth);
        bool useLog = _vizLogScale;

        // When wrap width < file size, switch to positional-entropy-within-wrap mode:
        // X = position within wrap period (0..wrapW-1), bar height = byte entropy at that position.
        // This reveals which offsets within the record are fixed (low entropy) vs variable (high).
        bool positionalMode = wrapW < data.Length;

        if (positionalMode)
        {
            // Accumulate per-position byte histograms
            var posFreq = new int[wrapW * 256]; // [pos * 256 + byteValue]
            var posCounts = new int[wrapW];
            for (int i = 0; i < data.Length; i++)
            {
                int pos = i % wrapW;
                posFreq[pos * 256 + data[i]]++;
                posCounts[pos]++;
            }

            // Compute Shannon entropy at each wrap position
            var posEntropy = new double[wrapW];
            for (int p = 0; p < wrapW; p++)
            {
                double total = posCounts[p];
                if (total < 1) continue;
                double ent = 0;
                for (int v = 0; v < 256; v++)
                {
                    int c = posFreq[p * 256 + v];
                    if (c > 0)
                    {
                        double prob = c / total;
                        ent -= prob * Math.Log2(prob);
                    }
                }
                posEntropy[p] = ent; // 0..8
            }

            // Same for highlight blocks
            double[]? hlEntropy = null;
            if (highlightBlocks != null && highlightBlocks.Count > 0 && _result != null)
            {
                var hlPosFreq = new int[wrapW * 256];
                var hlPosCounts = new int[wrapW];
                int bs = _result.EntropyBlockSize;
                foreach (int bi in highlightBlocks)
                {
                    long start = (long)bi * bs;
                    long end   = Math.Min(start + bs, data.Length);
                    for (long i = start; i < end; i++)
                    {
                        int pos = (int)(i % wrapW);
                        hlPosFreq[pos * 256 + data[i]]++;
                        hlPosCounts[pos]++;
                    }
                }
                hlEntropy = new double[wrapW];
                for (int p = 0; p < wrapW; p++)
                {
                    double total = hlPosCounts[p];
                    if (total < 1) continue;
                    double ent = 0;
                    for (int v = 0; v < 256; v++)
                    {
                        int c = hlPosFreq[p * 256 + v];
                        if (c > 0)
                        {
                            double prob = c / total;
                            ent -= prob * Math.Log2(prob);
                        }
                    }
                    hlEntropy[p] = ent;
                }
            }
            bool hasHL = hlEntropy != null;

            var pixels = new byte[size * size * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            { pixels[i] = 0x14; pixels[i+1] = 0x14; pixels[i+2] = 0x1E; pixels[i+3] = 255; }

            for (int x = 0; x < size; x++)
            {
                int pos = (int)((long)x * wrapW / size);
                if (pos >= wrapW) pos = wrapW - 1;
                double ent   = posEntropy[pos];
                double hlEnt = hasHL ? hlEntropy![pos] : 0;
                double t     = ent / 8.0;
                double ht    = hlEnt / 8.0;
                int barH     = (int)(t * (size - 2));
                int hlH      = (int)(ht * (size - 2));

                for (int y = 0; y < size; y++)
                {
                    int screenY = size - 1 - y;
                    int pidx    = (screenY * size + x) * 4;
                    bool inBar  = y < barH;
                    bool inHL   = y < hlH;

                    if (hasHL)
                    {
                        if (inHL)
                        {
                            double frac = barH > 0 ? (double)y / barH : 0;
                            pixels[pidx+2] = 0xFF;
                            pixels[pidx+1] = (byte)(0x60 + frac * 0x9F);
                            pixels[pidx+0] = 0x00;
                            pixels[pidx+3] = 255;
                        }
                        else if (inBar)
                        {
                            pixels[pidx+2] = 0x22; pixels[pidx+1] = 0x30; pixels[pidx+0] = 0x55;
                            pixels[pidx+3] = 255;
                        }
                    }
                    else if (inBar)
                    {
                        var c = NormalizedEntropyToColor(t);
                        double fade = barH > 0 ? (double)y / barH : 0;
                        pixels[pidx+2] = (byte)(c.R * (0.3 + 0.7 * fade));
                        pixels[pidx+1] = (byte)(c.G * (0.3 + 0.7 * fade));
                        pixels[pidx+0] = (byte)(c.B * (0.3 + 0.7 * fade));
                        pixels[pidx+3] = 255;
                    }
                }
            }

            // Grid lines at entropy 2, 4, 6
            foreach (double ev in new[] { 2.0, 4.0, 6.0 })
            {
                int gy = size - 1 - (int)(ev / 8.0 * (size - 2));
                for (int gx = 0; gx < size; gx++)
                {
                    int pidx = (gy * size + gx) * 4;
                    pixels[pidx]   = Math.Max(pixels[pidx],   (byte)0x30);
                    pixels[pidx+1] = Math.Max(pixels[pidx+1], (byte)0x30);
                    pixels[pidx+2] = Math.Max(pixels[pidx+2], (byte)0x38);
                }
            }

            var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
                size, size, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
            bmp.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
            SetPlotBitmap(bmp);

            double avgEnt = posEntropy.Length > 0 ? posEntropy.Average() : 0;
            if (TxtDotPlotInfo != null)
                TxtDotPlotInfo.Text = $"positional entropy  ·  wrap {wrapW}  ·  avg {avgEnt:F2}/8.0  ·  {data.Length:N0} bytes";
            return;
        }

        // ── Standard byte-frequency histogram (wrap == file length) ───────

        // Global byte frequencies
        var freq = new long[256];
        foreach (byte b in data) freq[b]++;
        long maxFreq = 1;
        foreach (long f in freq) if (f > maxFreq) maxFreq = f;
        double normMax = useLog ? Math.Log(maxFreq + 1) : (double)maxFreq;

        // Highlight frequencies (selected blocks only)
        long[]? hlFreq = null;
        if (highlightBlocks != null && highlightBlocks.Count > 0 && _result != null)
        {
            hlFreq = new long[256];
            int bs = _result.EntropyBlockSize;
            foreach (int bi in highlightBlocks)
            {
                long start = (long)bi * bs;
                long end   = Math.Min(start + bs, data.Length);
                for (long i = start; i < end; i++)
                    hlFreq[data[i]]++;
            }
        }
        long hlMax = 1;
        if (hlFreq != null)
            foreach (long f in hlFreq) if (f > hlMax) hlMax = f;
        double hlNormMax = useLog ? Math.Log(hlMax + 1) : (double)hlMax;
        bool hasHL2 = hlFreq != null;

        var pixels2 = new byte[size * size * 4];
        // Dark background
        for (int i = 0; i < pixels2.Length; i += 4)
        { pixels2[i] = 0x14; pixels2[i+1] = 0x14; pixels2[i+2] = 0x1E; pixels2[i+3] = 255; }

        for (int x = 0; x < size; x++)
        {
            double normFreq  = freq[x] > 0 ? (useLog ? Math.Log(freq[x] + 1) : freq[x]) : 0;
            double normHlFreq = (hlFreq != null && hlFreq[x] > 0)
                               ? (useLog ? Math.Log(hlFreq[x] + 1) : hlFreq[x]) : 0;
            int barH  = normMax  > 0 ? (int)(normFreq  / normMax  * (size - 2)) : 0;
            int hlH   = hlNormMax > 0 ? (int)(normHlFreq / hlNormMax * (size - 2)) : 0;

            for (int y = 0; y < size; y++)
            {
                int screenY = size - 1 - y; // y=0 is bottom
                int pixIdx  = (screenY * size + x) * 4;
                bool inBar  = y < barH;
                bool inHL   = y < hlH;

                if (hasHL2)
                {
                    if (inHL)
                    {
                        double frac = barH > 0 ? (double)y / barH : 0;
                        pixels2[pixIdx+2] = 0xFF;
                        pixels2[pixIdx+1] = (byte)(0x60 + frac * 0x9F);
                        pixels2[pixIdx+0] = 0x00;
                        pixels2[pixIdx+3] = 255;
                    }
                    else if (inBar)
                    {
                        pixels2[pixIdx+2] = 0x22; pixels2[pixIdx+1] = 0x30; pixels2[pixIdx+0] = 0x55;
                        pixels2[pixIdx+3] = 255;
                    }
                }
                else if (inBar)
                {
                    double frac = barH > 0 ? (double)y / barH : 0;
                    pixels2[pixIdx+2] = 0x00;
                    pixels2[pixIdx+1] = (byte)(frac * 0xE0);
                    pixels2[pixIdx+0] = (byte)(0xFF - frac * 0x80);
                    pixels2[pixIdx+3] = 255;
                }
            }
        }

        // Faint horizontal grid at 25%, 50%, 75%
        foreach (double pct in new[] { 0.25, 0.50, 0.75 })
        {
            int gy = size - 1 - (int)(pct * (size - 2));
            for (int x = 0; x < size; x++)
            {
                int pidx = (gy * size + x) * 4;
                pixels2[pidx]   = Math.Max(pixels2[pidx],   (byte)0x30);
                pixels2[pidx+1] = Math.Max(pixels2[pidx+1], (byte)0x30);
                pixels2[pidx+2] = Math.Max(pixels2[pidx+2], (byte)0x38);
            }
        }

        var bmp2 = new System.Windows.Media.Imaging.WriteableBitmap(
            size, size, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        bmp2.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels2, size * 4, 0);
        SetPlotBitmap(bmp2);

        int nonZero = 0;
        foreach (long f in freq) if (f > 0) nonZero++;
        if (TxtDotPlotInfo != null)
            TxtDotPlotInfo.Text = $"{nonZero}/256 byte values  ·  {data.Length:N0} bytes";
    }

    // ── Right panel: Position Heat Map ────────────────────────────────

    /// <summary>
    /// 256×256 heat map: X = file-position bucket (256 slices), Y = byte value (0–255).
    /// Brightness = log-normalized frequency of (position, value) pairs.
    /// Reveals byte-distribution drift across the file (e.g., strings cluster in one region,
    /// code in another, high-entropy compressed data in a third).
    /// </summary>
    private void DrawPositionHeatMap(byte[]? data, HashSet<int>? highlightBlocks = null)
    {
        if (data == null || data.Length == 0)
        {
            DotPlotImage.Source = null;
            if (TxtDotPlotInfo != null) TxtDotPlotInfo.Text = "no data";
            return;
        }

        const int size = 256;
        int wrapW = Math.Max(1, _vizWrapWidth);
        var hmap = new long[size * size]; // [xBucket * size + byteValue]
        for (int i = 0; i < data.Length; i++)
        {
            // Fold: position within each wrap-width span → X bucket.
            // All bytes at the same offset-within-wrap land in the same column,
            // so periodic structures at the wrap width stack up as bright bands.
            int posInWrap = i % wrapW;
            int xb = (int)((long)posInWrap * size / wrapW);
            if (xb >= size) xb = size - 1;
            hmap[xb * size + data[i]]++;
        }
        long maxFreq = 1;
        foreach (long f in hmap) if (f > maxFreq) maxFreq = f;
        double logMax = Math.Log(maxFreq + 1);

        // Build highlighted column set (folded at wrap width)
        bool[]? hlX = null;
        if (highlightBlocks != null && highlightBlocks.Count > 0 && _result != null)
        {
            hlX = new bool[size];
            int bs = _result.EntropyBlockSize;
            if (bs >= wrapW)
            {
                // Block spans full wrap period → every column is highlighted
                for (int x = 0; x < size; x++) hlX[x] = true;
            }
            else
            {
                foreach (int bi in highlightBlocks)
                {
                    long byteStart = (long)bi * bs;
                    long byteEnd   = Math.Min(byteStart + bs, data.Length);
                    int w0 = (int)(byteStart % wrapW);
                    int w1 = (int)((byteEnd - 1) % wrapW);
                    // Mark columns covered by this block within the wrap period
                    int xStart = (int)((long)w0 * size / wrapW);
                    int xEnd   = (int)((long)w1 * size / wrapW);
                    if (xStart <= xEnd)
                        for (int x = xStart; x <= xEnd && x < size; x++) hlX[x] = true;
                    else
                    {
                        // Wraps around: mark [xStart..size) and [0..xEnd]
                        for (int x = xStart; x < size; x++) hlX[x] = true;
                        for (int x = 0; x <= xEnd && x < size; x++) hlX[x] = true;
                    }
                }
            }
        }
        bool hasHL = hlX != null;

        var pixels = new byte[size * size * 4];

        for (int xb = 0; xb < size; xb++)
        {
            bool colHL = hasHL && hlX![xb];
            for (int bv = 0; bv < size; bv++) // bv = byte value
            {
                long  count  = hmap[xb * size + bv];
                double t     = count > 0 ? Math.Log(count + 1) / logMax : 0;
                int   screenY = size - 1 - bv;  // flip: bv=0 at bottom
                int   pidx    = (screenY * size + xb) * 4;

                byte r, g, b;
                if (hasHL)
                {
                    if (colHL && t > 0.001)
                    {
                        r = (byte)(0xC0 + t * 0x3F); g = (byte)(t * 0xFF); b = 0x00;
                    }
                    else if (t > 0.001)
                    {
                        r = (byte)(t * 0x28); g = (byte)(t * 0x38); b = (byte)(0x18 + t * 0x60);
                    }
                    else { r = 0x08; g = 0x08; b = 0x14; }
                }
                else
                {
                    if (t < 0.001) { r = 0x14; g = 0x14; b = 0x1E; }
                    else if (t < 0.5)
                    {
                        double s = t / 0.5;
                        r = 0x00; g = (byte)(s * 0x80); b = (byte)(0x80 + s * 0x7F);
                    }
                    else
                    {
                        double s = (t - 0.5) / 0.5;
                        r = (byte)(s * 0xFF); g = (byte)(0x80 + s * 0x7F); b = 0xFF;
                    }
                }
                pixels[pidx] = b; pixels[pidx+1] = g; pixels[pidx+2] = r; pixels[pidx+3] = 255;
            }
        }

        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
            size, size, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        SetPlotBitmap(bmp);

        if (TxtDotPlotInfo != null)
            TxtDotPlotInfo.Text = $"position ({size} buckets) × byte value  ·  {data.Length:N0} bytes";
    }

    // ── Right panel: Entropy Waveform ─────────────────────────────────

    /// <summary>
    /// 256×256 filled-area waveform of per-block Shannon entropy (byte-level).
    /// X = block index (time axis), Y = entropy 0–8.
    /// Color follows the same blue→green→yellow→orange→red gradient as the entropy bars.
    /// Selected blocks are overlaid in orange/yellow; the rest are dimmed.
    /// </summary>
    private void DrawEntropyWaveform(byte[]? data, HashSet<int>? highlightBlocks = null,
                                      int outW = 0, int outH = 0)
    {
        if (data == null || _result == null || _result.EntropyMap.Length == 0)
        {
            DotPlotImage.Source = null;
            if (TxtDotPlotInfo != null) TxtDotPlotInfo.Text = "no data";
            return;
        }

        var map       = _result.EntropyMap;
        int numBlocks = map.Length;
        int bs        = _result.EntropyBlockSize;
        int wrapW     = Math.Max(1, _vizWrapWidth);

        // Fold: convert wrap width (bytes) into a block-count fold period.
        // When wrapW < total file size, blocks at the same offset within the fold
        // are averaged, revealing periodic entropy patterns at that period.
        int foldBlocks = Math.Max(1, wrapW / Math.Max(1, bs));
        bool folded    = foldBlocks < numBlocks;

        double[] displayMap;
        int displayCount;
        if (folded)
        {
            displayMap   = new double[foldBlocks];
            var counts   = new int[foldBlocks];
            for (int bi = 0; bi < numBlocks; bi++)
            {
                int slot = bi % foldBlocks;
                displayMap[slot] += map[bi];
                counts[slot]++;
            }
            for (int i = 0; i < foldBlocks; i++)
                if (counts[i] > 0) displayMap[i] /= counts[i];
            displayCount = foldBlocks;
        }
        else
        {
            displayMap   = map;
            displayCount = numBlocks;
        }

        // Auto-size: one column per display-block (cap 8192) × 512 rows
        int W = outW > 0 ? outW : Math.Min(displayCount, 8192);
        int H = outH > 0 ? outH : 512;
        bool hasHL    = highlightBlocks != null && highlightBlocks.Count > 0;

        var pixels = new byte[W * H * 4];
        // Dark background
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 0x14; pixels[i+1] = 0x14; pixels[i+2] = 0x1E; pixels[i+3] = 255; }

        // Faint horizontal grid lines at integer entropy values 1–7
        for (int ei = 1; ei <= 7; ei++)
        {
            int gy = H - 1 - (int)(ei / 8.0 * (H - 4));
            gy = Math.Clamp(gy, 0, H - 1);
            for (int gx = 0; gx < W; gx++)
            {
                int pi = (gy * W + gx) * 4;
                pixels[pi] = 0x28; pixels[pi+1] = 0x28; pixels[pi+2] = 0x32;
            }
        }

        // Build folded highlight set (which display slots contain a highlighted block?)
        HashSet<int>? hlSlots = null;
        if (hasHL && folded)
        {
            hlSlots = new HashSet<int>();
            foreach (int bi in highlightBlocks!)
                hlSlots.Add(bi % foldBlocks);
        }

        for (int px = 0; px < W; px++)
        {
            int bStart = (int)((long)px * displayCount / W);
            int bEnd   = Math.Max(bStart + 1, (int)((long)(px + 1) * displayCount / W));
            bEnd = Math.Min(bEnd, displayCount);

            // Average entropy over the covered display-blocks
            double sum = 0;
            for (int bi = bStart; bi < bEnd; bi++) sum += displayMap[bi];
            double entropy  = sum / (bEnd - bStart);
            double t        = Math.Clamp(entropy / 8.0, 0, 1);
            int    barTop   = H - 1 - (int)(t * (H - 4));
            barTop = Math.Clamp(barTop, 0, H - 1);

            bool colHL = false;
            if (hasHL)
            {
                if (folded)
                {
                    for (int bi = bStart; bi < bEnd && !colHL; bi++)
                        if (hlSlots!.Contains(bi)) colHL = true;
                }
                else
                {
                    for (int bi = bStart; bi < bEnd && !colHL; bi++)
                        if (highlightBlocks!.Contains(bi)) colHL = true;
                }
            }

            var baseColor = NormalizedEntropyToColor(t);

            for (int py2 = barTop; py2 < H; py2++)
            {
                int pidx = (py2 * W + px) * 4;
                double fade = py2 == barTop ? 1.0 : 1.0 - (py2 - barTop) * 0.4 / (H - barTop);
                fade = Math.Max(fade, 0.15);

                if (hasHL)
                {
                    if (colHL)
                    {
                        pixels[pidx+2] = (byte)(0xC0 + t * 0x3F);
                        pixels[pidx+1] = (byte)(t * 0xFF * fade);
                        pixels[pidx+0] = 0x00;
                    }
                    else
                    {
                        pixels[pidx+2] = (byte)(baseColor.R * 0.18 * fade);
                        pixels[pidx+1] = (byte)(baseColor.G * 0.18 * fade);
                        pixels[pidx+0] = (byte)(baseColor.B * 0.18 * fade);
                    }
                }
                else
                {
                    pixels[pidx+2] = (byte)(baseColor.R * fade);
                    pixels[pidx+1] = (byte)(baseColor.G * fade);
                    pixels[pidx+0] = (byte)(baseColor.B * fade);
                }
                pixels[pidx+3] = 255;

                // Bright top-edge cap
                if (py2 == barTop)
                {
                    if (hasHL && colHL)
                    { pixels[pidx+2] = 0xFF; pixels[pidx+1] = 0xFF; pixels[pidx+0] = 0x80; }
                    else if (!hasHL)
                    { pixels[pidx+2] = 0xFF; pixels[pidx+1] = 0xFF; pixels[pidx+0] = 0xFF; }
                    else
                    { pixels[pidx+2] = 0x40; pixels[pidx+1] = 0x50; pixels[pidx+0] = 0x70; }
                }
            }
        }

        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
            W, H, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, W, H), pixels, W * 4, 0);
        DotPlotImage.Source = bmp;

        double avgEntropy = displayCount > 0 ? displayMap.Average() : 0;
        string foldNote = folded ? $"  ·  folded {foldBlocks} blocks (wrap {wrapW})" : "";
        if (TxtDotPlotInfo != null)
            TxtDotPlotInfo.Text =
                $"{numBlocks} blocks  ·  avg {avgEntropy:F2}  ·  max 8.0  ·  block {bs}B{foldNote}";
    }

    // ── Right panel: Parallel Coordinates ────────────────────────────────

    /// <summary>
    /// 256×256 parallel-coordinates plot.  Each block is rendered as a polyline
    /// crossing N vertical axes (one per entropy dimension).  Axis order left→right:
    /// byte-entropy, nibble-entropy, bit-entropy, bigram-entropy, divergence, delta.
    ///
    /// What it reveals:
    ///   • Bundles of nearly-parallel lines = homogeneous region (all code, all compressed…)
    ///   • Fan-outs between axes = regions where one entropy metric diverges from others
    ///   • Outlier lines = blocks at structural boundaries or with unusual byte profiles
    ///   • Selection: selected blocks glow orange; rest dim to near-invisible.
    /// </summary>
    private void DrawParallelCoords(byte[]? data, HashSet<int>? highlight = null)
    {
        if (_result == null)
        {
            DotPlotImage.Source = null;
            if (TxtDotPlotInfo != null) TxtDotPlotInfo.Text = "no data";
            return;
        }

        // Gather available axes (skip null/empty maps)
        var axes = new List<(string label, double[] map, double max)>();
        if (_result.EntropyMap      is { Length: > 0 } em)  axes.Add(("B-8",  em,  8.0));
        if (_result.NibbleEntropyMap is { Length: > 0 } nm) axes.Add(("N-4",  nm,  4.0));
        if (_result.BitEntropyMap    is { Length: > 0 } bm) axes.Add(("Bit",  bm,  1.0));
        if (_result.BigramEntropyMap is { Length: > 0 } gm) axes.Add(("G-16", gm, 16.0));
        if (_result.DivergenceMap    is { Length: > 0 } dm)
        {
            double mx = 1.0; foreach (double v in dm) if (v > mx) mx = v;
            axes.Add(("Div", dm, mx));
        }
        if (_result.DeltaEntropyMap  is { Length: > 0 } de)
        {
            double mx = 1.0; foreach (double v in de) if (v > mx) mx = v;
            axes.Add(("ΔE",  de, mx));
        }

        if (axes.Count < 2)
        {
            DotPlotImage.Source = null;
            if (TxtDotPlotInfo != null) TxtDotPlotInfo.Text = "need ≥2 entropy axes";
            return;
        }

        const int W = 256, H = 256;
        const int PAD_X = 18, PAD_Y_TOP = 24, PAD_Y_BOT = 8;
        int usableW = W - 2 * PAD_X;
        int usableH = H - PAD_Y_TOP - PAD_Y_BOT;

        // Axis x-positions (evenly spaced within usable area)
        int nAxes   = axes.Count;
        var axisX   = new int[nAxes];
        for (int i = 0; i < nAxes; i++)
            axisX[i] = PAD_X + (nAxes < 2 ? 0 : i * usableW / (nAxes - 1));

        int numBlocks = axes[0].map.Length;
        bool hasHL    = highlight != null && highlight.Count > 0;

        // Wrap-width folding: average block metrics at same fold-offset
        int wrapW      = Math.Max(1, _vizWrapWidth);
        int pcBs       = _result.EntropyBlockSize;
        int foldBlocks = Math.Max(1, wrapW / Math.Max(1, pcBs));
        bool folded    = foldBlocks < numBlocks;

        // Build folded axis data if needed
        var fAxes = axes;  // use original by default
        int drawCount = numBlocks;
        if (folded)
        {
            drawCount = foldBlocks;
            fAxes = new List<(string label, double[] map, double max)>();
            foreach (var (label, map, max) in axes)
            {
                var foldedMap = new double[foldBlocks];
                var cnt       = new int[foldBlocks];
                for (int bi = 0; bi < map.Length; bi++)
                {
                    int slot = bi % foldBlocks;
                    foldedMap[slot] += map[bi];
                    cnt[slot]++;
                }
                for (int i = 0; i < foldBlocks; i++)
                    if (cnt[i] > 0) foldedMap[i] /= cnt[i];
                fAxes.Add((label, foldedMap, max));
            }
        }

        // Build folded highlight set
        HashSet<int>? hlFolded = null;
        if (hasHL && folded)
        {
            hlFolded = new HashSet<int>();
            foreach (int bi in highlight!) hlFolded.Add(bi % foldBlocks);
        }

        // For large block counts draw every Nth block so we don't thrash
        int stride = Math.Max(1, drawCount / 6000);

        var pixels = new byte[W * H * 4];
        // Background
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 0x10; pixels[i+1] = 0x10; pixels[i+2] = 0x18; pixels[i+3] = 255; }

        // Axis vertical lines + tick marks
        foreach (int ax in axisX)
        {
            for (int y = PAD_Y_TOP; y < H - PAD_Y_BOT; y++)
            {
                int pi = (y * W + ax) * 4;
                pixels[pi] = 0x38; pixels[pi+1] = 0x38; pixels[pi+2] = 0x48;
            }
            // Top tick (high-entropy)
            for (int dx = -2; dx <= 2; dx++)
            {
                int tx = ax + dx;
                if (tx < 0 || tx >= W) continue;
                int pi = (PAD_Y_TOP * W + tx) * 4;
                pixels[pi] = 0x60; pixels[pi+1] = 0x60; pixels[pi+2] = 0x80;
            }
        }

        // ── Draw polylines ─────────────────────────────────────────────
        // Two passes: dim lines first, then bright selected lines on top
        for (int pass = 0; pass < (hasHL ? 2 : 1); pass++)
        {
            bool drawSelected = pass == 1;

            for (int bi = 0; bi < drawCount; bi += stride)
            {
                bool sel = folded
                    ? (hlFolded != null && hlFolded.Contains(bi))
                    : (highlight != null && highlight.Contains(bi));
                if (hasHL && sel != drawSelected) continue;

                // Base color from byte entropy (axis 0)
                double t0 = fAxes[0].map[bi] / fAxes[0].max;
                var    c  = NormalizedEntropyToColor(Math.Clamp(t0, 0, 1));

                byte lr, lg, lb, la;
                if (hasHL && !sel)
                {
                    lr = (byte)(c.R * 0.07); lg = (byte)(c.G * 0.07);
                    lb = (byte)(c.B * 0.07); la = 180;
                }
                else if (hasHL && sel)
                {
                    lr = 0xFF; lg = (byte)(0x60 + t0 * 0x9F); lb = 0x10; la = 240;
                }
                else
                {
                    lr = (byte)(c.R * 0.55); lg = (byte)(c.G * 0.55);
                    lb = (byte)(c.B * 0.55); la = 120;
                }

                // Y position for each axis
                var pts = new (int x, int y)[nAxes];
                for (int a = 0; a < nAxes; a++)
                {
                    double t = bi < fAxes[a].map.Length
                        ? Math.Clamp(fAxes[a].map[bi] / fAxes[a].max, 0, 1) : 0;
                    pts[a] = (axisX[a], PAD_Y_TOP + (int)((1 - t) * usableH));
                }

                // Draw segments between consecutive axes
                for (int a = 0; a < nAxes - 1; a++)
                    DrawLine(pixels, W, H,
                        pts[a].x, pts[a].y, pts[a+1].x, pts[a+1].y,
                        lr, lg, lb, la);
            }
        }

        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
            W, H, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, W, H), pixels, W * 4, 0);
        SetPlotBitmap(bmp);

        string axisNames = string.Join("  →  ", axes.Select(a => a.label));
        string foldNote = folded ? $"  ·  folded {foldBlocks} (wrap {wrapW})" : "";
        if (TxtDotPlotInfo != null)
            TxtDotPlotInfo.Text = $"{numBlocks} blocks  ·  axes: {axisNames}{foldNote}";
    }

    // ── Right panel: RadViz ────────────────────────────────────────────

    /// <summary>
    /// 256×256 RadViz scatter plot.  N anchor points are arranged uniformly on a
    /// unit circle (one per entropy dimension).  Each block is plotted as the
    /// normalised weighted-mean of those anchors, where the weight for each anchor
    /// equals the block's normalised value on that dimension.
    ///
    /// What it reveals:
    ///   • Points near an anchor = blocks dominated by that entropy metric
    ///   • Dense cluster at center = flat/uniform blocks (padding, null fill)
    ///   • Spread clusters = genuinely mixed or transitional content
    ///   • Same axis order and coloring as parallel coordinates.
    /// </summary>
    private void DrawRadViz(byte[]? data, HashSet<int>? highlight = null)
    {
        if (_result == null)
        {
            DotPlotImage.Source = null;
            if (TxtDotPlotInfo != null) TxtDotPlotInfo.Text = "no data";
            return;
        }

        var axes = new List<(string label, double[] map, double max)>();
        if (_result.EntropyMap       is { Length: > 0 } em) axes.Add(("B-8",  em,  8.0));
        if (_result.NibbleEntropyMap is { Length: > 0 } nm) axes.Add(("N-4",  nm,  4.0));
        if (_result.BitEntropyMap    is { Length: > 0 } bm) axes.Add(("Bit",  bm,  1.0));
        if (_result.BigramEntropyMap is { Length: > 0 } gm) axes.Add(("G-16", gm, 16.0));
        if (_result.DivergenceMap    is { Length: > 0 } dm)
        {
            double mx = 1.0; foreach (double v in dm) if (v > mx) mx = v;
            axes.Add(("Div", dm, mx));
        }
        if (_result.DeltaEntropyMap  is { Length: > 0 } de)
        {
            double mx = 1.0; foreach (double v in de) if (v > mx) mx = v;
            axes.Add(("ΔE",  de, mx));
        }

        if (axes.Count < 2)
        {
            DotPlotImage.Source = null;
            if (TxtDotPlotInfo != null) TxtDotPlotInfo.Text = "need ≥2 entropy axes";
            return;
        }

        const int W = 256, H = 256;
        const double CX = 128, CY = 128, RADIUS = 100;

        int nAxes     = axes.Count;
        int numBlocks = axes[0].map.Length;
        bool hasHL    = highlight != null && highlight.Count > 0;

        // Wrap-width folding
        int rvWrapW    = Math.Max(1, _vizWrapWidth);
        int rvBs       = _result.EntropyBlockSize;
        int foldBlocks = Math.Max(1, rvWrapW / Math.Max(1, rvBs));
        bool folded    = foldBlocks < numBlocks;

        var fAxes = axes;
        int drawCount = numBlocks;
        if (folded)
        {
            drawCount = foldBlocks;
            fAxes = new List<(string label, double[] map, double max)>();
            foreach (var (label, map, max) in axes)
            {
                var fm = new double[foldBlocks];
                var cn = new int[foldBlocks];
                for (int bi = 0; bi < map.Length; bi++)
                {
                    int slot = bi % foldBlocks;
                    fm[slot] += map[bi];
                    cn[slot]++;
                }
                for (int j = 0; j < foldBlocks; j++)
                    if (cn[j] > 0) fm[j] /= cn[j];
                fAxes.Add((label, fm, max));
            }
        }

        HashSet<int>? hlFolded = null;
        if (hasHL && folded)
        {
            hlFolded = new HashSet<int>();
            foreach (int bi in highlight!) hlFolded.Add(bi % foldBlocks);
        }

        // Anchor positions (start at top, go clockwise)
        var anchors = new (double ax, double ay)[nAxes];
        for (int i = 0; i < nAxes; i++)
        {
            double angle = -Math.PI / 2 + i * 2 * Math.PI / nAxes;
            anchors[i] = (CX + RADIUS * Math.Cos(angle),
                          CY + RADIUS * Math.Sin(angle));
        }

        var pixels = new byte[W * H * 4];
        // Background
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 0x10; pixels[i+1] = 0x10; pixels[i+2] = 0x18; pixels[i+3] = 255; }

        // Outer circle
        const int STEPS = 360;
        for (int s = 0; s < STEPS; s++)
        {
            double a  = s * 2 * Math.PI / STEPS;
            int    cx2 = (int)(CX + RADIUS * Math.Cos(a));
            int    cy2 = (int)(CY + RADIUS * Math.Sin(a));
            if (cx2 >= 0 && cx2 < W && cy2 >= 0 && cy2 < H)
            {
                int pi = (cy2 * W + cx2) * 4;
                pixels[pi] = 0x30; pixels[pi+1] = 0x30; pixels[pi+2] = 0x44;
            }
        }

        // Spoke lines + anchor dots
        for (int i = 0; i < nAxes; i++)
        {
            DrawLine(pixels, W, H,
                (int)CX, (int)CY, (int)anchors[i].ax, (int)anchors[i].ay,
                0x28, 0x28, 0x3C, 200);
            // Anchor dot (3×3)
            for (int dy2 = -2; dy2 <= 2; dy2++)
            for (int dx2 = -2; dx2 <= 2; dx2++)
            {
                int px2 = (int)anchors[i].ax + dx2;
                int py2 = (int)anchors[i].ay + dy2;
                if (px2 < 0 || px2 >= W || py2 < 0 || py2 >= H) continue;
                int pi = (py2 * W + px2) * 4;
                pixels[pi] = 0x60; pixels[pi+1] = 0x70; pixels[pi+2] = 0xA0;
            }
        }

        // ── Plot block points (two passes: background then selected) ──
        int stride = Math.Max(1, drawCount / 8000);

        for (int pass = 0; pass < (hasHL ? 2 : 1); pass++)
        {
            bool drawSel = pass == 1;

            for (int bi = 0; bi < drawCount; bi += stride)
            {
                bool sel = folded
                    ? (hlFolded != null && hlFolded.Contains(bi))
                    : (highlight != null && highlight.Contains(bi));
                if (hasHL && sel != drawSel) continue;

                // Normalised feature vector
                double sumW = 0, px3 = 0, py3 = 0;
                for (int a = 0; a < nAxes; a++)
                {
                    double w = bi < fAxes[a].map.Length
                        ? Math.Clamp(fAxes[a].map[bi] / fAxes[a].max, 0, 1) : 0;
                    px3  += w * anchors[a].ax;
                    py3  += w * anchors[a].ay;
                    sumW += w;
                }
                if (sumW < 1e-9) { px3 = CX; py3 = CY; }
                else { px3 /= sumW; py3 /= sumW; }

                int ipx = (int)Math.Clamp(px3, 0, W - 1);
                int ipy = (int)Math.Clamp(py3, 0, H - 1);

                double t0 = fAxes[0].map[bi] / fAxes[0].max;
                var    c  = NormalizedEntropyToColor(Math.Clamp(t0, 0, 1));

                byte pr, pg, pb;
                if (hasHL && !sel)
                {
                    pr = (byte)(c.R * 0.10); pg = (byte)(c.G * 0.10); pb = (byte)(c.B * 0.10);
                }
                else if (hasHL && sel)
                {
                    pr = 0xFF; pg = (byte)(0x60 + t0 * 0x9F); pb = 0x10;
                }
                else
                {
                    pr = (byte)(c.R * 0.75); pg = (byte)(c.G * 0.75); pb = (byte)(c.B * 0.75);
                }

                // Plot 2×2 dot (selected: 3×3)
                int dotR = (hasHL && sel) ? 2 : 1;
                for (int dy2 = -dotR; dy2 <= dotR; dy2++)
                for (int dx2 = -dotR; dx2 <= dotR; dx2++)
                {
                    int px4 = ipx + dx2, py4 = ipy + dy2;
                    if (px4 < 0 || px4 >= W || py4 < 0 || py4 >= H) continue;
                    int pi = (py4 * W + px4) * 4;
                    // Accumulate (additive blending so dense areas brighten)
                    pixels[pi+0] = (byte)Math.Min(255, pixels[pi+0] + pb / 4);
                    pixels[pi+1] = (byte)Math.Min(255, pixels[pi+1] + pg / 4);
                    pixels[pi+2] = (byte)Math.Min(255, pixels[pi+2] + pr / 4);
                    if (hasHL && sel)
                    { pixels[pi+0] = pb; pixels[pi+1] = pg; pixels[pi+2] = pr; }
                }
            }
        }

        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
            W, H, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, W, H), pixels, W * 4, 0);
        SetPlotBitmap(bmp);

        string axisNames = string.Join("  ", axes.Select((a, i) =>
            $"[{i+1}]{a.label}"));
        string rvFoldNote = folded ? $"  ·  folded {foldBlocks} (wrap {rvWrapW})" : "";
        if (TxtDotPlotInfo != null)
            TxtDotPlotInfo.Text = $"{numBlocks} blocks  ·  {axisNames}{rvFoldNote}";
    }

    // ── Right panel: Autocorrelation Heatmap ─────────────────────────

    /// <summary>
    /// Autocorrelation heatmap.  X = file position (block index), Y = lag (1..maxLag).
    /// Brightness shows how strongly the byte stream at each block correlates with
    /// itself shifted by <c>lag</c> bytes.  Bright horizontal bands indicate repeating
    /// structures (records, headers, padding) at that lag/period.  Encrypted or
    /// compressed regions go dark because there is no periodic structure.
    /// Selected blocks are rendered in an orange/yellow palette; the rest are dimmed.
    /// </summary>
    private void DrawAutocorrelation(byte[]? data, HashSet<int>? highlightBlocks = null)
    {
        if (data == null || _result == null || _result.EntropyMap.Length == 0)
        {
            DotPlotImage.Source = null;
            if (TxtDotPlotInfo != null) TxtDotPlotInfo.Text = "no data";
            return;
        }

        int numBlocks = _result.EntropyMap.Length;
        int bs        = _result.EntropyBlockSize;
        bool hasHL    = highlightBlocks != null && highlightBlocks.Count > 0;
        int acWrapW   = Math.Max(1, _vizWrapWidth);

        // Wrap-width folding: fold block columns at wrap period
        int acFoldBlocks = Math.Max(1, acWrapW / Math.Max(1, bs));
        bool acFolded    = acFoldBlocks < numBlocks;
        int displayBlocks = acFolded ? acFoldBlocks : numBlocks;

        // Image dimensions: one column per display-block, rows for lags 1..maxLag
        int maxLag = Math.Max(16, _vizMaxLag);
        int W = Math.Min(displayBlocks, 8192);
        int H = maxLag;

        // Compute autocorrelation per block at each lag.
        // Raw computation is per original block; we fold afterward.
        int rawW = Math.Min(numBlocks, 8192);
        var rawAc = new float[H * rawW];
        int sampleLen = Math.Min(bs, 512);  // cap per-block sample for speed

        for (int col = 0; col < rawW; col++)
        {
            int bi = col * numBlocks / rawW;
            long blockStart = (long)bi * bs;
            if (blockStart + sampleLen + maxLag > data.Length) continue;

            double sum = 0;
            for (int i = 0; i < sampleLen; i++)
                sum += data[blockStart + i];
            double mean = sum / sampleLen;

            double varSum = 0;
            for (int i = 0; i < sampleLen; i++)
            {
                double d = data[blockStart + i] - mean;
                varSum += d * d;
            }
            if (varSum < 1e-8) continue;

            for (int lag = 1; lag <= maxLag; lag++)
            {
                if (blockStart + sampleLen + lag > data.Length) break;
                double crossSum = 0;
                for (int i = 0; i < sampleLen; i++)
                    crossSum += (data[blockStart + i] - mean)
                              * (data[blockStart + i + lag] - mean);
                double r = crossSum / varSum;
                rawAc[(lag - 1) * rawW + col] = (float)Math.Abs(r);
            }
        }

        // Fold raw autocorrelation into display map
        float[] acMap;
        if (acFolded)
        {
            acMap = new float[H * W];
            var cnt = new int[H * W];
            for (int lag = 0; lag < H; lag++)
            {
                for (int col = 0; col < rawW; col++)
                {
                    int dCol = col % W;  // fold column
                    float v = rawAc[lag * rawW + col];
                    acMap[lag * W + dCol] += v;
                    cnt[lag * W + dCol]++;
                }
            }
            for (int i = 0; i < acMap.Length; i++)
                if (cnt[i] > 0) acMap[i] /= cnt[i];
        }
        else
        {
            acMap = rawAc;
        }

        // Find global max for normalization
        float globalMax = 0.001f;
        foreach (float v in acMap) if (v > globalMax) globalMax = v;

        // Build pixel buffer
        var pixels = new byte[W * H * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 0x14; pixels[i+1] = 0x14; pixels[i+2] = 0x1E; pixels[i+3] = 255; }

        // Build highlighted-column lookup (folded)
        bool[]? hlCol = null;
        if (hasHL)
        {
            hlCol = new bool[W];
            foreach (int bi in highlightBlocks!)
            {
                int origCol = (int)((long)bi * rawW / numBlocks);
                int dCol = acFolded ? (origCol % W) : origCol;
                if (dCol >= 0 && dCol < W) hlCol[dCol] = true;
            }
        }

        for (int lag = 0; lag < H; lag++)
        {
            int screenY = lag;
            for (int col = 0; col < W; col++)
            {
                float t = acMap[lag * W + col] / globalMax;
                t = Math.Clamp(t, 0f, 1f);
                int pidx = (screenY * W + col) * 4;

                byte r, g, b;
                if (hasHL && hlCol != null)
                {
                    if (hlCol[col] && t > 0.005f)
                    {
                        if (t < 0.5f)
                        {
                            float s = t / 0.5f;
                            r = (byte)(0xC0 + s * 0x3F); g = (byte)(s * 0xA0); b = 0x00;
                        }
                        else
                        {
                            float s = (t - 0.5f) / 0.5f;
                            r = 0xFF; g = (byte)(0xA0 + s * 0x5F); b = (byte)(s * 0xFF);
                        }
                    }
                    else if (t > 0.005f)
                    {
                        r = (byte)(t * 0x28); g = (byte)(t * 0x38); b = (byte)(0x18 + t * 0x50);
                    }
                    else { r = 0x14; g = 0x14; b = 0x1E; }
                }
                else
                {
                    if (t < 0.01f)       { r = 0x14; g = 0x14; b = 0x1E; }
                    else if (t < 0.25f)  { float s = t / 0.25f;
                        r = 0x00; g = (byte)(s * 0x60); b = (byte)(0x60 + s * 0x9F); }
                    else if (t < 0.50f)  { float s = (t - 0.25f) / 0.25f;
                        r = 0x00; g = (byte)(0x60 + s * 0x9F); b = (byte)(0xFF - s * 0x80); }
                    else if (t < 0.75f)  { float s = (t - 0.50f) / 0.25f;
                        r = (byte)(s * 0xFF); g = 0xFF; b = (byte)(0x7F - s * 0x7F); }
                    else                 { float s = (t - 0.75f) / 0.25f;
                        r = 0xFF; g = 0xFF; b = (byte)(s * 0xFF); }
                }
                pixels[pidx] = b; pixels[pidx+1] = g; pixels[pidx+2] = r; pixels[pidx+3] = 255;
            }
        }

        // Faint horizontal grid lines at power-of-two lags
        for (int p = 1; p <= 8; p++)
        {
            int lagRow = (1 << p) - 1;
            if (lagRow >= H) break;
            for (int x = 0; x < W; x++)
            {
                int pidx = (lagRow * W + x) * 4;
                pixels[pidx]   = Math.Max(pixels[pidx],   (byte)0x30);
                pixels[pidx+1] = Math.Max(pixels[pidx+1], (byte)0x30);
                pixels[pidx+2] = Math.Max(pixels[pidx+2], (byte)0x38);
            }
        }

        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
            W, H, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, W, H), pixels, W * 4, 0);
        DotPlotImage.Source = bmp;   // native resolution (like waveform — no upscale)

        // Find the lag with the strongest average correlation (dominant period)
        float bestAvg = 0; int bestLag = 1;
        for (int lag = 0; lag < H; lag++)
        {
            float rowSum = 0; int cnt2 = 0;
            for (int col = 0; col < W; col++)
            {
                float v = acMap[lag * W + col];
                if (v > 0.01f) { rowSum += v; cnt2++; }
            }
            float avg = cnt2 > 0 ? rowSum / cnt2 : 0;
            if (avg > bestAvg) { bestAvg = avg; bestLag = lag + 1; }
        }

        string acFoldNote = acFolded ? $"  ·  folded {acFoldBlocks} (wrap {acWrapW})" : "";
        if (TxtDotPlotInfo != null)
            TxtDotPlotInfo.Text =
                $"{numBlocks} blocks  ·  lags 1–{maxLag}  ·  dominant period ≈ {bestLag} bytes  ·  block {bs}B{acFoldNote}";
    }

    // ── Pixel-level drawing helpers ───────────────────────────────────

    /// <summary>
    /// Bresenham line draw with alpha blend into a Bgr32 pixel buffer.
    /// Buffer layout: [B, G, R, A] per pixel, stride = w*4.
    /// </summary>
    private static void DrawLine(byte[] pixels, int w, int h,
        int x0, int y0, int x1, int y1,
        byte r, byte g, byte b, byte alpha = 255)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            if ((uint)x0 < (uint)w && (uint)y0 < (uint)h)
            {
                int i = (y0 * w + x0) * 4;
                int ia = 255 - alpha;
                pixels[i+0] = (byte)((pixels[i+0] * ia + b * alpha) >> 8);
                pixels[i+1] = (byte)((pixels[i+1] * ia + g * alpha) >> 8);
                pixels[i+2] = (byte)((pixels[i+2] * ia + r * alpha) >> 8);
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = err << 1;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // ── Utilities ───────────────────────────────────────────────────────

    // ── Report export ────────────────────────────────────────────────────

    private void BtnExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null || _fileData == null)
        {
            MessageBox.Show("Load a file first.", "Export Report",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description       = "Choose output folder for the report",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        string folder = IOPath.Combine(dlg.SelectedPath,
            $"BinaryCarver_{SanitizeFileName(_result.FileName)}_{DateTime.Now:yyyyMMdd_HHmmss}");

        BtnExportReport.IsEnabled = false;
        TxtStatus.Text = "Exporting report…";

        // Rendering uses DotPlotImage (UI thread required), so run synchronously.
        // Typical export is <2s even for large files with many regions.
        try
        {
            ExportReport(folder);
            TxtStatus.Text = $"Report saved → {folder}";
            Process.Start(new ProcessStartInfo("explorer.exe", folder)
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Export failed.";
            MessageBox.Show($"Export failed:\n{ex.Message}", "Export Report",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnExportReport.IsEnabled = true;
        }
    }

    /// <summary>Master export routine — creates folder tree, renders PNGs, writes HTML.</summary>
    // Each export entry bundles the images map + paths to extracted data files
    private record ExportData(
        Dictionary<string, string> Imgs,   // name → absolute path of PNG
        string? RawFile,                   // absolute path to extracted binary, or null
        string? EntropyFile                // absolute path to entropy CSV, or null
    );

    private void ExportReport(string root)
    {
        Directory.CreateDirectory(root);

        // ── Full file ──────────────────────────────────────────────────
        string fullDir = IOPath.Combine(root, "full");
        Directory.CreateDirectory(fullDir);
        var fullImgs = ExportVisualizationsForSubject(fullDir, _fileData!, null, fullFile: true);
        // Full-file entropy CSV (all blocks, all metrics)
        string fullEntropyPath = IOPath.Combine(fullDir, "entropy.csv");
        WriteEntropyCSV(fullEntropyPath, null);
        var fullData = new ExportData(fullImgs, null, fullEntropyPath);

        // ── Selection overlay ──────────────────────────────────────────
        ExportData? selData = null;
        if (_selectedPageIndices.Count > 0)
        {
            string selDir = IOPath.Combine(root, "selection");
            Directory.CreateDirectory(selDir);
            var selImgs = ExportVisualizationsForSubject(selDir, _fileData!,
                new HashSet<int>(_selectedPageIndices), fullFile: true);
            string selEntropyPath = IOPath.Combine(selDir, "entropy.csv");
            WriteEntropyCSV(selEntropyPath, _selectedPageIndices);
            byte[]? selRaw = ExtractBlockBytes(_selectedPageIndices);
            string? selRawPath = null;
            if (selRaw != null)
            {
                selRawPath = IOPath.Combine(selDir, "selection.bin");
                File.WriteAllBytes(selRawPath, selRaw);
            }
            selData = new ExportData(selImgs, selRawPath, selEntropyPath);
        }

        // ── Carved regions ─────────────────────────────────────────────
        var carvedEntries = new List<(CarvedRegion r, ExportData d)>();
        if (_result!.Regions.Count > 0)
        {
            string carvedRoot = IOPath.Combine(root, "carved");
            Directory.CreateDirectory(carvedRoot);
            for (int i = 0; i < _result.Regions.Count; i++)
            {
                var region = _result.Regions[i];
                byte[]? rb = ExtractRegionBytes(region);
                if (rb == null || rb.Length < 4) continue;

                string rDir = IOPath.Combine(carvedRoot,
                    SanitizeFileName($"{i:D3}_{region.FileType}_{region.OffsetHex}"));
                Directory.CreateDirectory(rDir);

                var imgs = ExportVisualizationsForSubject(rDir, rb, null, fullFile: false);

                string ext  = GetDefaultExtension(region.FileType);
                string rRaw = IOPath.Combine(rDir, SanitizeFileName($"{region.FileType}{ext}"));
                File.WriteAllBytes(rRaw, rb);

                int bs     = _result.EntropyBlockSize;
                int bStart = (int)(region.Offset / bs);
                int bEnd   = Math.Min((int)((region.Offset + region.Size - 1) / bs),
                                      _result.EntropyMap.Length - 1);
                var blockSet      = new HashSet<int>(Enumerable.Range(bStart, bEnd - bStart + 1));
                string rEntropyPath = IOPath.Combine(rDir, "entropy.csv");
                WriteEntropyCSV(rEntropyPath, blockSet);

                carvedEntries.Add((region, new ExportData(imgs, rRaw, rEntropyPath)));
            }
        }

        // ── Manual regions ─────────────────────────────────────────────
        var manualEntries = new List<(ManualRegion r, ExportData d)>();
        if (_manualRegions.Count > 0)
        {
            string manRoot = IOPath.Combine(root, "manual");
            Directory.CreateDirectory(manRoot);
            foreach (var mr in _manualRegions)
            {
                byte[]? mb = ExtractBlockBytes(mr.PageIndices);
                if (mb == null || mb.Length < 4) continue;

                string mDir = IOPath.Combine(manRoot, SanitizeFileName(mr.Name));
                Directory.CreateDirectory(mDir);

                var imgs = ExportVisualizationsForSubject(mDir, mb, null, fullFile: false);

                string mRaw        = IOPath.Combine(mDir, SanitizeFileName(mr.Name) + ".bin");
                File.WriteAllBytes(mRaw, mb);

                string mEntropyPath = IOPath.Combine(mDir, "entropy.csv");
                WriteEntropyCSV(mEntropyPath, mr.PageIndices);

                manualEntries.Add((mr, new ExportData(imgs, mRaw, mEntropyPath)));
            }
        }

        // ── HTML report ────────────────────────────────────────────────
        string html = BuildHtmlReport(root, fullData, selData, carvedEntries, manualEntries);
        File.WriteAllText(IOPath.Combine(root, "index.html"), html, System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// Write a CSV of per-block entropy metrics.
    /// blockFilter = null → all blocks.  Otherwise only the specified block indices.
    /// Columns: Block, Offset, ByteEntropy, NibbleEntropy, BitEntropy,
    ///          BigramEntropy, Divergence, DeltaEntropy, GapClass
    /// </summary>
    private void WriteEntropyCSV(string path, IEnumerable<int>? blockFilter)
    {
        if (_result == null) return;
        int bs = _result.EntropyBlockSize;

        var blocks = blockFilter != null
            ? blockFilter.Where(b => b >= 0 && b < _result.EntropyMap.Length)
                         .OrderBy(b => b)
            : Enumerable.Range(0, _result.EntropyMap.Length);

        using var w = new StreamWriter(path, append: false, System.Text.Encoding.UTF8);
        w.WriteLine("Block,Offset,ByteEntropy,NibbleEntropy,BitEntropy," +
                    "BigramEntropy,Divergence,DeltaEntropy");

        foreach (int bi in blocks)
        {
            double byteE  = bi < _result.EntropyMap.Length      ? _result.EntropyMap[bi]       : 0;
            double nibE   = _result.NibbleEntropyMap is { } nm && bi < nm.Length ? nm[bi] : 0;
            double bitE   = _result.BitEntropyMap    is { } bm && bi < bm.Length ? bm[bi] : 0;
            double bigE   = _result.BigramEntropyMap is { } gm && bi < gm.Length ? gm[bi] : 0;
            double divE   = _result.DivergenceMap    is { } dm && bi < dm.Length ? dm[bi] : 0;
            double delE   = _result.DeltaEntropyMap  is { } de && bi < de.Length ? de[bi] : 0;
            long   offset = (long)bi * bs;
            w.WriteLine($"{bi},{offset},{byteE:F6},{nibE:F6},{bitE:F6}," +
                        $"{bigE:F6},{divE:F6},{delE:F6}");
        }
    }

    /// <summary>
    /// Render all applicable visualization modes for <paramref name="bytes"/> and save
    /// as PNGs into <paramref name="dir"/>.  Returns a name→relative-path map.
    /// fullFile=true renders all 6 modes; false renders only the 3 byte-data modes
    /// (dot plot, histogram, heatmap) which don't depend on a CarveResult.
    /// NOTE: must be called from the UI thread (WriteableBitmap requires it).
    /// </summary>
    private Dictionary<string, string> ExportVisualizationsForSubject(
        string dir, byte[] bytes, HashSet<int>? highlight, bool fullFile)
    {
        var imgs = new Dictionary<string, string>();

        var savedData = _fileData;
        _fileData = bytes;

        // All renderers now produce full-resolution bitmaps that are already live-panel
        // quality (4× upscale for grid modes via SetPlotBitmap; native block-count width
        // for the waveform).  Export just reads DotPlotImage.Source and saves it — no
        // secondary upscale needed.
        void Save(string name, Action render)
        {
            render();
            if (DotPlotImage.Source is System.Windows.Media.Imaging.WriteableBitmap bmp)
            {
                string file = IOPath.Combine(dir, name + ".png");
                SaveBitmapToPng(bmp, file);
                imgs[name] = file;
            }
        }

        // Byte-data modes: work for any byte slice
        Save("byte_pair", () => DrawDotPlot(bytes, highlight));
        Save("histogram",  () => DrawHistogram(bytes, highlight));
        Save("heatmap",    () => DrawPositionHeatMap(bytes, highlight));

        if (fullFile)
        {
            // Entropy waveform: auto-dims (one column per block, 512px tall)
            if (_result != null && _result.EntropyMap.Length > 0)
                Save("entropy_waveform", () => DrawEntropyWaveform(bytes, highlight));

            Save("parallel_coords",  () => DrawParallelCoords(bytes, highlight));
            Save("radviz",           () => DrawRadViz(bytes, highlight));
            Save("autocorrelation",  () => DrawAutocorrelation(bytes, highlight));
        }

        _fileData = savedData;
        UpdateRightPanel();   // restore the live display
        return imgs;
    }

    /// <summary>
    /// Nearest-neighbour upscale: each source pixel becomes a scale×scale block.
    /// Used to turn 256×256 visualizations into export-quality 1024×1024 PNGs.
    /// </summary>
    private static System.Windows.Media.Imaging.WriteableBitmap UpscaleBitmap(
        System.Windows.Media.Imaging.WriteableBitmap src, int scale)
    {
        int sw = src.PixelWidth, sh = src.PixelHeight;
        int dw = sw * scale,     dh = sh * scale;

        var srcPx = new byte[sw * sh * 4];
        src.CopyPixels(srcPx, sw * 4, 0);

        var dstPx = new byte[dw * dh * 4];
        for (int sy = 0; sy < sh; sy++)
        for (int sx = 0; sx < sw; sx++)
        {
            int si = (sy * sw + sx) * 4;
            for (int ky = 0; ky < scale; ky++)
            for (int kx = 0; kx < scale; kx++)
            {
                int di = ((sy * scale + ky) * dw + (sx * scale + kx)) * 4;
                dstPx[di]   = srcPx[si];
                dstPx[di+1] = srcPx[si+1];
                dstPx[di+2] = srcPx[si+2];
                dstPx[di+3] = srcPx[si+3];
            }
        }

        var dst = new System.Windows.Media.Imaging.WriteableBitmap(
            dw, dh, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        dst.WritePixels(new System.Windows.Int32Rect(0, 0, dw, dh), dstPx, dw * 4, 0);
        return dst;
    }

    /// <summary>
    /// Upscales a rendered bitmap 4× (nearest-neighbour) and sets it as the live panel
    /// image.  Every logical pixel becomes a 4×4 block, so individual data cells are
    /// clearly visible at screen resolution without requiring the user to zoom in.
    /// </summary>
    private void SetPlotBitmap(System.Windows.Media.Imaging.WriteableBitmap bmp)
        => DotPlotImage.Source = UpscaleBitmap(bmp, 4);

    private static void SaveBitmapToPng(
        System.Windows.Media.Imaging.WriteableBitmap bmp, string path)
    {
        using var fs = File.Create(path);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        enc.Save(fs);
    }

    private byte[]? ExtractRegionBytes(CarvedRegion region)
    {
        if (_fileData == null) return null;
        long start = region.Offset;
        long len   = Math.Min(region.Size, _fileData.LongLength - start);
        if (len <= 0) return null;
        var buf = new byte[len];
        Array.Copy(_fileData, start, buf, 0, len);
        return buf;
    }

    private byte[]? ExtractBlockBytes(IEnumerable<int> blockIndices)
    {
        if (_fileData == null || _result == null) return null;
        int bs     = _result.EntropyBlockSize;
        var sorted = blockIndices.OrderBy(x => x).ToList();
        if (sorted.Count == 0) return null;

        // Use contiguous span from first to last block (handles non-contiguous gracefully)
        long start = (long)sorted[0] * bs;
        long end   = Math.Min((long)(sorted[^1] + 1) * bs, _fileData.LongLength);
        long len   = end - start;
        if (len <= 0) return null;
        var buf = new byte[len];
        Array.Copy(_fileData, start, buf, 0, len);
        return buf;
    }

    private static string SanitizeFileName(string s)
    {
        foreach (char c in IOPath.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Length > 60 ? s[..60] : s;
    }

    // ── HTML report builder ──────────────────────────────────────────────

    private string BuildHtmlReport(
        string root,
        ExportData fullData,
        ExportData? selData,
        List<(CarvedRegion r, ExportData d)> carved,
        List<(ManualRegion r, ExportData d)> manual)
    {
        string Rel(string abs) =>
            IOPath.GetRelativePath(root, abs).Replace('\\', '/');

        string ImgGrid(Dictionary<string, string> imgs, string[] keys, string[] labels)
        {
            var sb2 = new System.Text.StringBuilder("<div class='grid'>");
            for (int k = 0; k < keys.Length; k++)
            {
                if (!imgs.TryGetValue(keys[k], out var path)) continue;
                string rel = Rel(path);
                sb2.Append($"<div class='cell'>" +
                    $"<a href='{rel}' target='_blank'>" +
                    $"<img src='{rel}' title='{labels[k]}'></a>" +
                    $"<div class='lbl'>{labels[k]}</div></div>");
            }
            sb2.Append("</div>");
            return sb2.ToString();
        }

        // Render the download bar (raw file + entropy CSV links)
        string Downloads(ExportData d)
        {
            var parts = new List<string>();
            if (d.RawFile != null)
            {
                string rel  = Rel(d.RawFile);
                string name = IOPath.GetFileName(d.RawFile);
                long   sz   = new FileInfo(d.RawFile).Length;
                string szHu = sz < 1024 ? $"{sz} B"
                            : sz < 1024*1024 ? $"{sz/1024.0:F1} KB"
                            : $"{sz/(1024.0*1024):F2} MB";
                parts.Add($"<a class='dl raw' href='{rel}' download>⬇ {name} ({szHu})</a>");
            }
            if (d.EntropyFile != null)
            {
                string rel  = Rel(d.EntropyFile);
                string name = IOPath.GetFileName(d.EntropyFile);
                parts.Add($"<a class='dl csv' href='{rel}' download>⬇ {name}</a>");
            }
            return parts.Count > 0
                ? $"<div class='downloads'>{string.Join("", parts)}</div>"
                : "";
        }

        string[] allKeys   = ["byte_pair","histogram","heatmap","entropy_waveform","parallel_coords","radviz"];
        string[] allLabels = ["Byte-Pair","Histogram","Pos.HeatMap","Entropy Waveform","Parallel Coords","RadViz"];
        string[] subKeys   = ["byte_pair","histogram","heatmap"];
        string[] subLabels = ["Byte-Pair","Histogram","Pos.HeatMap"];

        var sb = new System.Text.StringBuilder();
        sb.Append(HtmlHead(_result!.FileName, _result.FileSizeHuman));

        // ── Full file ──────────────────────────────────────────────────
        sb.Append("<section><h2>Full File</h2>");
        sb.Append($"<p class='meta'>{System.Net.WebUtility.HtmlEncode(_result.FileName)} · " +
                  $"{_result.FileSizeHuman} · {_result.Regions.Count} region(s) · " +
                  $"block {_result.EntropyBlockSize:N0} B</p>");
        sb.Append(Downloads(fullData));
        sb.Append(ImgGrid(fullData.Imgs, allKeys, allLabels));
        sb.Append("</section>");

        // ── Selection ──────────────────────────────────────────────────
        if (selData != null)
        {
            sb.Append("<section><h2>Current Selection</h2>");
            sb.Append($"<p class='meta'>{_selectedPageIndices.Count} block(s) · " +
                      $"block {_result.EntropyBlockSize:N0} B</p>");
            sb.Append(Downloads(selData));
            sb.Append(ImgGrid(selData.Imgs, allKeys, allLabels));
            sb.Append("</section>");
        }

        // ── Carved regions ─────────────────────────────────────────────
        if (carved.Count > 0)
        {
            sb.Append("<section><h2>Carved Regions</h2>");
            foreach (var (r, d) in carved)
            {
                sb.Append("<div class='region-card'>");
                sb.Append("<div class='region-hdr'>" +
                    $"<span class='tag'>{System.Net.WebUtility.HtmlEncode(r.FileType)}</span> " +
                    $"<span class='offset'>{r.OffsetHex}</span> · " +
                    $"{r.SizeDisplay} · {r.ConfidenceLevel}" +
                    (r.Description != r.FileType
                        ? $" · {System.Net.WebUtility.HtmlEncode(r.Description)}" : "") +
                    "</div>");
                sb.Append(Downloads(d));
                sb.Append(ImgGrid(d.Imgs, subKeys, subLabels));
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        // ── Manual regions ─────────────────────────────────────────────
        if (manual.Count > 0)
        {
            sb.Append("<section><h2>Manual Regions</h2>");
            foreach (var (mr, d) in manual)
            {
                int bs = _result.EntropyBlockSize;
                sb.Append("<div class='region-card'>");
                sb.Append("<div class='region-hdr'>" +
                    $"<span class='tag'>manual</span> " +
                    $"<b>{System.Net.WebUtility.HtmlEncode(mr.Name)}</b> · " +
                    $"{mr.BlockCount} block(s) · {mr.GetByteSize(bs):N0} B" +
                    (mr.Notes.Length > 0
                        ? $" · {System.Net.WebUtility.HtmlEncode(mr.Notes)}" : "") +
                    "</div>");
                sb.Append(Downloads(d));
                sb.Append(ImgGrid(d.Imgs, subKeys, subLabels));
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string HtmlHead(string fileName, string fileSize) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>BinaryCarver Report — {{System.Net.WebUtility.HtmlEncode(fileName)}}</title>
        <style>
        *{box-sizing:border-box;margin:0;padding:0}
        body{background:#0e0e16;color:#cdd6f4;font-family:'Segoe UI',system-ui,sans-serif;font-size:13px}
        header{background:#181825;border-bottom:1px solid #313244;padding:14px 24px;
                display:flex;align-items:center;gap:16px}
        header h1{font-size:18px;font-weight:600;color:#cba6f7}
        header .meta{color:#a6adc8;font-size:12px}
        main{padding:20px 24px;max-width:1400px;margin:0 auto}
        section{margin-bottom:32px}
        h2{font-size:15px;font-weight:600;color:#89b4fa;margin-bottom:8px;
            border-bottom:1px solid #313244;padding-bottom:4px}
        p.meta{color:#a6adc8;font-size:11px;margin-bottom:10px}
        .grid{display:flex;flex-wrap:wrap;gap:10px}
        .cell{display:flex;flex-direction:column;align-items:center;gap:4px}
        .cell img{width:400px;height:auto;image-rendering:pixelated;
                   border:1px solid #313244;border-radius:4px;
                   transition:border-color .15s}
        .cell img:hover{border-color:#89b4fa}
        .lbl{font-size:10px;color:#6c7086;text-align:center}
        .region-card{background:#181825;border:1px solid #313244;border-radius:6px;
                      padding:12px;margin-bottom:14px}
        .region-hdr{margin-bottom:10px;color:#cdd6f4;font-size:12px}
        .tag{background:#313244;border-radius:3px;padding:1px 6px;
              font-size:10px;font-weight:600;color:#a6e3a1;margin-right:4px}
        .offset{font-family:monospace;color:#f38ba8;font-size:11px}
        a{color:inherit;text-decoration:none}
        .downloads{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:10px}
        .dl{display:inline-flex;align-items:center;gap:4px;padding:3px 10px;
             border-radius:4px;font-size:11px;font-weight:500;border:1px solid;
             transition:opacity .15s}
        .dl:hover{opacity:.8}
        .dl.raw{background:#1e3a5f;border-color:#4a90d9;color:#89b4fa}
        .dl.csv{background:#1a3a28;border-color:#3a8a5a;color:#a6e3a1}
        </style>
        </head>
        <body>
        <header>
          <div>
            <h1>BinaryCarver Report</h1>
            <div class="meta">{{System.Net.WebUtility.HtmlEncode(fileName)}} · {{System.Net.WebUtility.HtmlEncode(fileSize)}} · generated {{DateTime.Now:yyyy-MM-dd HH:mm}}</div>
          </div>
        </header>
        <main>
        """;

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
