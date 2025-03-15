
namespace BiliVoxLive.Windows;
using System.Windows;

public partial class InputDialog : Window
{
    public string Message { get; }
    public string Answer => AnswerTextBox.Text;

    public InputDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        Message = message;
        DataContext = this;
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}