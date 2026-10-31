using System.Windows;
using System.Windows.Controls;

namespace HupAddonManager.App.Views;

public partial class TextWindow : Window
{
    public string? Result { get; private set; }
    public string Text => Body.Text;

    public TextWindow(string title, string? hint, string text, bool editable)
    {
        InitializeComponent();
        Services.DarkTitleBar.Attach(this);
        Title = title;
        HeaderText.Text = title;
        HintText.Text = hint ?? "";
        HintText.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        Body.Text = text;
        Body.IsReadOnly = !editable;
        if (!editable) Body.Background = (System.Windows.Media.Brush)FindResource("Bg");
        Loaded += (_, _) => { if (editable) { Body.Focus(); Body.SelectAll(); } };
    }

    public Button AddButton(string label, bool primary = false, bool left = false, Action<TextWindow>? onClick = null, bool closes = true)
    {
        var b = new Button { Content = label, MinWidth = 90, Margin = left ? new Thickness(0, 0, 8, 0) : new Thickness(8, 0, 0, 0) };
        if (primary) { b.Style = (Style)FindResource("PrimaryButton"); b.IsDefault = false; }
        b.Click += (_, _) =>
        {
            onClick?.Invoke(this);
            if (closes) { Result = label; DialogResult = true; }
        };
        (left ? LeftButtons : RightButtons).Children.Add(b);
        return b;
    }

    public void SetText(string text) => Body.Text = text;
}
