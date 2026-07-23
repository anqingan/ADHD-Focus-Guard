namespace Vigil.Infrastructure;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vigil");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string SecretFile => Path.Combine(Root, "api-key.bin");
    public static string DatabaseFile => Path.Combine(Root, "Vigil.db");
    public static string LogFile => Path.Combine(Root, "vigil.log");

    public static void EnsureCreated() => Directory.CreateDirectory(Root);
}
