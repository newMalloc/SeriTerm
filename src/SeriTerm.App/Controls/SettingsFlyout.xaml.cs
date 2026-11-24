using System.Windows;
using System.Windows.Controls;

namespace SeriTerm.App.Controls;

public partial class SettingsFlyout : UserControl
{
    public SettingsFlyout() => InitializeComponent();

    public event RoutedEventHandler? CloseRequested;
    public event RoutedEventHandler? AboutRequested;
    public event RoutedEventHandler? ChooseImageRequested;

    internal bool HasOpenDropDown => EncodingBox.IsDropDownOpen || BufferBox.IsDropDownOpen
        || FontSizeBox.IsDropDownOpen || FontFamilyBox.IsDropDownOpen;

    internal void Open()
    {
        ShowAppearance(false);
        EncodingBox.Focus();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        ShowAppearance(false);
        GlassFlyout.Reveal(GeneralPage);
        AppearanceButton.Focus();
    }

    private void OnAppearanceClick(object sender, RoutedEventArgs e)
    {
        ShowAppearance(true);
        GlassFlyout.Reveal(AppearancePage);
        BackButton.Focus();
    }

    private void ShowAppearance(bool show)
    {
        GeneralPage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        AppearancePage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = AppearancePage.Visibility;
        PageTitle.Text = show ? "个性化设置" : "设置";
        PageScroll.ScrollToTop();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, e);

    private void OnAboutClick(object sender, RoutedEventArgs e) => AboutRequested?.Invoke(this, e);

    private void OnChooseImageClick(object sender, RoutedEventArgs e) => ChooseImageRequested?.Invoke(this, e);
}
