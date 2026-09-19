using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

public class AppPathsTests
{
    [Fact]
    public void Constructor_ComposesTheThreeFilePathsFromTheRoot()
    {
        using var temp = new TempDirectory();

        var paths = new AppPaths(temp.Root);

        Assert.Equal(Path.GetFullPath(temp.Root), paths.RootDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "config.json"), paths.ConfigFilePath);
        Assert.Equal(Path.Combine(paths.RootDirectory, "queue.json"), paths.QueueFilePath);
        Assert.Equal(Path.Combine(paths.RootDirectory, "status.json"), paths.StatusFilePath);
    }

    [Fact]
    public void Default_ResolvesToProgramDataBootWakeMailer()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BootWakeMailer");

        Assert.Equal(expected, AppPaths.Default.RootDirectory);
        Assert.Equal(Path.Combine(expected, "config.json"), AppPaths.Default.ConfigFilePath);
        Assert.Equal(Path.Combine(expected, "queue.json"), AppPaths.Default.QueueFilePath);
        Assert.Equal(Path.Combine(expected, "status.json"), AppPaths.Default.StatusFilePath);

        // The fixed location required by requirements.md §2.
        Assert.Contains("ProgramData", AppPaths.Default.RootDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_MakesTheRootAbsolute()
    {
        var paths = new AppPaths("relative-data-dir");

        Assert.True(Path.IsPathFullyQualified(paths.RootDirectory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmptyRoot(string root)
    {
        Assert.Throws<ArgumentException>(() => new AppPaths(root));
    }

    [Fact]
    public void Constructor_RejectsNullRoot()
    {
        Assert.Throws<ArgumentNullException>(() => new AppPaths(null!));
    }

    [Fact]
    public void EnsureRootDirectory_CreatesNestedDirectoriesAndIsIdempotent()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(Path.Combine(temp.Root, "nested", "BootWakeMailer"));

        Assert.False(Directory.Exists(paths.RootDirectory));

        paths.EnsureRootDirectory();
        paths.EnsureRootDirectory();

        Assert.True(Directory.Exists(paths.RootDirectory));
    }
}
