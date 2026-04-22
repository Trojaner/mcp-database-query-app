namespace McpDatabaseQueryApp.Core.Configuration;

public static class PathResolver
{
    public static string Resolve(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path);

        if (expanded.Contains("%APPDATA%", StringComparison.OrdinalIgnoreCase))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            expanded = expanded.Replace("%APPDATA%", appData, StringComparison.OrdinalIgnoreCase);
        }

        if (expanded.Contains("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            expanded = expanded.Replace("%LOCALAPPDATA%", localAppData, StringComparison.OrdinalIgnoreCase);
        }

        if (expanded.Contains("~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = expanded.Replace("~", home, StringComparison.Ordinal);
        }

        return Path.GetFullPath(expanded.Replace('/', Path.DirectorySeparatorChar));
    }
}
