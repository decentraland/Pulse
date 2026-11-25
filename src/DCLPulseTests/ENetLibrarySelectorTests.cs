using PulseTestClient;

namespace DCLPulseTests;

[TestFixture]
public class ENetLibrarySelectorTests
{
    [TestCase("csharp", "enet-csharp")]
    [TestCase("rust", "enet-rust")]
    public void ResolvePath_PointsIntoTheChosenFolder(string choice, string folder)
    {
        string path = ENetLibrarySelector.ResolvePath(choice, AppContext.BaseDirectory);

        Assert.That(File.Exists(path), Is.True);
        Assert.That(new DirectoryInfo(Path.GetDirectoryName(path)!).Name, Is.EqualTo(folder));
    }

    [Test]
    public void ResolvePath_IsCaseInsensitive()
    {
        Assert.That(ENetLibrarySelector.ResolvePath("RUST", AppContext.BaseDirectory),
            Is.EqualTo(ENetLibrarySelector.ResolvePath("rust", AppContext.BaseDirectory)));
    }

    [Test]
    public void ResolvePath_RejectsUnknownChoice()
    {
        var ex = Assert.Throws<ArgumentException>(() => ENetLibrarySelector.ResolvePath("unity", AppContext.BaseDirectory));

        Assert.That(ex!.Message, Does.Contain("csharp").And.Contain("rust"));
    }

    [Test]
    public void ResolvePath_ReportsMissingFile()
    {
        string empty = Directory.CreateTempSubdirectory("enet-selector-").FullName;

        try
        {
            var ex = Assert.Throws<FileNotFoundException>(() => ENetLibrarySelector.ResolvePath("rust", empty));

            Assert.That(ex!.Message, Does.Contain(Path.Combine(empty, "enet-rust")));
        }
        finally { Directory.Delete(empty, true); }
    }

    [Test]
    public void TheTwoLibrariesDiffer()
    {
        byte[] csharp = File.ReadAllBytes(ENetLibrarySelector.ResolvePath("csharp", AppContext.BaseDirectory));
        byte[] rust = File.ReadAllBytes(ENetLibrarySelector.ResolvePath("rust", AppContext.BaseDirectory));

        Assert.That(csharp, Is.Not.EqualTo(rust));
    }

    [Test]
    public void OutputRootHasOnlyTheServerLibrary()
    {
        string rustPath = ENetLibrarySelector.ResolvePath("rust", AppContext.BaseDirectory);
        string rootPath = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(rustPath));

        Assert.That(File.ReadAllBytes(rootPath), Is.EqualTo(File.ReadAllBytes(rustPath)));
    }

    [Test]
    public void FromArgs_DefaultsToCsharp()
    {
        Assert.That(ClientOptions.FromArgs([]).ENetLibrary, Is.EqualTo("csharp"));
    }

    [Test]
    public void FromArgs_ReadsEnetOption()
    {
        Assert.That(ClientOptions.FromArgs(["--enet=rust"]).ENetLibrary, Is.EqualTo("rust"));
    }
}
