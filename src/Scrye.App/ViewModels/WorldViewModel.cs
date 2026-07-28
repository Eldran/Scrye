using System.Text;
using Avalonia.Threading;
using Scrye.Core.Model;
using Scrye.Core.Session;
using Scrye.Core.Text;

namespace Scrye.App.ViewModels;

/// <summary>
/// Wraps a live <see cref="MudSession"/> for one world tab. For the skeleton the
/// output is a growing plain-text string bound to a read-only TextBox — colour
/// and virtualization arrive with the dedicated OutputControl (Milestone 2).
/// </summary>
public sealed class WorldViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly MudSession _session;
    private readonly StringBuilder _buffer = new();

    public string Title { get; }
    public RelayCommand SubmitCommand { get; }

    private string _output = "";
    public string Output { get => _output; private set => SetField(ref _output, value); }

    private string _input = "";
    public string Input { get => _input; set => SetField(ref _input, value); }

    public WorldViewModel(WorldProfile profile)
    {
        Title = profile.Name;
        _session = new MudSession(profile);
        _session.LineReady += OnLine;
        _session.StateChanged += s => AppendSystem($"[{s}]");
        SubmitCommand = new RelayCommand(Submit);
    }

    public Task ConnectAsync() => _session.ConnectAsync();

    private void OnLine(Line line) => Dispatcher.UIThread.Post(() => Append(line.PlainText));

    public void AppendSystem(string text) => Dispatcher.UIThread.Post(() => Append("* " + text));

    private void Append(string text)
    {
        _buffer.AppendLine(text);
        Output = _buffer.ToString();   // skeleton: O(n) rebuild; the OutputControl replaces this
    }

    private void Submit()
    {
        string text = Input ?? "";
        Input = "";
        Append("> " + text);
        _session.Submit(text);
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
