using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VNAssetExplorer.App.Services;

public sealed class SemanticChoiceDialog
{
    private readonly Func<int, object> resolver;

    private SemanticChoiceDialog(string title, string message, IReadOnlyList<string> labels, Func<int, object> resolver)
    {
        Title = title;
        Message = message;
        Labels = labels;
        this.resolver = resolver;
    }

    public string Title { get; }
    public string Message { get; }
    public IReadOnlyList<string> Labels { get; }

    public object Resolve(int buttonIndex) => buttonIndex >= 0 && buttonIndex < Labels.Count
        ? resolver(buttonIndex)
        : resolver(Labels.Count - 1);

    public object ResolveClosed() => resolver(Labels.Count - 1);

    public static SemanticChoiceDialog ExportCapability(ExportMediaKind kind = ExportMediaKind.Image) => new(
        "导出",
        kind switch
        {
            ExportMediaKind.Audio => "请选择导出方式。兼容音频会导出已验证的 Ogg Vorbis payload；原样资源保留归档中的原始格式。",
            ExportMediaKind.Video => "请选择导出方式。兼容视频会导出已验证的 MP4 / H.264 payload；原样资源保留归档中的原始格式。",
            _ => "请选择导出方式。兼容 PNG 将图片保存为普通 PNG；原样资源保留归档中的原始格式。",
        },
        [kind switch { ExportMediaKind.Audio => "兼容音频", ExportMediaKind.Video => "兼容视频", _ => "兼容 PNG" }, "原样资源", "取消"],
        index => index switch
        {
            0 => global::VNAssetExplorer.App.Services.ExportCapability.CompatiblePng,
            1 => global::VNAssetExplorer.App.Services.ExportCapability.RawResource,
            _ => global::VNAssetExplorer.App.Services.ExportCapability.Cancel,
        });

    public static SemanticChoiceDialog BatchConflict(int conflictCount) => new(
        "确认批量导出冲突",
        $"发现 {conflictCount} 个文件名冲突。请选择如何处理本次导出。",
        ["全部自动重命名", "全部跳过", "取消导出"],
        index => index switch
        {
            0 => BatchPngConflictDecision.RenameAll,
            1 => BatchPngConflictDecision.SkipAll,
            _ => BatchPngConflictDecision.Cancel,
        });

    public Window CreateWindow(Window? owner = null) => new SemanticChoiceWindow(this) { Owner = owner };

    public int Show(Window? owner = null)
    {
        var window = (SemanticChoiceWindow)CreateWindow(owner);
        return window.ShowDialog() == true ? window.SelectedIndex : Labels.Count - 1;
    }

    private sealed class SemanticChoiceWindow : Window
    {
        private readonly SemanticChoiceDialog dialog;
        public int SelectedIndex { get; private set; } = -1;

        public SemanticChoiceWindow(SemanticChoiceDialog dialog)
        {
            this.dialog = dialog;
            Title = dialog.Title;
            Width = 560;
            MinWidth = 560;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Content = BuildContent();
            Loaded += (_, _) => Keyboard.Focus(((System.Windows.Controls.Panel)((Border)Content).Child).Children.OfType<StackPanel>().Last().Children[0]);
        }

        private UIElement BuildContent()
        {
            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            for (var i = 0; i < dialog.Labels.Count; i++)
            {
                var index = i;
                var button = new System.Windows.Controls.Button
                {
                    Content = dialog.Labels[i],
                    MinWidth = 112,
                    Height = 34,
                    Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                    IsDefault = i == 0,
                    IsCancel = i == dialog.Labels.Count - 1,
                };
                button.Click += (_, _) => { SelectedIndex = index; DialogResult = true; };
                buttons.Children.Add(button);
            }

            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = dialog.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 });
            panel.Children.Add(new Border { Height = 16, Background = System.Windows.Media.Brushes.Transparent });
            panel.Children.Add(buttons);
            var border = new Border { Padding = new Thickness(0), Child = panel };
            Closed += (_, _) => { if (DialogResult is null) SelectedIndex = -1; };
            return border;
        }

        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                SelectedIndex = -1;
                DialogResult = false;
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}