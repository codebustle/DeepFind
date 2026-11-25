using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeepFind.App;

/// <summary>Theme-aware confirmation for index maintenance. Defaults to the non-destructive choice.</summary>
internal static class ConfirmationDialog
{
    public static bool Ask(Window owner, string message)
    {
        var window = new Window
        {
            Owner = owner, Title = owner.Title, Width = 450, MinWidth = 320, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, FontFamily = owner.FontFamily, FontSize = 14
        };
        window.SetResourceReference(Window.BackgroundProperty, "Surface");
        window.SetResourceReference(Window.ForegroundProperty, "Ink");
        Theme.AttachWindow(window);

        var body = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0)
        };
        var no = new Button { Content = "No", IsCancel = true, IsDefault = true, MinWidth = 76, Margin = new Thickness(0, 0, 8, 0) };
        no.SetResourceReference(FrameworkElement.StyleProperty, "SoftButton");
        var yes = new Button { Content = "Yes", MinWidth = 76 };
        yes.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        yes.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);

        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 8,2 L 15,14 L 1,14 Z M 8,6 L 8,10 M 8,12 L 8,13"),
            Width = 16, Height = 16, StrokeThickness = 1.3, StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 12, 0)
        };
        icon.SetResourceReference(Shape.StrokeProperty, "Ink");
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        window.Content = body;
        window.Loaded += (_, _) => no.Focus();
        return window.ShowDialog() == true;
    }
}
