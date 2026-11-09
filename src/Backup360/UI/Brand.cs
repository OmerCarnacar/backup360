using System.Windows.Media;

namespace Backup360.UI;

/// <summary>Bulut hizmetlerini tanıtan logolar. Ağ protokolleri genel simgeyle gösterilir.</summary>
public static class Brand
{
    private static ImageSource Build(params (string Color, string Data)[] parts)
    {
        var group = new DrawingGroup();
        foreach (var (color, data) in parts)
            group.Children.Add(new GeometryDrawing((Brush)new BrushConverter().ConvertFromString(color)!, null, Geometry.Parse(data)));
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static readonly Lazy<ImageSource> Drive = new(() => Build(
        ("#0066DA", "M6.6 66.85l3.85 6.65c.8 1.4 1.95 2.5 3.3 3.3l13.75-23.8h-27.5c0 1.55.4 3.1 1.2 4.5z"),
        ("#00AC47", "M43.65 25l-13.75-23.8c-1.35.8-2.5 1.9-3.3 3.3l-25.4 44a9.06 9.06 0 0 0-1.2 4.5h27.5z"),
        ("#EA4335", "M73.55 76.8c1.35-.8 2.5-1.9 3.3-3.3l1.6-2.75 7.65-13.25c.8-1.4 1.2-2.95 1.2-4.5h-27.502l5.852 11.5z"),
        ("#00832D", "M43.65 25l13.75-23.8c-1.35-.8-2.9-1.2-4.5-1.2h-18.5c-1.6 0-3.15.45-4.5 1.2z"),
        ("#2684FC", "M59.8 53h-32.3l-13.75 23.8c1.35.8 2.9 1.2 4.5 1.2h50.8c1.6 0 3.15-.45 4.5-1.2z"),
        ("#FFBA00", "M73.4 26.5l-12.7-22c-.8-1.4-1.95-2.5-3.3-3.3l-13.75 23.8 16.15 28h27.45c0-1.55-.4-3.1-1.2-4.5z")));

    private static readonly Lazy<ImageSource> OneDrive = new(() => Build(
        ("#0364B8", "M9.8 8.4a6.2 6.2 0 0 1 11.2-1.7l-4.4 5.3z"),
        ("#0F78D4", "M6.3 11.2a4.7 4.7 0 0 1 3.5-2.8l6.8 3.6-6.1 6.6H5.6a4 4 0 0 1 .7-7.4z"),
        ("#28A8EA", "M21 6.7a4.9 4.9 0 0 1 2.4 8.9c-.4.3-.9.6-1.4.7l-5.4-4.3z"),
        ("#1490DF", "M16.6 12l5.4 4.3c-.6 1.6-2.1 2.3-3.6 2.3H10.5z")));

    private static readonly Lazy<ImageSource> Dropbox = new(() => Build(
        ("#0061FF", "M6 2L0 5.8l6 3.8 6-3.8zM18 2l-6 3.8 6 3.8 6-3.8zM0 13.4l6 3.8 6-3.8-6-3.8zM18 9.6l-6 3.8 6 3.8 6-3.8zM6 18.4l6 3.8 6-3.8-6-3.8z")));

    private static readonly Lazy<ImageSource> S3 = new(() => Build(
        ("#8C3123", "M4 5.2C4 3.5 7.6 2 12 2s8 1.5 8 3.2-3.6 3.2-8 3.2-8-1.5-8-3.2z"),
        ("#E05243", "M4 7.4l2 13.4C6.2 22 8.9 23 12 23s5.8-1 6-2.2l2-13.4c-1.6 1.3-4.6 2.2-8 2.2S5.6 8.7 4 7.4z")));

    public static ImageSource? For(string type) => type switch
    {
        "drive" => Drive.Value,
        "onedrive" => OneDrive.Value,
        "dropbox" => Dropbox.Value,
        "s3" => S3.Value,
        _ => null,
    };
}
