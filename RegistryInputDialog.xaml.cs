using System.Windows;
using AppBackupRestore.Services;

namespace AppBackupRestore;

public partial class RegistryInputDialog : Window
{
    public string RegistryKey { get; private set; } = "";

    public RegistryInputDialog()
    {
        InitializeComponent();
        // 在 InitializeComponent 之后通过 L10n 覆盖硬编码的界面文本
        Title = L10n.T("registryInput.title");
        if (RegKeyLabel != null) RegKeyLabel.Text = L10n.T("registryInput.label");
        if (RegKeyTextBox != null) RegKeyTextBox.ToolTip = L10n.T("registryInput.tooltip");
        if (OKButton != null) OKButton.Content = L10n.T("registryInput.ok");
        if (CancelButton != null) CancelButton.Content = L10n.T("registryInput.cancel");
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        RegistryKey = RegKeyTextBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
