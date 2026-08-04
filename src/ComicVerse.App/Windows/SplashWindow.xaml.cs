using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace ComicVerse.App.Windows;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var anim = new DoubleAnimation(0, 100, TimeSpan.FromMilliseconds(1400))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            anim.Completed += (_, _) => StatusText.Text = "准备就绪";
            SplashProgress.BeginAnimation(ProgressBar.ValueProperty, anim);
        };
    }
}
