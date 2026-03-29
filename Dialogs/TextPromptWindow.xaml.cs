using System.Windows;

namespace TrackBoxStudio.Dialogs;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string prompt)
        : this(title, prompt, string.Empty)
    {
    }

    public TextPromptWindow(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        Title = title;
        PromptTextBlock.Text = prompt;
        InputTextBox.Text = initialValue;
        InputTextBox.SelectAll();
        Loaded += (_, _) => InputTextBox.Focus();
    }

    public string ResponseText => InputTextBox.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
