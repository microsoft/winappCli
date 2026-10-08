using Windows.ApplicationModel;
namespace HarrowDock;
public static class StartupProbe {
    public static object Describe(StartupTask task) => task.RequestEnableAsync();
}
