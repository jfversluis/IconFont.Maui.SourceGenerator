using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Diagnostics;
using IconFontTemplate.SourceGenerator;
using Xunit;
using System.Text;
using System.Collections.Generic;
using System.Threading;

namespace IconFont.Maui.Template.SourceGenerator.Tests;

public class GeneratorTests
{
    [Fact]
    public void Generates_FluentIcons_With_Defaults()
    {
        var fontPath = Path.GetFullPath(Path.Combine(GetRepoRoot(), "tests/TestFonts/FluentSystemIcons-Regular.ttf"));
        Assert.True(File.Exists(fontPath));

        var compilation = CSharpCompilation.Create(
            "Tests",
            new[] { CSharpSyntaxTree.ParseText("""namespace Dummy { class C { } }""") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new FluentGlyphGenerator();
        var driver = CSharpGeneratorDriver.Create(new ISourceGenerator[] { generator },
            additionalTexts: new[] { new TestAdditionalText(fontPath) },
            optionsProvider: TestAnalyzerConfigOptionsProvider.With(
                perFile: new()
                {
                    [fontPath] = new()
                    {
                        ["build_metadata.AdditionalFiles.IconFontFile"] = Path.GetFileName(fontPath),
                        ["build_metadata.AdditionalFiles.IconFontAlias"] = "FluentIcons",
                        ["build_metadata.AdditionalFiles.IconFontClass"] = "FluentIcons",
                        ["build_metadata.AdditionalFiles.IconFontNamespace"] = "IconFontTemplate",
                    }
                }));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        Assert.Empty(diagnostics.Where(d => d.Id != "IFMT900"));

        var generated = outputCompilation.SyntaxTrees.FirstOrDefault(t => t.FilePath.EndsWith("FluentIcons.Generated.g.cs"));
        Assert.NotNull(generated);
        var text = generated!.ToString();
        Assert.Contains("namespace IconFontTemplate;", text);
        // Style is appended to class name — no nested classes
        Assert.Contains("public static partial class FluentIconsRegular", text);
        Assert.DoesNotContain("public static partial class Regular", text);
        Assert.Contains("public const string Add24", text);
    }

    [Fact]
    public void Generates_Multiple_Fonts()
    {
        var root = GetRepoRoot();
        var regular = Path.GetFullPath(Path.Combine(root, "tests/TestFonts/FluentSystemIcons-Regular.ttf"));
        var filled = Path.GetFullPath(Path.Combine(root, "tests/TestFonts/FluentSystemIcons-Filled.ttf"));
        Assert.True(File.Exists(regular));
        Assert.True(File.Exists(filled));

        var compilation = CSharpCompilation.Create(
            "Tests",
            new[] { CSharpSyntaxTree.ParseText("""namespace Dummy { class C { } }""") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new FluentGlyphGenerator();
        var driver = CSharpGeneratorDriver.Create(new ISourceGenerator[] { generator },
            additionalTexts: new[] { new TestAdditionalText(regular), new TestAdditionalText(filled) },
            optionsProvider: TestAnalyzerConfigOptionsProvider.With(
                perFile: new()
                {
                    [regular] = new()
                    {
                        ["build_metadata.AdditionalFiles.IconFontFile"] = Path.GetFileName(regular),
                        ["build_metadata.AdditionalFiles.IconFontAlias"] = "FluentIcons",
                        ["build_metadata.AdditionalFiles.IconFontClass"] = "FluentIcons",
                        ["build_metadata.AdditionalFiles.IconFontNamespace"] = "IconFontTemplate",
                    },
                    [filled] = new()
                    {
                        ["build_metadata.AdditionalFiles.IconFontFile"] = Path.GetFileName(filled),
                        ["build_metadata.AdditionalFiles.IconFontAlias"] = "FluentIconsFilled",
                        ["build_metadata.AdditionalFiles.IconFontClass"] = "FluentIconsFilled",
                        ["build_metadata.AdditionalFiles.IconFontNamespace"] = "IconFontTemplate",
                    }
                }));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        Assert.Empty(diagnostics.Where(d => d.Id != "IFMT900"));

        Assert.NotNull(outputCompilation.SyntaxTrees.FirstOrDefault(t => t.FilePath.EndsWith("FluentIcons.Generated.g.cs")));
        Assert.NotNull(outputCompilation.SyntaxTrees.FirstOrDefault(t => t.FilePath.EndsWith("FluentIconsFilled.Generated.g.cs")));
    }

    /// <summary>
    /// Verifies that generated classes are flat (no nesting) so XAML <c>{x:Static icons:ClassName.Glyph}</c> works
    /// without the problematic nested-class <c>+</c> syntax.
    /// This is the key scenario: any font, any style must produce <c>ClassStyle.Glyph</c> instead of <c>Class.Style.Glyph</c>.
    /// </summary>
    [Fact]
    public void Generated_Classes_Are_Flat_For_Xaml_XStatic_Compatibility()
    {
        var fontPath = Path.GetFullPath(Path.Combine(GetRepoRoot(), "tests/TestFonts/FluentSystemIcons-Regular.ttf"));
        Assert.True(File.Exists(fontPath));

        var compilation = CSharpCompilation.Create(
            "XamlCompatTests",
            new[] { CSharpSyntaxTree.ParseText("""namespace Dummy { class C { } }""") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new FluentGlyphGenerator();
        var driver = CSharpGeneratorDriver.Create(new ISourceGenerator[] { generator },
            additionalTexts: new[] { new TestAdditionalText(fontPath) },
            optionsProvider: TestAnalyzerConfigOptionsProvider.With(
                perFile: new()
                {
                    [fontPath] = new()
                    {
                        ["build_metadata.AdditionalFiles.IconFontFile"] = Path.GetFileName(fontPath),
                        ["build_metadata.AdditionalFiles.IconFontAlias"] = "MyCustomFont",
                        ["build_metadata.AdditionalFiles.IconFontClass"] = "MyCustomFont",
                        ["build_metadata.AdditionalFiles.IconFontNamespace"] = "MyCompany.Icons",
                    }
                }));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        Assert.Empty(diagnostics.Where(d => d.Id != "IFMT900"));

        var generated = outputCompilation.SyntaxTrees.FirstOrDefault(t => t.FilePath.EndsWith("MyCustomFont.Generated.g.cs"));
        Assert.NotNull(generated);
        var text = generated!.ToString();

        // Flat class: style appended to class name
        Assert.Contains("namespace MyCompany.Icons;", text);
        Assert.Contains("public static partial class MyCustomFontRegular", text);

        // No nesting — the class must NOT appear as a child of another class
        Assert.DoesNotContain("public static partial class MyCustomFont\n", text);
        Assert.DoesNotContain("public static partial class Regular", text);

        // The generated code should compile with a consumer that does `MyCustomFontRegular.SomeGlyph`
        // (simulating what {x:Static icons:MyCustomFontRegular.SomeGlyph} resolves to)
        var consumerCode = """
            namespace Consumer
            {
                class Test
                {
                    string GetGlyph() => MyCompany.Icons.MyCustomFontRegular.AccessTime20;
                }
            }
            """;

        var consumerTree = CSharpSyntaxTree.ParseText(consumerCode);
        var finalCompilation = outputCompilation.AddSyntaxTrees(consumerTree);
        var emitResult = finalCompilation.Emit(new System.IO.MemoryStream());
        Assert.True(emitResult.Success, 
            "Consumer code using flat class (x:Static pattern) should compile.\n" +
            string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IconFont.Maui.SourceGenerator.sln")))
            dir = dir.Parent!;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private sealed class TestAdditionalText : AdditionalText
    {
        private readonly string _path;
        public TestAdditionalText(string path) => _path = path;
        public override string Path => _path;
        public override SourceText? GetText(CancellationToken cancellationToken = default) => SourceText.From(File.ReadAllText(_path), Encoding.UTF8);
    }

    private sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _global;
        private readonly Dictionary<string, AnalyzerConfigOptions> _perFile;
        private TestAnalyzerConfigOptionsProvider(AnalyzerConfigOptions global, Dictionary<string, AnalyzerConfigOptions> perFile)
        {
            _global = global;
            _perFile = perFile;
        }
        public static AnalyzerConfigOptionsProvider With(Dictionary<string, Dictionary<string, string>> perFile)
            => new TestAnalyzerConfigOptionsProvider(TestAnalyzerConfigOptions.Empty, perFile.ToDictionary(kvp => kvp.Key, kvp => (AnalyzerConfigOptions)new TestAnalyzerConfigOptions(kvp.Value)));
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => TestAnalyzerConfigOptions.Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText text)
            => _perFile.TryGetValue(text.Path, out var opts) ? opts : TestAnalyzerConfigOptions.Empty;
    }

    private sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _dict;
        public static readonly AnalyzerConfigOptions Empty = new TestAnalyzerConfigOptions(new Dictionary<string, string>());
        public TestAnalyzerConfigOptions(IReadOnlyDictionary<string, string> dict) => _dict = dict;
        public override bool TryGetValue(string key, out string value) => _dict.TryGetValue(key, out value!);
    }
}
