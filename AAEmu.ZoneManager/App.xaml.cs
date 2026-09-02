using System.Windows;

namespace AAEmu.ZoneManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        uint? launchZoneKey = null;
        for (var index = 0; index + 1 < e.Args.Length; index++)
        {
            if (e.Args[index].Equals("--launch-zone", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(e.Args[index + 1], out var zoneKey))
            {
                launchZoneKey = zoneKey;
                break;
            }
        }
        var window = new MainWindow(launchZoneKey);
        MainWindow = window;
        window.Show();
    }
}
