using System.Windows;
using System.Windows.Controls;

namespace HupAddonManager.App.Views;

public partial class MessageWindow : Window
{
    public string? Result { get; private set; }

    public MessageWindow(string title, string message, string[] buttons, string? primary)
    {
        InitializeComponent();
        Services.DarkTitleBar.Attach(this);
        Title = title;
        HeaderText.Text = title;
        BodyText.Text = message;
        foreach (var label in buttons)
        {
            var b = new Button
            {
                Content = label,
                MinWidth = 90,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = label == primary,
                IsCancel = buttons.Length == 1 || (label != primary && label is "Cancel" or "No" or "Close"),
            };
            if (label == primary) b.Style = (Style)FindResource("PrimaryButton");
            b.Click += (_, _) => { Result = label; DialogResult = true; };
            Buttons.Children.Add(b);
        }
    }
}
