namespace FontA.Core.Fonts;

public sealed record VariationAxis(string Tag, float Min, float Default, float Max);

public sealed record FontInfo(
    string Path,
    int FaceIndex,
    string Family,
    string Style,
    string FullName,
    int Weight,
    bool IsItalic,
    IReadOnlyList<VariationAxis> Axes,
    IReadOnlyList<string> Features)
{
    public bool IsVariable => Axes.Count > 0;
}
