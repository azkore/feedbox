using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

internal sealed class AppConfig
{
    public string Database { get; private set; }
    public string Profile { get; private set; }
    public int Port { get; private set; }

    public static AppConfig Load(string directory, string[] args)
    {
        // Defaults and relative config paths follow the executable, not the launcher's working directory.
        // Existing installations may still have the old filenames. Prefer the renamed files when present.
        var databaseName = File.Exists(Path.Combine(directory, "feed-inbox.sqlite")) && !File.Exists(Path.Combine(directory, "feedbox.sqlite"))
            ? "feed-inbox.sqlite" : "feedbox.sqlite";
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "database", databaseName }, { "profile", "profile" }, { "port", "51883" }
        };
        var path = Path.Combine(directory, "feedbox.conf");
        if (!File.Exists(path)) path = Path.Combine(directory, "feed-inbox.conf");
        if (File.Exists(path))
        {
            int number = 0;
            foreach (var raw in File.ReadLines(path))
            {
                number++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int equals = line.IndexOf('=');
                var key = equals > 0 ? line.Substring(0, equals).Trim() : "";
                var value = equals > 0 ? line.Substring(equals + 1).Trim() : "";
                if (!settings.ContainsKey(key) || value.Length == 0)
                    throw new FormatException("Invalid setting in " + Path.GetFileName(path) + " on line " + number + ".");
                settings[key] = value;
            }
        }

        // Keep command-line overrides for existing scripts; ordinary launches don't need any.
        var database = Arg(args, "--db", settings["database"]);
        var profile = Arg(args, "--profile", settings["profile"]);
        int port;
        if (!int.TryParse(Arg(args, "--port", settings["port"]), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port < 1 || port > 65535) throw new FormatException("feedbox port must be between 1 and 65535.");
        return new AppConfig { Database = Resolve(directory, database), Profile = Resolve(directory, profile), Port = port };
    }

    private static string Resolve(string directory, string path)
    {
        return Path.GetFullPath(Path.Combine(directory, Environment.ExpandEnvironmentVariables(path)));
    }

    private static string Arg(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return fallback;
        if (index + 1 == args.Length || string.IsNullOrWhiteSpace(args[index + 1])) throw new ArgumentException("Missing value for " + name);
        return args[index + 1];
    }
}
