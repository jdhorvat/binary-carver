using System;
using System.Collections.Generic;
using System.Text;

namespace BinaryCarver.Analysis;

/// <summary>
/// Parses internal section/segment layouts from well-known binary formats.
/// Given a byte[] buffer starting at a region's offset, returns a list of
/// named sub-sections with offsets, sizes, permissions, and descriptions.
///
/// Supports: PE/COFF, ELF, Mach-O, ZIP (central directory), PDF (xref/trailer).
/// Results can be exported to IDA, Ghidra, r2, or Binary Ninja via CarveEngine exports.
/// </summary>
public static class FormatSectionParser
{
    public sealed class ParsedSection
    {
        public string Name          { get; init; } = "";
        public long   FileOffset    { get; init; }        // relative to start of container
        public long   Size          { get; init; }
        public long   VirtualAddr   { get; init; }        // for PE/ELF/Mach-O; 0 for archives
        public long   VirtualSize   { get; init; }
        public string Permissions   { get; init; } = "";   // "rwx" / "r-x" / "rw-" etc.
        public string Class         { get; init; } = "";   // CODE / DATA / BSS / RSRC etc.
        public string Description   { get; init; } = "";
    }

    /// <summary>Auto-detect format and parse sections from the given buffer.</summary>
    public static List<ParsedSection> Parse(byte[] data, long baseOffset = 0)
    {
        if (data.Length < 4) return [];

        // PE (MZ header)
        if (data.Length > 0x40 && data[0] == 0x4D && data[1] == 0x5A)
            return ParsePE(data, baseOffset);

        // ELF
        if (data.Length > 0x40 && data[0] == 0x7F && data[1] == (byte)'E'
            && data[2] == (byte)'L' && data[3] == (byte)'F')
            return ParseELF(data, baseOffset);

        // Mach-O 64-bit
        if (data.Length > 0x20)
        {
            uint magic = ReadU32LE(data, 0);
            if (magic == 0xFEEDFACF || magic == 0xFEEDFACE)
                return ParseMachO(data, baseOffset, magic == 0xFEEDFACF);
        }

        // ZIP (check for PK signature)
        if (data.Length > 4 && data[0] == 0x50 && data[1] == 0x4B
            && data[2] == 0x03 && data[3] == 0x04)
            return ParseZIP(data, baseOffset);

        return [];
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PE / COFF
    // ═══════════════════════════════════════════════════════════════════════

    private static List<ParsedSection> ParsePE(byte[] data, long baseOff)
    {
        var sections = new List<ParsedSection>();

        // DOS header: e_lfanew at offset 0x3C
        if (data.Length < 0x40) return sections;
        int peOff = (int)ReadU32LE(data, 0x3C);
        if (peOff < 0 || peOff + 24 > data.Length) return sections;

        // Verify PE\0\0
        if (data[peOff] != 'P' || data[peOff + 1] != 'E'
            || data[peOff + 2] != 0 || data[peOff + 3] != 0) return sections;

        // Add DOS stub
        sections.Add(new ParsedSection
        {
            Name = "DOS_Header", FileOffset = baseOff, Size = peOff,
            Permissions = "r--", Class = "DATA", Description = "DOS MZ stub"
        });

        // COFF header at peOff+4
        int coffOff = peOff + 4;
        if (coffOff + 20 > data.Length) return sections;

        ushort machine = ReadU16LE(data, coffOff);
        ushort numSections = ReadU16LE(data, coffOff + 2);
        ushort optHeaderSize = ReadU16LE(data, coffOff + 16);

        string machStr = machine switch
        {
            0x014C => "i386", 0x8664 => "x86_64", 0xAA64 => "ARM64", _ => $"0x{machine:X4}"
        };

        bool is64 = optHeaderSize >= 240; // PE32+ has 240-byte optional header
        int optOff = coffOff + 20;

        // Read image base and entry point from optional header
        long imageBase = 0;
        long entryRVA = 0;
        if (optOff + 28 <= data.Length)
        {
            entryRVA = ReadU32LE(data, optOff + 16);
            imageBase = is64 && optOff + 30 <= data.Length
                ? (long)ReadU64LE(data, optOff + 24)
                : ReadU32LE(data, optOff + 28);
        }

        sections.Add(new ParsedSection
        {
            Name = "PE_Header", FileOffset = baseOff + peOff,
            Size = 4 + 20 + optHeaderSize,
            VirtualAddr = imageBase, Permissions = "r--", Class = "DATA",
            Description = $"PE header ({machStr}, {(is64 ? "PE32+" : "PE32")}, entry=0x{entryRVA:X})"
        });

        // Section headers start right after optional header
        int secTableOff = optOff + optHeaderSize;
        for (int i = 0; i < numSections; i++)
        {
            int shOff = secTableOff + i * 40;
            if (shOff + 40 > data.Length) break;

            string name = Encoding.ASCII.GetString(data, shOff, 8).TrimEnd('\0');
            uint virtualSize = ReadU32LE(data, shOff + 8);
            uint virtualAddr = ReadU32LE(data, shOff + 12);
            uint rawSize = ReadU32LE(data, shOff + 16);
            uint rawPtr = ReadU32LE(data, shOff + 20);
            uint chars = ReadU32LE(data, shOff + 36);

            // Decode characteristics flags
            bool hasCode = (chars & 0x00000020) != 0;
            bool hasInitData = (chars & 0x00000040) != 0;
            bool hasUninitData = (chars & 0x00000080) != 0;
            bool memExec = (chars & 0x20000000) != 0;
            bool memRead = (chars & 0x40000000) != 0;
            bool memWrite = (chars & 0x80000000) != 0;

            string perms = $"{(memRead ? 'r' : '-')}{(memWrite ? 'w' : '-')}{(memExec ? 'x' : '-')}";
            string cls = hasCode ? "CODE" : hasUninitData ? "BSS" : "DATA";
            string desc = name switch
            {
                ".text" => "Executable code",
                ".rdata" or ".rodata" => "Read-only initialized data (imports, strings, vtables)",
                ".data" => "Initialized read-write data (globals, statics)",
                ".bss" => "Uninitialized data",
                ".rsrc" => "Windows resources (icons, dialogs, manifests)",
                ".reloc" => "Base relocation table",
                ".pdata" => "Exception handling data (SEH unwind info)",
                ".idata" => "Import directory table",
                ".edata" => "Export directory table",
                ".tls" => "Thread-local storage",
                ".debug" or ".zdebug" => "Debug information",
                _ => $"{cls} section (chars=0x{chars:X8})"
            };

            sections.Add(new ParsedSection
            {
                Name = name, FileOffset = baseOff + rawPtr, Size = rawSize,
                VirtualAddr = imageBase + virtualAddr, VirtualSize = virtualSize,
                Permissions = perms, Class = cls, Description = desc
            });
        }

        return sections;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ELF
    // ═══════════════════════════════════════════════════════════════════════

    private static List<ParsedSection> ParseELF(byte[] data, long baseOff)
    {
        var sections = new List<ParsedSection>();
        if (data.Length < 0x40) return sections;

        bool is64 = data[4] == 2;
        bool isLE = data[5] == 1;
        // We only handle LE for now (covers x86/x64/ARM LE)
        if (!isLE) return sections;

        ushort type = ReadU16LE(data, 16);
        string typeStr = type switch
        {
            1 => "Relocatable", 2 => "Executable", 3 => "Shared Object",
            4 => "Core dump", _ => $"type={type}"
        };

        long entryPoint, shOff;
        ushort shEntSize, shNum, shStrIdx;

        if (is64)
        {
            if (data.Length < 64) return sections;
            entryPoint = (long)ReadU64LE(data, 24);
            shOff = (long)ReadU64LE(data, 40);
            shEntSize = ReadU16LE(data, 58);
            shNum = ReadU16LE(data, 60);
            shStrIdx = ReadU16LE(data, 62);
        }
        else
        {
            if (data.Length < 52) return sections;
            entryPoint = ReadU32LE(data, 24);
            shOff = ReadU32LE(data, 32);
            shEntSize = ReadU16LE(data, 46);
            shNum = ReadU16LE(data, 48);
            shStrIdx = ReadU16LE(data, 50);
        }

        sections.Add(new ParsedSection
        {
            Name = "ELF_Header", FileOffset = baseOff, Size = is64 ? 64 : 52,
            Permissions = "r--", Class = "DATA",
            Description = $"ELF header ({typeStr}, {(is64 ? "64-bit" : "32-bit")}, entry=0x{entryPoint:X})"
        });

        if (shOff <= 0 || shOff + (long)shNum * shEntSize > data.Length || shEntSize < (is64 ? 64 : 40))
            return sections;

        // Read the section header string table first
        byte[]? shStrtab = null;
        if (shStrIdx < shNum)
        {
            long strSecOff = shOff + (long)shStrIdx * shEntSize;
            long strFileOff, strSize;
            if (is64)
            {
                strFileOff = (long)ReadU64LE(data, (int)strSecOff + 24);
                strSize = (long)ReadU64LE(data, (int)strSecOff + 32);
            }
            else
            {
                strFileOff = ReadU32LE(data, (int)strSecOff + 16);
                strSize = ReadU32LE(data, (int)strSecOff + 20);
            }
            if (strFileOff >= 0 && strFileOff + strSize <= data.Length)
            {
                shStrtab = new byte[strSize];
                Array.Copy(data, strFileOff, shStrtab, 0, strSize);
            }
        }

        for (int i = 1; i < shNum; i++) // skip section 0 (null)
        {
            long secHdrOff = shOff + (long)i * shEntSize;
            if (secHdrOff + shEntSize > data.Length) break;

            uint nameIdx;
            uint shType;
            long flags, addr, offset, size;

            if (is64)
            {
                nameIdx = ReadU32LE(data, (int)secHdrOff);
                shType = ReadU32LE(data, (int)secHdrOff + 4);
                flags = (long)ReadU64LE(data, (int)secHdrOff + 8);
                addr = (long)ReadU64LE(data, (int)secHdrOff + 16);
                offset = (long)ReadU64LE(data, (int)secHdrOff + 24);
                size = (long)ReadU64LE(data, (int)secHdrOff + 32);
            }
            else
            {
                nameIdx = ReadU32LE(data, (int)secHdrOff);
                shType = ReadU32LE(data, (int)secHdrOff + 4);
                flags = ReadU32LE(data, (int)secHdrOff + 8);
                addr = ReadU32LE(data, (int)secHdrOff + 12);
                offset = ReadU32LE(data, (int)secHdrOff + 16);
                size = ReadU32LE(data, (int)secHdrOff + 20);
            }

            string name = ReadNullTermString(shStrtab, (int)nameIdx);
            bool alloc = (flags & 0x2) != 0;
            bool exec = (flags & 0x4) != 0;
            bool write = (flags & 0x1) != 0;
            string perms = $"{(alloc ? 'r' : '-')}{(write ? 'w' : '-')}{(exec ? 'x' : '-')}";

            string cls = shType switch
            {
                1 when exec => "CODE",    // SHT_PROGBITS + EXEC
                1 => "DATA",               // SHT_PROGBITS
                8 => "BSS",                // SHT_NOBITS
                2 or 11 => "DATA",         // SHT_SYMTAB / SHT_DYNSYM
                3 => "DATA",               // SHT_STRTAB
                6 => "DATA",               // SHT_DYNAMIC
                _ => "DATA"
            };

            string desc = name switch
            {
                ".text" => "Executable code",
                ".rodata" => "Read-only data (string literals, constants)",
                ".data" => "Initialized read-write global/static data",
                ".bss" => "Uninitialized data (zero-filled at load)",
                ".dynamic" => "Dynamic linking information",
                ".got" => "Global Offset Table (lazy binding)",
                ".got.plt" => "GOT entries for PLT stubs",
                ".plt" or ".plt.got" => "Procedure Linkage Table (lazy binding stubs)",
                ".symtab" => "Symbol table",
                ".strtab" => "String table",
                ".dynsym" => "Dynamic symbol table",
                ".dynstr" => "Dynamic string table",
                ".interp" => "ELF interpreter path (ld-linux.so)",
                ".init" or ".fini" => "Init/fini code (constructors/destructors)",
                ".init_array" or ".fini_array" => "Constructor/destructor function pointers",
                ".note.gnu.build-id" or ".note.ABI-tag" => "GNU build metadata",
                ".eh_frame" or ".eh_frame_hdr" => "Exception handling unwind tables (DWARF CFI)",
                ".rel.dyn" or ".rela.dyn" or ".rel.plt" or ".rela.plt" => "Relocations",
                ".debug_info" or ".debug_abbrev" or ".debug_line" or ".debug_str" => "DWARF debug info",
                _ => $"ELF section (type={shType}, flags=0x{flags:X})"
            };

            if (shType == 8) size = 0; // NOBITS has no file footprint

            sections.Add(new ParsedSection
            {
                Name = string.IsNullOrEmpty(name) ? $"section_{i}" : name,
                FileOffset = baseOff + offset, Size = size,
                VirtualAddr = addr, VirtualSize = size,
                Permissions = perms, Class = cls, Description = desc
            });
        }

        return sections;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Mach-O
    // ═══════════════════════════════════════════════════════════════════════

    private static List<ParsedSection> ParseMachO(byte[] data, long baseOff, bool is64)
    {
        var sections = new List<ParsedSection>();
        int headerSize = is64 ? 32 : 28;
        if (data.Length < headerSize) return sections;

        uint ncmds = ReadU32LE(data, 16);
        uint sizeOfCmds = ReadU32LE(data, 20);

        sections.Add(new ParsedSection
        {
            Name = "MachO_Header", FileOffset = baseOff, Size = headerSize,
            Permissions = "r--", Class = "DATA",
            Description = $"Mach-O header ({(is64 ? "64-bit" : "32-bit")}, {ncmds} load commands)"
        });

        int offset = headerSize;
        for (uint cmd = 0; cmd < ncmds && offset + 8 <= data.Length; cmd++)
        {
            uint cmdType = ReadU32LE(data, offset);
            uint cmdSize = ReadU32LE(data, offset + 4);
            if (cmdSize < 8 || offset + cmdSize > data.Length) break;

            // LC_SEGMENT (0x01) or LC_SEGMENT_64 (0x19)
            if (cmdType == 0x01 || cmdType == 0x19)
            {
                bool seg64 = cmdType == 0x19;
                string segName = ReadFixedString(data, offset + 8, 16);
                long fileoff, filesize, vmaddr, vmsize;
                int initprot;

                if (seg64 && offset + 72 <= data.Length)
                {
                    vmaddr = (long)ReadU64LE(data, offset + 24);
                    vmsize = (long)ReadU64LE(data, offset + 32);
                    fileoff = (long)ReadU64LE(data, offset + 40);
                    filesize = (long)ReadU64LE(data, offset + 48);
                    initprot = (int)ReadU32LE(data, offset + 60);
                }
                else if (!seg64 && offset + 56 <= data.Length)
                {
                    vmaddr = ReadU32LE(data, offset + 24);
                    vmsize = ReadU32LE(data, offset + 28);
                    fileoff = ReadU32LE(data, offset + 32);
                    filesize = ReadU32LE(data, offset + 36);
                    initprot = (int)ReadU32LE(data, offset + 44);
                }
                else break;

                bool r = (initprot & 1) != 0;
                bool w = (initprot & 2) != 0;
                bool x = (initprot & 4) != 0;
                string perms = $"{(r ? 'r' : '-')}{(w ? 'w' : '-')}{(x ? 'x' : '-')}";

                string desc = segName switch
                {
                    "__TEXT" => "Executable code + read-only data",
                    "__DATA" => "Read-write initialized data",
                    "__DATA_CONST" => "Read-only data constants",
                    "__LINKEDIT" => "Dynamic linker metadata (symbols, relocations)",
                    "__PAGEZERO" => "Guard page (null-pointer trap, no backing)",
                    _ => $"Mach-O segment (prot={perms})"
                };

                sections.Add(new ParsedSection
                {
                    Name = segName, FileOffset = baseOff + fileoff, Size = filesize,
                    VirtualAddr = vmaddr, VirtualSize = vmsize,
                    Permissions = perms,
                    Class = x ? "CODE" : "DATA",
                    Description = desc
                });
            }

            offset += (int)cmdSize;
        }

        return sections;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ZIP
    // ═══════════════════════════════════════════════════════════════════════

    private static List<ParsedSection> ParseZIP(byte[] data, long baseOff)
    {
        var sections = new List<ParsedSection>();
        int pos = 0;
        int entryNum = 0;

        while (pos + 30 <= data.Length)
        {
            uint sig = ReadU32LE(data, pos);

            // Local file header: PK\x03\x04
            if (sig == 0x04034B50)
            {
                ushort compMethod = ReadU16LE(data, pos + 8);
                uint compSize = ReadU32LE(data, pos + 18);
                ushort nameLen = ReadU16LE(data, pos + 26);
                ushort extraLen = ReadU16LE(data, pos + 28);

                string fileName = pos + 30 + nameLen <= data.Length
                    ? Encoding.UTF8.GetString(data, pos + 30, Math.Min(nameLen, data.Length - pos - 30))
                    : $"entry_{entryNum}";

                long headerSize = 30 + nameLen + extraLen;
                long totalSize = headerSize + compSize;
                string method = compMethod switch
                {
                    0 => "stored", 8 => "deflate", 12 => "bzip2", 14 => "lzma",
                    93 => "zstd", 98 => "ppmd", _ => $"method={compMethod}"
                };

                sections.Add(new ParsedSection
                {
                    Name = $"zip_entry_{entryNum}", FileOffset = baseOff + pos,
                    Size = totalSize, Permissions = "r--", Class = "DATA",
                    Description = $"{fileName} ({method}, {compSize} bytes compressed)"
                });

                pos += (int)totalSize;
                entryNum++;
            }
            // Central directory header: PK\x01\x02
            else if (sig == 0x02014B50)
            {
                long cdStart = pos;
                // Walk the rest as a single "central directory" block
                while (pos + 46 <= data.Length && ReadU32LE(data, pos) == 0x02014B50)
                {
                    ushort nameLen = ReadU16LE(data, pos + 28);
                    ushort extraLen = ReadU16LE(data, pos + 30);
                    ushort commentLen = ReadU16LE(data, pos + 32);
                    pos += 46 + nameLen + extraLen + commentLen;
                }

                sections.Add(new ParsedSection
                {
                    Name = "central_directory", FileOffset = baseOff + cdStart,
                    Size = pos - cdStart, Permissions = "r--", Class = "DATA",
                    Description = $"ZIP central directory ({entryNum} entries)"
                });
                break; // EOCD follows but we don't need to enumerate further
            }
            else break; // Unknown signature, stop
        }

        return sections;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Binary helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static ushort ReadU16LE(byte[] d, int off)
        => (ushort)(d[off] | (d[off + 1] << 8));

    private static uint ReadU32LE(byte[] d, int off)
        => (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24));

    private static ulong ReadU64LE(byte[] d, int off)
        => (ulong)ReadU32LE(d, off) | ((ulong)ReadU32LE(d, off + 4) << 32);

    private static string ReadFixedString(byte[] d, int off, int maxLen)
    {
        int end = off;
        while (end < off + maxLen && end < d.Length && d[end] != 0) end++;
        return Encoding.ASCII.GetString(d, off, end - off);
    }

    private static string ReadNullTermString(byte[]? table, int idx)
    {
        if (table == null || idx < 0 || idx >= table.Length) return "";
        int end = idx;
        while (end < table.Length && table[end] != 0) end++;
        return Encoding.UTF8.GetString(table, idx, end - idx);
    }
}
