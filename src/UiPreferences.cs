using System;
using System.IO;

namespace RemoteControl
{
    // Never read by the service. Ordinary users can persist window preferences without machine-policy writes.
    internal static class UiPreferences
    {
        internal static string FilePath
        { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteControl", "ui-settings.txt"); } }
        internal static void SavePosition(int x, int y) {
            // An elevated settings window must never write through a user-controlled profile path.
            if (RemoteControlSecrets.ConfigProtection.IsAdministrator) return;
            SavePositionTo(FilePath, x, y);
        }
        internal static void SavePositionTo(string path, int x, int y)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + y.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        internal static bool LoadPosition(out int x, out int y)
        {
            x = y = 0;
            try { string[] parts = File.ReadAllLines(FilePath); return parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y); }
            catch { return false; }
        }
    }
}
