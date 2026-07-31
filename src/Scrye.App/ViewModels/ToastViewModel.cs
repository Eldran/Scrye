namespace Scrye.App.ViewModels;

/// <summary>One entry in the app-level toast stack (bottom-right overlay):
/// trigger notifications and connection changes. Auto-expires; click dismisses.</summary>
public sealed class ToastViewModel : ViewModelBase
{
    public string Title { get; }
    public string Body { get; }

    public ToastViewModel(string title, string body)
    {
        Title = title;
        Body = body;
    }
}
