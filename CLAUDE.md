# BinaryCarver — Region Carving Workbench

## Overview
A Windows WPF desktop application (.NET 9, C#, x64) that scans binary files for embedded files using magic-byte signatures, detects PE overlay data, and supports arbitrary region extraction. Think binwalk with a GUI.

**Status: Full engine with research-backed improvements, multi-base entropy analysis, divergence-fed sizing, gap classification, interactive inspection, 3D data visualization, drill-in navigation, and memory-mapped I/O.**

## Tech Stack
- .NET 9, C#, WPF (XAML), x64 only
- No NuGet dependencies (pure .NET — uses System.Text.Json, System.IO.MemoryMappedFiles from .NET 9 BCL)
- Uses `System.Windows.Forms` for FolderBrowserDialog (UseWindowsForms=true in csproj)
- Build: `build.bat` in project root
- `reference materials/` excluded from compilation via csproj ItemGroup

## Project Structure
```
BinaryCarver/
├── Analysis/
│   ├── CarveEngine.cs            # Two-pass engine + divergence sizing + gap classification + mmap I/O
│   │                              # Public API: Analyze(), ExtractRegion(), ExtractRange(), RecomputeEntropy()
│   ├── AhoCorasick.cs            # Multi-pattern matching automaton (single-pass, O(n+m+z))
│   ├── SignatureDatabase.cs      # 53+ signatures with footers, min/max sizes, specificity weighting
│   ├── FormatValidators.cs       # Per-format structural validators (PE, JPEG, ZIP, PNG, PDF, ELF, etc.)
│   └── FormatSectionParser.cs    # Parses internal section headers from PE/ELF/Mach-O/ZIP for RE tool export
├── Models/
│   ├── CarveResult.cs            # CarveResult (6 entropy maps + gaps), CarvedRegion (+SizingMethod), GapRegion, OverlayInfo
│   └── CustomSignature.cs        # CustomSignature model + CustomSignatureStore (JSON persistence)
├── MainWindow.xaml               # UI: byte map, 7-track entropy, block inspector, region grid, 3D viewport, settings bar
├── MainWindow.xaml.cs            # Code-behind: interactive maps, block extraction, 3D view, drill-in, all feature wiring
├── SignatureEditorWindow.xaml     # Dialog: custom signature editor
├── SignatureEditorWindow.xaml.cs  # Code-behind: signature editor logic
├── App.xaml / App.xaml.cs        # Theme (BinaryLens color scheme)
├── BinaryCarver.csproj
├── build.bat
└── reference materials/          # Research papers + reference codebases (excluded from build)
    ├── *.pdf                     # Bin-Carver, Scalpel, taxonomy papers
    ├── binwalk-master/           # Binwalk Rust rewrite (reference)
    ├── scalpel-master/           # Scalpel file carver (reference)
    ├── carve-exe-main/           # PE executable carver (reference)
    └── Jpeg-Carver-master/       # JPEG recovery tool (reference)
```

## Architecture

### Two-Pass Carving Engine (Analysis/CarveEngine.cs)

**Pre-pass — Entropy Analysis:**
Compute multi-base entropy BEFORE Pass 2 so that divergence data is available for boundary-aware sizing. This includes byte, nibble, bit, bigram entropy per block, cross-base divergence, and delta entropy (rate of change).

**Pass 1 — Discover + Validate:**
1. Build Aho-Corasick automaton from all 53+ magic byte patterns
2. Single-pass scan of entire file (O(n) vs O(n*k) for k signatures)
3. Handle offset-based signatures separately (TAR at 257, MP4 ftyp at 4, EXT at 0x438)
4. For each magic match, run format-specific validator → assign confidence (Invalid/Low/Medium/High)
5. Scan custom user signatures
6. Detect PE overlays

**Pass 2 — Resolve + Size:**
1. Apply signature specificity weighting (magic length → confidence adjustment)
2. De-duplicate matches at same offset (keep highest confidence)
3. Estimate sizes via four-priority system:
   - **Header-based parsing** (PE section tables, BMP size field, RIFF size, OLE sectors, SQLite pages, ELF sections)
   - **Footer scanning** with modes: FORWARD (first footer), FORWARD_NEXT (exclude footer), REVERSE (last footer — for PDF, RTF)
   - **Divergence-based boundary detection** — find next significant divergence peak (>mean+2σ) after region start
   - **Fallback** to next-signature boundary
4. Each region records its `SizingMethod` ("Header", "Footer", "Divergence", "Fallback")
5. Apply min/max size filtering per signature
6. Resolve overlapping regions (higher confidence wins)
7. Sort and index

**Post-pass — Gap Classification:**
Identify uncarved gaps between regions and classify each by byte frequency distribution (BFD):
- **Padding**: Low entropy, few unique bytes (null fill, 0xFF, alignment)
- **Text**: Medium entropy, >85% printable ASCII
- **Code**: Medium-high entropy, structured BFD with diverse byte values
- **Compressed**: Very high entropy (>7.5), near-uniform BFD
- **Structured**: Medium entropy, repetitive patterns (tables, headers, metadata)

**Public API:**
- `CarveEngine.Analyze(filePath, customSigs, recursive)` — full analysis pipeline
- `CarveEngine.ExtractRegion(filePath, region, outputPath)` — extract a carved region to disk
- `CarveEngine.ExtractRange(filePath, offset, size, outputPath)` — extract arbitrary byte range
- `CarveEngine.RecomputeEntropy(data, result, blockSize)` — recompute all entropy maps with a new block size (called when user changes block size in settings)

### Signature Specificity Weighting
Magic byte length determines intrinsic reliability. Short signatures (≤3 bytes, e.g. `MZ`) are more prone to false positives than long ones (≥12 bytes, e.g. SQLite). The engine adjusts initial confidence:
- ≤3 byte magic + Medium confidence → downgraded to Low
- ≥8 byte magic + Low confidence → upgraded to Medium
- ≥12 byte magic + Medium confidence → upgraded to High

### Aho-Corasick Scanner (Analysis/AhoCorasick.cs)
Custom implementation of the Aho-Corasick multi-pattern matching automaton. Builds a trie from all magic byte patterns with 256-entry jump tables per node, computes failure links via BFS, and scans the input in a single pass. Returns all (patternIndex, offset) matches.

### Signature Database (Analysis/SignatureDatabase.cs)
53+ signatures across 8 categories: Executables, Archives, Images, Documents, Audio, Database, Crypto, Firmware/Filesystem. Each signature defines:
- Magic bytes + optional SearchOffset
- Footer pattern + FooterMode (None/Forward/ForwardNext/Reverse)
- MinSize / MaxSize bounds
- Category for UI color-coding
- DefaultExt for extraction

New signatures vs original: CPIO, LZ4, LZMA, MP4/ftyp, SquashFS (LE+BE), CramFS, JFFS2, UBI, uImage, FIT/DTB, AndroidBoot, EXT2/3/4, SVG, JSON.

### Format Validators (Analysis/FormatValidators.cs)
Per-format structural validation beyond magic bytes, based on binwalk, Jpeg-Carver, and Bin-Carver research:

- **PE**: DOS header → PE signature → machine type → section count → section integrity (overlap/alignment) → packer detection (UPX, ASPack, Themida, VMP, Enigma)
- **ELF**: Class (32/64-bit) → endianness → version → type validation
- **JPEG**: Valid second marker → Oscar Method (0xFF00 stuffed byte frequency in 4KB sample, expected 9.7–47) → RST marker sequence validation (modulo-8 ordering)
- **ZIP**: Version → compression method → filename length/readability validation
- **PNG**: IHDR chunk present + length=13 → width/height > 0 → valid bit depth + color type + compression
- **PDF**: Version string → "obj" keyword nearby → xref table detection
- **GIF**: Width/height → global color table → valid first block type
- **BMP**: File size → reserved fields = 0 → pixel offset → valid DIB header size
- **GZIP**: Compression method = deflate → flags sanity → OS byte
- **SQLite**: Full 16-byte magic → valid page size (power of 2)
- **OLE/DOC**: Major version (3/4) → byte order → sector size power
- **FLAC**: STREAMINFO block type → block length = 34
- **MP4**: ftyp box size reasonable → brand is printable ASCII

### Multi-Base Entropy Analysis (CarveEngine.cs)
Per-block entropy is computed in four bases simultaneously, plus two derived metrics:
- **Byte entropy**: Standard 256-symbol Shannon entropy (max 8.0 bits)
- **Nibble entropy**: Each byte → two 4-bit nibbles, 16-symbol Shannon (max 4.0 bits)
- **Bit entropy**: 1-bits vs 0-bits, 2-symbol Shannon (max 1.0 bit)
- **Bigram entropy**: Consecutive byte pairs, up to 65536 symbols (max 16.0 bits, uses Dictionary for sparse blocks)
- **Divergence**: Normalize each base to 0–1, compute standard deviation of the four normalized values. High divergence = structural boundary where bases disagree. Fed back into Pass 2 for sizing.
- **Delta entropy**: `|entropy[i] - entropy[i-1]|` — rate of change between adjacent blocks. Spikes mark transitions regardless of absolute entropy level.

The key insight: some embedded file boundaries are invisible in standard byte entropy but produce sharp transitions in nibble or bit entropy (e.g. hex-encoded payloads, BCD data, bit-stuffed protocols). The divergence track synthesizes all four signals into a single boundary detector, and delta entropy catches transitions that raw entropy misses.

### Memory-Mapped File I/O
Files >64MB are read via `MemoryMappedFile` instead of `File.ReadAllBytes` to reduce peak heap pressure. The OS pages data in on demand during the copy to `byte[]`, avoiding the double-buffering overhead of the BCL file reader. Maximum supported file size is ~2GB (C# array limit).

### UI Features
- **Top bar**: Back (drill-in navigation), Open, Recursive toggle, Extract Selected/All, Open in BinaryLens, Signatures, Export
- **Byte map**: Color-coded region visualization with gap classification overlay (Padding/Text/Code/Compressed/Structured)
- **Multi-base entropy heatmaps**: Seven tracks:
  - **Byte** (base-256, max 8.0): Standard Shannon entropy — detects encryption/compression
  - **Nibble** (base-16, max 4.0): Reveals hex-structured data (e.g. ASCII hex dumps, BCD)
  - **Bit** (base-2, max 1.0): Exposes bit-bias and fill patterns (null padding, 0xFF fill)
  - **Bigram** (byte-pairs, max 16.0): Captures sequential byte correlations and structure
  - **Divergence** (0–1): Cross-base disagreement; bright = structural boundary
  - **Delta** (rate of change): Entropy transitions between adjacent blocks; spikes = boundaries
  - All tracks share the same block grid (default 1024 bytes) with per-block tooltips
  - Entropy tracks use blue→green→yellow→orange→red gradient; divergence/delta use dark→amber→white
- **Interactive byte map**: Click a region → select in grid. Click a gap → open block inspector.
- **Interactive entropy tracks**: Click any track → open block inspector at that offset. Ctrl-click to multi-select. Shift-click for range selection.
- **Entropy block inspector**: Shows block address range (offset, size, hex range), all six entropy values + divergence + delta, gap classification if applicable. Buttons: "Extract Block" (save single block), "Extract to Next Boundary" (save from block to next divergence peak), "Drill In", "Drill In Range".
- **Region grid**: Icon, index, type (depth-indented), offset, size, entropy, depth, confidence, sizing method, signature
- **Detail panel**: Region properties (including sizing method) + hex preview + Drill In button
- **Custom range extraction**: Offset + Size inputs (hex/decimal) → Extract Range
- **Signature editor**: Modal dialog for custom binary/text signatures, persisted to JSON
- **BinaryLens integration**: Launch on carved PE files with auto-extraction
- **Export**: JSON, CSV, IDA MAP, IDAPython, Ghidra Script, r2 Script, Binary Ninja Script. RE tool exports include internal section detail (PE .text/.data, ELF segments, Mach-O load commands, ZIP entries) parsed via `FormatSectionParser`.
- **Import MAP**: Load region definitions from IDA MAP files or BinaryCarver JSON exports — merges into current results, skips duplicates.
- **User-adjustable settings bar**:
  - Block Size (256/512/1024/2048/4096/8192): Re-computes all entropy maps and redraws via `CarveEngine.RecomputeEntropy`. 3D page size always matches block size (unified — no separate control).
  - Color Mode (8 modes): Entropy, Nibble, Bit, Bigram, Divergence, Delta, Region Type, Gap Class — applied to 3D view. Changing color mode preserves camera position.
- **3D Data Visualization** (Viewport3D):
  - Rectangular prism of colored cubes — each cube represents one data page
  - Dimensions auto-computed via cubic root for roughly cubic shape (capped at 4096 pages)
  - Color-batched meshes for performance (cubes grouped by color → single GeometryModel3D per group)
  - Spherical camera: mouse drag rotates (theta/phi), scroll wheel zooms
  - Zoom-extents: camera distance computed from bounding sphere radius / sin(FOV/2) to fit entire prism
  - Click a cube → shows block inspector for that page's entropy block
  - Ctrl-click → toggle individual cubes in multi-selection
  - Shift-click → range-select from last selection to clicked cube
  - Selection highlights rendered on separate `HighlightModel3D` overlay (doesn't rebuild data cubes or reset camera)
  - Right-click context menu: Select All Color/Size/Size&Color, Filter by Color/Size/Size&Color/Type, Isolate, Drill Into, Extract Selection, Clear Filter/Selection
  - Filter system: `_filterSet` (HashSet) — non-matching pages render as tiny dark ghosts (25% scale), preserving spatial context. Filter label shown in status bar and 3D info overlay.
  - Show/hide toggle button in settings bar
- **Drill-in navigation**:
  - Drill into a carved region or entropy block range → extracts to GUID-suffixed temp file, runs full engine on it
  - Multi-level drill supported via `Stack<(string, CarveResult)>` — can drill multiple levels deep
  - "◀ Back" button in toolbar pops the stack and restores parent file with all visuals
  - Temp files use GUID suffix to avoid collision when re-drilling same offset
- **Debounced resize**: 150ms DispatcherTimer prevents UI thrashing during window resize
- **Deferred initial render**: Dispatcher.BeginInvoke at Loaded priority ensures ActualWidth is valid before first draw

### Key Implementation Details (MainWindow.xaml.cs)

**Field summary (important state):**
- `_result: CarveResult?` — current analysis result
- `_filePath: string?` — current file being analyzed
- `_drillStack: Stack<(string FilePath, CarveResult Result)>` — parent state for drill-in Back navigation
- `_inspectedBlockIndex: int` — currently inspected entropy block (-1 = none)
- `_rangeStartBlock / _rangeEndBlock: int` — shift-click range selection bounds
- `_selectedPageIndices: HashSet<int>` — multi-selected 3D page indices for highlight
- `_3dPagesPerRow / _3dPagesPerCol / _3dNumPages` — cached grid layout for 3D hit-testing (page size = `_result.EntropyBlockSize`, no separate field)
- `_camTheta / _camPhi / _camDist` — spherical camera state for 3D view

**3D architecture (two separate ModelVisual3D elements):**
- `DataModel3D` — holds the color-batched cube meshes (rebuilt only when data/color-mode/page-size changes)
- `HighlightModel3D` — holds the semi-transparent white overlay cubes (rebuilt independently on selection changes via `UpdateHighlight3D()`, never touches data model or camera)

**Click vs drag in 3D:** Mouse-down records position, mouse-up checks if distance > 4px — if no, fires `Viewport3D_Click()`; if yes, it was a rotation drag. This prevents selection from fighting with rotation.

**Type disambiguation:** The project imports both `System.Windows` and `System.Windows.Forms` (for FolderBrowserDialog). This causes ambiguity on `Point` and `MouseEventArgs`. All 3D-related uses must be fully qualified: `System.Windows.Point`, `System.Windows.Input.MouseEventArgs`.

## Build
```
build.bat   # Requires .NET 9 SDK on Windows
```
- `reference materials/` is excluded from compilation via `<Compile Remove>` in csproj
- Cannot build in Cowork sandbox (no .NET SDK). Verify via Python structural validation scripts.
- **Validation approach**: XML parse check on all XAML files, proper brace-counting (handling strings, comments, interpolation) on all C# files.

## Research Sources
The engine architecture incorporates techniques from:
- **Binwalk** (Rust): Aho-Corasick scanning, confidence scoring, offset jumping, structural validation
- **Scalpel**: Two-pass carving, footer modes (FORWARD/REVERSE/NEXT), Boyer-Moore matching, min/max filtering
- **Bin-Carver papers**: PE/ELF section table validation, road-map-based reassembly concepts
- **Jpeg-Carver**: Oscar Method (stuffed byte frequency), RST marker validation, multi-tier confidence
- **Taxonomy paper**: Entropy-based fragment classification, byteplot visualization concepts, BFD gap classification

## Session Continuation Notes

### Known Issues / Gotchas
- **Type ambiguity**: `System.Windows.Forms` is imported for FolderBrowserDialog. Any use of `Point` or `MouseEventArgs` in 3D code must be fully qualified (`System.Windows.Point`, `System.Windows.Input.MouseEventArgs`) or you get CS0104.
- **No .NET SDK in Cowork sandbox**: Cannot build/test. All validation is done via Python scripts (XML parsing, brace counting with proper string/comment handling).
- **Naive brace counting fails**: A regex-based brace counter gives false mismatches because of braces inside `$"..."` interpolated strings, `@"..."` verbatim strings, and switch expressions. The proper approach is a character-by-character parser that tracks string/comment context.

### What's Working (as of last session)
- Full two-pass engine with all improvements
- Multi-base entropy (byte, nibble, bit, bigram, divergence, delta)
- 7-track entropy visualization with interactive click → block inspector
- 3D data visualization with rotate/zoom, click-to-inspect, multi-select (Ctrl/Shift)
- Drill-in with multi-level stack navigation + Back button
- Settings bar (block size, color mode, 3D page size) all functional
- Color mode change preserves camera; zoom-extents on first show / page size change
- Selection highlight on separate ModelVisual3D (doesn't break rotation)
- GUID-suffixed temp files prevent drill-in collisions
- Debounced resize, deferred initial render

### Companion App
BinaryLens — a PE analyzer. BinaryCarver can launch it on carved PE regions via the "Open in BinaryLens" button.
