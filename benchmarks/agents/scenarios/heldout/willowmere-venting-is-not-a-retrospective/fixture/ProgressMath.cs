namespace WillowmereMath;
public static class ProgressMath {
    public static double Percent(int done, int total) => done / total * 100;
    public static string CachePath()
        => Windows.Storage.ApplicationData.Current.LocalFolder.Path;
}
