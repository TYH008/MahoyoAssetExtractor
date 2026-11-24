namespace VNAssetExplorer.Application;

public enum ImageSortKey { ResourceOrder, Name, Type, Size }
public enum ImageSortDirection { Ascending, Descending }

public static class ImageClassificationOrdering
{
    public static IReadOnlyList<ClassifiedImage> Sort(IEnumerable<ClassifiedImage> images, ImageSortKey key, ImageSortDirection direction)
    {
        ArgumentNullException.ThrowIfNull(images);
        var items = images.ToArray();
        IOrderedEnumerable<ClassifiedImage> ordered = key switch
        {
            ImageSortKey.Name when direction == ImageSortDirection.Ascending => items.OrderBy(image => image.Name, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Name => items.OrderByDescending(image => image.Name, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Type when direction == ImageSortDirection.Ascending => items.OrderBy(image => image.Type, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Type => items.OrderByDescending(image => image.Type, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Size when direction == ImageSortDirection.Ascending => items.OrderBy(image => image.Size),
            ImageSortKey.Size => items.OrderByDescending(image => image.Size),
            ImageSortKey.ResourceOrder when direction == ImageSortDirection.Ascending => items.OrderBy(image => image.ResourceOrdinal),
            _ => items.OrderByDescending(image => image.ResourceOrdinal),
        };
        return (direction == ImageSortDirection.Ascending
                ? ordered.ThenBy(image => image.ResourceOrdinal)
                : ordered.ThenByDescending(image => image.ResourceOrdinal))
            .ToArray();
    }
}
