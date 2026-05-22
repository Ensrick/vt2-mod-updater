using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

public class SteamPathsTests
{
    [Fact]
    public void FindLibraryOwningApp_SingleLibrary_ReturnsItsPath()
    {
        var vdf = """
                  "libraryfolders"
                  {
                      "0"
                      {
                          "path"		"C:\\Program Files (x86)\\Steam"
                          "apps"
                          {
                              "552500"		"71643442055"
                          }
                      }
                  }
                  """;
        Assert.Equal(@"C:\Program Files (x86)\Steam", SteamPaths.FindLibraryOwningApp(vdf, "552500"));
    }

    [Fact]
    public void FindLibraryOwningApp_MultiLibrary_AppInSecondLibrary()
    {
        var vdf = """
                  "libraryfolders"
                  {
                      "0" { "path" "C:\\Steam"        "apps" { "105600" "1" } }
                      "1" { "path" "D:\\SteamLibrary" "apps" { "552500" "1" } }
                  }
                  """;
        Assert.Equal(@"D:\SteamLibrary", SteamPaths.FindLibraryOwningApp(vdf, "552500"));
    }

    [Fact]
    public void FindLibraryOwningApp_ManyLibraries_AppInLast()
    {
        var vdf = """
                  "libraryfolders"
                  {
                      "0" { "path" "C:\\Steam" "apps" { "1" "1" } }
                      "1" { "path" "D:\\L1"    "apps" { "2" "2" } }
                      "2" { "path" "E:\\L2"    "apps" { } }
                      "3" { "path" "F:\\L3"    "apps" { "552500" "5" } }
                  }
                  """;
        Assert.Equal(@"F:\L3", SteamPaths.FindLibraryOwningApp(vdf, "552500"));
    }

    [Fact]
    public void FindLibraryOwningApp_RejectsSubstringCollision()
    {
        // 1552500 must not match 552500.
        var vdf = """
                  "libraryfolders"
                  {
                      "0" { "path" "C:\\A" "apps" { "1552500" "1" } }
                      "1" { "path" "C:\\B" "apps" { "552500"  "1" } }
                  }
                  """;
        Assert.Equal(@"C:\B", SteamPaths.FindLibraryOwningApp(vdf, "552500"));
    }

    [Fact]
    public void FindLibraryOwningApp_FirstLibraryEmptyApps_SkipsAndContinues()
    {
        var vdf = """
                  "libraryfolders"
                  {
                      "0" { "path" "C:\\A" "apps" {} }
                      "1" { "path" "C:\\B" "apps" { "552500" "1" } }
                  }
                  """;
        Assert.Equal(@"C:\B", SteamPaths.FindLibraryOwningApp(vdf, "552500"));
    }

    [Fact]
    public void FindLibraryOwningApp_NoMatch_ReturnsNull()
    {
        var vdf = """
                  "libraryfolders"
                  {
                      "0" { "path" "C:\\A" "apps" { "12345" "1" } }
                  }
                  """;
        Assert.Null(SteamPaths.FindLibraryOwningApp(vdf, "552500"));
    }

    [Fact]
    public void FindLibraryOwningApp_HandlesDoubleEscapedPaths()
    {
        // The VDF format uses \\ to represent a single \.
        var vdf = """
                  "libraryfolders"
                  {
                      "0" { "path" "C:\\(025) Steam" "apps" { "552500" "1" } }
                  }
                  """;
        Assert.Equal(@"C:\(025) Steam", SteamPaths.FindLibraryOwningApp(vdf, "552500"));
    }

    [Fact]
    public void FindLibraryOwningApp_EmptyInput_ReturnsNull()
    {
        Assert.Null(SteamPaths.FindLibraryOwningApp("", "552500"));
        Assert.Null(SteamPaths.FindLibraryOwningApp("{}", "552500"));
    }
}
