
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace IconFontTemplate.SourceGenerator;

[Generator]
public sealed class FluentGlyphGenerator : ISourceGenerator
{
    private const string DefaultFontFileName = "FluentSystemIcons-Regular.ttf";

    private static readonly DiagnosticDescriptor MissingFontDescriptor = new(
        id: "IFMT001",
        title: "Font metadata missing",
        messageFormat: "Unable to locate {0} as an AdditionalFile. Glyph constants will not be generated.",
        category: "IconFont.Maui.Template",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ParseFontDescriptor = new(
        id: "IFMT002",
        title: "Font parsing failed",
        messageFormat: "Failed to parse {0}: {1}",
        category: "IconFont.Maui.Template",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InfoDescriptor = new(
        id: "IFMT900",
        title: "IconFont generator info",
        messageFormat: "{0}",
        category: "IconFont.Maui.Template",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context)
    {
        var ttfFiles = context.AdditionalFiles
            .Where(file => string.Equals(Path.GetExtension(file.Path), ".ttf", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var configMap = ParseConfig(context.AdditionalFiles);

        context.ReportDiagnostic(Diagnostic.Create(InfoDescriptor, Location.None, $"AdditionalFiles={context.AdditionalFiles.Length}, TtfFiles={ttfFiles.Count}"));

        if (ttfFiles.Count == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingFontDescriptor, Location.None, DefaultFontFileName));
            return;
        }

        foreach (var fontFile in ttfFiles)
        {
            var opts = context.AnalyzerConfigOptions.GetOptions(fontFile);
            opts.TryGetValue("build_metadata.AdditionalFiles.IconFontFile", out var fontFileName);
            opts.TryGetValue("build_metadata.AdditionalFiles.IconFontClass", out var iconClassName);
            opts.TryGetValue("build_metadata.AdditionalFiles.IconFontNamespace", out var iconNamespace);

            fontFileName = string.IsNullOrWhiteSpace(fontFileName) ? Path.GetFileName(fontFile.Path) : fontFileName;
            var fileStem = Path.GetFileNameWithoutExtension(fontFileName);
            iconClassName = string.IsNullOrWhiteSpace(iconClassName) ? null : iconClassName!;
            iconNamespace = string.IsNullOrWhiteSpace(iconNamespace) ? null : iconNamespace!;

            context.ReportDiagnostic(Diagnostic.Create(InfoDescriptor, Location.None, $"File={fontFile.Path}; Class={iconClassName ?? ""}; Namespace={iconNamespace ?? ""}"));
            if (configMap.TryGetValue(Path.GetFileName(fontFile.Path), out var cfg))
            {
                if (string.IsNullOrWhiteSpace(iconClassName)) iconClassName = cfg.ClassName;
                if (string.IsNullOrWhiteSpace(iconNamespace)) iconNamespace = cfg.Namespace;
            }
            if (string.IsNullOrWhiteSpace(iconClassName)) iconClassName = DeriveClassName(fileStem);
            if (string.IsNullOrWhiteSpace(iconNamespace)) iconNamespace = "IconFontTemplate";

            if (!FileExists(fontFile.Path))
            {
                context.ReportDiagnostic(Diagnostic.Create(MissingFontDescriptor, Location.None, fontFileName));
                continue;
            }

            try
            {
                using var stream = OpenRead(fontFile.Path);

                var tables = OpenTypeReader.ReadTableDirectory(stream);
                if (!tables.TryGetValue("post", out var postRecord) || !tables.TryGetValue("cmap", out var cmapRecord))
                {
                    context.ReportDiagnostic(Diagnostic.Create(ParseFontDescriptor, Location.None, fontFileName, "Required 'post' or 'cmap' table not found"));
                    continue;
                }

                var glyphNames = OpenTypeReader.ReadGlyphNames(stream, postRecord);
                var codepointToGlyph = OpenTypeReader.ReadCmapMappings(stream, cmapRecord);

                if (glyphNames.Count == 0 || codepointToGlyph.Count == 0)
                {
                    context.ReportDiagnostic(Diagnostic.Create(ParseFontDescriptor, Location.None, fontFileName, "Glyph names or cmap mappings could not be extracted"));
                    continue;
                }

                var glyphsByStyle = new SortedDictionary<string, List<GlyphEntry>>(StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var kvp in codepointToGlyph)
                {
                    var codepoint = kvp.Key;
                    var glyphIndex = kvp.Value;

                    if (!glyphNames.TryGetValue(glyphIndex, out var rawName) || string.IsNullOrWhiteSpace(rawName))
                    {
                        continue;
                    }

                    if (!TryParseGlyphName(rawName, out var styleName, out var constantName))
                    {
                        continue;
                    }

                    var uniquenessKey = styleName + ":" + constantName;
                    if (!seen.Add(uniquenessKey))
                    {
                        continue;
                    }

                    if (!glyphsByStyle.TryGetValue(styleName, out var list))
                    {
                        list = new List<GlyphEntry>();
                        glyphsByStyle.Add(styleName, list);
                    }

                    list.Add(new GlyphEntry(constantName, rawName, codepoint));
                }

                if (glyphsByStyle.Count == 0)
                {
                    context.ReportDiagnostic(Diagnostic.Create(ParseFontDescriptor, Location.None, fontFileName, "No glyphs matched the expected naming pattern"));
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(InfoDescriptor, Location.None, $"Parsed styles={glyphsByStyle.Count} for {iconClassName}"));
                var source = GenerateSource(glyphsByStyle, iconNamespace!, iconClassName!);
                var hintName = $"{iconClassName!}.Generated.g.cs";
                context.AddSource(hintName, SourceText.From(source, Encoding.UTF8));
                context.ReportDiagnostic(Diagnostic.Create(InfoDescriptor, Location.None, $"Emitted {hintName}"));
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(Diagnostic.Create(ParseFontDescriptor, Location.None, fontFileName!, ex.Message));
            }
        }
    }

    private static string GenerateSource(SortedDictionary<string, List<GlyphEntry>> glyphsByStyle, string iconNamespace, string iconClassName)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
                builder.AppendLine("// Generated by IconFont.Maui.Template.SourceGenerator");
        builder.AppendLine($"namespace {iconNamespace};");
        builder.AppendLine();

        foreach (var kvp in glyphsByStyle)
        {
            var styleName = kvp.Key;
            var flatClassName = iconClassName + styleName;
            var glyphs = kvp.Value.OrderBy(g => g.ConstantName, StringComparer.Ordinal).ToList();

            builder.AppendLine($"public static partial class {flatClassName}");
            builder.AppendLine("{");

            foreach (var glyph in glyphs)
            {
                builder.AppendLine($"    /// <summary>Glyph '{glyph.RawName}' mapped to U+{glyph.Codepoint:X4}.</summary>");
                builder.AppendLine($"    public const string {glyph.ConstantName} = \"{EncodeCodepoint(glyph.Codepoint)}\";");
                builder.AppendLine();
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static bool TryParseGlyphName(string rawName, out string styleName, out string constantName)
    {
        styleName = "Regular";
        constantName = string.Empty;

        if (string.IsNullOrWhiteSpace(rawName))
        {
            return false;
        }

        var working = rawName;

        const string RegularSuffix = "_regular";
        const string FilledSuffix = "_filled";
        const string RtlSuffix = "_rtl";
        const string LtrSuffix = "_ltr";

        if (working.EndsWith(RegularSuffix, StringComparison.OrdinalIgnoreCase))
        {
            styleName = "Regular";
            working = working.Substring(0, working.Length - RegularSuffix.Length);
        }
        else if (working.EndsWith(FilledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            styleName = "Filled";
            working = working.Substring(0, working.Length - FilledSuffix.Length);
        }
        else if (working.EndsWith(RtlSuffix, StringComparison.OrdinalIgnoreCase))
        {
            styleName = "Rtl";
            working = working.Substring(0, working.Length - RtlSuffix.Length);
        }
        else if (working.EndsWith(LtrSuffix, StringComparison.OrdinalIgnoreCase))
        {
            styleName = "Ltr";
            working = working.Substring(0, working.Length - LtrSuffix.Length);
        }

        const string Prefix = "ic_fluent_";
        if (working.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            working = working.Substring(Prefix.Length);
        }

        var segments = working.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            var formatted = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(segment.ToLowerInvariant());
            sb.Append(RemoveNonAlphaNumeric(formatted));
        }

        if (sb.Length == 0)
        {
            return false;
        }

        constantName = sb.ToString();
        if (char.IsDigit(constantName[0]))
        {
            constantName = "Glyph" + constantName;
        }

        return true;
    }

    private static string RemoveNonAlphaNumeric(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string EncodeCodepoint(uint codepoint)
    {
        return codepoint <= 0xFFFF ? $@"\u{codepoint:X4}" : $@"\U{codepoint:X8}";
    }

    private readonly struct GlyphEntry
    {
        public GlyphEntry(string constantName, string rawName, uint codepoint)
        {
            ConstantName = constantName;
            RawName = rawName;
            Codepoint = codepoint;
        }

        public string ConstantName { get; }
        public string RawName { get; }
        public uint Codepoint { get; }
    }

private static class OpenTypeReader
{
    internal static Dictionary<string, TableRecord> ReadTableDirectory(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var reader = new BigEndianReader(stream);

        reader.ReadUInt32(); // scaler type
        ushort numTables = reader.ReadUInt16();
        reader.Skip(6);

        var tables = new Dictionary<string, TableRecord>(StringComparer.Ordinal);
        for (int i = 0; i < numTables; i++)
        {
            var tagBytes = reader.ReadBytes(4);
            var tag = Encoding.ASCII.GetString(tagBytes);
            reader.ReadUInt32();
            uint offset = reader.ReadUInt32();
            uint length = reader.ReadUInt32();
            tables[tag] = new TableRecord(offset, length);
        }

        return tables;
    }

    internal static Dictionary<ushort, string> ReadGlyphNames(Stream stream, TableRecord postRecord)
    {
        var reader = new BigEndianReader(stream);
        reader.Seek(postRecord.Offset);

        uint format = reader.ReadUInt32();
        if (format != 0x00020000)
        {
            return new Dictionary<ushort, string>();
        }

        reader.Skip(28);
        ushort numGlyphs = reader.ReadUInt16();
        var glyphNameIndex = new ushort[numGlyphs];
        for (int i = 0; i < numGlyphs; i++)
        {
            glyphNameIndex[i] = reader.ReadUInt16();
        }

        int maxIndex = glyphNameIndex.Length > 0 ? glyphNameIndex.Max(i => (int)i) : -1;
        int customCount = Math.Max(0, maxIndex - MacStandardGlyphNames.Length + 1);
        var customNames = new List<string>(customCount);
        for (int i = 0; i < customCount; i++)
        {
            int length = reader.ReadByte();
            if (length < 0)
            {
                break;
            }

            var data = length > 0 ? reader.ReadBytes(length) : Array.Empty<byte>();
            customNames.Add(Encoding.ASCII.GetString(data));
        }

        var names = new Dictionary<ushort, string>();
        for (ushort glyphIndex = 0; glyphIndex < glyphNameIndex.Length; glyphIndex++)
        {
            int index = glyphNameIndex[glyphIndex];
            string? glyphName = index < MacStandardGlyphNames.Length
                ? MacStandardGlyphNames[index]
                : GetCustomName(customNames, index - MacStandardGlyphNames.Length);

            if (!string.IsNullOrWhiteSpace(glyphName))
            {
                names[glyphIndex] = glyphName!;
            }
        }

        return names;
    }

    internal static Dictionary<uint, ushort> ReadCmapMappings(Stream stream, TableRecord cmapRecord)
    {
        var reader = new BigEndianReader(stream);
        reader.Seek(cmapRecord.Offset);

        reader.ReadUInt16();
        ushort numTables = reader.ReadUInt16();

        var subtables = new List<CmapSubtable>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            ushort platformId = reader.ReadUInt16();
            ushort encodingId = reader.ReadUInt16();
            uint offset = reader.ReadUInt32();
            subtables.Add(new CmapSubtable(platformId, encodingId, offset));
        }

        foreach (var subtable in subtables.OrderByDescending(GetPriority))
        {
            var mapping = TryReadFormat12(stream, cmapRecord.Offset + subtable.Offset);
            if (mapping != null)
            {
                return mapping;
            }
        }

        foreach (var subtable in subtables.OrderByDescending(GetPriority))
        {
            var mapping = TryReadFormat4(stream, cmapRecord.Offset + subtable.Offset);
            if (mapping != null)
            {
                return mapping;
            }
        }

        return new Dictionary<uint, ushort>();
    }

    private static Dictionary<uint, ushort>? TryReadFormat12(Stream stream, long offset)
    {
        var reader = new BigEndianReader(stream);
        reader.Seek(offset);

        ushort format = reader.ReadUInt16();
        if (format != 12)
        {
            return null;
        }

        reader.ReadUInt16();
        reader.ReadUInt32();
        reader.ReadUInt32();
        uint numGroups = reader.ReadUInt32();

        var map = new Dictionary<uint, ushort>();
        for (uint i = 0; i < numGroups; i++)
        {
            uint startCode = reader.ReadUInt32();
            uint endCode = reader.ReadUInt32();
            uint startGlyph = reader.ReadUInt32();
            for (uint code = startCode; code <= endCode; code++)
            {
                map[code] = (ushort)(startGlyph + (code - startCode));
            }
        }

        return map;
    }

    private static Dictionary<uint, ushort>? TryReadFormat4(Stream stream, long offset)
    {
        var reader = new BigEndianReader(stream);
        reader.Seek(offset);

        ushort format = reader.ReadUInt16();
        if (format != 4)
        {
            return null;
        }

        ushort length = reader.ReadUInt16();
        reader.ReadUInt16();
        ushort segCountX2 = reader.ReadUInt16();
        int segCount = segCountX2 / 2;
        reader.ReadUInt16();
        reader.ReadUInt16();
        reader.ReadUInt16();

        var endCount = new ushort[segCount];
        for (int i = 0; i < segCount; i++)
        {
            endCount[i] = reader.ReadUInt16();
        }

        reader.ReadUInt16();

        var startCount = new ushort[segCount];
        for (int i = 0; i < segCount; i++)
        {
            startCount[i] = reader.ReadUInt16();
        }

        var idDelta = new short[segCount];
        for (int i = 0; i < segCount; i++)
        {
            idDelta[i] = unchecked((short)reader.ReadUInt16());
        }

        var idRangeOffset = new ushort[segCount];
        for (int i = 0; i < segCount; i++)
        {
            idRangeOffset[i] = reader.ReadUInt16();
        }

        int glyphArrayLength = Math.Max(0, (length / 2) - 8 - segCount * 4);
        var glyphIdArray = new ushort[glyphArrayLength];
        for (int i = 0; i < glyphArrayLength; i++)
        {
            glyphIdArray[i] = reader.ReadUInt16();
        }

        var map = new Dictionary<uint, ushort>();
        for (int seg = 0; seg < segCount; seg++)
        {
            ushort start = startCount[seg];
            ushort end = endCount[seg];
            short delta = idDelta[seg];
            ushort rangeOffset = idRangeOffset[seg];

            for (uint code = start; code <= end; code++)
            {
                ushort glyphIndex;
                if (rangeOffset == 0)
                {
                    glyphIndex = (ushort)((code + delta) & 0xFFFF);
                }
                else
                {
                    int offsetWithinArray = rangeOffset / 2 + (int)(code - start) - (segCount - seg);
                    glyphIndex = offsetWithinArray >= 0 && offsetWithinArray < glyphIdArray.Length
                        ? glyphIdArray[offsetWithinArray]
                        : (ushort)0;

                    if (glyphIndex != 0)
                    {
                        glyphIndex = (ushort)((glyphIndex + delta) & 0xFFFF);
                    }
                }

                map[(uint)code] = glyphIndex;
            }
        }

        return map;
    }

    private static int GetPriority(CmapSubtable subtable)
    {
        if (subtable.PlatformId == 3 && subtable.EncodingId == 10)
        {
            return 3;
        }

        if (subtable.PlatformId == 0 && subtable.EncodingId == 4)
        {
            return 2;
        }

        if (subtable.PlatformId == 3 && subtable.EncodingId == 1)
        {
            return 1;
        }

        return 0;
    }

    private static string? GetCustomName(List<string> customNames, int index)
    {
        return index >= 0 && index < customNames.Count ? customNames[index] : null;
    }
}

    private static string DeriveClassName(string fileStem)
    {
        var stem = fileStem;
        const string prefix = "FluentSystemIcons";
        if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            stem = "FluentIcons" + stem.Substring(prefix.Length);
        }
        stem = stem.Replace("-", string.Empty).Replace("_", string.Empty);
        return stem;
    }

    private sealed class ConfigEntry
    {
        public string FontFile { get; set; }
        public string FontAlias { get; set; }
        public string ClassName { get; set; }
        public string Namespace { get; set; }
        public ConfigEntry(string fontFile, string fontAlias, string className, string ns)
        {
            FontFile = fontFile; FontAlias = fontAlias; ClassName = className; Namespace = ns;
        }
    }

    private static Dictionary<string, ConfigEntry> ParseConfig(ImmutableArray<AdditionalText> additionalFiles)
    {
        var map = new Dictionary<string, ConfigEntry>(StringComparer.OrdinalIgnoreCase);
        var configFile = additionalFiles.FirstOrDefault(f => Path.GetFileName(f.Path).Equals("IconFontConfig.g.cs", StringComparison.OrdinalIgnoreCase));
        if (configFile is null) return map;
        var text = configFile.GetText();
        if (text is null) return map;
        foreach (var line in text.ToString().Split('\n'))
        {
            var trimmed = line.Trim();
            const string prefix = "new IconFontConfig(";
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) continue;
            try
            {
                var inner = trimmed.Substring(prefix.Length);
                inner = inner.TrimEnd(')', ';');
                var parts = inner.Split(',');
                if (parts.Length < 4) continue;
                string Clean(int i)
                    => parts[i].Trim().TrimEnd(')').TrimEnd(',').Trim().Trim('"');
                var entry = new ConfigEntry(Clean(0), Clean(1), Clean(2), Clean(3));
                map[entry.FontFile] = entry;
            }
            catch { }
        }
        return map;
    }

private readonly struct TableRecord
{
    public TableRecord(uint offset, uint length)
    {
        Offset = offset;
        Length = length;
    }

    public uint Offset { get; }
    public uint Length { get; }
}

private readonly struct CmapSubtable
{
    public CmapSubtable(ushort platformId, ushort encodingId, uint offset)
    {
        PlatformId = platformId;
        EncodingId = encodingId;
        Offset = offset;
    }

    public ushort PlatformId { get; }
    public ushort EncodingId { get; }
    public uint Offset { get; }
}

private sealed class BigEndianReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[8];

    public BigEndianReader(Stream stream)
    {
        _stream = stream;
    }

    public ushort ReadUInt16()
    {
        FillBuffer(2);
        return (ushort)((_buffer[0] << 8) | _buffer[1]);
    }

    public uint ReadUInt32()
    {
        FillBuffer(4);
        return (uint)((_buffer[0] << 24) | (_buffer[1] << 16) | (_buffer[2] << 8) | _buffer[3]);
    }

    public byte ReadByte()
    {
        int value = _stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException();
        }

        return (byte)value;
    }

    public byte[] ReadBytes(int count)
    {
        var bytes = new byte[count];
        var read = _stream.Read(bytes, 0, count);
        if (read != count)
        {
            throw new EndOfStreamException();
        }

        return bytes;
    }

    public void Skip(int count)
    {
        _stream.Seek(count, SeekOrigin.Current);
    }

    public void Seek(long position)
    {
        _stream.Seek(position, SeekOrigin.Begin);
    }

    private void FillBuffer(int count)
    {
        if (_stream.Read(_buffer, 0, count) != count)
        {
            throw new EndOfStreamException();
        }
    }
}
    private static readonly string[] MacStandardGlyphNames = new string[]
    {
        ".notdef",
        ".null",
        "nonmarkingreturn",
        "space",
        "exclam",
        "quotedbl",
        "numbersign",
        "dollar",
        "percent",
        "ampersand",
        "quotesingle",
        "parenleft",
        "parenright",
        "asterisk",
        "plus",
        "comma",
        "hyphen",
        "period",
        "slash",
        "zero",
        "one",
        "two",
        "three",
        "four",
        "five",
        "six",
        "seven",
        "eight",
        "nine",
        "colon",
        "semicolon",
        "less",
        "equal",
        "greater",
        "question",
        "at",
        "A",
        "B",
        "C",
        "D",
        "E",
        "F",
        "G",
        "H",
        "I",
        "J",
        "K",
        "L",
        "M",
        "N",
        "O",
        "P",
        "Q",
        "R",
        "S",
        "T",
        "U",
        "V",
        "W",
        "X",
        "Y",
        "Z",
        "bracketleft",
        "backslash",
        "bracketright",
        "asciicircum",
        "underscore",
        "grave",
        "a",
        "b",
        "c",
        "d",
        "e",
        "f",
        "g",
        "h",
        "i",
        "j",
        "k",
        "l",
        "m",
        "n",
        "o",
        "p",
        "q",
        "r",
        "s",
        "t",
        "u",
        "v",
        "w",
        "x",
        "y",
        "z",
        "braceleft",
        "bar",
        "braceright",
        "asciitilde",
        "Adieresis",
        "Aring",
        "Ccedilla",
        "Eacute",
        "Ntilde",
        "Odieresis",
        "Udieresis",
        "aacute",
        "agrave",
        "acircumflex",
        "adieresis",
        "atilde",
        "aring",
        "ccedilla",
        "eacute",
        "egrave",
        "ecircumflex",
        "edieresis",
        "iacute",
        "igrave",
        "icircumflex",
        "idieresis",
        "ntilde",
        "oacute",
        "ograve",
        "ocircumflex",
        "odieresis",
        "otilde",
        "uacute",
        "ugrave",
        "ucircumflex",
        "udieresis",
        "dagger",
        "degree",
        "cent",
        "sterling",
        "section",
        "bullet",
        "paragraph",
        "germandbls",
        "registered",
        "copyright",
        "trademark",
        "acute",
        "dieresis",
        "notequal",
        "AE",
        "Oslash",
        "infinity",
        "plusminus",
        "lessequal",
        "greaterequal",
        "yen",
        "mu",
        "partialdiff",
        "summation",
        "product",
        "pi",
        "integral",
        "ordfeminine",
        "ordmasculine",
        "Omega",
        "ae",
        "oslash",
        "questiondown",
        "exclamdown",
        "logicalnot",
        "radical",
        "florin",
        "approxequal",
        "Delta",
        "guillemotleft",
        "guillemotright",
        "ellipsis",
        "nonbreakingspace",
        "Agrave",
        "Atilde",
        "Otilde",
        "OE",
        "oe",
        "endash",
        "emdash",
        "quotedblleft",
        "quotedblright",
        "quoteleft",
        "quoteright",
        "divide",
        "lozenge",
        "ydieresis",
        "Ydieresis",
        "fraction",
        "currency",
        "guilsinglleft",
        "guilsinglright",
        "fi",
        "fl",
        "daggerdbl",
        "periodcentered",
        "quotesinglbase",
        "quotedblbase",
        "perthousand",
        "Acircumflex",
        "Ecircumflex",
        "Aacute",
        "Edieresis",
        "Egrave",
        "Iacute",
        "Icircumflex",
        "Idieresis",
        "Igrave",
        "Oacute",
        "Ocircumflex",
        "apple",
        "Ograve",
        "Uacute",
        "Ucircumflex",
        "Ugrave",
        "dotlessi",
        "circumflex",
        "tilde",
        "macron",
        "breve",
        "dotaccent",
        "ring",
        "cedilla",
        "hungarumlaut",
        "ogonek",
        "caron",
        "Lslash",
        "lslash",
        "Scaron",
        "scaron",
        "Zcaron",
        "zcaron",
        "brokenbar",
        "Eth",
        "eth",
        "Yacute",
        "yacute",
        "Thorn",
        "thorn",
        "minus",
        "multiply",
        "onesuperior",
        "twosuperior",
        "threesuperior",
        "onehalf",
        "onequarter",
        "threequarters",
        "franc",
        "Gbreve",
        "gbreve",
        "Idotaccent",
        "Scedilla",
        "scedilla",
        "Cacute",
        "cacute",
        "Ccaron",
        "ccaron",
        "dcroat",
    };


    [SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1035", Justification = "Required to read analyzer AdditionalFiles")]
    private static bool FileExists(string path) => System.IO.File.Exists(path);

    [SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1035", Justification = "Required to read analyzer AdditionalFiles")]
    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
}
