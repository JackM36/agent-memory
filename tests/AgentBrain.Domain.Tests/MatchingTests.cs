using AgentBrain.Domain.Matching;

namespace AgentBrain.Domain.Tests;

public sealed class MatchingTests
{
    [Theory]
    [InlineData("Assets/Scripts/Save/**", "Assets/Scripts/Save/SaveSystem.cs", true)]
    [InlineData("Assets/Scripts/Save/**", "Assets/Scripts/Save/Slots/Slot.cs", true)]
    [InlineData("Assets/Scripts/Save/**", "Assets/Scripts/Load/Loader.cs", false)]
    [InlineData("Assets/Scripts/Save/", "Assets/Scripts/Save/Deep/File.cs", true)]
    [InlineData("src/*.ts", "src/index.ts", true)]
    [InlineData("src/*.ts", "src/app/index.ts", false)]
    [InlineData("src/**/*.ts", "src/index.ts", true)]
    [InlineData("src/**/*.ts", "src/app/deep/index.ts", true)]
    [InlineData("SaveSystem.cs", "Assets/Scripts/Save/SaveSystem.cs", true)]
    [InlineData("SaveSystem.cs", "SaveSystem.cs", true)]
    [InlineData("/SaveSystem.cs", "Assets/SaveSystem.cs", false)]
    [InlineData("*.asmdef", "Packages/com.me.kb/Runtime/Kb.asmdef", true)]
    [InlineData("file?.cs", "file1.cs", true)]
    [InlineData("file?.cs", "file12.cs", false)]
    [InlineData("assets/scripts/**", "Assets/Scripts/A.cs", true)]
    [InlineData("src/[ab].ts", "src/[ab].ts", true)]
    [InlineData("src/[ab].ts", "src/a.ts", false)]
    public void Globs_match_project_relative_paths(string pattern, string path, bool expected)
    {
        Assert.True(GlobPattern.TryParse(pattern, out var glob));
        Assert.Equal(expected, glob.IsMatch(path));
    }

    [Theory]
    [InlineData("Assets\\Scripts\\Save\\SaveSystem.cs")]
    [InlineData("./Assets/Scripts/Save/SaveSystem.cs")]
    [InlineData("/Assets/Scripts/Save/SaveSystem.cs")]
    public void Request_paths_are_normalised(string path)
    {
        Assert.True(GlobPattern.TryParse("Assets/Scripts/Save/*.cs", out var glob));
        Assert.True(glob.IsMatch(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside/**")]
    [InlineData("a/../b")]
    [InlineData("C:/Users/x")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    public void Unsafe_or_empty_globs_are_rejected(string pattern) => Assert.False(GlobPattern.TryParse(pattern, out _));

    [Theory]
    [InlineData("SaveSystem", "SaveSystem", MatchStrength.Strong)]
    [InlineData("SaveSystem", "savesystem", MatchStrength.Weak)]
    [InlineData("SaveSystem", "LoadSystem", MatchStrength.None)]
    public void Symbols_match_with_strength(string entry, string request, MatchStrength expected) =>
        Assert.Equal(expected, SymbolMatcher.Match(entry, request));
}
