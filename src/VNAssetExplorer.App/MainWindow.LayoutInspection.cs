using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VNAssetExplorer.App;

public partial class MainWindow
{
    internal static IReadOnlyList<FrameworkElement> InspectBulkLayout(DependencyObject root)
    {
        var elements = new List<FrameworkElement>();
        Visit(root);
        return elements;
        void Visit(DependencyObject node)
        {
            if (node is FrameworkElement element) elements.Add(element);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Visit(VisualTreeHelper.GetChild(node, i));
        }
    }
}
