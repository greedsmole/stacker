# Third-party components

Stacker uses the following open-source components. Retain their package license notices when redistributing a build.

- Avalonia / Avalonia.Fonts.Inter — https://github.com/AvaloniaUI/Avalonia — MIT; the bundled Inter font is distributed under the SIL Open Font License.
- SkiaSharp and HarfBuzzSharp — https://github.com/mono/SkiaSharp — MIT; shipped inside the self-contained archive through Avalonia's rendering stack.
- CommunityToolkit.Mvvm — https://github.com/CommunityToolkit/dotnet — MIT.
- YamlDotNet — https://github.com/aaubry/YamlDotNet — MIT.
- TextMateSharp and TextMateSharp.Grammars — https://github.com/danipen/TextMateSharp — MIT. The grammar bundle includes third-party language grammars and themes; see that project's bundled license files for their notices.
- Roslyn and its MSBuild workspace — https://github.com/dotnet/roslyn — MIT.
- Microsoft.Extensions.DependencyInjection — https://github.com/dotnet/runtime — MIT.
- .NET runtime — https://github.com/dotnet/runtime — MIT and third-party notices included by the self-contained publish.

Git and GitHub CLI are separately installed tools and are not redistributed by Stacker.

## Versioned review storage

Microsoft.Data.Sqlite 10.0.14 (Microsoft, MIT) and its transitive SQLitePCLRaw components provide local transactional
storage. SQLite is in the public domain. Their managed and native runtime components are included by .NET publish.
See [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) and
[SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) for source and license notices.
