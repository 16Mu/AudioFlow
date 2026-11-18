using System.Windows;
using System.Windows.Media;

namespace AudioFlow.App;

public static class Theme
{
    public static Brush Brush(string token) => (Brush)Application.Current.FindResource(token);
    public static void Use(FrameworkElement element, DependencyProperty property, string token) => element.SetResourceReference(property, token);
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached("CornerRadius", typeof(CornerRadius), typeof(Theme), new FrameworkPropertyMetadata(new CornerRadius(10)));
    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);
    public static readonly DependencyProperty ButtonIntentProperty = DependencyProperty.RegisterAttached("ButtonIntent", typeof(string), typeof(Theme), new FrameworkPropertyMetadata("Normal"));
    public static string GetButtonIntent(DependencyObject element) => (string)element.GetValue(ButtonIntentProperty);
    public static void SetButtonIntent(DependencyObject element, string value) => element.SetValue(ButtonIntentProperty, value);
}
