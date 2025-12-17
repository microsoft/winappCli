using Microsoft.JavaScript.NodeApi;

namespace winMlAddon;

[JSExport]
public class Prediction
{
    [JSExport]
    public Box? Box { get; set; }

    [JSExport]
    public string Label { get; set; }

    [JSExport]
    public float Confidence { get; set; }
}

[JSExport]
public class Box
{
    [JSExport]
    public float Xmin { get; set; }

    [JSExport]
    public float Ymin { get; set; }

    [JSExport]
    public float Xmax { get; set; }

    [JSExport]
    public float Ymax { get; set; }

    [JSExport]
    public Box(float xmin, float ymin, float xmax, float ymax)
    {
        Xmin = xmin;
        Ymin = ymin;
        Xmax = xmax;
        Ymax = ymax;
    }
}