using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;

namespace VNAssetExplorer.App;

internal sealed class ExtractionChoiceDialog : Window
{
    private int choice;

    private ExtractionChoiceDialog(Window owner, string title, string message, string back, string[] actions)
    {
        Owner = owner;
        Title = title;
        Language = owner.Language;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 27, 56));
        Foreground = owner.Foreground;
        FontFamily = owner.FontFamily;
        FontSize = owner.FontSize;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        MaxWidth = Math.Min(520, SystemParameters.WorkArea.Width);
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        for (var index = 0; index < actions.Length; index++)
            AddButton(actions[index], index + 1);
        AddButton(back, 0);
        Content = panel;

        void AddButton(string label, int value)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                Style = (Style)owner.FindResource(typeof(Button)),
                IsDefault = actions.Length > 1 ? value == 1 : value == 0,
                IsCancel = value == 0,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) => { choice = value; Close(); };
            panel.Children.Add(button);
        }
    }

    internal static int Show(Window owner, string title, string message, string back, params string[] actions)
    {
        var dialog = new ExtractionChoiceDialog(owner, title, message, back, actions);
        dialog.ShowDialog();
        return dialog.choice;
    }
}
