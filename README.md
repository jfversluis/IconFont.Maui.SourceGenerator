# IconFont.Maui.SourceGenerator

A Roslyn source generator that parses TTF icon fonts and emits strongly-typed glyph constants for .NET MAUI.

## 📦 What it does

Given a TTF font file with named glyphs (e.g., Fluent UI System Icons), this generator:

1. Reads the OpenType `post` and `cmap` tables at build time
2. Extracts glyph names and Unicode codepoints
3. Emits flat `public static partial class` types with `const string` fields for each glyph

The generated classes use a **flat naming convention** (e.g., `FluentIconsRegular.Add24` instead of `FluentIcons.Regular.Add24`) so they work directly with XAML `{x:Static}` without nested-class issues.

## 🚀 Usage

This package is consumed by font-specific libraries (like [IconFont.Maui.Template](https://github.com/jfversluis/IconFont)).

### In your font library `.csproj`:

```xml
<PackageReference Include="IconFont.Maui.SourceGenerator" Version="1.0.0"
    OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all" />
```

### Define your fonts in an `IconFont.props`:

```xml
<Project>
  <PropertyGroup>
    <IconFontNamespace>MyCompany.Icons</IconFontNamespace>
  </PropertyGroup>
  <ItemGroup>
    <IconFontDefinition Include="Resources/Fonts/MyFont-Regular.ttf">
      <FontAlias>MyFont</FontAlias>
      <FontClass>MyFont</FontClass>
      <FontNamespace>MyCompany.Icons</FontNamespace>
    </IconFontDefinition>
  </ItemGroup>
</Project>
```

The package's MSBuild targets automatically:
- Wire up font files as `AdditionalFiles` for the Roslyn generator
- Generate `IconFontConfig.g.cs` with font metadata
- Generate `IconFontExtensions.g.cs` with `UseMyFont()` builder extensions
- Generate an `.editorconfig` to pass metadata to the analyzer

## 🧪 Building & Testing

```bash
dotnet build IconFont.Maui.SourceGenerator.sln
dotnet test
```

## 📄 License

MIT License — see [LICENSE](LICENSE).
