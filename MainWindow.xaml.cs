using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Linq;
using System.Windows.Data;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using TagLib;

// --- ÇAKIŞMA ÖNLEYİCİ ALIAS'LAR ---
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Image = System.Windows.Controls.Image;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;

using NAudio.Wave;
using NAudio.Dsp;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brushes = System.Windows.Media.Brushes;

namespace SoundVibe
{
    public struct Particle
    {
        public double X, Y;
        public double VX, VY;
        public double Life, MaxLife;
        public double Size;
        public int Type;
        public bool IsAlive => Life > 0;
    }

    public sealed class VisualizerHost : FrameworkElement
    {
        private readonly DrawingVisual _visual = new DrawingVisual();
        private const int HISTORY_DEPTH = 12;
        private float[][] _fftHistory = new float[HISTORY_DEPTH][];
        private int _historyIndex = 0;
        private double _rotBass = 0;
        private double _rotMid = 0;
        private double _rotHigh = 0;
        private const int MAX_PARTICLES = 300;
        private Particle[] _particles = new Particle[MAX_PARTICLES];
        private Random _rnd = new Random();
        private SolidColorBrush _crtScanlineBrush;
        private SolidColorBrush _particleGlowBrush;

        public VisualizerHost()
        {
            AddVisualChild(_visual);
            AddLogicalChild(_visual);
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

            for (int i = 0; i < HISTORY_DEPTH; i++)
                _fftHistory[i] = new float[64];

            _crtScanlineBrush = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0));
            _crtScanlineBrush.Freeze();

            _particleGlowBrush = new SolidColorBrush(Color.FromArgb(200, 209, 59, 138));
            _particleGlowBrush.Freeze();
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _visual;

        public void UpdateThemeBrushes(Color particleColor, Color crtColor)
        {
            _particleGlowBrush = new SolidColorBrush(Color.FromArgb(200, particleColor.R, particleColor.G, particleColor.B));
            _particleGlowBrush.Freeze();

            _crtScanlineBrush = new SolidColorBrush(crtColor);
            _crtScanlineBrush.Freeze();
        }

        public void ResetHistory()
        {
            for (int i = 0; i < HISTORY_DEPTH; i++)
                Array.Clear(_fftHistory[i], 0, _fftHistory[i].Length);
            _historyIndex = 0;
            _rotBass = 0; _rotMid = 0; _rotHigh = 0;
        }

        public void DrawBars(float[] data, double width, double height, Brush brush, bool mirror = false)
        {
            int count = data.Length;
            double bw = width / count;

            using var dc = _visual.RenderOpen();
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

            for (int i = 0; i < count; i++)
            {
                double intensity = Math.Sqrt(data[i]) * 350.0;
                intensity = Math.Max(4.0, Math.Min(height * 0.92, intensity));

                double x = i * bw + 1;
                double w = Math.Max(2.0, bw - 2.0);

                if (mirror)
                {
                    double cy = height / 2.0;
                    dc.DrawRectangle(brush, null, new Rect(x, cy - intensity / 2.0, w, intensity));
                }
                else
                {
                    dc.DrawRectangle(brush, null, new Rect(x, height - intensity, w, intensity));
                }
            }
        }

