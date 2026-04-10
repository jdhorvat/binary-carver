# BinaryCarver

A Windows desktop application for scanning binary files to find embedded files, analyze entropy patterns, and export region maps to popular analysis tools. Think **binwalk with a GUI** — plus 5-mode 3D visualization, 8 interactive 2D analysis views with wrap-width data folding, multi-base entropy analysis, and one-click export to IDA Pro, Ghidra, radare2, and Binary Ninja.

Built with .NET 9, C#, WPF. Zero external dependencies.

## Features

### Carving Engine
- **Two-pass architecture**: Aho-Corasick multi-pattern scan (O(n+m+z)) followed by per-format structural validation
- **53+ file signatures**: PE, ELF, Mach-O, ZIP, GZIP, 7Z, RAR, PNG, JPEG, GIF, BMP, PDF, SQLite, and many more — with footer detection, min/max sizes, and specificity weighting
- **Format validators**: Deep structural parsing for PE (section tables, packer detection), ZIP (central directory), PNG (chunk chain), JPEG (Oscar Method + RST markers), PDF (xref/trailer), ELF (program headers), and more
- **Confidence scoring**: Low / Medium / High based on structural validation depth
- **Boundary detection**: Four sizing strategies — Header parsing, Footer search, Divergence-fed entropy boundary, Fallback heuristic
- **Gap classification**: Uncarved regions classified as Padding, Text, Code, Compressed, or Structured via byte frequency analysis
- **Recursive carving**: Optionally scan inside carved regions for nested embedded files
- **Memory-mapped I/O**: Handles large files (>64MB) efficiently via `MemoryMappedFile`

### Multi-Base Entropy Analysis
Six simultaneous entropy tracks computed per block:

- **Byte** (base-256, 0–8 bits): Standard Shannon entropy — detects encryption/compression
- **Nibble** (base-16, 0–4 bits): Reveals hex-structured data (BCD, hex dumps)
- **Bit** (base-2, 0–1 bit): Exposes bit-bias and fill patterns (null padding, 0xFF fill)
- **Bigram** (byte-pairs, 0–16 bits): Captures sequential byte correlations
- **Divergence** (cross-base): When entropy bases disagree, there's a structural boundary
- **Delta** (rate of change): Entropy transitions between blocks — spikes mark boundaries

### 3D Data Visualization
Five layout modes for exploring file data spatially:

- **Auto Cube**: Cubic-root auto-sizing for roughly cubic shape
- **Wrap Grid**: Rectangular grid with configurable W×H folding
- **Flat Grid**: 2D grid layout with adjustable columns and rows
- **HCP Spheres**: Hexagonal close-packed kissing tangential spheres — true crystallographic packing geometry
- **Voxel Volume**: Byte-level D×D×D volumetric rendering with slice-based textured quads

All modes support:
- Three shape modes: Cubes, Bars (height = value), Spheres (radius = value) — HCP forces spheres
- Adjustable block spread factor
- Spherical camera with mouse drag rotation and scroll zoom
- Click, Ctrl-click, Shift-click, double-click (flood-fill) selection
- Right-click context menu: Select/Filter by Color, Size, Type; Isolate; Drill Into; Extract; Add to Region
- Filter system: non-matching pages render as tiny dark ghosts for spatial context
- Eight color modes: Byte/Nibble/Bit/Bigram Entropy, Divergence, Delta, Region Type, Gap Class

3D controls toolbar sits directly above the viewport with a wrapping layout that adapts to column width.

### 2D Visualizations (Right Panel)
Eight interactive visualization modes with universal wrap-width data folding:

- **Byte-Pair Plot**: 256×256 dot plot of consecutive byte pairs — reveals byte-pair correlations
- **Byte Histogram**: Byte frequency distribution; switches to positional entropy chart when wrap width is active
- **Position Heat Map**: 2D heatmap of byte values at each position within the wrap period
- **Entropy Waveform**: Block entropy bar chart; auto-switches to byte-level folded heatmap when block-level fold is too small (<8 blocks)
- **Parallel Coordinates**: 6-axis polyline plot (one line per block across entropy dimensions) with fold averaging
- **RadViz**: Radial visualization of block entropy vectors with fold averaging
- **Autocorrelation**: 2D heatmap of lag correlation per block; skips folding when fold period is too small
- **Byte Presence**: 256-column frequency map where rows = file windows — reveals text bands, compression uniformity, encoding transitions

Default "All Views" tile overview shows all 8 as clickable thumbnails. Click any tile for full-size interactive mode with pan, zoom, and rubber-band selection.

### Analysis Tool Integration
Export region maps (with parsed internal section detail) to:

- **IDA Pro MAP** (.map) — native import
- **IDAPython** (.py) — segments, comments, colors with PE/ELF/Mach-O section detail
- **Ghidra Script** (.py) — bookmarks, comments, labels with section annotations
- **radare2** (.r2) — sections, flags, comments
- **Binary Ninja** (.py) — segments, sections, comments
- **JSON** / **CSV** — full metadata export

**Import**: Load IDA MAP files or BinaryCarver JSON exports to merge external region definitions.

### Manual Regions
- Create user-defined regions from selected 3D blocks
- Visually blended in 3D (70% region color, 30% entropy)
- Non-contiguous block selection supported
- Export/Import (JSON, CSV, IDA MAP, r2 script)
- 8-color palette for visual distinction

### UI Layout
- **Top**: File info + action buttons (Open, Extract, Export, Signatures, BinaryLens integration)
- **Left panel** (3 sections, proportions 2:2:5): Carved Regions | Manual Regions | Block Inspector/Detail
- **Center**: 3D viewport with controls toolbar above
- **Shared controls strip**: Block size and color mode selectors spanning the viewport and 2D panel
- **Right**: 2D visualization panel with mode selector and parameter strip
- **Bottom**: 6-track entropy bars (Byte, Nibble, Bit, Bigram, Divergence, Delta)
- Adjustable block size (64–8192), color mode, wrap width, stride, max lag
- Drill-in navigation with multi-level stack and Back button
- Drag-and-drop file loading, debounced resize

## Download

Grab the latest pre-built binary from [Releases](../../releases). Extract and run `BinaryCarver.exe` — no install needed, no dependencies.

## Building from Source

Requires .NET 9 SDK (Windows, x64).

```
cd code
build.bat
```

Or manually:
```
cd code
dotnet build -c Release -r win-x64
```

## Usage

1. Launch `BinaryCarver.exe`
2. Drop a binary file onto the window (or click Open File)
3. Explore the byte map, entropy tracks, and region grid
4. Click entropy tracks to inspect individual blocks
5. Use the 3D controls above the viewport to switch layout modes and adjust parameters
6. Right-click the 3D view to filter, select, drill in, or extract
7. Use the right panel to view 2D visualizations — adjust wrap width to reveal periodic structure
8. Use Export to generate scripts for your analysis tool of choice

## License

MIT
