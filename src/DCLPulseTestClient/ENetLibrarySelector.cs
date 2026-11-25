using System.Runtime.InteropServices;

namespace PulseTestClient;

/// <summary>
///     Chooses which native ENet library the test client loads: ENet-CSharp's (what the Unity client
///     uses) or the Rust-built one the Pulse server ships (what the Bevy client uses).
/// </summary>
public static class ENetLibrarySelector
{
    public const string CSHARP = "csharp";
    public const string RUST = "rust";

    private const string LIBRARY_NAME = "enet";

    private static bool used;

    /// <summary>
    ///     Returns the full path of the native library for <paramref name="choice" /> under
    ///     <paramref name="baseDirectory" />. Throws <see cref="ArgumentException" /> for an unknown
    ///     choice and <see cref="FileNotFoundException" /> when the file is absent.
    /// </summary>
    public static string ResolvePath(string choice, string baseDirectory)
    {
        string folder = choice.ToLowerInvariant() switch
                        {
                            CSHARP => "enet-csharp",
                            RUST => "enet-rust",
                            _ => throw new ArgumentException(
                                $"Unknown ENet library '{choice}'. Use '{CSHARP}' or '{RUST}'.", nameof(choice)),
                        };

        string path = Path.Combine(baseDirectory, folder, FileName());

        if (!File.Exists(path))
            throw new FileNotFoundException($"ENet library for '{choice}' not found at {path}.", path);

        return path;
    }

    /// <summary>
    ///     Registers the DllImport resolver for the assembly that holds ENet.cs. Call once, before any
    ///     ENet call. The path is resolved eagerly so a bad choice fails at startup.
    /// </summary>
    public static void Use(string choice)
    {
        if (used)
            throw new InvalidOperationException("The ENet library has already been selected; a resolver can be registered only once.");

        string path = ResolvePath(choice, AppContext.BaseDirectory);

        NativeLibrary.SetDllImportResolver(typeof(ENet.Library).Assembly, (name, _, _) =>
            name == LIBRARY_NAME ? NativeLibrary.Load(path) : IntPtr.Zero);

        used = true;
    }

    private static string FileName()
    {
        if (OperatingSystem.IsWindows())
            return "enet.dll";

        return OperatingSystem.IsMacOS() ? "libenet.dylib" : "libenet.so";
    }
}