        public void DrawCyberWave(float[] data, double width, double height, Brush fillBrush, Pen pen)
        {
            int count = data.Length;
            if (count < 2) return;
            double bw = width / count;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, height), true, true);
                for (int i = 0; i < count; i++)
                {
                    double y = (height / 2.0) - (Math.Sqrt(data[i]) * 300.0);
                    ctx.LineTo(new Point(i * bw, y), true, false);
                }
                ctx.LineTo(new Point(width, height), true, false);
            }
            geo.Freeze();
            using var dc = _visual.RenderOpen();
            dc.PushOpacity(0.22);
            dc.DrawGeometry(fillBrush, null, geo);
            dc.Pop();
            dc.DrawGeometry(null, pen, geo);
        }

        public void DrawClassicWave(float[] data, double width, double height, Pen pen)
        {
            int count = data.Length;
            if (count < 2) return;
            double bw = width / count;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, height / 2.0), false, false);
                for (int i = 0; i < count; i++)
                {
                    double y = (height / 2.0) - (Math.Sqrt(data[i]) * 300.0);
                    ctx.LineTo(new Point(i * bw, y), true, false);
                }
            }
            geo.Freeze();
            using var dc = _visual.RenderOpen();
            dc.DrawGeometry(null, pen, geo);
        }

        public void DrawClassicCircular(float[] data, double width, double height, Pen pen, double bass, double rotationAngle)
        {
            Array.Copy(data, _fftHistory[_historyIndex], Math.Min(data.Length, 64));
            using var dc = _visual.RenderOpen();
            int count = data.Length;
            double cx = width / 2.0;
            double cy = height / 2.0;
            double maxAllowedRadius = 380.0;

            double staticBaseRadius = 45.0;
            double pulse = bass * 15.0;
            double baseRadius = staticBaseRadius + pulse;
            if (baseRadius > 110.0) baseRadius = 110.0;
            for (int step = 0; step < HISTORY_DEPTH; step++)
            {
                int hIdx = (_historyIndex + 1 + step) % HISTORY_DEPTH;
                float[] pastData = _fftHistory[hIdx];

                double opacity = (step + 1.0) / HISTORY_DEPTH;
                dc.PushOpacity(opacity);
                for (int i = 0; i < count; i++)
                {
                    double angle = (i * 2 * Math.PI) / count + rotationAngle;
                    double decay = 1.0 - (step / (double)HISTORY_DEPTH);
                    decay = Math.Pow(decay, 1.5);

                    double intensity = Math.Sqrt(pastData[i]) * 600.0 * decay;
                    double targetRadius = baseRadius + intensity;
                    if (targetRadius > maxAllowedRadius) targetRadius = maxAllowedRadius;
                    double x1 = cx + Math.Cos(angle) * baseRadius;
                    double y1 = cy + Math.Sin(angle) * baseRadius;
                    double x2 = cx + Math.Cos(angle) * targetRadius;
                    double y2 = cy + Math.Sin(angle) * targetRadius;
                    dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
                }
                dc.Pop();
            }
            _historyIndex = (_historyIndex + 1) % HISTORY_DEPTH;
        }

        public void DrawSmoothedBlocks(float[] data, double width, double height, Brush brush)
        {
            Array.Copy(data, _fftHistory[_historyIndex], Math.Min(data.Length, 64));
            using var dc = _visual.RenderOpen();
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

            int count = data.Length;
            double bw = width / count;
            double w = Math.Max(2.0, bw - 2.0);
            for (int i = 0; i < count; i++)
            {
                float avgData = 0;
                for (int j = 0; j < HISTORY_DEPTH; j++) avgData += _fftHistory[j][i];
                avgData /= HISTORY_DEPTH;
                double intensity = Math.Sqrt(avgData) * 350.0;
                intensity = Math.Max(4.0, Math.Min(height * 0.92, intensity));
                dc.DrawRectangle(brush, null, new Rect(i * bw + 1, height - intensity, w, intensity));
            }
            _historyIndex = (_historyIndex + 1) % HISTORY_DEPTH;
        }

        public void DrawFractureBars(float[] data, double width, double height, Brush brush, double bass)
        {
            int count = data.Length;
            double bw = width / count;
            using var dc = _visual.RenderOpen();
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
            for (int i = 0; i < count; i++)
            {
                double intensity = Math.Sqrt(data[i]) * 350.0;
                intensity = Math.Max(4.0, Math.Min(height * 0.92, intensity));

                double baseY = height - intensity;
                double w = Math.Max(2.0, bw - 2.0);
                int segments = Math.Max(1, Math.Min(10, (int)(intensity / 10)));

                double segHeight = intensity / segments;
                for (int s = 0; s < segments; s++)
                {
                    double xOffset = 0;
                    if (bass > 0.8 && _rnd.NextDouble() > 0.5)
                        xOffset = (_rnd.NextDouble() - 0.5) * bass * 15.0;
                    double x = (i * bw + 1) + xOffset;
                    double y = baseY + (s * segHeight);
                    dc.DrawRectangle(brush, null, new Rect(x, y + 1, w, segHeight - 2));
                }
            }
        }

        public void DrawLiquidWave(float[] data, double width, double height, Pen pen)
        {
            int count = data.Length;
            if (count < 2) return;
            double bw = width / (count - 1);

            using var dc = _visual.RenderOpen();
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, height / 2.0), false, false);
                Point prevPoint = new Point(0, height / 2.0);

                for (int i = 1; i < count; i++)
                {
                    double y = (height / 2.0) - (Math.Sqrt(data[i]) * 300.0);
                    Point targetPoint = new Point(i * bw, y);

                    Point controlPoint1 = new Point(prevPoint.X + bw / 2, prevPoint.Y);
                    Point controlPoint2 = new Point(targetPoint.X - bw / 2, targetPoint.Y);

                    ctx.BezierTo(controlPoint1, controlPoint2, targetPoint, true, true);
                    prevPoint = targetPoint;
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        public void DrawOrbitalGalaxy(float[] data, double width, double height, Pen pen, double bass)
        {
            Array.Copy(data, _fftHistory[_historyIndex], Math.Min(data.Length, 64));
            _rotBass += 0.006 + (bass * 0.02);
            _rotMid -= 0.012;
            _rotHigh += 0.025;

            using var dc = _visual.RenderOpen();
            int count = data.Length;
            double cx = width / 2.0;
            double cy = height / 2.0;

            double staticBaseRadius = 95.0;
            double pulse = bass * 22.0;
            double baseRadius = staticBaseRadius + pulse;
            for (int step = 0; step < HISTORY_DEPTH; step++)
            {
                int hIdx = (_historyIndex + 1 + step) % HISTORY_DEPTH;
                float[] pastData = _fftHistory[hIdx];

                double decay = (step + 1.0) / HISTORY_DEPTH;
                decay = Math.Pow(decay, 3.5);

                dc.PushOpacity(decay);
                for (int i = 0; i < count; i++)
                {
                    double intensity = Math.Sqrt(pastData[i]) * 450.0;
                    double angleOffset = _rotMid;
                    if (i < 5) angleOffset = _rotBass;
                    else if (i > count / 2) angleOffset = _rotHigh;
                    double angle = (i * 2 * Math.PI) / count + angleOffset;
                    double organicShift = Math.Sin(angle * 3 + _rotMid) * 15.0;
                    double targetRadius = baseRadius + (intensity * decay) + organicShift;
                    double x1 = cx + Math.Cos(angle) * baseRadius;
                    double y1 = cy + Math.Sin(angle) * baseRadius;
                    double x2 = cx + Math.Cos(angle) * targetRadius;
                    double y2 = cy + Math.Sin(angle) * targetRadius;
                    dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
                }
                dc.Pop();
            }
            _historyIndex = (_historyIndex + 1) % HISTORY_DEPTH;
        }

        public void DrawAudioTunnel(float[] data, double width, double height, Pen pen, double bass)
        {
            Array.Copy(data, _fftHistory[_historyIndex], Math.Min(data.Length, 64));
            using var dc = _visual.RenderOpen();

            double cx = width / 2.0;
            double cy = height / 2.0;
            for (int step = 0; step < HISTORY_DEPTH; step++)
            {
                int hIdx = (_historyIndex + 1 + step) % HISTORY_DEPTH;
                float[] pastData = _fftHistory[hIdx];

                double depthScale = (step + 1.0) / HISTORY_DEPTH;
                double zScale = Math.Pow(depthScale, 2.0);

                dc.PushOpacity(zScale);
                dc.PushTransform(new ScaleTransform(zScale, zScale, cx, cy));

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    bool isFirst = true;
                    for (int i = 0; i < pastData.Length; i++)
                    {
                        double angle = (i * 2 * Math.PI) / pastData.Length;
                        double intensity = Math.Sqrt(pastData[i]) * 200.0;
                        double r = 150.0 + intensity;

                        double x = cx + Math.Cos(angle) * r;
                        double y = cy + Math.Sin(angle) * r;

                        if (isFirst) { ctx.BeginFigure(new Point(x, y), true, true); isFirst = false; }
                        else { ctx.LineTo(new Point(x, y), true, false); }
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);

                dc.Pop();
                dc.Pop();
            }
            _historyIndex = (_historyIndex + 1) % HISTORY_DEPTH;
        }

        public void DrawParticleReactor(float[] data, double width, double height, Brush brush, double bass, double delta)
        {
            using var dc = _visual.RenderOpen();
            double cx = width / 2.0;
            double cy = height / 2.0;
            if (bass > 0.9)
            {
                int particlesToSpawn = (int)(bass * 15);
                for (int i = 0; i < MAX_PARTICLES && particlesToSpawn > 0; i++)
                {
                    if (!_particles[i].IsAlive)
                    {
                        double angle = _rnd.NextDouble() * Math.PI * 2;
                        double speed = 2.0 + (_rnd.NextDouble() * bass * 15.0);

                        _particles[i].X = cx + Math.Cos(angle) * 30;
                        _particles[i].Y = cy + Math.Sin(angle) * 30;
                        _particles[i].VX = Math.Cos(angle) * speed;
                        _particles[i].VY = Math.Sin(angle) * speed;
                        _particles[i].Life = 1.0;
                        _particles[i].MaxLife = 0.5 + _rnd.NextDouble();
                        _particles[i].Size = 2.0 + _rnd.NextDouble() * 5.0;
                        _particles[i].Type = 0;
                        particlesToSpawn--;
                    }
                }
            }

            for (int i = 0; i < MAX_PARTICLES; i++)
            {
                if (_particles[i].IsAlive)
                {
                    _particles[i].X += _particles[i].VX;
                    _particles[i].Y += _particles[i].VY;
                    _particles[i].VX *= 0.96;
                    _particles[i].VY *= 0.96;
                    if (_particles[i].Type == 0) _particles[i].VY += 0.2;

                    _particles[i].Life -= delta;
                    double opacity = Math.Max(0, _particles[i].Life / _particles[i].MaxLife);
                    dc.PushOpacity(opacity);

                    double currentSize = _particles[i].Size + (bass * 2.0);
                    dc.DrawEllipse(_particleGlowBrush, null, new Point(_particles[i].X, _particles[i].Y), currentSize, currentSize);

                    dc.Pop();
                }
            }

            double coreSize = 20 + (bass * 30);
            dc.DrawEllipse(brush, null, new Point(cx, cy), coreSize, coreSize);
        }

        public void DrawampCRT(float[] data, double width, double height, Brush brush)
        {
            int count = 32;
            if (data.Length < count * 2) return;
            double bw = width / count;

            using var dc = _visual.RenderOpen();
            for (int i = 0; i < count; i++)
            {
                double rawVal = (data[i * 2] + data[i * 2 + 1]) / 2.0;
                double intensity = Math.Sqrt(rawVal) * 300.0;
                intensity = Math.Max(4.0, Math.Min(height * 0.92, intensity));
                intensity = Math.Round(intensity / 10.0) * 10.0;
                double x = i * bw + (bw * 0.1);
                double w = bw * 0.8;
                dc.DrawRectangle(brush, null, new Rect(x, height - intensity, w, intensity));
            }

            for (double y = 0; y < height; y += 4)
                dc.DrawRectangle(_crtScanlineBrush, null, new Rect(0, y, width, 2));
        }


        // 13. Synthwave Horizon — spectrum skyline + lightweight perspective grid.
        public void DrawSynthwaveHorizon(float[] data, double width, double height, Brush brush, Pen pen, double bass)
        {
            using var dc = _visual.RenderOpen();
            double horizon = height * 0.68;
            double cx = width / 2.0;

            // Perspective grid: intentionally sparse so it stays cheap at 60 FPS.
            dc.PushOpacity(0.22);
            for (int i = -7; i <= 7; i++)
            {
                double topX = cx + i * (width / 28.0);
                double bottomX = cx + i * (width / 8.0);
                dc.DrawLine(pen, new Point(topX, horizon), new Point(bottomX, height));
            }

            for (int row = 1; row <= 5; row++)
            {
                double t = row / 5.0;
                double eased = t * t;
                double y = horizon + (height - horizon) * eased;
                dc.DrawLine(pen, new Point(0, y), new Point(width, y));
            }
            dc.Pop();

            // Bass-reactive "sun" kept behind the skyline.
            double sunRadius = Math.Min(width, height) * 0.12 + Math.Sqrt(Math.Max(0, bass)) * 12.0;
            dc.PushOpacity(0.18);
            dc.DrawEllipse(brush, null, new Point(cx, horizon - 18), sunRadius, sunRadius);
            dc.Pop();

            int count = Math.Min(48, data.Length);
            if (count <= 0) return;
            double bw = width / count;
            for (int i = 0; i < count; i++)
            {
                int src = (int)((i / (double)Math.Max(1, count - 1)) * (data.Length - 1));
                double intensity = Math.Sqrt(Math.Max(0, data[src])) * 230.0;
                intensity = Math.Min(horizon * 0.78, Math.Max(3.0, intensity));
                double x = i * bw + 0.7;
                double w = Math.Max(1.5, bw - 1.4);
                dc.DrawRectangle(brush, null, new Rect(x, horizon - intensity, w, intensity));
            }

            dc.PushOpacity(0.55);
            dc.DrawLine(pen, new Point(0, horizon), new Point(width, horizon));
            dc.Pop();
        }

        // 14. Aero Aurora — two soft FFT ribbons; Frutiger/Aero themed but still low-cost.
        public void DrawAeroAurora(float[] data, double width, double height, Brush brush, Pen pen, double bass, double phase)
        {
            using var dc = _visual.RenderOpen();
            int points = Math.Min(32, data.Length);
            if (points < 2) return;

            for (int layer = 0; layer < 2; layer++)
            {
                double center = height * (layer == 0 ? 0.48 : 0.58);
                double ampMul = layer == 0 ? 150.0 : 105.0;
                double phaseShift = phase * (layer == 0 ? 1.0 : -0.72) + layer * 1.35;

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(0, center), true, true);
                    for (int i = 0; i < points; i++)
                    {
                        int src = (int)((i / (double)(points - 1)) * (data.Length - 1));
                        double x = i * (width / (points - 1.0));
                        double energy = Math.Sqrt(Math.Max(0, data[src])) * ampMul;
                        double drift = Math.Sin(i * 0.48 + phaseShift) * (8.0 + Math.Sqrt(Math.Max(0, bass)) * 8.0);
                        double y = Math.Max(4, Math.Min(height - 4, center - energy + drift));
                        ctx.LineTo(new Point(x, y), true, false);
                    }
                    ctx.LineTo(new Point(width, center + 28), true, false);
                    ctx.LineTo(new Point(0, center + 28), true, false);
                }
                geo.Freeze();

                dc.PushOpacity(layer == 0 ? 0.18 : 0.10);
                dc.DrawGeometry(brush, null, geo);
                dc.Pop();

                dc.PushOpacity(layer == 0 ? 0.85 : 0.42);
                dc.DrawGeometry(null, pen, geo);
                dc.Pop();
            }
        }

        // 15. Pulse Rings — ultra-light concentric energy rings.
        public void DrawPulseRings(float[] data, double width, double height, Pen pen, double bass, double rotationAngle)
        {
            using var dc = _visual.RenderOpen();
            double cx = width / 2.0;
            double cy = height / 2.0;
            double maxR = Math.Max(18.0, Math.Min(width, height) * 0.46);
            int rings = 8;

            for (int r = 0; r < rings; r++)
            {
                int start = r * data.Length / rings;
                int end = Math.Max(start + 1, (r + 1) * data.Length / rings);
                end = Math.Min(end, data.Length);

                double sum = 0;
                for (int i = start; i < end; i++)
                    sum += Math.Max(0, data[i]);
                double avg = sum / Math.Max(1, end - start);

                double baseR = maxR * ((r + 1.0) / rings);
                double pulse = Math.Sqrt(avg) * 34.0 + (r < 2 ? Math.Sqrt(Math.Max(0, bass)) * 6.0 : 0.0);
                double wobble = Math.Sin(rotationAngle * 2.0 + r * 0.9) * 2.5;
                double radius = Math.Max(4.0, baseR + pulse + wobble);

                dc.PushOpacity(0.18 + (r + 1.0) / rings * 0.72);
                dc.DrawEllipse(null, pen, new Point(cx, cy), radius, radius);
                dc.Pop();
            }

            // Small core pulse gives the rings a focal point without particles.
            dc.PushOpacity(0.28);
            double core = 8.0 + Math.Sqrt(Math.Max(0, bass)) * 18.0;
            dc.DrawEllipse(pen.Brush, null, new Point(cx, cy), core, core);
            dc.Pop();
        }

        // 16. Neon DNA — dual helix driven by frequency energy.
        public void DrawNeonDna(float[] data, double width, double height, Pen pen, double phase)
        {
            using var dc = _visual.RenderOpen();
            int points = Math.Min(36, data.Length);
            if (points < 2) return;

            double cy = height / 2.0;
            double step = width / (points - 1.0);
            Point prevA = new Point(0, cy);
            Point prevB = new Point(0, cy);

            for (int i = 0; i < points; i++)
            {
                int src = (int)((i / (double)(points - 1)) * (data.Length - 1));
                double energy = Math.Sqrt(Math.Max(0, data[src]));
                double amp = Math.Min(height * 0.42, 14.0 + energy * 105.0);
                double angle = i * 0.62 + phase * 2.1;
                double x = i * step;
                double offset = Math.Sin(angle) * amp;

                Point a = new Point(x, cy + offset);
                Point b = new Point(x, cy - offset);

                if (i > 0)
                {
                    dc.DrawLine(pen, prevA, a);
                    dc.DrawLine(pen, prevB, b);
                }

                if ((i & 1) == 0)
                {
                    dc.PushOpacity(0.25 + Math.Min(0.45, energy * 0.9));
                    dc.DrawLine(pen, a, b);
                    dc.Pop();
                }

                prevA = a;
                prevB = b;
            }
        }

        // 17. Spectrum Mountains — a few FFT-history ridges instead of expensive full-depth trails.
        public void DrawSpectrumMountains(float[] data, double width, double height, Pen pen)
        {
            Array.Copy(data, _fftHistory[_historyIndex], Math.Min(data.Length, 64));
            using var dc = _visual.RenderOpen();

            const int trails = 5;
            int count = Math.Min(32, data.Length);
            if (count < 2) return;

            for (int trail = trails - 1; trail >= 0; trail--)
            {
                int hIdx = (_historyIndex - trail + HISTORY_DEPTH) % HISTORY_DEPTH;
                float[] past = _fftHistory[hIdx];
                double baseline = height * 0.88 - trail * 9.0;

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    for (int i = 0; i < count; i++)
                    {
                        int src = (int)((i / (double)(count - 1)) * (data.Length - 1));
                        double x = i * (width / (count - 1.0));
                        double y = baseline - Math.Sqrt(Math.Max(0, past[src])) * 210.0;
                        y = Math.Max(2.0, Math.Min(height - 2.0, y));
                        if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                        else ctx.LineTo(new Point(x, y), true, false);
                    }
                }
                geo.Freeze();

                dc.PushOpacity(0.20 + (trails - trail) * 0.14);
                dc.DrawGeometry(null, pen, geo);
                dc.Pop();
            }

            _historyIndex = (_historyIndex + 1) % HISTORY_DEPTH;
        }

        public void DrawMiniBars(float[] data, double width, double height, Brush brush)
        {
            int count = data.Length;
            double bw = width / count;
            using var dc = _visual.RenderOpen();
            for (int i = 0; i < count; i++)
            {
                double intensity = Math.Sqrt(data[i]) * 150.0;
                intensity = Math.Max(3.0, Math.Min(height, intensity));
                dc.DrawRectangle(brush, null,
                    new Rect(i * bw + 0.5, height - intensity, Math.Max(1.5, bw - 1.0), intensity));
            }
        }

        public void Clear() { using var dc = _visual.RenderOpen(); }
    }

    // =================================================================
    //  MAIN WINDOW & THEME ENGINE
    // =================================================================
    public partial class MainWindow : Window
    {
        private int _currentTheme = 0;
        private bool isMiniPlayer = false;
        private bool isUpdatingSlider = false;
        private double _circularRotationAngle = 0.0;
        private Stopwatch _renderTimer = new Stopwatch();
        private Stopwatch _uiTimer = new Stopwatch();
        private bool _isRendering = false;
        private double _lastFrameDelta = 1.0 / 60.0;
        private int _renderFps = 60;
        private bool _isClosing = false;

        private readonly float[] _fftScratch = new float[1024];
        private readonly float[] _miniFft = new float[32];
        private readonly float[] _logFft = new float[64];
        private readonly DispatcherTimer _playbackUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        private FileSystemWatcher? _libraryWatcher;
        private CancellationTokenSource? _libraryLoadCts;
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private HwndSource? _hwndSource;

        private readonly ObservableCollection<string> _libraryViews = new ObservableCollection<string>();
        private SoundVibeState _state = new SoundVibeState();
        private Dictionary<string, LibraryCacheEntry> _libraryCache = new Dictionary<string, LibraryCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private string _activeLibraryView = "All Songs";
        private string _pendingSearch = "";
        private string _resumePath = "";
        private double _resumeSeconds = 0;

        private static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aiff", ".aac" };

        private static readonly string AppDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundVibe");
        private static readonly string ConfigPath = Path.Combine(AppDataRoot, "SoundVibe_Config.json");
        private static readonly string StatePath = Path.Combine(AppDataRoot, "SoundVibe_State.json");
        private static readonly string CachePath = Path.Combine(AppDataRoot, "SoundVibe_LibraryCache.json");

        private string _lastFolderPath = "";

        private const int NUM_BARS = 64;
        private const int MINI_BARS = 32;
        private const int KARAOKE_VISUALIZER_INDEX = 17;
        private float[] smoothedFft = new float[NUM_BARS];
        private double _visualPhase = 0.0;

        private VisualizerHost? _mainVisHost;
        private VisualizerHost? _miniVisHost;
        private LinearGradientBrush? _visBrush;
        private LinearGradientBrush? _miniVisBrush;
        private Pen? _wavePen;

        private TextBlock? _txtNowPlayingTitle;
        private TextBlock? _txtNowPlayingArtist;
        private TextBlock? _txtMiniTitle;
        private TextBlock? _txtMiniArtist;
        private TextBlock? _txtMetaData;
        private TextBlock? _txtCurrentTime;
        private TextBlock? _txtTotalTime;
        private TextBlock? _txtWidgetLyrics;
        private TextBlock? _txtBigKaraokeLyrics;
        private TextBlock? _txtQueueCount;
        private TextBlock? _txtEqPresetName;

        private TextBlock? _txtSidebarNowPlaying;
        private TextBlock? _txtSidebarArtist;

        private Image? _imgAlbumCover;
        private Image? _imgMiniCover;
        private Image? _imgMiniPlayerCover;
        private RotateTransform? _vinylRotation;
        private System.Windows.Media.Effects.DropShadowEffect? _playGlow;
        private Rectangle? _bgColorRect;
        private Image? _bgAmbientGlow;
        private Canvas? _mainVisCanvas;
        private Canvas? _miniVisCanvas;

        public ObservableCollection<SongModel> Playlist { get; set; } = new ObservableCollection<SongModel>();

        public MainWindow()
        {
            InitializeComponent();
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            Directory.CreateDirectory(AppDataRoot);

            LstSongs.ItemsSource = Playlist;
            LstQueue.ItemsSource = PlaybackManager.Instance.UserQueue;
            CmbLibraryView.ItemsSource = _libraryViews;

            _playbackUiTimer.Tick += (s, e) => UpdatePlaybackUi();
            _searchDebounceTimer.Tick += (s, e) =>
            {
                _searchDebounceTimer.Stop();
                ApplyLibraryFilter();
            };

            SourceInitialized += (s, e) =>
            {
                _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                _hwndSource?.AddHook(WndProc);
            };

            this.Loaded += async (s, e) =>
            {
                CacheUiRefs();
                _bgAmbientGlow = this.FindName("BgAmbientGlow") as Image;
                WindowsServices.EnableAcrylicBlur(this);
                LoadPersistentState();
                LoadLibraryCache();
                RefreshLibraryViews();
                ApplyTheme(0);
                SetupVisualizer();
                SetupShuffleRepeatButtons();
                SetupEqualizer();
                SetupTrayIcon();
                await LoadSettingsAsync();
            };

            this.StateChanged += (s, e) =>
            {
                if (this.WindowState == WindowState.Minimized) StopRendering();
                else if (AudioEngine.Instance.IsPlaying) StartRendering();
            };

            this.Closing += (s, e) =>
            {
                _isClosing = true;
                SaveSettings();
                SavePersistentState();
                SaveLibraryCache();
                _searchDebounceTimer.Stop();
                StopRendering();
                DisposeLibraryWatcher();
                _hwndSource?.RemoveHook(WndProc);
                _trayIcon?.Dispose();
                _libraryLoadCts?.Cancel();
                _libraryLoadCts?.Dispose();
                AudioEngine.Instance.Dispose();
            };

            AudioEngine.Instance.TrackEnded += (s, e) =>
                Dispatcher.Invoke(() => { if (PlaybackManager.Instance.GoToNext()) PlayCurrentSong(); });

            PlaybackManager.Instance.UserQueue.CollectionChanged += (s, e) => UpdateQueueCount();

            BtnTheme.Click += BtnTheme_Click;
            BtnClose.Click += BtnClose_Click;
            BtnMinimize.Click += BtnMinimize_Click;
            BtnMainToMini.Click += ToggleMiniPlayer;
            BtnAddFolder.Click += BtnAddFolder_Click;
            BtnTray.Click += (s, e) => HideToTray();
            BtnNewPlaylist.Click += BtnNewPlaylist_Click;
            BtnAddToPlaylist.Click += BtnAddToPlaylist_Click;
            BtnResumeLast.Click += BtnResumeLast_Click;
            BtnClearQueue.Click += (s, e) => PlaybackManager.Instance.ClearUserQueue();
            QueueCard.MouseLeftButtonUp += (s, e) => QueuePopup.IsOpen = !QueuePopup.IsOpen;
            CmbLibraryView.SelectionChanged += CmbLibraryView_SelectionChanged;
            CmbRenderMode.SelectionChanged += CmbRenderMode_SelectionChanged;
            BtnLyricMinus.Click += (s, e) => ChangeLyricOffset(-100);
            BtnLyricPlus.Click += (s, e) => ChangeLyricOffset(100);

            if (this.FindName("BtnPlayPause") is Button btnPlay) btnPlay.Click += BtnPlayPause_Click;
            if (this.FindName("BtnPrev") is Button btnPrev) btnPrev.Click += BtnPrev_Click;
            if (this.FindName("BtnNext") is Button btnNext) btnNext.Click += BtnNext_Click;

            BtnMiniClose.Click += BtnClose_Click;
            BtnMiniToMain.Click += ToggleMiniPlayer;
            BtnMiniPlayPause.Click += BtnPlayPause_Click;
            BtnMiniPrev.Click += BtnPrev_Click;
            BtnMiniNext.Click += BtnNext_Click;

            LstSongs.SelectionChanged += LstSongs_SelectionChanged;
            SldVolume.ValueChanged += SldVolume_ValueChanged;

            BindSlider(SldTimeline, () => SldTimeline.Value);
            BindSlider(SldMiniTimeline, () => SldMiniTimeline.Value);

            CmbVisualizerMode.SelectionChanged += (s, e) => SetupVisualizer();
            VisualizerCanvas.SizeChanged += (s, e) => ResizeVisualizerHosts();
            MiniVisualizerCanvas.SizeChanged += (s, e) => ResizeVisualizerHosts();
            TxtSearch.TextChanged += TxtSearch_TextChanged;

            var tabLibrary = this.FindName("TabLibrary") as RadioButton;
            var tabEq = this.FindName("TabEqualizer") as RadioButton;
            var playerPanel = this.FindName("PlayerViewPanel") as Grid;
            var eqPanel = this.FindName("EqViewPanel") as Grid;

            if (tabLibrary != null) tabLibrary.Checked += (s, e) =>
            {
                if (playerPanel != null) playerPanel.Visibility = Visibility.Visible;
                if (eqPanel != null) eqPanel.Visibility = Visibility.Collapsed;
            };
            if (tabEq != null) tabEq.Checked += (s, e) =>
            {
                if (playerPanel != null) playerPanel.Visibility = Visibility.Collapsed;
                if (eqPanel != null) eqPanel.Visibility = Visibility.Visible;
            };
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(AppDataRoot);
                var current = PlaybackManager.Instance.CurrentSong;
                var settings = new SoundVibeConfig
                {
                    Volume = SldVolume?.Value ?? 50,
                    ThemeIndex = _currentTheme,
                    EqPresetIndex = FindVisualChildren<ComboBox>(this.FindName("EqViewPanel") as Grid).FirstOrDefault()?.SelectedIndex ?? 0,
                    LastFolderPath = _lastFolderPath,
                    RenderFps = _renderFps,
                    LyricOffsetMs = LyricEngine.Instance.OffsetMilliseconds,
                    LastSongPath = current?.FilePath ?? _resumePath,
                    LastPositionSeconds = current != null ? AudioEngine.Instance.CurrentTime.TotalSeconds : _resumeSeconds,
                    LastLibraryView = _activeLibraryView,
                    VisualizerModeIndex = Math.Max(0, CmbVisualizerMode?.SelectedIndex ?? 0)
                };
                System.IO.File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch { }
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                string legacyPath = Path.Combine(AppContext.BaseDirectory, "SoundVibe_Config.json");
                string path = System.IO.File.Exists(ConfigPath) ? ConfigPath : legacyPath;
                if (!System.IO.File.Exists(path)) return;

                var json = await Task.Run(() => System.IO.File.ReadAllText(path));
                var settings = JsonSerializer.Deserialize<SoundVibeConfig>(json, JsonOptions);
                if (settings == null) return;

                SldVolume.Value = Math.Max(0, Math.Min(100, settings.Volume));
                AudioEngine.Instance.SetVolume((float)(SldVolume.Value / 100.0));

                _currentTheme = Math.Max(0, Math.Min(_themeDefs.Length - 1, settings.ThemeIndex));
                ApplyTheme(_currentTheme);

                var presetCombo = FindVisualChildren<ComboBox>(this.FindName("EqViewPanel") as Grid).FirstOrDefault();
                if (presetCombo != null && settings.EqPresetIndex >= 0 && settings.EqPresetIndex < presetCombo.Items.Count)
                    presetCombo.SelectedIndex = settings.EqPresetIndex;

                _renderFps = settings.RenderFps == 0 || settings.RenderFps == 30 ? settings.RenderFps : 60;
                CmbRenderMode.SelectedIndex = _renderFps == 0 ? 0 : _renderFps == 30 ? 1 : 2;

                if (CmbVisualizerMode != null && CmbVisualizerMode.Items.Count > 0)
                    CmbVisualizerMode.SelectedIndex = Math.Max(0, Math.Min(CmbVisualizerMode.Items.Count - 1, settings.VisualizerModeIndex));

                LyricEngine.Instance.OffsetMilliseconds = Math.Max(-3000, Math.Min(3000, settings.LyricOffsetMs));
                UpdateLyricOffsetLabel();

                _resumePath = settings.LastSongPath ?? "";
                _resumeSeconds = Math.Max(0, settings.LastPositionSeconds);
                _lastFolderPath = settings.LastFolderPath ?? "";

                if (!string.IsNullOrWhiteSpace(_lastFolderPath) && Directory.Exists(_lastFolderPath))
                    await LoadFolderAsync(_lastFolderPath);

                if (!string.IsNullOrWhiteSpace(settings.LastLibraryView) && _libraryViews.Contains(settings.LastLibraryView))
                {
                    _activeLibraryView = settings.LastLibraryView;
                    CmbLibraryView.SelectedItem = _activeLibraryView;
                }

                UpdateResumeButton();
            }
            catch { }
            finally
            {
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        // --- YENİ EKLENEN: OSD / TOAST BİLDİRİM METODU ---
        private void ShowToastNotification(SongModel song)
        {
            // Yeni bağımsız pencereyi oluşturur ve ekranda uçurur!
            new ToastWindow(song).Show();
        }
        // ----------------------------------------

        private void StartRendering()
        {
            if (!AudioEngine.Instance.IsPlaying) return;

            if (!_playbackUiTimer.IsEnabled)
                _playbackUiTimer.Start();

            if (_renderFps <= 0 || WindowState == WindowState.Minimized || !IsVisible)
                return;

            if (!_isRendering)
            {
                CompositionTarget.Rendering += RenderLoop;
                _renderTimer.Restart();
                _isRendering = true;
            }
        }

        private void StopRendering()
        {
            _playbackUiTimer.Stop();
            if (_isRendering)
            {
                CompositionTarget.Rendering -= RenderLoop;
                _renderTimer.Stop();
                _renderTimer.Reset();
                _isRendering = false;
            }
        }

        private void RestartVisualRenderingOnly()
        {
            if (_isRendering)
            {
                CompositionTarget.Rendering -= RenderLoop;
                _renderTimer.Reset();
                _isRendering = false;
            }
            if (AudioEngine.Instance.IsPlaying)
                StartRendering();
        }

        private void UpdatePlaybackUi()
        {
            TimeSpan currentTime = AudioEngine.Instance.CurrentTime;
            TimeSpan totalTime = AudioEngine.Instance.TotalTime;
            double total = totalTime.TotalSeconds;
            double current = currentTime.TotalSeconds;

            if (!isUpdatingSlider)
            {
                if (isMiniPlayer && SldMiniTimeline != null)
                {
                    SldMiniTimeline.Maximum = Math.Max(1, total);
                    SldMiniTimeline.Value = Math.Min(current, SldMiniTimeline.Maximum);
                }
                else if (SldTimeline != null)
                {
                    SldTimeline.Maximum = Math.Max(1, total);
                    SldTimeline.Value = Math.Min(current, SldTimeline.Maximum);
                }
            }

            if (_txtCurrentTime != null) _txtCurrentTime.Text = currentTime.ToString(@"mm\:ss");
            if (_txtTotalTime != null) _txtTotalTime.Text = totalTime.ToString(@"mm\:ss");

            string lyric = LyricEngine.Instance.GetCurrentLyric(currentTime);
            if (_txtWidgetLyrics != null) _txtWidgetLyrics.Text = lyric;
            if ((CmbVisualizerMode?.SelectedIndex ?? 0) == KARAOKE_VISUALIZER_INDEX && _txtBigKaraokeLyrics != null)
                _txtBigKaraokeLyrics.Text = lyric;
            UpdateQueueCount();
        }

        private void UpdateQueueCount()
        {
            if (_txtQueueCount != null)
                _txtQueueCount.Text = PlaybackManager.Instance.QueueCount.ToString();
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject? depObj) where T : DependencyObject
        {
            if (depObj != null)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(depObj, i);
                    if (child != null && child is T t) yield return t;
                    foreach (T childOfChild in FindVisualChildren<T>(child)) yield return childOfChild;
                }
            }
        }

        private void SetupShuffleRepeatButtons()
        {
            var shuffleBtn = this.FindName("BtnShuffle") as Button;
            if (shuffleBtn != null) shuffleBtn.Click += (s, e) => {
                PlaybackManager.Instance.IsShuffle = !PlaybackManager.Instance.IsShuffle;
                shuffleBtn.Foreground = PlaybackManager.Instance.IsShuffle ? (Brush)FindResource("ThemeAccentBrush") : Brushes.White;
            };

            var repeatBtn = this.FindName("BtnRepeat") as Button;
            if (repeatBtn != null) repeatBtn.Click += (s, e) => {
                PlaybackManager.Instance.IsRepeat = !PlaybackManager.Instance.IsRepeat;
                repeatBtn.Foreground = PlaybackManager.Instance.IsRepeat ? (Brush)FindResource("ThemeAccentBrush") : Brushes.White;
            };
        }

        // EQ SLIDER GERÇEK ZAMANLI BAĞLANTISI (Slider değeri anında NAudio'ya gidiyor)
        private void SetupEqualizer()
        {
            var eqPanel = this.FindName("EqViewPanel") as Grid;
            if (eqPanel == null) return;

            var sliders = FindVisualChildren<Slider>(eqPanel).Where(s => s.Orientation == Orientation.Vertical).ToList();
            for (int i = 0; i < sliders.Count && i < 10; i++)
            {
                int bandIndex = i;
                sliders[i].ValueChanged += (s, e) => {
                    float gain = (float)(sliders[bandIndex].Value - 50) * (15f / 50f);
                    AudioEngine.Instance.SetEqGain(bandIndex, gain);
                };
            }

            var presetCombo = FindVisualChildren<ComboBox>(eqPanel).FirstOrDefault();
            if (presetCombo != null)
            {
                presetCombo.SelectionChanged += (s, e) => ApplyEqPreset(presetCombo.SelectedIndex, sliders);
            }
        }

        private void ApplyEqPreset(int index, List<Slider> sliders)
        {
            string[] names = { "Flat (Normal)", "Bass Boost", "Vocal Enhancer", "Electronic" };
            float[][] presets = {
                new float[] { 50, 50, 50, 50, 50, 50, 50, 50, 50, 50 },
                new float[] { 70, 65, 60, 55, 50, 45, 50, 55, 60, 65 },
                new float[] { 45, 45, 50, 60, 65, 65, 60, 50, 45, 45 },
                new float[] { 65, 60, 50, 45, 45, 50, 55, 60, 65, 65 }
            };
            if (index >= 0 && index < presets.Length && sliders.Count >= 10)
            {
                for (int i = 0; i < 10; i++) sliders[i].Value = presets[index][i];
                if (_txtEqPresetName != null) _txtEqPresetName.Text = names[index];
            }
        }

        private static (
            Color c1, Color c2, Color c3, Color accent,
            Color glassColor, Color darkBgColor, Color windowBg1, Color windowBg2,
            string borderHex, double ambientOpacity, string name
        )[] _themeDefs = new[]
        {
            ( ParseColor("#FFFF006E"), ParseColor("#FF8A2BE2"), ParseColor("#FF00F0FF"), ParseColor("#FFFF006E"), Color.FromArgb(22, 255, 0, 110), Color.FromArgb(215, 8, 3, 20), Color.FromArgb(85, 15, 2, 32), Color.FromArgb(75, 6, 0, 18), "#30FF006E", 0.40, "🌃 Neon Dream" ),
            ( ParseColor("#FF00AAFF"), ParseColor("#FF80D8FF"), ParseColor("#FFE8F8FF"), ParseColor("#FF00AAFF"), Color.FromArgb(55, 135, 206, 250), Color.FromArgb(165, 5, 20, 65), Color.FromArgb(90, 3, 14, 45), Color.FromArgb(80, 2, 8, 30), "#3800AAFF", 0.55, "💎 Aero Glass" ),
            ( ParseColor("#FF1DB954"), ParseColor("#FF148040"), ParseColor("#FF3DDC84"), ParseColor("#FF1DB954"), Color.FromArgb(18, 255, 255, 255), Color.FromArgb(238, 18, 18, 18), Color.FromArgb(255, 18, 18, 18), Color.FromArgb(255, 12, 12, 12), "#201DB954", 0.12, "🎵 Midnight Green" ),
            ( ParseColor("#FF2ECC00"), ParseColor("#FFFF6600"), ParseColor("#FFFFD700"), ParseColor("#FF2ECC00"), Color.FromArgb(18, 47, 217, 0), Color.FromArgb(248, 4, 4, 4), Color.FromArgb(255, 5, 5, 5), Color.FromArgb(255, 2, 2, 2), "#282ECC00", 0.25, "📺 RetroClassic" ),
            ( Colors.Gray, Colors.DarkGray, Colors.White, Colors.Gray, Color.FromArgb(45, 128, 128, 128), Color.FromArgb(200, 10, 10, 10), Color.FromArgb(255, 15, 15, 15), Color.FromArgb(255, 5, 5, 5), "#30808080", 0.50, "🎨 Dinamik Adaptif" )
        };

        private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        private static Color AdjustColor(Color color, double factor)
        {
            byte r = (byte)Math.Max(0, Math.Min(255, color.R * factor));
            byte g = (byte)Math.Max(0, Math.Min(255, color.G * factor));
            byte b = (byte)Math.Max(0, Math.Min(255, color.B * factor));
            return Color.FromRgb(r, g, b);
        }

        private void GenerateDynamicTheme(Color dom)
        {
            Color c1 = AdjustColor(dom, 1.2);
            Color c2 = AdjustColor(dom, 0.8);
            Color c3 = AdjustColor(dom, 1.5);
            Color glass = Color.FromArgb(50, dom.R, dom.G, dom.B);
            Color darkBg = Color.FromArgb(230, (byte)(dom.R * 0.1), (byte)(dom.G * 0.1), (byte)(dom.B * 0.1));
            Color bg1 = Color.FromArgb(200, (byte)(dom.R * 0.08), (byte)(dom.G * 0.08), (byte)(dom.B * 0.08));
            Color bg2 = Color.FromArgb(220, (byte)(dom.R * 0.03), (byte)(dom.G * 0.03), (byte)(dom.B * 0.03));
            string hex = $"#50{dom.R:X2}{dom.G:X2}{dom.B:X2}";
            _themeDefs[4] = (c1, c2, c3, dom, glass, darkBg, bg1, bg2, hex, 0.60, "🎨 Dinamik Adaptif");
        }

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            _currentTheme = (_currentTheme + 1) % _themeDefs.Length;
            Color dom = Colors.Black;
            if (_bgColorRect?.Fill is RadialGradientBrush rgb && rgb.GradientStops.Count > 0)
                dom = rgb.GradientStops[0].Color;
            if (_currentTheme == 4) GenerateDynamicTheme(dom);
            ApplyTheme(_currentTheme);
            SetFakeAmbientGlow(dom);
        }

        private void ApplyTheme(int index)
        {
            var t = _themeDefs[index];
            var sg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            sg.GradientStops.Add(new GradientStop(t.c1, 0.0));
            sg.GradientStops.Add(new GradientStop(t.c2, 1.0));
            sg.Freeze();
            this.Resources["ThemeSliderGradient"] = sg;

            var vsg = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
            vsg.GradientStops.Add(new GradientStop(t.c1, 0.0));
            vsg.GradientStops.Add(new GradientStop(t.c2, 1.0));
            vsg.Freeze();
            this.Resources["ThemeVerticalSliderGradient"] = vsg;

            var mg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            mg.GradientStops.Add(new GradientStop(t.c1, 0.0));
            mg.GradientStops.Add(new GradientStop(t.c2, 1.0));
            mg.Freeze();
            this.Resources["ThemeMiniGradient"] = mg;

            var wbg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            wbg.GradientStops.Add(new GradientStop(t.windowBg1, 0.0));
            wbg.GradientStops.Add(new GradientStop(t.windowBg2, 0.5));
            wbg.GradientStops.Add(new GradientStop(t.windowBg1, 1.0));
            wbg.Freeze();
            this.Resources["ThemeWindowBg"] = wbg;

            var borderBrush = new SolidColorBrush(ParseColor(t.borderHex));
            borderBrush.Freeze();
            this.Resources["ThemeWindowBorderBrush"] = borderBrush;

            var accentBrush = new SolidColorBrush(t.accent); accentBrush.Freeze();
            var accentLightBrush = new SolidColorBrush(t.c1); accentLightBrush.Freeze();
            var glassBrush = new SolidColorBrush(t.glassColor);
            glassBrush.Freeze();
            var darkBgBrush = new SolidColorBrush(t.darkBgColor); darkBgBrush.Freeze();

            this.Resources["ThemeAccentColor"] = t.accent;
            this.Resources["ThemeAccentBrush"] = accentBrush;
            this.Resources["ThemeAccentLightBrush"] = accentLightBrush;
            this.Resources["ThemeGlassBrush"] = glassBrush;
            this.Resources["ThemeDarkBgBrush"] = darkBgBrush;

            var visBrush = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
            visBrush.GradientStops.Add(new GradientStop(t.c1, 0.0));
            visBrush.GradientStops.Add(new GradientStop(t.c2, 0.55));
            visBrush.GradientStops.Add(new GradientStop(t.c3, 1.0));
            visBrush.Freeze();
            _visBrush = visBrush;

            var miniBrush = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) };
            miniBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, t.c1.R, t.c1.G, t.c1.B), 0.0));
            miniBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, t.c3.R, t.c3.G, t.c3.B), 1.0));
            miniBrush.Freeze();
            _miniVisBrush = miniBrush;

            _wavePen = new Pen(_visBrush, 2.5) { LineJoin = PenLineJoin.Round };
            _wavePen.Freeze();

            if (_playGlow != null) _playGlow.Color = t.accent;
            var bgAmbient = this.FindName("BgAmbientGlow") as System.Windows.Controls.Image;
            if (bgAmbient != null) bgAmbient.Opacity = t.ambientOpacity;

            if (BtnTheme != null) BtnTheme.Content = t.name;
            ApplyThemeBrushesToHosts();
        }

        private void ApplyThemeBrushesToHosts()
        {
            var t = _themeDefs[_currentTheme];
            Color crtColor = _currentTheme switch
            {
                1 => Color.FromArgb(60, 180, 230, 255),
                2 => Color.FromArgb(160, 0, 0, 0),
                3 => Color.FromArgb(120, 0, 0, 0),
                4 => Color.FromArgb(90, (byte)(t.accent.R * 0.5), (byte)(t.accent.G * 0.5), (byte)(t.accent.B * 0.5)),
                _ => Color.FromArgb(100, 0, 0, 0)
            };
            _mainVisHost?.UpdateThemeBrushes(t.accent, crtColor);
            _miniVisHost?.UpdateThemeBrushes(t.accent, crtColor);
        }

        private void CacheUiRefs()
        {
            _txtNowPlayingTitle = this.FindName("TxtNowPlayingTitle") as TextBlock;
            _txtNowPlayingArtist = this.FindName("TxtNowPlayingArtist") as TextBlock;
            _txtMiniTitle = this.FindName("TxtMiniTitle") as TextBlock;
            _txtMiniArtist = this.FindName("TxtMiniArtist") as TextBlock;
            _txtMetaData = this.FindName("TxtMetaData") as TextBlock;
            _txtCurrentTime = this.FindName("TxtCurrentTime") as TextBlock;
            _txtTotalTime = this.FindName("TxtTotalTime") as TextBlock;
            _txtWidgetLyrics = this.FindName("TxtWidgetLyrics") as TextBlock;
            _txtBigKaraokeLyrics = this.FindName("TxtBigKaraokeLyrics") as TextBlock;
            _txtQueueCount = this.FindName("TxtQueueCount") as TextBlock;
            _txtEqPresetName = this.FindName("TxtEqPresetName") as TextBlock;

            _txtSidebarNowPlaying = this.FindName("TxtSidebarNowPlaying") as TextBlock;
            _txtSidebarArtist = this.FindName("TxtSidebarArtist") as TextBlock;

            _imgAlbumCover = this.FindName("ImgAlbumCover") as Image;
            _imgMiniCover = this.FindName("ImgMiniCover") as Image;
            _imgMiniPlayerCover = this.FindName("ImgMiniPlayerCover") as Image;
            _vinylRotation = this.FindName("VinylRotation") as RotateTransform;
            _playGlow = this.FindName("PlayGlow") as System.Windows.Media.Effects.DropShadowEffect;
            _bgColorRect = this.FindName("BgColorRect") as Rectangle;
            _mainVisCanvas = this.FindName("VisualizerCanvas") as Canvas;
            _miniVisCanvas = this.FindName("MiniVisualizerCanvas") as Canvas;
        }

        private void BindSlider(Slider? sld, Func<double> getValue)
        {
            if (sld == null) return;
            sld.ApplyTemplate();

            if (sld.Template.FindName("PART_Track", sld)
                is System.Windows.Controls.Primitives.Track track)
            {
                track.Thumb?.ClearValue(StyleProperty);
            }

            sld.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
                new System.Windows.Controls.Primitives.DragStartedEventHandler((s, e) => isUpdatingSlider = true));

            sld.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
                new System.Windows.Controls.Primitives.DragCompletedEventHandler((s, e) => {
                    AudioEngine.Instance.Seek(TimeSpan.FromSeconds(getValue()));
                    isUpdatingSlider = false;
                }));

            sld.PreviewMouseLeftButtonDown += (s, e) => {
                isUpdatingSlider = true;
            };

            sld.PreviewMouseLeftButtonUp += (s, e) => {
                double mouseX = e.GetPosition(sld).X;
                double ratio = mouseX / sld.ActualWidth;
                ratio = Math.Max(0.0, Math.Min(1.0, ratio));

                double seekTo = sld.Minimum + ratio * (sld.Maximum - sld.Minimum);
                AudioEngine.Instance.Seek(TimeSpan.FromSeconds(seekTo));
                isUpdatingSlider = false;
            };
        }

        private void SetupVisualizer()
        {
            if (_mainVisCanvas == null || CmbVisualizerMode == null) return;
            _mainVisCanvas.Children.Clear();
            _miniVisCanvas?.Children.Clear();

            _mainVisHost = new VisualizerHost();
            _miniVisHost = _miniVisCanvas != null ? new VisualizerHost() : null;

            if (_mainVisHost != null)
                _mainVisCanvas.Children.Add(_mainVisHost);
            if (_miniVisHost != null && _miniVisCanvas != null)
                _miniVisCanvas.Children.Add(_miniVisHost);

            ResizeVisualizerHosts();
            _mainVisHost?.ResetHistory();
            _miniVisHost?.ResetHistory();
            ApplyThemeBrushesToHosts();

            int mode = CmbVisualizerMode.SelectedIndex;
            if (_txtBigKaraokeLyrics != null)
                _txtBigKaraokeLyrics.Visibility = mode == KARAOKE_VISUALIZER_INDEX ? Visibility.Visible : Visibility.Collapsed;

            bool needsOverflow = mode == 4 || mode == 8 || mode == 9 || mode == 10 || mode == 14;
            _mainVisCanvas.ClipToBounds = !needsOverflow;

            // The built-in reflection works well for conventional bar/wave modes.
            // Full-scene/circular visualizers already draw their own depth, so skip
            // the duplicate VisualBrush render for them.
            bool showReflection =
                mode == 0 || mode == 1 || mode == 2 || mode == 3 ||
                mode == 5 || mode == 6 || mode == 7 || mode == 11;

            if (_mainVisCanvas.Parent is Grid parentGrid)
            {
                foreach (var child in parentGrid.Children)
                {
                    if (child is Rectangle rect && rect.Fill is VisualBrush vb && vb.Visual == _mainVisCanvas)
                    {
                        rect.Visibility = showReflection ? Visibility.Visible : Visibility.Collapsed;
                        break;
                    }
                }
            }
        }

        private void ResizeVisualizerHosts()
        {
            if (_mainVisHost != null && _mainVisCanvas != null)
            {
                _mainVisHost.Width = _mainVisCanvas.ActualWidth > 0 ? _mainVisCanvas.ActualWidth : 700;
                _mainVisHost.Height = _mainVisCanvas.ActualHeight > 0 ? _mainVisCanvas.ActualHeight : 150;
            }

            if (_miniVisHost != null && _miniVisCanvas != null)
            {
                _miniVisHost.Width = _miniVisCanvas.ActualWidth > 0 ? _miniVisCanvas.ActualWidth : 520;
                _miniVisHost.Height = _miniVisCanvas.ActualHeight > 0 ? _miniVisCanvas.ActualHeight : 80;
            }
        }

        private void RenderLoop(object? sender, EventArgs e)
        {
            if (!AudioEngine.Instance.IsPlaying || _renderFps <= 0) return;

            int frameMs = _renderFps <= 30 ? 33 : 16;
            if (_renderTimer.ElapsedMilliseconds < frameMs) return;

            _lastFrameDelta = Math.Min(_renderTimer.ElapsedMilliseconds / 1000.0, 0.1);
            _renderTimer.Restart();

            if (!isMiniPlayer && _vinylRotation != null)
            {
                _vinylRotation.Angle += _renderFps <= 30 ? 0.8 : 0.4;
                if (_vinylRotation.Angle >= 360.0) _vinylRotation.Angle -= 360.0;
            }

            int copied = AudioEngine.Instance.CopyFftData(_fftScratch);
            if (copied <= 0) return;

            BuildLogFrequencyBands(_fftScratch, copied, AudioEngine.Instance.SampleRate, _logFft);
            for (int i = 0; i < NUM_BARS; i++)
            {
                // Faster attack, slower release: punchy transients without jitter.
                float target = _logFft[i];
                float factor = target > smoothedFft[i] ? 0.42f : 0.18f;
                smoothedFft[i] += (target - smoothedFft[i]) * factor;
            }

            double bass = ComputeFrequencyEnergy(_fftScratch, copied, AudioEngine.Instance.SampleRate, 35, 180);

            if (_bgAmbientGlow != null)
            {
                double baseOpacity = _themeDefs[_currentTheme].ambientOpacity;
                _bgAmbientGlow.Opacity = Math.Min(1.0, baseOpacity + (Math.Sqrt(Math.Max(0, bass)) * 0.22));
            }

            if (!isMiniPlayer && _playGlow != null)
            {
                _playGlow.BlurRadius = Math.Min(50.0, 10.0 + Math.Sqrt(Math.Max(0, bass)) * 42.0);
                double maxOpacity = (_currentTheme == 2 || _currentTheme == 3) ? 0.65 : 1.0;
                _playGlow.Opacity = Math.Min(maxOpacity, 0.3 + Math.Sqrt(Math.Max(0, bass)) * 0.62);
            }

            _circularRotationAngle += _renderFps <= 30 ? 0.03 : 0.015;
            if (_circularRotationAngle > Math.PI * 2) _circularRotationAngle -= Math.PI * 2;

            _visualPhase += _lastFrameDelta * 1.35;
            if (_visualPhase > Math.PI * 2048) _visualPhase -= Math.PI * 2048;

            int visMode = CmbVisualizerMode?.SelectedIndex ?? 0;
            if (!isMiniPlayer && _mainVisHost != null && _mainVisCanvas != null && visMode != KARAOKE_VISUALIZER_INDEX)
            {
                double h = _mainVisCanvas.ActualHeight;
                double w = _mainVisCanvas.ActualWidth;
                if (w < 1 || h < 1) return;

                switch (visMode)
                {
                    case 0: _mainVisHost.DrawBars(smoothedFft, w, h, _visBrush!, mirror: false); break;
                    case 1: _mainVisHost.DrawCyberWave(smoothedFft, w, h, _visBrush!, _wavePen!); break;
                    case 2: _mainVisHost.DrawClassicWave(smoothedFft, w, h, _wavePen!); break;
                    case 3: _mainVisHost.DrawBars(smoothedFft, w, h, _visBrush!, mirror: true); break;
                    case 4: _mainVisHost.DrawClassicCircular(smoothedFft, w, h, _wavePen!, bass, _circularRotationAngle); break;
                    case 5: _mainVisHost.DrawSmoothedBlocks(smoothedFft, w, h, _visBrush!); break;
                    case 6: _mainVisHost.DrawFractureBars(smoothedFft, w, h, _visBrush!, bass); break;
                    case 7: _mainVisHost.DrawLiquidWave(smoothedFft, w, h, _wavePen!); break;
                    case 8: _mainVisHost.DrawOrbitalGalaxy(smoothedFft, w, h, _wavePen!, bass); break;
                    case 9: _mainVisHost.DrawAudioTunnel(smoothedFft, w, h, _wavePen!, bass); break;
                    case 10: _mainVisHost.DrawParticleReactor(smoothedFft, w, h, _visBrush!, bass, _lastFrameDelta); break;
                    case 11: _mainVisHost.DrawampCRT(smoothedFft, w, h, _visBrush!); break;
                    case 12: _mainVisHost.DrawSynthwaveHorizon(smoothedFft, w, h, _visBrush!, _wavePen!, bass); break;
                    case 13: _mainVisHost.DrawAeroAurora(smoothedFft, w, h, _visBrush!, _wavePen!, bass, _visualPhase); break;
                    case 14: _mainVisHost.DrawPulseRings(smoothedFft, w, h, _wavePen!, bass, _circularRotationAngle); break;
                    case 15: _mainVisHost.DrawNeonDna(smoothedFft, w, h, _wavePen!, _visualPhase); break;
                    case 16: _mainVisHost.DrawSpectrumMountains(smoothedFft, w, h, _wavePen!); break;
                }
            }
            else if (isMiniPlayer && _miniVisHost != null && _miniVisCanvas != null)
            {
                double mh = _miniVisCanvas.ActualHeight;
                double mw = _miniVisCanvas.ActualWidth;
                if (mw < 1 || mh < 1) return;

                for (int i = 0; i < MINI_BARS; i++)
                    _miniFft[i] = smoothedFft[i * 2];
                _miniVisHost.DrawMiniBars(_miniFft, mw, mh, _miniVisBrush!);
            }
        }

        private static void BuildLogFrequencyBands(float[] fft, int fftCount, int sampleRate, float[] output)
        {
            Array.Clear(output, 0, output.Length);
            if (fftCount <= 1 || sampleRate <= 0 || output.Length == 0) return;

            // fftCount is half the FFT size (real spectrum). Derive the real bin width
            // instead of hard-coding 1024 so this stays correct if FFT size changes.
            double fftSize = fftCount * 2.0;
            double binHz = sampleRate / fftSize;
            if (binHz <= 0) return;

            // With a 2048-point FFT the first useful bin is ~21.5 Hz @ 44.1 kHz.
            // Starting below the actual resolution used to make several left-most bars
            // read the exact same FFT bin, which produced the flat high plateau.
            const double requestedMinHz = 20.0;
            double minHz = Math.Max(requestedMinHz, binHz);
            double maxHz = Math.Min(20000.0, sampleRate / 2.0);
            if (maxHz <= minHz) return;

            int minBin = Math.Max(1, (int)Math.Ceiling(minHz / binHz));
            int maxBinExclusive = Math.Min(fftCount, (int)Math.Floor(maxHz / binHz) + 1);
            if (maxBinExclusive <= minBin) return;

            // Build strictly increasing band edges. This prevents multiple visual bars
            // from pointing at the same low-frequency FFT bin. At low frequencies each
            // bar gets at least one unique bin; higher bands naturally become wider.
            int[] edges = new int[output.Length + 1];
            edges[0] = minBin;
            edges[output.Length] = maxBinExclusive;

            double maxBinForCurve = Math.Max(minBin + 1.0, maxBinExclusive - 1.0);
            for (int i = 1; i < output.Length; i++)
            {
                double t = i / (double)output.Length;
                int desired = (int)Math.Round(minBin * Math.Pow(maxBinForCurve / minBin, t));

                int minAllowed = edges[i - 1] + 1;
                int maxAllowed = maxBinExclusive - (output.Length - i);
                edges[i] = Math.Max(minAllowed, Math.Min(maxAllowed, desired));
            }

            for (int band = 0; band < output.Length; band++)
            {
                int start = edges[band];
                int endExclusive = edges[band + 1];
                if (start >= fftCount || endExclusive <= start) continue;

                endExclusive = Math.Min(endExclusive, fftCount);
                double sumSq = 0.0;
                double peak = 0.0;
                int n = 0;

                for (int i = start; i < endExclusive; i++)
                {
                    double v = Math.Max(0.0, fft[i]);
                    sumSq += v * v;
                    if (v > peak) peak = v;
                    n++;
                }

                if (n > 0)
                {
                    // RMS keeps wide high-frequency bands from being averaged into
                    // nothing, while a small peak component preserves transients.
                    double rms = Math.Sqrt(sumSq / n);
                    output[band] = (float)(rms * 0.82 + peak * 0.18);
                }
            }
        }

        private static double ComputeFrequencyEnergy(float[] fft, int fftCount, int sampleRate, double minHz, double maxHz)
        {
            if (fftCount <= 1 || sampleRate <= 0) return 0;

            double binHz = sampleRate / (fftCount * 2.0);
            if (binHz <= 0) return 0;

            int i0 = Math.Max(1, Math.Min(fftCount - 1, (int)Math.Ceiling(minHz / binHz)));
            int i1 = Math.Max(i0, Math.Min(fftCount - 1, (int)Math.Floor(maxHz / binHz)));

            double sumSq = 0.0;
            int count = 0;
            for (int i = i0; i <= i1; i++)
            {
                double v = Math.Max(0.0, fft[i]);
                sumSq += v * v;
                count++;
            }
            return count > 0 ? Math.Sqrt(sumSq / count) : 0.0;
        }

        private void SetFakeAmbientGlow(Color dominant)
        {
            if (_bgColorRect == null) return;
            RadialGradientBrush brush;
            switch (_currentTheme)
            {
                case 1:
                    brush = new RadialGradientBrush();
                    brush.GradientStops.Add(new GradientStop(Color.FromArgb(110, 0, 170, 255), 0.0)); brush.GradientStops.Add(new GradientStop(Color.FromArgb(30, 0, 80, 160), 1.0)); break;
                case 2:
                    brush = new RadialGradientBrush();
                    brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 18, 18, 18), 0.0)); brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 10, 10, 10), 1.0)); break;
                case 3:
                    brush = new RadialGradientBrush();
                    brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 8, 8, 8), 0.0)); brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 2, 2, 2), 1.0)); break;
                case 4:
                    brush = new RadialGradientBrush();
                    brush.GradientStops.Add(new GradientStop(Color.FromArgb(180, dominant.R, dominant.G, dominant.B), 0.0)); brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, dominant.R, dominant.G, dominant.B), 1.0)); break;
                default:
                    brush = new RadialGradientBrush();
                    brush.GradientStops.Add(new GradientStop(Color.FromArgb(160, dominant.R, dominant.G, dominant.B), 0.0)); brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, dominant.R, dominant.G, dominant.B), 1.0)); break;
            }
            brush.Freeze();
            _bgColorRect.Fill = brush;
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var src = e.OriginalSource as DependencyObject;
            while (src != null)
            {
                if (ReferenceEquals(src, QueueCard) || src is Button || src is ListBox || src is ListBoxItem ||
                    src is Slider || src is ScrollViewer || src is TextBox || src is ComboBox ||
                    src is System.Windows.Controls.Primitives.Thumb ||
                    src is System.Windows.Controls.Primitives.RepeatButton)
                    return;
                src = VisualTreeHelper.GetParent(src);
            }

            this.DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => this.Close();
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _pendingSearch = TxtSearch.Text?.Trim() ?? "";
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private void ApplyLibraryFilter()
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(Playlist);
            string query = _pendingSearch;
            string active = _activeLibraryView;

            view.SortDescriptions.Clear();
            if (active == "Recently Played")
                view.SortDescriptions.Add(new SortDescription(nameof(SongModel.LastPlayedUtc), ListSortDirection.Descending));
            else if (active == "Most Played")
                view.SortDescriptions.Add(new SortDescription(nameof(SongModel.PlayCount), ListSortDirection.Descending));

            view.Filter = item =>
            {
                if (item is not SongModel song) return false;

                if (!string.IsNullOrWhiteSpace(query) &&
                    song.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    song.Artist.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                if (active == "Favorites") return song.IsFavorite;
                if (active == "Recently Played") return song.LastPlayedUtc.HasValue;
                if (active == "Most Played") return song.PlayCount > 0;
                if (active != "All Songs")
                {
                    return _state.Playlists.TryGetValue(active, out var paths) &&
                           paths.Any(p => string.Equals(p, song.FilePath, StringComparison.OrdinalIgnoreCase));
                }
                return true;
            };
            view.Refresh();
        }

        private void RefreshLibraryViews()
        {
            string desired = _activeLibraryView;
            _libraryViews.Clear();
            _libraryViews.Add("All Songs");
            _libraryViews.Add("Favorites");
            _libraryViews.Add("Recently Played");
            _libraryViews.Add("Most Played");
            foreach (string name in _state.Playlists.Keys.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
                _libraryViews.Add(name);

            if (!_libraryViews.Contains(desired)) desired = "All Songs";
            _activeLibraryView = desired;
            CmbLibraryView.SelectedItem = desired;
        }

        private void CmbLibraryView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLibraryView.SelectedItem is string selected)
            {
                _activeLibraryView = selected;
                ApplyLibraryFilter();
            }
        }

        private void BtnNewPlaylist_Click(object sender, RoutedEventArgs e)
        {
            string? name = PromptForText("New Playlist", "Playlist name:");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (name.Equals("All Songs", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Favorites", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Recently Played", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Most Played", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Bu isim SoundVibe tarafından ayrılmış.", "SoundVibe", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!_state.Playlists.ContainsKey(name))
                _state.Playlists[name] = new List<string>();
            _activeLibraryView = name;
            RefreshLibraryViews();
            SavePersistentState();
        }

        private void BtnAddToPlaylist_Click(object sender, RoutedEventArgs e)
        {
            SongModel? song = LstSongs.SelectedItem as SongModel ?? PlaybackManager.Instance.CurrentSong;
            if (song != null) AddSongToPlaylist(song);
        }

        private void MenuToggleFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem m && m.DataContext is SongModel song)
            {
                song.IsFavorite = !song.IsFavorite;
                SyncSongState(song);
                SavePersistentState();
                if (_activeLibraryView == "Favorites") ApplyLibraryFilter();
            }
        }

        private void MenuAddToActivePlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem m && m.DataContext is SongModel song)
                AddSongToPlaylist(song);
        }

        private void AddSongToPlaylist(SongModel song)
        {
            string playlistName = _activeLibraryView;
            if (!_state.Playlists.ContainsKey(playlistName))
            {
                string? entered = PromptForText("Add to Playlist", "Playlist name (existing or new):");
                if (string.IsNullOrWhiteSpace(entered)) return;
                playlistName = entered.Trim();
                if (!_state.Playlists.ContainsKey(playlistName))
                    _state.Playlists[playlistName] = new List<string>();
            }

            var paths = _state.Playlists[playlistName];
            if (!paths.Any(p => string.Equals(p, song.FilePath, StringComparison.OrdinalIgnoreCase)))
                paths.Add(song.FilePath);
            SavePersistentState();
            RefreshLibraryViews();
        }

        private string? PromptForText(string title, string prompt)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 360,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(18, 18, 22)),
                Foreground = Brushes.White,
                WindowStyle = WindowStyle.ToolWindow
            };
            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) };
            var box = new TextBox { Height = 28, Padding = new Thickness(6, 2, 6, 2) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = "OK", Width = 70, Height = 28, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", Width = 70, Height = 28, IsCancel = true };
            ok.Click += (s, e) => dialog.DialogResult = true;
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            Grid.SetRow(box, 1); Grid.SetRow(buttons, 2);
            grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(buttons);
            dialog.Content = grid;
            dialog.Loaded += (s, e) => { box.Focus(); Keyboard.Focus(box); };
            return dialog.ShowDialog() == true ? box.Text : null;
        }

        private void MenuPlayNext_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem m && m.DataContext is SongModel s) PlaybackManager.Instance.AddPlayNext(s);
        }
        private void MenuAddQueue_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem m && m.DataContext is SongModel s) PlaybackManager.Instance.AddLastQueue(s);
        }

        private void QueueRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is SongModel song)
                PlaybackManager.Instance.RemoveFromUserQueue(song);
        }

        private void CmbRenderMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _renderFps = CmbRenderMode.SelectedIndex switch
            {
                0 => 0,
                1 => 30,
                _ => 60
            };
            if (_renderFps == 0)
            {
                _mainVisHost?.Clear();
                _miniVisHost?.Clear();
            }
            RestartVisualRenderingOnly();
        }

        private void ChangeLyricOffset(int deltaMs)
        {
            LyricEngine.Instance.OffsetMilliseconds = Math.Max(-3000, Math.Min(3000, LyricEngine.Instance.OffsetMilliseconds + deltaMs));
            UpdateLyricOffsetLabel();
        }

        private void UpdateLyricOffsetLabel()
        {
            if (TxtLyricOffset != null)
                TxtLyricOffset.Text = $"{LyricEngine.Instance.OffsetMilliseconds:+0;-0;0}ms";
        }

        private void SetupTrayIcon()
        {
            if (_trayIcon != null) return;
            try
            {
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Show SoundVibe", null, (s, e) => Dispatcher.Invoke(RestoreFromTray));
                menu.Items.Add("Play / Pause", null, (s, e) => Dispatcher.Invoke(() => BtnPlayPause_Click(this, new RoutedEventArgs())));
                menu.Items.Add("Previous", null, (s, e) => Dispatcher.Invoke(() => BtnPrev_Click(this, new RoutedEventArgs())));
                menu.Items.Add("Next", null, (s, e) => Dispatcher.Invoke(() => BtnNext_Click(this, new RoutedEventArgs())));
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("Exit", null, (s, e) => Dispatcher.Invoke(Close));

                _trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Application,
                    Text = "SoundVibe",
                    Visible = true,
                    ContextMenuStrip = menu
                };
                _trayIcon.DoubleClick += (s, e) => Dispatcher.Invoke(RestoreFromTray);
            }
            catch { }
        }

        private void HideToTray()
        {
            StopRendering();
            Hide();
            if (_trayIcon != null) _trayIcon.Visible = true;
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = isMiniPlayer;
            if (AudioEngine.Instance.IsPlaying) StartRendering();
        }

        private const int WM_APPCOMMAND = 0x0319;
        private const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
        private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
        private const int APPCOMMAND_MEDIA_STOP = 13;
        private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_APPCOMMAND) return IntPtr.Zero;
            int command = ((int)((long)lParam >> 16)) & 0x7FF;
            switch (command)
            {
                case APPCOMMAND_MEDIA_NEXTTRACK:
                    BtnNext_Click(this, new RoutedEventArgs()); handled = true; break;
                case APPCOMMAND_MEDIA_PREVIOUSTRACK:
                    BtnPrev_Click(this, new RoutedEventArgs()); handled = true; break;
                case APPCOMMAND_MEDIA_PLAY_PAUSE:
                    BtnPlayPause_Click(this, new RoutedEventArgs()); handled = true; break;
                case APPCOMMAND_MEDIA_STOP:
                    AudioEngine.Instance.Pause(); UpdatePlayButtons(false); StopRendering(); handled = true; break;
            }
            return IntPtr.Zero;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

            string? folder = paths.FirstOrDefault(Directory.Exists);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                await LoadFolderAsync(folder);
                return;
            }

            SongModel? firstAdded = null;
            foreach (string file in paths.Where(IsAudioFile))
            {
                if (Playlist.Any(x => string.Equals(x.FilePath, file, StringComparison.OrdinalIgnoreCase))) continue;
                SongModel? song = await Task.Run(() => SongModel.CreateFromFile(file));
                if (song == null) continue;
                ApplySongState(song);
                Playlist.Add(song);
                UpdateCacheEntry(song);
                firstAdded ??= song;
            }
            PlaybackManager.Instance.UpdateQueue(Playlist.ToList());
            SaveLibraryCache();
            if (firstAdded != null)
            {
                PlaybackManager.Instance.SetCurrentSong(firstAdded);
                PlayCurrentSong();
            }
        }

        private void LoadPersistentState()
        {
            try
            {
                if (System.IO.File.Exists(StatePath))
                {
                    var loaded = JsonSerializer.Deserialize<SoundVibeState>(System.IO.File.ReadAllText(StatePath), JsonOptions);
                    if (loaded != null) _state = loaded;
                }
            }
            catch { _state = new SoundVibeState(); }

            _state.Playlists = new Dictionary<string, List<string>>(_state.Playlists ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);
            _state.Songs = new Dictionary<string, SongStateRecord>(_state.Songs ?? new Dictionary<string, SongStateRecord>(), StringComparer.OrdinalIgnoreCase);
        }

        private void SavePersistentState()
        {
            try
            {
                Directory.CreateDirectory(AppDataRoot);
                System.IO.File.WriteAllText(StatePath, JsonSerializer.Serialize(_state, JsonOptions));
            }
            catch { }
        }

        private void ApplySongState(SongModel song)
        {
            if (_state.Songs.TryGetValue(song.FilePath, out var record))
            {
                song.IsFavorite = record.IsFavorite;
                song.PlayCount = record.PlayCount;
                song.LastPlayedUtc = record.LastPlayedUtc;
            }
        }

        private void SyncSongState(SongModel song)
        {
            _state.Songs[song.FilePath] = new SongStateRecord
            {
                IsFavorite = song.IsFavorite,
                PlayCount = song.PlayCount,
                LastPlayedUtc = song.LastPlayedUtc
            };
        }

        private void LoadLibraryCache()
        {
            try
            {
                if (System.IO.File.Exists(CachePath))
                {
                    var list = JsonSerializer.Deserialize<List<LibraryCacheEntry>>(System.IO.File.ReadAllText(CachePath), JsonOptions) ?? new List<LibraryCacheEntry>();
                    _libraryCache = list.Where(x => !string.IsNullOrWhiteSpace(x.FilePath))
                        .GroupBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToDictionary(x => x.FilePath, x => x, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { _libraryCache = new Dictionary<string, LibraryCacheEntry>(StringComparer.OrdinalIgnoreCase); }
        }

        private void SaveLibraryCache()
        {
            try
            {
                Directory.CreateDirectory(AppDataRoot);
                System.IO.File.WriteAllText(CachePath, JsonSerializer.Serialize(_libraryCache.Values.ToList(), JsonOptions));
            }
            catch { }
        }

        private void UpdateCacheEntry(SongModel song)
        {
            try
            {
                _libraryCache[song.FilePath] = new LibraryCacheEntry
                {
                    FilePath = song.FilePath,
                    LastWriteUtcTicks = System.IO.File.GetLastWriteTimeUtc(song.FilePath).Ticks,
                    Title = song.Title,
                    Artist = song.Artist,
                    Bitrate = song.Bitrate,
                    SampleRate = song.SampleRate
                };
            }
            catch { }
        }

        private static bool IsAudioFile(string path)
        {
            try { return System.IO.File.Exists(path) && AudioExtensions.Contains(Path.GetExtension(path)); }
            catch { return false; }
        }

        private void SetupLibraryWatcher(string path)
        {
            DisposeLibraryWatcher();
            try
            {
                _libraryWatcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                _libraryWatcher.Created += LibraryWatcher_Created;
                _libraryWatcher.Deleted += LibraryWatcher_Deleted;
                _libraryWatcher.Renamed += LibraryWatcher_Renamed;
            }
            catch { DisposeLibraryWatcher(); }
        }

        private void DisposeLibraryWatcher()
        {
            if (_libraryWatcher == null) return;
            try
            {
                _libraryWatcher.EnableRaisingEvents = false;
                _libraryWatcher.Created -= LibraryWatcher_Created;
                _libraryWatcher.Deleted -= LibraryWatcher_Deleted;
                _libraryWatcher.Renamed -= LibraryWatcher_Renamed;
                _libraryWatcher.Dispose();
            }
            catch { }
            _libraryWatcher = null;
        }

        private async void LibraryWatcher_Created(object sender, FileSystemEventArgs e)
        {
            if (!AudioExtensions.Contains(Path.GetExtension(e.FullPath))) return;
            await Task.Delay(350);
            if (_isClosing || !System.IO.File.Exists(e.FullPath)) return;
            SongModel? song = await Task.Run(() => SongModel.CreateFromFile(e.FullPath));
            if (song == null || _isClosing) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (Playlist.Any(x => string.Equals(x.FilePath, song.FilePath, StringComparison.OrdinalIgnoreCase))) return;
                ApplySongState(song);
                Playlist.Add(song);
                UpdateCacheEntry(song);
                PlaybackManager.Instance.UpdateQueue(Playlist.ToList());
                SaveLibraryCache();
                ApplyLibraryFilter();
            });
        }

        private void LibraryWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            if (!AudioExtensions.Contains(Path.GetExtension(e.FullPath))) return;
            Dispatcher.BeginInvoke(new Action(() => RemoveLibraryPath(e.FullPath)));
        }

        private async void LibraryWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            await Dispatcher.InvokeAsync(() => RemoveLibraryPath(e.OldFullPath));
            if (AudioExtensions.Contains(Path.GetExtension(e.FullPath)))
                LibraryWatcher_Created(sender, new FileSystemEventArgs(WatcherChangeTypes.Created, Path.GetDirectoryName(e.FullPath) ?? "", Path.GetFileName(e.FullPath)));
        }

        private void RemoveLibraryPath(string path)
        {
            var song = Playlist.FirstOrDefault(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (song != null) Playlist.Remove(song);
            _libraryCache.Remove(path);
            _state.Songs.Remove(path);
            foreach (var list in _state.Playlists.Values)
                list.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            PlaybackManager.Instance.RemoveFromUserQueueByPath(path);
            PlaybackManager.Instance.UpdateQueue(Playlist.ToList());
            SaveLibraryCache();
            SavePersistentState();
        }

        private async void BtnResumeLast_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_resumePath)) return;
            var song = Playlist.FirstOrDefault(x => string.Equals(x.FilePath, _resumePath, StringComparison.OrdinalIgnoreCase));
            if (song == null) return;
            PlaybackManager.Instance.SetCurrentSong(song);
            PlayCurrentSong();
            await Task.Delay(100);
            AudioEngine.Instance.Seek(TimeSpan.FromSeconds(Math.Min(_resumeSeconds, Math.Max(0, AudioEngine.Instance.TotalTime.TotalSeconds - 1))));
            BtnResumeLast.Visibility = Visibility.Collapsed;
        }

        private void UpdateResumeButton()
        {
            bool canResume = _resumeSeconds > 1 && !string.IsNullOrWhiteSpace(_resumePath) &&
                             Playlist.Any(x => string.Equals(x.FilePath, _resumePath, StringComparison.OrdinalIgnoreCase));
            BtnResumeLast.Visibility = canResume ? Visibility.Visible : Visibility.Collapsed;
            if (canResume)
                BtnResumeLast.Content = $"↻ Resume {TimeSpan.FromSeconds(_resumeSeconds):mm\\:ss}";
        }

        private void ToggleMiniPlayer(object sender, RoutedEventArgs e)
        {
            isMiniPlayer = !isMiniPlayer;
            var mainUI = this.FindName("MainUI") as Grid;
            var miniUI = this.FindName("MiniUI") as Grid;

            if (isMiniPlayer)
            {
                if (mainUI != null) mainUI.Visibility = Visibility.Collapsed;
                if (miniUI != null) miniUI.Visibility = Visibility.Visible;
                this.Width = 450; this.Height = 80; this.Topmost = true;
                var wa = SystemParameters.WorkArea;
                this.Left = wa.Right - 465; this.Top = wa.Bottom - 95;
            }
            else
            {
                if (mainUI != null) mainUI.Visibility = Visibility.Visible;
                if (miniUI != null) miniUI.Visibility = Visibility.Collapsed;
                this.Width = 1100; this.Height = 750; this.Topmost = false;
                var wa = SystemParameters.WorkArea;
                this.Left = (wa.Width - 1100) / 2; this.Top = (wa.Height - 750) / 2;
            }
            SetupVisualizer();
        }

        private async void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "SoundVibe Müzik Klasörü Seçimi" };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            await LoadFolderAsync(dlg.SelectedPath);
        }

        // --- GÜNCELLENEN: YÜKLEME KODUNU DIŞARI ALDIK (OTOMATİK YÜKLEME İÇİN) ---
        private async Task LoadFolderAsync(string path)
        {
            if (!Directory.Exists(path)) return;

            _libraryLoadCts?.Cancel();
            _libraryLoadCts?.Dispose();
            _libraryLoadCts = new CancellationTokenSource();
            CancellationToken token = _libraryLoadCts.Token;
            DisposeLibraryWatcher();

            BtnAddFolder.Content = "⏳ Loading...";
            BtnAddFolder.IsEnabled = false;

            try
            {
                var loadedSongs = await Task.Run(() =>
                {
                    var temp = new List<SongModel>();
                    IEnumerable<string> files;
                    try
                    {
                        files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }
                    catch
                    {
                        files = Array.Empty<string>();
                    }

                    foreach (string file in files)
                    {
                        token.ThrowIfCancellationRequested();
                        SongModel? song = null;
                        try
                        {
                            long ticks = System.IO.File.GetLastWriteTimeUtc(file).Ticks;
                            if (_libraryCache.TryGetValue(file, out var cached) && cached.LastWriteUtcTicks == ticks)
                                song = SongModel.CreateFromCache(cached);
                            else
                                song = SongModel.CreateFromFile(file);
                        }
                        catch { }
                        if (song != null) temp.Add(song);
                    }
                    return temp;
                }, token);

                token.ThrowIfCancellationRequested();
                Playlist.Clear();
                const int chunkSize = 80;
                for (int i = 0; i < loadedSongs.Count; i += chunkSize)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var song in loadedSongs.Skip(i).Take(chunkSize))
                    {
                        ApplySongState(song);
                        Playlist.Add(song);
                        UpdateCacheEntry(song);
                    }
                    await Task.Yield();
                }

                TxtSearch.Text = "";
                _pendingSearch = "";
                PlaybackManager.Instance.UpdateQueue(Playlist.ToList());
                _lastFolderPath = path;
                SetupLibraryWatcher(path);
                SaveLibraryCache();
                ApplyLibraryFilter();
                UpdateResumeButton();

                _ = LoadMissingCoversSequentiallyAsync(
                    Playlist.Where(x => x.CoverImage == null).ToList(), token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    BtnAddFolder.Content = "📁 Add Folder";
                    BtnAddFolder.IsEnabled = true;
                }
            }
        }

        private async Task LoadMissingCoversSequentiallyAsync(List<SongModel> songs, CancellationToken token)
        {
            foreach (var song in songs)
            {
                if (token.IsCancellationRequested || _isClosing) break;

                try
                {
                    BitmapImage? cover = await Task.Run(
                        () => SongModel.LoadCoverFromFile(song.FilePath, 180), token);

                    if (cover != null && !token.IsCancellationRequested && !_isClosing)
                        song.CoverImage = cover;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // A corrupt/in-use file should not stop the rest of the library.
                }

                // Yield between files so large libraries never monopolize the UI loop.
                await Task.Yield();
            }
        }

        private void LstSongs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is SongModel song)
            {
                if (PlaybackManager.Instance.CurrentSong == song) return;
                PlaybackManager.Instance.SetCurrentSong(song);
                PlayCurrentSong();
            }
        }

        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            if (AudioEngine.Instance.CurrentTime.TotalSeconds > 3.0)
            {
                AudioEngine.Instance.Seek(TimeSpan.Zero);
                return;
            }
            if (PlaybackManager.Instance.GoToPrevious()) PlayCurrentSong();
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (PlaybackManager.Instance.GoToNext()) PlayCurrentSong();
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (AudioEngine.Instance.IsPlaying)
            {
                AudioEngine.Instance.Pause();
                UpdatePlayButtons(false);
                StopRendering();
                return;
            }

            if (AudioEngine.Instance.HasTrack)
            {
                AudioEngine.Instance.Resume();
                UpdatePlayButtons(AudioEngine.Instance.IsPlaying);
                if (AudioEngine.Instance.IsPlaying) StartRendering();
                return;
            }

            var song = PlaybackManager.Instance.CurrentSong ?? Playlist.FirstOrDefault();
            if (song == null)
            {
                UpdatePlayButtons(false);
                return;
            }

            PlaybackManager.Instance.SetCurrentSong(song);
            PlayCurrentSong();
        }

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => AudioEngine.Instance.SetVolume((float)(SldVolume.Value / 100.0));

        private async void PlayCurrentSong()
        {
            var song = PlaybackManager.Instance.CurrentSong;
            if (song == null) return;
            string requestedPath = song.FilePath;

            try
            {
                LstSongs.SelectedItem = song;
                LstSongs.ScrollIntoView(song);
            }
            catch { }

            float vol = SldVolume != null ? (float)(SldVolume.Value / 100.0) : 0.5f;
            AudioEngine.Instance.Play(song.FilePath, vol);
            if (!AudioEngine.Instance.HasTrack)
            {
                UpdatePlayButtons(false);
                StopRendering();
                return;
            }

            LyricEngine.Instance.LoadLyrics(song.FilePath);

            song.PlayCount++;
            song.LastPlayedUtc = DateTime.UtcNow;
            SyncSongState(song);
            SavePersistentState();
            if (_activeLibraryView == "Recently Played" || _activeLibraryView == "Most Played")
                ApplyLibraryFilter();

            Array.Clear(smoothedFft, 0, smoothedFft.Length);
            Array.Clear(_logFft, 0, _logFft.Length);
            SetupVisualizer();
            UpdatePlayButtons(true);
            StartRendering();
            BtnResumeLast.Visibility = Visibility.Collapsed;

            Color domColor = Color.FromRgb(74, 144, 226);
            UpdateUI(song, domColor);

            if (song.CoverImage == null)
            {
                BitmapImage? cover = await Task.Run(() => SongModel.LoadCoverFromFile(song.FilePath, 350));
                if (cover != null)
                {
                    song.CoverImage = cover;
                    if (PlaybackManager.Instance.CurrentSong?.FilePath == requestedPath)
                        UpdateUI(song, domColor);
                }
            }

            if (song.CoverImage != null)
                domColor = await ExtractDominantColorAsync(song.CoverImage);

            if (PlaybackManager.Instance.CurrentSong?.FilePath != requestedPath) return;

            if (_currentTheme == 4)
            {
                GenerateDynamicTheme(domColor);
                ApplyTheme(4);
            }
            UpdateUI(song, domColor);
            ShowToastNotification(song);
        }

        private static async Task<Color> ExtractDominantColorAsync(BitmapSource source)
        {
            try
            {
                const int size = 16;
                int stride = size * 4;
                var pixels = new byte[size * stride];
                var scaled = new TransformedBitmap(source, new ScaleTransform(
                    size / (double)Math.Max(1, source.PixelWidth),
                    size / (double)Math.Max(1, source.PixelHeight)));
                var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                converted.CopyPixels(pixels, stride, 0);

                return await Task.Run(() =>
                {
                    var buckets = new Dictionary<int, (int Count, long R, long G, long B, double Weight)>();
                    for (int i = 0; i + 3 < pixels.Length; i += 4)
                    {
                        byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                        if (a < 80) continue;
                        int max = Math.Max(r, Math.Max(g, b));
                        int min = Math.Min(r, Math.Min(g, b));
                        if (max < 28) continue;
                        double saturation = max == 0 ? 0 : (max - min) / (double)max;
                        double brightness = max / 255.0;
                        double weight = 0.35 + saturation * 1.4 + Math.Min(brightness, 0.85) * 0.35;
                        int key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                        if (!buckets.TryGetValue(key, out var v)) v = (0, 0, 0, 0, 0);
                        buckets[key] = (v.Count + 1, v.R + r, v.G + g, v.B + b, v.Weight + weight);
                    }
                    if (buckets.Count == 0) return Color.FromRgb(74, 144, 226);
                    var best = buckets.Values.OrderByDescending(v => v.Weight * Math.Sqrt(v.Count)).First();
                    return Color.FromRgb(
                        (byte)Math.Min(255, best.R / Math.Max(1, best.Count)),
                        (byte)Math.Min(255, best.G / Math.Max(1, best.Count)),
                        (byte)Math.Min(255, best.B / Math.Max(1, best.Count)));
                });
            }
            catch
            {
                return Color.FromRgb(74, 144, 226);
            }
        }

        private void UpdatePlayButtons(bool playing)
        {
            string icon = playing ? "⏸" : "▶";
            if (BtnPlayPause != null) BtnPlayPause.Content = icon;
            if (BtnMiniPlayPause != null) BtnMiniPlayPause.Content = icon;
        }

        private void UpdateUI(SongModel song, Color dominantColor)
        {
            if (_txtNowPlayingTitle != null) _txtNowPlayingTitle.Text = song.Title;
            if (_txtMiniTitle != null) _txtMiniTitle.Text = song.Title;
            if (_txtNowPlayingArtist != null) _txtNowPlayingArtist.Text = song.Artist;
            if (_txtMiniArtist != null) _txtMiniArtist.Text = song.Artist;

            if (_txtMetaData != null)
            {
                string fmt = string.IsNullOrWhiteSpace(song.FilePath) ? "MP3" : System.IO.Path.GetExtension(song.FilePath).TrimStart('.').ToUpperInvariant();
                if (string.IsNullOrEmpty(fmt)) fmt = "MP3";
                _txtMetaData.Text = $"{fmt} • {song.Bitrate} kbps • {(song.SampleRate / 1000.0):0.0} kHz";
            }

            if (_txtSidebarNowPlaying != null) _txtSidebarNowPlaying.Text = song.Title;
            if (_txtSidebarArtist != null) _txtSidebarArtist.Text = song.Artist;
            if (_imgAlbumCover != null)
            {
                RenderOptions.SetBitmapScalingMode(_imgAlbumCover, BitmapScalingMode.HighQuality);
                _imgAlbumCover.Source = song.CoverImage;
            }
            if (_imgMiniCover != null) _imgMiniCover.Source = song.CoverImage;
            if (_imgMiniPlayerCover != null) _imgMiniPlayerCover.Source = song.CoverImage;

            SetFakeAmbientGlow(dominantColor);
        }
    }

    public sealed class SoundVibeConfig
    {
        public double Volume { get; set; } = 50;
        public int ThemeIndex { get; set; } = 0;
        public int EqPresetIndex { get; set; } = 0;
        public string LastFolderPath { get; set; } = "";
        public int RenderFps { get; set; } = 60;
        public int LyricOffsetMs { get; set; } = 200;
        public string LastSongPath { get; set; } = "";
        public double LastPositionSeconds { get; set; } = 0;
        public string LastLibraryView { get; set; } = "All Songs";
        public int VisualizerModeIndex { get; set; } = 10;
    }

    public sealed class SoundVibeState
    {
        public Dictionary<string, SongStateRecord> Songs { get; set; } = new Dictionary<string, SongStateRecord>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Playlists { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class SongStateRecord
    {
        public bool IsFavorite { get; set; }
        public int PlayCount { get; set; }
        public DateTime? LastPlayedUtc { get; set; }
    }

    public sealed class LibraryCacheEntry
    {
        public string FilePath { get; set; } = "";
        public long LastWriteUtcTicks { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public int Bitrate { get; set; }
        public int SampleRate { get; set; }
    }

    #region [ 2. PLAYBACK & QUEUE MANAGER ]
    public class PlaybackManager
    {
        public static PlaybackManager Instance { get; } = new PlaybackManager();

        private List<SongModel> _originalQueue = new List<SongModel>();
        private List<SongModel> _shuffledQueue = new List<SongModel>();
        private int _currentIndex = -1;
        private readonly Random _rnd = new Random();
        private bool _isShuffle = false;

        public ObservableCollection<SongModel> UserQueue { get; } = new ObservableCollection<SongModel>();

        public bool IsShuffle
        {
            get => _isShuffle;
            set
            {
                SongModel? current = CurrentSong;
                _isShuffle = value;
                if (_isShuffle)
                    GenerateShuffleQueue(current);
                else
                    _currentIndex = current == null ? -1 : _originalQueue.IndexOf(current);
            }
        }

        public bool IsRepeat { get; set; } = false;

        public SongModel? CurrentSong
        {
            get
            {
                var targetList = IsShuffle ? _shuffledQueue : _originalQueue;
                return (_currentIndex >= 0 && _currentIndex < targetList.Count) ? targetList[_currentIndex] : null;
            }
        }

        public int QueueCount => UserQueue.Count;

        private PlaybackManager() { }

        public void UpdateQueue(List<SongModel> songs)
        {
            SongModel? current = CurrentSong;
            _originalQueue = new List<SongModel>(songs);
            if (IsShuffle)
            {
                GenerateShuffleQueue(current);
            }
            else
            {
                _currentIndex = current == null ? -1 : _originalQueue.IndexOf(current);
            }
        }

        private void GenerateShuffleQueue(SongModel? current = null)
        {
            _shuffledQueue = new List<SongModel>(_originalQueue);
            for (int n = _shuffledQueue.Count - 1; n > 0; n--)
            {
                int k = _rnd.Next(n + 1);
                (_shuffledQueue[n], _shuffledQueue[k]) = (_shuffledQueue[k], _shuffledQueue[n]);
            }
            if (current != null && _shuffledQueue.Remove(current))
            {
                _shuffledQueue.Insert(0, current);
                _currentIndex = 0;
            }
        }

        public void AddPlayNext(SongModel song)
        {
            UserQueue.Insert(0, song);
        }

        public void AddLastQueue(SongModel song)
        {
            UserQueue.Add(song);
        }

        public void RemoveFromUserQueue(SongModel song)
        {
            UserQueue.Remove(song);
        }

        public void RemoveFromUserQueueByPath(string path)
        {
            for (int i = UserQueue.Count - 1; i >= 0; i--)
                if (string.Equals(UserQueue[i].FilePath, path, StringComparison.OrdinalIgnoreCase))
                    UserQueue.RemoveAt(i);
        }

        public void ClearUserQueue() => UserQueue.Clear();

        public void SetCurrentSong(SongModel song)
        {
            var targetList = IsShuffle ? _shuffledQueue : _originalQueue;
            _currentIndex = targetList.IndexOf(song);
            if (_currentIndex < 0 && IsShuffle && _originalQueue.Contains(song))
            {
                _shuffledQueue.Insert(0, song);
                _currentIndex = 0;
            }
        }

        public bool GoToNext()
        {
            var targetList = IsShuffle ? _shuffledQueue : _originalQueue;
            if (targetList.Count == 0) return false;
            if (IsRepeat) return CurrentSong != null;

            if (UserQueue.Count > 0)
            {
                var next = UserQueue[0];
                UserQueue.RemoveAt(0);
                SetCurrentSong(next);
                return CurrentSong != null;
            }

            _currentIndex++;
            if (_currentIndex >= targetList.Count)
            {
                if (IsShuffle)
                {
                    GenerateShuffleQueue();
                    _currentIndex = 0;
                }
                else
                {
                    _currentIndex = 0;
                }
            }
            return true;
        }

        public bool GoToPrevious()
        {
            var targetList = IsShuffle ? _shuffledQueue : _originalQueue;
            if (targetList.Count == 0) return false;
            _currentIndex--;
            if (_currentIndex < 0) _currentIndex = targetList.Count - 1;
            return true;
        }
    }
    #endregion

    #region [ 3. ASYNC DATA MODEL ]
    public class SongModel : INotifyPropertyChanged
    {
        private BitmapImage? _coverImage;
        private bool _isFavorite;
        private int _playCount;
        private DateTime? _lastPlayedUtc;

        public string Title { get; set; } = "Bilinmeyen Şarkı";
        public string Artist { get; set; } = "Bilinmeyen Sanatçı";
        public string FilePath { get; set; } = "";
        public int Bitrate { get; set; }
        public int SampleRate { get; set; }

        public BitmapImage? CoverImage
        {
            get => _coverImage;
            set
            {
                if (ReferenceEquals(_coverImage, value)) return;
                _coverImage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
            }
        }

        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite == value) return;
                _isFavorite = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            }
        }

        public int PlayCount
        {
            get => _playCount;
            set
            {
                if (_playCount == value) return;
                _playCount = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayCount)));
            }
        }

        public DateTime? LastPlayedUtc
        {
            get => _lastPlayedUtc;
            set
            {
                if (_lastPlayedUtc == value) return;
                _lastPlayedUtc = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastPlayedUtc)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static SongModel? CreateFromFile(string path)
        {
            try
            {
                using var tagFile = TagLib.File.Create(path);
                var song = new SongModel
                {
                    FilePath = path,
                    Title = string.IsNullOrWhiteSpace(tagFile.Tag.Title) ? Path.GetFileNameWithoutExtension(path) : tagFile.Tag.Title,
                    Artist = string.IsNullOrWhiteSpace(string.Join(", ", tagFile.Tag.Performers)) ? "Bilinmeyen Sanatçı" : string.Join(", ", tagFile.Tag.Performers),
                    Bitrate = tagFile.Properties.AudioBitrate != 0 ? tagFile.Properties.AudioBitrate : ((tagFile.Properties.AudioSampleRate * 16 * Math.Max(1, tagFile.Properties.AudioChannels)) / 1000),
                    SampleRate = tagFile.Properties.AudioSampleRate
                };

                if (tagFile.Tag.Pictures.Length >= 1)
                    song.CoverImage = BitmapFromBytes((byte[])tagFile.Tag.Pictures[0].Data.Data, 350);
                return song;
            }
            catch
            {
                return null;
            }
        }

        public static SongModel CreateFromCache(LibraryCacheEntry cache)
        {
            return new SongModel
            {
                FilePath = cache.FilePath,
                Title = string.IsNullOrWhiteSpace(cache.Title) ? Path.GetFileNameWithoutExtension(cache.FilePath) : cache.Title,
                Artist = string.IsNullOrWhiteSpace(cache.Artist) ? "Bilinmeyen Sanatçı" : cache.Artist,
                Bitrate = cache.Bitrate,
                SampleRate = cache.SampleRate
            };
        }

        public static BitmapImage? LoadCoverFromFile(string path, int decodePixelWidth)
        {
            try
            {
                using var tagFile = TagLib.File.Create(path);
                if (tagFile.Tag.Pictures.Length < 1) return null;
                return BitmapFromBytes((byte[])tagFile.Tag.Pictures[0].Data.Data, decodePixelWidth);
            }
            catch { return null; }
        }

        private static BitmapImage? BitmapFromBytes(byte[] bytes, int decodePixelWidth)
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = Math.Max(32, decodePixelWidth);
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
    #endregion

    #region [ 4. AUDIO ENGINE & EQUALIZER ]

    public class EqualizerProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly BiQuadFilter[,] _filters;
        private readonly float[] _gains = new float[10];
        private readonly float[] _frequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        public WaveFormat WaveFormat => _source.WaveFormat;

        public EqualizerProvider(ISampleProvider source)
        {
            _source = source;
            int channels = source.WaveFormat.Channels;
            _filters = new BiQuadFilter[channels, _frequencies.Length];

            for (int c = 0; c < channels; c++)
            {
                for (int b = 0; b < _frequencies.Length; b++)
                {
                    _filters[c, b] = BiQuadFilter.PeakingEQ(source.WaveFormat.SampleRate, _frequencies[b], 1.0f, 0f);
                }
            }
        }

        public void UpdateGain(int bandIndex, float gainDb)
        {
            if (bandIndex < 0 || bandIndex >= _frequencies.Length) return;
            _gains[bandIndex] = gainDb;
            for (int c = 0; c < WaveFormat.Channels; c++)
            {
                _filters[c, bandIndex].SetPeakingEq(WaveFormat.SampleRate, _frequencies[bandIndex], 1.0f, gainDb);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);
            int channels = WaveFormat.Channels;

            for (int i = 0; i < samplesRead; i++)
            {
                int ch = i % channels;
                float sample = buffer[offset + i];

                for (int b = 0; b < _frequencies.Length; b++)
                {
                    if (_gains[b] != 0)
                        sample = _filters[ch, b].Transform(sample);
                }
                buffer[offset + i] = sample;
            }
            return samplesRead;
        }
    }

    public class AudioEngine : IDisposable
    {
        public static AudioEngine Instance { get; } = new AudioEngine();

        private WaveOutEvent? outputDevice;
        private AudioFileReader? audioFile;
        private AudioAnalyzer? analyzer;
        private EqualizerProvider? equalizer;
        private float[] currentEqGains = new float[10];

        public bool IsPlaying => outputDevice?.PlaybackState == PlaybackState.Playing;
        public bool HasTrack => audioFile != null && outputDevice != null;
        public TimeSpan CurrentTime => audioFile?.CurrentTime ?? TimeSpan.Zero;
        public TimeSpan TotalTime => audioFile?.TotalTime ?? TimeSpan.Zero;
        public int SampleRate => audioFile?.WaveFormat.SampleRate ?? 44100;
        public event EventHandler? TrackEnded;

        private AudioEngine() { }

        public void Play(string path, float volume)
        {
            try
            {
                Stop();

                audioFile = new AudioFileReader(path);
                analyzer = new AudioAnalyzer(audioFile);
                equalizer = new EqualizerProvider(analyzer);
                for (int i = 0; i < 10; i++) equalizer.UpdateGain(i, currentEqGains[i]);

                outputDevice = new WaveOutEvent();
                outputDevice.PlaybackStopped += OnPlaybackStopped;
                outputDevice.Init(equalizer);
                outputDevice.Volume = volume;
                outputDevice.Play();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ses cihazı başlatılamadı: \n{ex.Message}", "Donanım Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                Stop();
            }
        }

        public void SetEqGain(int bandIndex, float gainDb)
        {
            if (bandIndex >= 0 && bandIndex < 10)
            {
                currentEqGains[bandIndex] = gainDb;
                equalizer?.UpdateGain(bandIndex, gainDb);
            }
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null) return;
            try
            {
                if (audioFile == null) return;
                if (audioFile.Length - audioFile.Position < audioFile.WaveFormat.AverageBytesPerSecond)
                    TrackEnded?.Invoke(this, EventArgs.Empty);
            }
            catch (ObjectDisposedException) { }
        }

        public void Pause() => outputDevice?.Pause();
        public void Resume() => outputDevice?.Play();
        public void Stop()
        {
            if (outputDevice != null)
            {
                outputDevice.PlaybackStopped -= OnPlaybackStopped;
                outputDevice.Stop();
                outputDevice.Dispose();
                outputDevice = null;
            }
            audioFile?.Dispose();
            audioFile = null;
            analyzer = null;
            equalizer = null;
        }

        public void SetVolume(float v)
        {
            if (outputDevice != null) outputDevice.Volume = v;
        }

        public void Seek(TimeSpan t)
        {
            if (audioFile != null) audioFile.CurrentTime = t;
        }

        public int CopyFftData(float[] destination)
        {
            if (destination == null || destination.Length == 0 || analyzer == null) return 0;
            return analyzer.CopyFftResults(destination);
        }

        public void Dispose() => Stop();
    }

    public class AudioAnalyzer : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _fftLength = 2048;
        private int _fftPos = 0;
        private Complex[] _fftBuffer;
        private float[] _fftResults;
        private readonly object _fftLock = new object();

        public WaveFormat WaveFormat => _source.WaveFormat;

        public AudioAnalyzer(ISampleProvider source)
        {
            _source = source;
            _fftBuffer = new Complex[_fftLength];
            _fftResults = new float[_fftLength / 2];
        }

        public int CopyFftResults(float[] destination)
        {
            if (destination == null || destination.Length == 0) return 0;
            lock (_fftLock)
            {
                int count = Math.Min(destination.Length, _fftResults.Length);
                Array.Copy(_fftResults, 0, destination, 0, count);
                return count;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);
            int channels = _source.WaveFormat.Channels;

            for (int i = 0; i < samplesRead; i += channels)
            {
                float mono = 0f;
                for (int c = 0; c < channels; c++)
                    if (i + c < samplesRead) mono += buffer[offset + i + c];
                mono /= channels;

                double window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * _fftPos / (_fftLength - 1));
                _fftBuffer[_fftPos].X = (float)(mono * window);
                _fftBuffer[_fftPos].Y = 0f;
                _fftPos++;

                if (_fftPos >= _fftLength)
                {
                    FastFourierTransform.FFT(true, (int)Math.Log(_fftLength, 2.0), _fftBuffer);
                    lock (_fftLock)
                    {
                        for (int j = 0; j < _fftResults.Length; j++)
                            _fftResults[j] = (float)Math.Sqrt(_fftBuffer[j].X * _fftBuffer[j].X + _fftBuffer[j].Y * _fftBuffer[j].Y);
                    }
                    _fftPos = 0;
                }
            }
            return samplesRead;
        }
    }
    #endregion

    #region [ 5. LYRIC ENGINE & WINDOWS API ]
    public class LyricEngine
    {
        public static LyricEngine Instance { get; } = new LyricEngine();
        private List<KeyValuePair<TimeSpan, string>> _syncLyrics = new();
        private string _staticLyrics = "";
        public int OffsetMilliseconds { get; set; } = 200;

        private LyricEngine() { }

        public void LoadLyrics(string filePath)
        {
            _syncLyrics.Clear();
            _staticLyrics = "";
            try
            {
                string lrc = System.IO.Path.ChangeExtension(filePath, ".lrc");
                if (System.IO.File.Exists(lrc))
                {
                    string[] formats = { @"mm\:ss\.ff", @"m\:ss\.ff", @"mm\:ss\.fff", @"m\:ss\.fff", @"mm\:ss" };
                    foreach (var line in System.IO.File.ReadAllLines(lrc))
                    {
                        if (!line.StartsWith("[") || !line.Contains("]")) continue;
                        int end = line.IndexOf("]");
                        string ts = line.Substring(1, end - 1);
                        string txt = line.Substring(end + 1).Trim();
                        if (TimeSpan.TryParseExact(ts, formats, null, System.Globalization.TimeSpanStyles.None, out TimeSpan t))
                            _syncLyrics.Add(new KeyValuePair<TimeSpan, string>(t, txt));
                    }
                }
                else
                {
                    using var tf = TagLib.File.Create(filePath);
                    if (!string.IsNullOrWhiteSpace(tf.Tag.Lyrics)) _staticLyrics = tf.Tag.Lyrics;
                }

                if (_syncLyrics.Count > 1)
                    _syncLyrics.Sort((a, b) => a.Key.CompareTo(b.Key));
            }
            catch { }
        }

        public string GetCurrentLyric(TimeSpan t)
        {
            if (_syncLyrics.Count > 0)
            {
                TimeSpan offsetTime = t.Add(TimeSpan.FromMilliseconds(OffsetMilliseconds));

                // Binary search avoids walking the whole lyric list four times a second.
                int lo = 0;
                int hi = _syncLyrics.Count - 1;
                int best = -1;
                while (lo <= hi)
                {
                    int mid = lo + ((hi - lo) / 2);
                    if (_syncLyrics[mid].Key <= offsetTime)
                    {
                        best = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                if (best < 0) return "♪ ... ♪";
                string value = _syncLyrics[best].Value;
                return string.IsNullOrWhiteSpace(value) ? "♪" : value;
            }
            return string.IsNullOrWhiteSpace(_staticLyrics) ? "♪ Lirik Bulunamadı ♪" : _staticLyrics;
        }
    }

    public static class WindowsServices
    {
        [DllImport("user32.dll")]
        internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        public static void EnableAcrylicBlur(Window window)
        {
            var wh = new WindowInteropHelper(window);
            var accent = new AccentPolicy { AccentState = 4, GradientColor = (0x01 << 24) | 0x40200D };
            int sz = Marshal.SizeOf(accent);
            var ptr = Marshal.AllocHGlobal(sz);
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = 19, SizeOfData = sz, Data = ptr };
            SetWindowCompositionAttribute(wh.Handle, ref data);
            Marshal.FreeHGlobal(ptr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data; public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags; public int GradientColor; public int AnimationId;
    }
    #endregion
}