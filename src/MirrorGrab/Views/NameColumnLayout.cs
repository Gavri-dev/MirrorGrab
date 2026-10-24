namespace MirrorGrab.Views;

/// <summary>
/// Splits the flexible row width (what's left after the fixed columns) between the Name
/// column and the progress bar. The split is kept as a share of that width, so resizing
/// the window grows or shrinks both; dragging the Name edge moves the split.
/// </summary>
public static class NameColumnLayout
{
    public const double DefaultShare = 0.6;
    public const double MinName = 120;
    public const double MinProgress = 80;

    public static double NameWidth(double share, double flexible)
    {
        if (!(share > 0 && share < 1)) share = DefaultShare;   // also catches NaN
        return Clamp(share * flexible, flexible);
    }

    /// <summary>Share after dragging the Name edge so the column is <paramref name="desiredName"/> wide.</summary>
    public static double ShareFor(double desiredName, double flexible, double currentShare)
        => flexible >= MinName + MinProgress
            ? Clamp(desiredName, flexible) / flexible
            : currentShare;   // no room to split (or not laid out yet)

    private static double Clamp(double name, double flexible)
        => Math.Max(MinName, Math.Min(name, flexible - MinProgress));
}
