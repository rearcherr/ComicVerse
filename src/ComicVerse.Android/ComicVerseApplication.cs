using Android.App;

namespace ComicVerse.Droid;

[Application]
public class ComicVerseApplication : Application
{
    public override void OnCreate()
    {
        base.OnCreate();
        AppServices.Init(this);
    }
}
