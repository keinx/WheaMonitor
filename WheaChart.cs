using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WheaMonitor
{
    /// <summary>
    /// Lightweight, dependency-free line chart with a per-minute hover readout.
    /// </summary>
    public sealed class WheaChart : FrameworkElement
    {
        private const int BucketCount = 60;
        private static readonly Brush HitTestBrush = MakeBrush(Color.FromArgb(0x01, 0, 0, 0));
        private static readonly Brush GridHBrush = MakeBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        private static readonly Brush GridVBrush = MakeBrush(Color.FromRgb(0x31, 0x31, 0x31));
        private static readonly Brush AxisBrush = MakeBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
        private static readonly Brush LabelBrush = MakeBrush(Color.FromRgb(0x7E, 0x7E, 0x7E));
        private static readonly Brush EmptyTitleBrush = MakeBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        private static readonly Brush EmptySubBrush = MakeBrush(Color.FromRgb(0x66, 0x66, 0x66));
        private static readonly Brush LineBrush = MakeBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
        private static readonly Brush GlowBrush = MakeBrush(Color.FromArgb(0x2E, 0x60, 0xCD, 0xFF));
        private static readonly Brush DotCoreBrush = MakeBrush(Color.FromRgb(0xE8, 0xF7, 0xFF));
        private static readonly Brush HoverBandBrush = MakeBrush(Color.FromArgb(0x1C, 0x60, 0xCD, 0xFF));
        private static readonly Brush TipBackgroundBrush = MakeBrush(Color.FromRgb(0x34, 0x34, 0x34));
        private static readonly Brush TipBorderBrush = MakeBrush(Color.FromRgb(0x50, 0x50, 0x50));
        private static readonly Brush TipPrimaryBrush = MakeBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
        private static readonly Brush TipSecondaryBrush = MakeBrush(Color.FromRgb(0x9C, 0x9C, 0x9C));
        private static readonly Pen GridHPen = MakePen(GridHBrush, 1.0);
        private static readonly Pen GridVPen = MakePen(GridVBrush, 1.0);
        private static readonly Pen AxisPen = MakePen(AxisBrush, 1.0);
        private static readonly Pen LinePen = MakePen(LineBrush, 2.0);
        private static readonly Pen GlowPen = MakePen(GlowBrush, 7.0);
        private static readonly Pen HoverLinePen = MakePen(
            MakeBrush(Color.FromArgb(0xC0, 0x60, 0xCD, 0xFF)), 1.0);
        private static readonly Pen TipBorderPen = MakePen(TipBorderBrush, 1.0);

        private readonly FontFamily _font = new FontFamily("Segoe UI Variable Text");
        private readonly int[] _values = new int[BucketCount];
        private readonly string[] _labels = new string[BucketCount];
        private DateTime _windowStart = DateTime.Now;
        private TimeSpan _bucketSize = TimeSpan.FromMinutes(1);
        private LinearGradientBrush _areaBrush;
        private int _hoverIndex = -1;

        public WheaChart()
        {
            SnapsToDevicePixels = true;
            ClipToBounds = true;
            Cursor = Cursors.Cross;

            _areaBrush = new LinearGradientBrush(
                Color.FromArgb(0x48, 0x60, 0xCD, 0xFF),
                Color.FromArgb(0x02, 0x60, 0xCD, 0xFF),
                new Point(0, 0),
                new Point(0, 1));
            _areaBrush.Freeze();

            MouseMove += OnMouseMove;
            MouseLeave += OnMouseLeave;
        }

        public void SetData(
            int[] values,
            DateTime windowStart,
            string[] labels,
            TimeSpan bucketSize)
        {
            if (values == null)
            {
                throw new ArgumentNullException("values");
            }

            for (int i = 0; i < _values.Length; i++)
            {
                _values[i] = i < values.Length ? values[i] : 0;
                _labels[i] = labels != null && i < labels.Length && labels[i] != null
                    ? labels[i]
                    : string.Empty;
            }

            _windowStart = windowStart;
            _bucketSize = bucketSize > TimeSpan.Zero ? bucketSize : TimeSpan.FromMinutes(1);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double width = ActualWidth;
            double height = ActualHeight;
            if (width < 80 || height < 60)
            {
                return;
            }

            // A near-transparent fill keeps the whole chart hit-testable for hover.
            dc.DrawRectangle(HitTestBrush, null, new Rect(0, 0, width, height));

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Rect plot = GetPlotRect();
            if (plot.Width < 20 || plot.Height < 20)
            {
                return;
            }

            int maxValue = 0;
            for (int i = 0; i < _values.Length; i++)
            {
                if (_values[i] > maxValue)
                {
                    maxValue = _values[i];
                }
            }

            int axisMax = NiceMax(maxValue);

            // Horizontal grid, four equal bands.
            for (int i = 0; i <= 4; i++)
            {
                double y = plot.Top + plot.Height * i / 4.0;
                dc.DrawLine(i == 4 ? AxisPen : GridHPen,
                    new Point(plot.Left, y),
                    new Point(plot.Right, y));
            }

            // Vertical grid, twelve equal bands.
            for (int i = 0; i <= 12; i++)
            {
                double x = plot.Left + plot.Width * i / 12.0;
                dc.DrawLine(GridVPen,
                    new Point(x, plot.Top),
                    new Point(x, plot.Bottom));
            }

            DrawYAxis(dc, plot, axisMax, dpi);
            DrawXAxis(dc, plot, dpi);

            Point[] points = new Point[BucketCount];
            for (int i = 0; i < BucketCount; i++)
            {
                double x = plot.Left + plot.Width * i / (BucketCount - 1.0);
                double y = plot.Bottom - (plot.Height * _values[i] / axisMax);
                if (y < plot.Top)
                {
                    y = plot.Top;
                }
                points[i] = new Point(x, y);
            }

            if (maxValue == 0)
            {
                DrawEmptyState(dc, plot, dpi);
            }
            else
            {
                StreamGeometry area = BuildGeometry(points, plot, true);
                dc.DrawGeometry(_areaBrush, null, area);

                StreamGeometry line = BuildGeometry(points, plot, false);
                dc.DrawGeometry(null, GlowPen, line);
                dc.DrawGeometry(null, LinePen, line);

                Point last = points[BucketCount - 1];
                dc.DrawEllipse(GlowBrush, null, last, 7.0, 7.0);
                dc.DrawEllipse(LineBrush, null, last, 3.2, 3.2);
                dc.DrawEllipse(DotCoreBrush, null, last, 1.3, 1.3);
            }

            DrawHover(dc, plot, points, dpi);
        }

        private Rect GetPlotRect()
        {
            return new Rect(34, 10, Math.Max(0, ActualWidth - 42), Math.Max(0, ActualHeight - 34));
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            Rect plot = GetPlotRect();
            Point position = e.GetPosition(this);
            if (!plot.Contains(position))
            {
                SetHoverIndex(-1);
                return;
            }

            double ratio = (position.X - plot.Left) / plot.Width;
            int index = (int)Math.Round(ratio * (BucketCount - 1));
            if (index < 0)
            {
                index = 0;
            }
            if (index >= BucketCount)
            {
                index = BucketCount - 1;
            }

            SetHoverIndex(index);
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            SetHoverIndex(-1);
        }

        private void SetHoverIndex(int index)
        {
            if (_hoverIndex == index)
            {
                return;
            }

            _hoverIndex = index;
            InvalidateVisual();
        }

        private void DrawHover(DrawingContext dc, Rect plot, Point[] points, double dpi)
        {
            int index = _hoverIndex;
            if (index < 0 || index >= BucketCount)
            {
                return;
            }

            double x = points[index].X;
            double bandWidth = plot.Width / (BucketCount - 1.0);
            Rect band = new Rect(x - bandWidth / 2.0, plot.Top, bandWidth, plot.Height);
            if (band.Left < plot.Left)
            {
                band.X = plot.Left;
            }
            if (band.Right > plot.Right)
            {
                band.X = plot.Right - band.Width;
            }
            dc.DrawRectangle(HoverBandBrush, null, band);
            dc.DrawLine(HoverLinePen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            if (_values[index] > 0)
            {
                dc.DrawEllipse(GlowBrush, null, points[index], 6.0, 6.0);
                dc.DrawEllipse(LineBrush, null, points[index], 3.0, 3.0);
                dc.DrawEllipse(DotCoreBrush, null, points[index], 1.2, 1.2);
            }

            DateTime start = _windowStart.AddTicks(_bucketSize.Ticks * index);
            string timeFormat = _bucketSize.TotalSeconds < 60 ? "HH:mm:ss" : "HH:mm";
            string timeText = index == BucketCount - 1
                ? start.ToString(timeFormat, CultureInfo.CurrentCulture) + " – 现在"
                : start.ToString(timeFormat, CultureInfo.CurrentCulture) + " – " +
                  start.AddTicks(_bucketSize.Ticks).ToString(timeFormat, CultureInfo.CurrentCulture);
            string countText = string.Format(
                CultureInfo.CurrentCulture,
                "错误次数  {0:N0}",
                _values[index]);
            string eventText = string.IsNullOrEmpty(_labels[index])
                ? "无 WHEA 事件"
                : _labels[index];

            FormattedText timeLine = MakeText(timeText, 10.0, TipSecondaryBrush, dpi);
            FormattedText countLine = MakeText(countText, 11.5, TipPrimaryBrush, dpi, FontWeights.SemiBold);
            FormattedText eventLine = MakeText(eventText, 10.0, TipSecondaryBrush, dpi);

            double tipWidth = Math.Max(154, Math.Max(timeLine.Width,
                Math.Max(countLine.Width, eventLine.Width)) + 22);
            double tipHeight = timeLine.Height + countLine.Height + eventLine.Height + 20;
            double tipX = x - tipWidth / 2.0;
            if (tipX < 4)
            {
                tipX = 4;
            }
            if (tipX + tipWidth > ActualWidth - 4)
            {
                tipX = ActualWidth - 4 - tipWidth;
            }

            double tipY = points[index].Y - tipHeight - 12;
            if (tipY < 4)
            {
                tipY = points[index].Y + 14;
            }
            if (tipY + tipHeight > ActualHeight - 4)
            {
                tipY = ActualHeight - 4 - tipHeight;
            }
            if (tipY < 4)
            {
                tipY = 4;
            }

            Rect tip = new Rect(tipX, tipY, tipWidth, tipHeight);
            dc.DrawRoundedRectangle(TipBackgroundBrush, TipBorderPen, tip, 6, 6);

            double textX = tip.Left + 11;
            double textY = tip.Top + 9;
            dc.DrawText(timeLine, new Point(textX, textY));
            textY += timeLine.Height + 2;
            dc.DrawText(countLine, new Point(textX, textY));
            textY += countLine.Height + 1;
            dc.DrawText(eventLine, new Point(textX, textY));
        }

        private void DrawYAxis(DrawingContext dc, Rect plot, int axisMax, double dpi)
        {
            int[] bands = new int[] { 0, 2, 4 };
            for (int i = 0; i < bands.Length; i++)
            {
                int band = bands[i];
                int value = axisMax * (4 - band) / 4;
                string label = value.ToString(CultureInfo.CurrentCulture);
                FormattedText text = MakeText(label, 9.5, LabelBrush, dpi);
                double y = plot.Top + plot.Height * band / 4.0 - text.Height / 2.0;
                double x = plot.Left - 8 - text.Width;
                dc.DrawText(text, new Point(x, y));
            }
        }

        private void DrawXAxis(DrawingContext dc, Rect plot, double dpi)
        {
            int[] bands = new int[] { 0, 15, 30, 45, 59 };
            for (int i = 0; i < bands.Length; i++)
            {
                int band = bands[i];
                DateTime time = _windowStart.AddTicks(_bucketSize.Ticks * band);
                string timeFormat = _bucketSize.TotalSeconds < 60 ? "HH:mm:ss" : "HH:mm";
                string label = i == bands.Length - 1
                    ? "现在"
                    : time.ToString(timeFormat, CultureInfo.CurrentCulture);
                FormattedText text = MakeText(label, 9.5, LabelBrush, dpi);
                double x = plot.Left + plot.Width * band / (BucketCount - 1.0);
                double tx = x - text.Width / 2.0;
                if (i == 0)
                {
                    tx = plot.Left;
                }
                else if (i == bands.Length - 1)
                {
                    tx = x - text.Width;
                }

                dc.DrawText(text, new Point(tx, plot.Bottom + 7));
            }
        }

        private void DrawEmptyState(DrawingContext dc, Rect plot, double dpi)
        {
            FormattedText title = MakeText("暂无 WHEA 事件", 13, EmptyTitleBrush, dpi);
            FormattedText subtitle = MakeText("持续监视中，发生硬件错误后将在此显示", 10.5, EmptySubBrush, dpi);
            double centerX = plot.Left + plot.Width / 2.0;
            double centerY = plot.Top + plot.Height / 2.0;
            dc.DrawText(title, new Point(centerX - title.Width / 2.0, centerY - title.Height - 3));
            dc.DrawText(subtitle, new Point(centerX - subtitle.Width / 2.0, centerY + 3));
        }

        private static StreamGeometry BuildGeometry(Point[] points, Rect plot, bool closed)
        {
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                if (closed)
                {
                    context.BeginFigure(new Point(plot.Left, plot.Bottom), true, true);
                    context.LineTo(points[0], true, false);
                }
                else
                {
                    context.BeginFigure(points[0], false, false);
                }

                for (int i = 0; i < points.Length - 1; i++)
                {
                    Point p0 = i == 0 ? points[i] : points[i - 1];
                    Point p1 = points[i];
                    Point p2 = points[i + 1];
                    Point p3 = i + 2 < points.Length ? points[i + 2] : p2;

                    Point c1 = new Point(
                        p1.X + (p2.X - p0.X) / 6.0,
                        ClampY(p1.Y + (p2.Y - p0.Y) / 6.0, plot));
                    Point c2 = new Point(
                        p2.X - (p3.X - p1.X) / 6.0,
                        ClampY(p2.Y - (p3.Y - p1.Y) / 6.0, plot));

                    context.BezierTo(c1, c2, p2, true, false);
                }

                if (closed)
                {
                    context.LineTo(new Point(plot.Right, plot.Bottom), true, false);
                }
            }

            geometry.Freeze();
            return geometry;
        }

        private static double ClampY(double y, Rect plot)
        {
            if (y < plot.Top)
            {
                return plot.Top;
            }
            if (y > plot.Bottom)
            {
                return plot.Bottom;
            }
            return y;
        }

        private FormattedText MakeText(string text, double size, Brush brush, double dpi)
        {
            return MakeText(text, size, brush, dpi, FontWeights.Normal);
        }

        private FormattedText MakeText(string text, double size, Brush brush, double dpi, FontWeight weight)
        {
            return new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(_font, FontStyles.Normal, weight, FontStretches.Normal),
                size,
                brush,
                dpi);
        }

        private static int NiceMax(int value)
        {
            if (value <= 2)
            {
                return 2;
            }
            if (value <= 4)
            {
                return 4;
            }
            if (value <= 8)
            {
                return 8;
            }
            if (value <= 16)
            {
                return 16;
            }
            return ((value + 9) / 10) * 10;
        }

        private static Brush MakeBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen MakePen(Brush brush, double thickness)
        {
            Pen pen = new Pen(brush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            return pen;
        }
    }
}
