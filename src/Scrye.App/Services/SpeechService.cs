using System;

namespace Scrye.App.Services;

/// <summary>
/// Text-to-speech for the accessibility mode (.tts / TTS toggle). Windows uses
/// System.Speech's SpeechSynthesizer (async queue); other platforms are a silent
/// no-op for now. A flood guard drops the backlog when the MUD outpaces the voice,
/// so speech tracks the present instead of narrating the past.
/// </summary>
public sealed class SpeechService : IDisposable
{
    private System.Speech.Synthesis.SpeechSynthesizer? _synth;
    private int _queued;                       // prompts spoken-but-not-finished
    private const int FloodLimit = 12;         // beyond this, dump the backlog

    public static bool Supported => OperatingSystem.IsWindows();

    private int _rate;
    /// <summary>Speaking rate, -10 (slow) … +10 (fast); 0 = default.</summary>
    public int Rate
    {
        get => _rate;
        set
        {
            _rate = Math.Clamp(value, -10, 10);
            if (!OperatingSystem.IsWindows()) return;   // guard inline so CA1416 sees it
            if (_synth is not null) try { _synth.Rate = _rate; } catch { }
        }
    }

    private System.Speech.Synthesis.SpeechSynthesizer? Synth()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (_synth is null)
        {
            try
            {
                _synth = new System.Speech.Synthesis.SpeechSynthesizer();
                _synth.SetOutputToDefaultAudioDevice();
                _synth.Rate = _rate;
                _synth.SpeakCompleted += (_, _) => { if (_queued > 0) _queued--; };
            }
            catch { _synth = null; }
        }
        return _synth;
    }

    /// <summary>Queue a line of speech (no-op off-Windows or on empty text).</summary>
    public void Speak(string text)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(text)) return;
        var synth = Synth();
        if (synth is null) return;
        try
        {
            if (_queued >= FloodLimit) { synth.SpeakAsyncCancelAll(); _queued = 0; }
            _queued++;
            synth.SpeakAsync(text);
        }
        catch { /* audio must never break the client */ }
    }

    /// <summary>Stop talking and drop everything queued.</summary>
    public void Stop()
    {
        if (OperatingSystem.IsWindows())
            try { _synth?.SpeakAsyncCancelAll(); } catch { }
        _queued = 0;
    }

    public void Dispose()
    {
        Stop();
        if (OperatingSystem.IsWindows())
            try { _synth?.Dispose(); } catch { }
        _synth = null;
    }
}
