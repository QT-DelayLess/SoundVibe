// SoundVibe — subtle karaoke line transitions.
// This code does not modify playback, SoundCloud's official player or FFT.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SoundVibe
{
    public partial class MainWindow
    {
        // Store last *semantic* lyrics, independent from any legacy UI assignments.
        // This prevents restarting the fade every 250 ms when the UI timer ticks.
        private string? _svLastFullscreenLyric;
        private string? _svLastBigKaraokeLyric;

        private string SvKaraokeLine(TimeSpan position, bool online)
        {
            if (online)
            {
                if (_scLyricsLoading)
                    return _scLyricsStatus;
                if (!string.IsNullOrWhiteSpace(_scLyricsStatus))
                    return _scLyricsStatus;
            }

            var engine = LyricEngine.Instance;
            if (engine.HasSynchronizedLyrics)
                return engine.GetCurrentLyric(position);

            // Do not show the entire plain-lyrics document in the fullscreen area.
            return engine.HasPlainLyrics
                ? "♪ Senkronize söz bulunamadı ♪"
                : "♪ Lirik bulunamadı ♪";
        }

        private void SvRefreshLocalKaraoke(TimeSpan position)
        {
            if (_scActive) return;
            string text = SvKaraokeLine(position, false);
            if (_fullscreenVis)
                SvPresentLyric(TxtFullscreenLyrics, text, ref _svLastFullscreenLyric);
            if ((CmbVisualizerMode?.SelectedIndex ?? -1) == KARAOKE_VISUALIZER_INDEX)
                SvPresentLyric(_txtBigKaraokeLyrics, text, ref _svLastBigKaraokeLyric);
        }

        private void SvRefreshOnlineKaraoke()
        {
            if (!_scActive) return;
            string text = SvKaraokeLine(TimeSpan.FromSeconds(Math.Max(0, _scPositionSeconds)), true);
            if (_fullscreenVis)
                SvPresentLyric(TxtFullscreenLyrics, text, ref _svLastFullscreenLyric);
            if ((CmbVisualizerMode?.SelectedIndex ?? -1) == KARAOKE_VISUALIZER_INDEX)
                SvPresentLyric(_txtBigKaraokeLyrics, text, ref _svLastBigKaraokeLyric);
        }

        private static void SvPresentLyric(TextBlock? target, string text, ref string? lastText)
        {
            if (target == null) return;
            bool changed = !string.Equals(lastText, text, StringComparison.Ordinal);
            if (target.Text != text)
                target.Text = text;
            if (!changed) return;
            lastText = text;

            // Simple, low-cost opacity fade. No per-frame layout animation.
            target.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation
                {
                    From = 0.25,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(270)),
                    FillBehavior = FillBehavior.Stop
                });
        }
    }
}
