using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

[assembly: AssemblyTitle("WHEA 监视器")]
[assembly: AssemblyDescription("WHEA 硬件错误实时监视器")]
[assembly: AssemblyProduct("WHEA 监视器")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace WheaMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow window = new MainWindow();
            app.Run(window);
        }
    }

    internal sealed class WheaEventInfo
    {
        public long RecordId { get; set; }
        public DateTime Time { get; set; }
        public int EventId { get; set; }
        public int Level { get; set; }
    }

    internal enum MonitorState
    {
        Loading,
        Live,
        Error
    }

    internal enum ChartRange
    {
        FiveMinutes,
        OneHour,
        SixHours
    }

    public sealed class MainWindow : Window
    {
        private const string ProviderName = "Microsoft-Windows-WHEA-Logger";
        private const int BucketCount = 60;

        private readonly ConcurrentQueue<WheaEventInfo> _incoming = new ConcurrentQueue<WheaEventInfo>();
        private readonly List<WheaEventInfo> _events = new List<WheaEventInfo>();
        private readonly HashSet<long> _seen = new HashSet<long>();

        private EventLogWatcher _watcher;
        private DispatcherTimer _timer;
        private DispatcherTimer _tickerTimer;
        private WheaChart _chart;

        private Grid _chartHost;
        private Border _livePill;
        private Ellipse _liveDot;
        private TextBlock _liveText;
        private TextBlock _chartSubtitle;
        private TextBlock _chartCurrentText;
        private TextBlock _totalRangeLabel;
        private TextBlock _perMinuteValue;
        private TextBlock _totalValue;
        private TextBlock _correctedValue;
        private TextBlock _severeValue;
        private TextBlock _lastTimeValue;
        private TextBlock _lastEventValue;
        private Ellipse _footerStatusDot;
        private TextBlock _footerStatus;
        private TextBlock _footerLast;
        private ScrollViewer _detailScroller;
        private TextBlock _detailTickerText;
        private Border _detailLevelPill;
        private TextBlock _detailLevelText;
        private RadioButton _range5Button;
        private RadioButton _range60Button;
        private RadioButton _range6HButton;
        private Button _minimizeButton;
        private Button _maximizeButton;
        private Button _closeButton;

        private readonly object _detailLock = new object();
        private DateTime _detailTime = DateTime.MinValue;
        private long _detailRecordId;
        private int _detailLevel;
        private string _detailText = string.Empty;
        private string _detailError = string.Empty;
        private volatile bool _detailLoaded;
        private string _shownDetailSignature = string.Empty;
        private string _tickerText = string.Empty;
        private double _tickerOffset;
        private DateTime _tickerHoldUntil = DateTime.MinValue;

        private volatile bool _historyLoaded;
        private volatile bool _closing;
        private bool _watcherRunning;
        private string _watcherError = string.Empty;
        private string _historyError = string.Empty;
        private MonitorState _monitorState = MonitorState.Loading;
        private ChartRange _chartRange = ChartRange.OneHour;
        private HwndSource _hwndSource;

        public MainWindow()
        {
            Title = "WHEA 监视器";
            Width = 1040;
            Height = 720;
            MinWidth = 900;
            MinHeight = 640;

            // Keep the bottom ticker on screen on smaller or high-DPI displays.
            double workHeight = SystemParameters.WorkArea.Height;
            Height = Math.Min(Height, Math.Max(480, workHeight - 16));
            MinHeight = Math.Min(MinHeight, Height);

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            LoadInterface();
            ConfigureWindowChrome();
            WireEvents();

            SourceInitialized += delegate
            {
                ApplyWindows11Frame();
                IntPtr handle = new WindowInteropHelper(this).Handle;
                _hwndSource = HwndSource.FromHwnd(handle);
                if (_hwndSource != null)
                {
                    _hwndSource.AddHook(WindowProc);
                }
            };
            Closed += delegate
            {
                if (_hwndSource != null)
                {
                    _hwndSource.RemoveHook(WindowProc);
                    _hwndSource = null;
                }
            };
            Loaded += delegate { StartMonitoring(); };
            Closing += delegate { StopMonitoring(); };
        }

        private void LoadInterface()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream("MainWindow.xaml"))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("MainWindow.xaml resource is missing.");
                }

                Content = (UIElement)XamlReader.Load(stream);
            }

            FrameworkElement root = (FrameworkElement)Content;
            _chartHost = Find<Grid>(root, "ChartHost");
            _livePill = Find<Border>(root, "LivePill");
            _liveDot = Find<Ellipse>(root, "LiveDot");
            _liveText = Find<TextBlock>(root, "LiveText");
            _chartSubtitle = Find<TextBlock>(root, "ChartSubtitle");
            _chartCurrentText = Find<TextBlock>(root, "ChartCurrentText");
            _totalRangeLabel = Find<TextBlock>(root, "TotalRangeLabel");
            _perMinuteValue = Find<TextBlock>(root, "PerMinuteValue");
            _totalValue = Find<TextBlock>(root, "TotalValue");
            _correctedValue = Find<TextBlock>(root, "CorrectedValue");
            _severeValue = Find<TextBlock>(root, "SevereValue");
            _lastTimeValue = Find<TextBlock>(root, "LastTimeValue");
            _lastEventValue = Find<TextBlock>(root, "LastEventValue");
            _footerStatusDot = Find<Ellipse>(root, "FooterStatusDot");
            _footerStatus = Find<TextBlock>(root, "FooterStatus");
            _footerLast = Find<TextBlock>(root, "FooterLast");
            _detailScroller = Find<ScrollViewer>(root, "DetailScroller");
            _detailTickerText = Find<TextBlock>(root, "DetailTickerText");
            _detailLevelPill = Find<Border>(root, "DetailLevelPill");
            _detailLevelText = Find<TextBlock>(root, "DetailLevelText");
            _range5Button = Find<RadioButton>(root, "Range5Button");
            _range60Button = Find<RadioButton>(root, "Range60Button");
            _range6HButton = Find<RadioButton>(root, "Range6HButton");
            _minimizeButton = Find<Button>(root, "MinimizeButton");
            _maximizeButton = Find<Button>(root, "MaximizeButton");
            _closeButton = Find<Button>(root, "CloseButton");

            _chart = new WheaChart
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _chartHost.Children.Add(_chart);
        }

        private static T Find<T>(FrameworkElement root, string name) where T : class
        {
            T element = root.FindName(name) as T;
            if (element == null)
            {
                throw new InvalidOperationException("UI element not found: " + name);
            }
            return element;
        }

        private void ConfigureWindowChrome()
        {
            WindowChrome chrome = new WindowChrome
            {
                CaptionHeight = 48,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, chrome);

            WindowChrome.SetIsHitTestVisibleInChrome(_minimizeButton, true);
            WindowChrome.SetIsHitTestVisibleInChrome(_maximizeButton, true);
            WindowChrome.SetIsHitTestVisibleInChrome(_closeButton, true);
        }

        private void WireEvents()
        {
            _minimizeButton.Click += delegate { WindowState = WindowState.Minimized; };
            _maximizeButton.Click += delegate
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            };
            _closeButton.Click += delegate { Close(); };

            _range5Button.Checked +=
                delegate { SetChartRange(ChartRange.FiveMinutes); };
            _range60Button.Checked +=
                delegate { SetChartRange(ChartRange.OneHour); };
            _range6HButton.Checked +=
                delegate { SetChartRange(ChartRange.SixHours); };

            StateChanged += delegate
            {
                _maximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
                _maximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";

                if (_tickerTimer != null)
                {
                    if (WindowState == WindowState.Minimized)
                    {
                        _tickerTimer.Stop();
                    }
                    else if (!string.IsNullOrEmpty(_tickerText))
                    {
                        _tickerTimer.Start();
                    }
                }
            };

            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTimerTick;

            _tickerTimer = new DispatcherTimer(DispatcherPriority.Background);
            _tickerTimer.Interval = TimeSpan.FromMilliseconds(50);
            _tickerTimer.Tick += OnTickerTick;
            _detailScroller.SizeChanged += delegate
            {
                if (_tickerTimer != null &&
                    !string.IsNullOrEmpty(_tickerText) &&
                    WindowState != WindowState.Minimized)
                {
                    _tickerTimer.Start();
                }
            };
        }

        private void ApplyWindows11Frame()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int enabled = 1;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }

            int rounded = 2;
            DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));

            int borderColor = 0x00343434;
            DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));
        }

        private void StartMonitoring()
        {
            _timer.Start();
            SetMonitorState(MonitorState.Loading, "正在读取系统事件日志…");

            try
            {
                EventLogQuery query = new EventLogQuery(
                    "System",
                    PathType.LogName,
                    "*[System[Provider[@Name='" + ProviderName + "']]]");

                _watcher = new EventLogWatcher(query);
                _watcher.EventRecordWritten += OnWheaEventWritten;
                _watcher.Enabled = true;
                _watcherRunning = true;
            }
            catch (Exception ex)
            {
                _watcherRunning = false;
                _watcherError = ex.Message;
            }

            ThreadPool.QueueUserWorkItem(delegate { LoadHistory(); });
            ThreadPool.QueueUserWorkItem(delegate { LoadLatestDetail(); });
            UpdateMonitorState();
        }

        private void StopMonitoring()
        {
            _closing = true;

            if (_timer != null)
            {
                _timer.Stop();
            }
            if (_tickerTimer != null)
            {
                _tickerTimer.Stop();
            }

            if (_watcher != null)
            {
                try
                {
                    _watcher.Enabled = false;
                    _watcher.EventRecordWritten -= OnWheaEventWritten;
                    _watcher.Dispose();
                }
                catch
                {
                }
                _watcher = null;
            }
        }

        private void LoadHistory()
        {
            try
            {
                DateTime windowStart = GetWindowStart(DateTime.Now, TimeSpan.FromMinutes(6));
                string iso = windowStart.ToUniversalTime().ToString(
                    "yyyy-MM-ddTHH:mm:ss.fffffffZ",
                    CultureInfo.InvariantCulture);

                string xpath = "*[System[Provider[@Name='" + ProviderName +
                    "'] and TimeCreated[@SystemTime>='" + iso + "']]]";

                EventLogQuery query = new EventLogQuery("System", PathType.LogName, xpath);
                using (EventLogReader reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null)
                    {
                        try
                        {
                            WheaEventInfo info = ToEventInfo(record);
                            if (info != null)
                            {
                                _incoming.Enqueue(info);
                            }
                        }
                        finally
                        {
                            record.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _historyError = ex.Message;
            }
            finally
            {
                _historyLoaded = true;
                if (!_closing)
                {
                    Dispatcher.BeginInvoke(new Action(UpdateMonitorState));
                }
            }
        }

        private void LoadLatestDetail()
        {
            try
            {
                EventLogQuery query = new EventLogQuery(
                    "System",
                    PathType.LogName,
                    "*[System[Provider[@Name='" + ProviderName + "']]]");
                query.ReverseDirection = true;

                using (EventLogReader reader = new EventLogReader(query))
                {
                    EventRecord record = reader.ReadEvent();
                    if (record != null)
                    {
                        try
                        {
                            WheaEventInfo info = ToEventInfo(record);
                            if (info != null)
                            {
                                TryUpdateDetail(record, info);
                            }
                        }
                        finally
                        {
                            record.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_detailLock)
                {
                    _detailError = ex.Message;
                }
            }
            finally
            {
                _detailLoaded = true;
                if (!_closing)
                {
                    Dispatcher.BeginInvoke(new Action(RefreshDetailTicker));
                }
            }
        }

        private void TryUpdateDetail(EventRecord record, WheaEventInfo info)
        {
            if (record == null || info == null)
            {
                return;
            }

            lock (_detailLock)
            {
                if (IsDetailOlderLocked(info))
                {
                    return;
                }
            }

            string detail;
            try
            {
                detail = record.FormatDescription();
            }
            catch
            {
                detail = string.Empty;
            }

            detail = NormalizeDetail(detail);
            if (string.IsNullOrEmpty(detail))
            {
                detail = "该事件未提供可显示的详细信息。";
            }

            lock (_detailLock)
            {
                if (IsDetailOlderLocked(info))
                {
                    return;
                }

                _detailTime = info.Time;
                _detailRecordId = info.RecordId;
                _detailLevel = info.Level;
                _detailText = detail;
                _detailError = string.Empty;
            }
        }

        private bool IsDetailOlderLocked(WheaEventInfo info)
        {
            if (info.Time < _detailTime)
            {
                return true;
            }
            if (info.Time == _detailTime && info.RecordId <= _detailRecordId)
            {
                return true;
            }
            return false;
        }

        private static string NormalizeDetail(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Replace('\t', ' ').Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (result.Length > 0)
                {
                    result.Append("  ·  ");
                }
                result.Append(line);
            }

            return result.ToString();
        }

        private void OnWheaEventWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (_closing || e.EventRecord == null)
            {
                return;
            }

            EventRecord record = e.EventRecord;
            try
            {
                WheaEventInfo info = ToEventInfo(record);
                if (info != null)
                {
                    _incoming.Enqueue(info);
                    TryUpdateDetail(record, info);
                }
            }
            catch
            {
            }
            finally
            {
                record.Dispose();
            }
        }

        private static WheaEventInfo ToEventInfo(EventRecord record)
        {
            if (record == null)
            {
                return null;
            }

            return new WheaEventInfo
            {
                RecordId = record.RecordId.HasValue ? record.RecordId.Value : 0L,
                Time = record.TimeCreated.HasValue ? record.TimeCreated.Value : DateTime.Now,
                EventId = record.Id,
                Level = record.Level.HasValue ? (int)record.Level.Value : 0
            };
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_closing)
            {
                return;
            }

            DateTime now = DateTime.Now;
            TimeSpan bucketSize = GetBucketSize(_chartRange);
            DateTime windowStart = GetWindowStart(now, bucketSize);
            DateTime retentionStart = GetWindowStart(now, TimeSpan.FromMinutes(6));

            WheaEventInfo incoming;
            while (_incoming.TryDequeue(out incoming))
            {
                if (incoming.RecordId != 0 && !_seen.Add(incoming.RecordId))
                {
                    continue;
                }
                if (incoming.Time < retentionStart || incoming.Time > now.AddMinutes(2))
                {
                    if (incoming.RecordId != 0)
                    {
                        _seen.Remove(incoming.RecordId);
                    }
                    continue;
                }
                _events.Add(incoming);
            }

            for (int i = _events.Count - 1; i >= 0; i--)
            {
                WheaEventInfo item = _events[i];
                if (item.Time < retentionStart || item.Time > now.AddMinutes(2))
                {
                    _events.RemoveAt(i);
                    if (item.RecordId != 0)
                    {
                        _seen.Remove(item.RecordId);
                    }
                }
            }

            if (WindowState == WindowState.Minimized)
            {
                return;
            }

            int[] buckets = new int[BucketCount];
            WheaEventInfo[] bucketLatest = new WheaEventInfo[BucketCount];
            int total = 0;
            int lastMinute = 0;
            int corrected = 0;
            int severe = 0;
            WheaEventInfo latest = null;
            DateTime oneMinuteAgo = now.AddMinutes(-1);
            long bucketTicks = bucketSize.Ticks;

            for (int i = 0; i < _events.Count; i++)
            {
                WheaEventInfo item = _events[i];
                if (item.Time < windowStart || item.Time > now.AddMinutes(2))
                {
                    continue;
                }

                total++;

                if (item.Time >= oneMinuteAgo)
                {
                    lastMinute++;
                }
                if (item.Level == 3)
                {
                    corrected++;
                }
                else if (item.Level >= 1 && item.Level <= 2)
                {
                    severe++;
                }
                if (latest == null || item.Time > latest.Time)
                {
                    latest = item;
                }

                long offsetTicks = item.Time.Ticks - windowStart.Ticks;
                int bucket = (int)(offsetTicks / bucketTicks);
                if (bucket >= 0 && bucket < BucketCount)
                {
                    buckets[bucket]++;
                    if (bucketLatest[bucket] == null || item.Time > bucketLatest[bucket].Time)
                    {
                        bucketLatest[bucket] = item;
                    }
                }
            }

            string[] bucketLabels = new string[BucketCount];
            for (int i = 0; i < BucketCount; i++)
            {
                WheaEventInfo item = bucketLatest[i];
                if (item != null)
                {
                    bucketLabels[i] = string.Format(
                        CultureInfo.CurrentCulture,
                        "ID {0} · {1} · {2}",
                        item.EventId,
                        GetLevelText(item.Level),
                        item.Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture));
                }
            }

            _chart.SetData(buckets, windowStart, bucketLabels, bucketSize);
            _chartCurrentText.Text = string.Format(
                CultureInfo.CurrentCulture,
                "{0} {1} 次",
                GetCurrentBucketPrefix(_chartRange),
                buckets[BucketCount - 1]);

            _perMinuteValue.Text = lastMinute.ToString("N0", CultureInfo.CurrentCulture);
            _totalValue.Text = total.ToString("N0", CultureInfo.CurrentCulture) + " 次";
            _correctedValue.Text = corrected.ToString("N0", CultureInfo.CurrentCulture);
            _severeValue.Text = severe.ToString("N0", CultureInfo.CurrentCulture);

            if (latest != null)
            {
                string timeText = latest.Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
                string levelText = GetLevelText(latest.Level);
                _lastTimeValue.Text = timeText;
                _lastEventValue.Text = string.Format("ID {0} · {1}", latest.EventId, levelText);
                _footerLast.Text = string.Format("ID {0} · {1} · {2}", latest.EventId, levelText, timeText);
            }
            else
            {
                _lastTimeValue.Text = "—";
                _lastEventValue.Text = "—";
                _footerLast.Text = "—";
            }

            RefreshDetailTicker();

            if (_monitorState == MonitorState.Loading && _historyLoaded)
            {
                UpdateMonitorState();
            }
        }

        private void SetChartRange(ChartRange range)
        {
            if (_chartRange == range)
            {
                return;
            }

            _chartRange = range;
            _chartSubtitle.Text = GetRangeSubtitle(range);
            _totalRangeLabel.Text = GetRangeTotalLabel(range);
            OnTimerTick(null, EventArgs.Empty);
        }

        private void RefreshDetailTicker()
        {
            DateTime time;
            long recordId;
            int level;
            string detail;
            string error;
            bool loaded;

            lock (_detailLock)
            {
                time = _detailTime;
                recordId = _detailRecordId;
                level = _detailLevel;
                detail = _detailText;
                error = _detailError;
                loaded = _detailLoaded;
            }

            if (!loaded)
            {
                return;
            }

            if (time == DateTime.MinValue)
            {
                string emptySignature = string.IsNullOrEmpty(error) ? "empty" : "error";
                if (_shownDetailSignature != emptySignature)
                {
                    _shownDetailSignature = emptySignature;
                    SetDetailTicker(
                        string.IsNullOrEmpty(error)
                            ? "暂无 WHEA 错误事件"
                            : "无法读取最新 WHEA 详细信息",
                        0);
                }
                return;
            }

            string signature = time.Ticks.ToString(CultureInfo.InvariantCulture) +
                "|" + recordId.ToString(CultureInfo.InvariantCulture);
            if (signature == _shownDetailSignature)
            {
                return;
            }
            _shownDetailSignature = signature;

            string ticker = detail + "          ";
            SetDetailTicker(ticker, level);
        }

        private void SetDetailTicker(string text, int level)
        {
            _tickerText = text;
            _detailTickerText.Text = text;
            _detailTickerText.ToolTip = text.Trim();
            _detailScroller.ScrollToHorizontalOffset(0);
            _tickerOffset = 0;
            _tickerHoldUntil = DateTime.Now.AddSeconds(2.2);
            ApplyDetailLevel(level);

            if (_tickerTimer != null && WindowState != WindowState.Minimized)
            {
                _tickerTimer.Start();
            }
        }

        private void ApplyDetailLevel(int level)
        {
            string label = level == 0 ? "—" : GetLevelText(level);
            Color background;
            Color border;
            Color foreground;

            if (level == 1 || level == 2)
            {
                background = Color.FromRgb(0x3A, 0x20, 0x23);
                border = Color.FromRgb(0x5F, 0x30, 0x35);
                foreground = Color.FromRgb(0xFF, 0xC2, 0xC8);
            }
            else if (level == 3)
            {
                background = Color.FromRgb(0x33, 0x2B, 0x14);
                border = Color.FromRgb(0x5C, 0x4A, 0x1D);
                foreground = Color.FromRgb(0xF4, 0xD7, 0x7C);
            }
            else if (level == 4)
            {
                background = Color.FromRgb(0x18, 0x2C, 0x3A);
                border = Color.FromRgb(0x2A, 0x4E, 0x66);
                foreground = Color.FromRgb(0xA9, 0xDD, 0xFF);
            }
            else
            {
                background = Color.FromRgb(0x2F, 0x2F, 0x2F);
                border = Color.FromRgb(0x44, 0x44, 0x44);
                foreground = Color.FromRgb(0xB8, 0xB8, 0xB8);
            }

            _detailLevelPill.Background = new SolidColorBrush(background);
            _detailLevelPill.BorderBrush = new SolidColorBrush(border);
            _detailLevelText.Foreground = new SolidColorBrush(foreground);
            _detailLevelText.Text = label;
        }

        private void OnTickerTick(object sender, EventArgs e)
        {
            if (_closing ||
                WindowState == WindowState.Minimized ||
                string.IsNullOrEmpty(_tickerText))
            {
                _tickerTimer.Stop();
                return;
            }

            double scrollable = _detailScroller.ScrollableWidth;
            if (scrollable <= 1)
            {
                _tickerTimer.Stop();
                return;
            }

            DateTime now = DateTime.Now;
            if (now < _tickerHoldUntil)
            {
                return;
            }

            _tickerOffset += 2.2;
            if (_tickerOffset >= scrollable)
            {
                _tickerOffset = 0;
                _tickerHoldUntil = now.AddSeconds(2.0);
                _detailScroller.ScrollToHorizontalOffset(0);
                return;
            }

            _detailScroller.ScrollToHorizontalOffset(_tickerOffset);
        }

        private void UpdateMonitorState()
        {
            if (!_watcherRunning)
            {
                string detail = string.IsNullOrEmpty(_watcherError)
                    ? "无法订阅 WHEA 事件"
                    : _watcherError;
                SetMonitorState(MonitorState.Error, detail);
                return;
            }

            if (!_historyLoaded)
            {
                SetMonitorState(MonitorState.Loading, "正在读取系统事件日志…");
                return;
            }

            string liveDetail = "正在实时监视系统事件日志中的 WHEA-Logger 事件。";
            if (!string.IsNullOrEmpty(_historyError))
            {
                liveDetail += "\n历史记录读取失败：" + _historyError;
            }
            SetMonitorState(MonitorState.Live, liveDetail);
        }

        private void SetMonitorState(MonitorState state, string detail)
        {
            _monitorState = state;

            Color color;
            Color pillBackground;
            Color pillBorder;
            Color pillText;
            string liveText;
            string footerText;

            if (state == MonitorState.Live)
            {
                color = Color.FromRgb(0x6C, 0xCB, 0x5F);
                pillBackground = Color.FromRgb(0x1B, 0x33, 0x27);
                pillBorder = Color.FromRgb(0x2E, 0x5B, 0x40);
                pillText = Color.FromRgb(0xBC, 0xE9, 0xB7);
                liveText = "正在监视";
                footerText = "正在监视";
            }
            else if (state == MonitorState.Loading)
            {
                color = Color.FromRgb(0xFF, 0xB9, 0x00);
                pillBackground = Color.FromRgb(0x33, 0x2B, 0x14);
                pillBorder = Color.FromRgb(0x5C, 0x4A, 0x1D);
                pillText = Color.FromRgb(0xF4, 0xD7, 0x7C);
                liveText = "正在读取";
                footerText = "正在读取";
            }
            else
            {
                color = Color.FromRgb(0xFF, 0x99, 0xA4);
                pillBackground = Color.FromRgb(0x3A, 0x20, 0x23);
                pillBorder = Color.FromRgb(0x5F, 0x30, 0x35);
                pillText = Color.FromRgb(0xFF, 0xC2, 0xC8);
                liveText = "监视不可用";
                footerText = "监视不可用";
            }

            SolidColorBrush dotBrush = new SolidColorBrush(color);
            _liveDot.Fill = dotBrush;
            _footerStatusDot.Fill = dotBrush;
            _livePill.Background = new SolidColorBrush(pillBackground);
            _livePill.BorderBrush = new SolidColorBrush(pillBorder);
            _liveText.Foreground = new SolidColorBrush(pillText);
            _liveText.Text = liveText;
            _footerStatus.Text = footerText;

            _livePill.ToolTip = detail;
            _footerStatus.ToolTip = detail;
            _footerStatusDot.ToolTip = detail;
        }

        private static string GetLevelText(int level)
        {
            if (level == 1)
            {
                return "严重";
            }
            if (level == 2)
            {
                return "错误";
            }
            if (level == 3)
            {
                return "警告";
            }
            if (level == 4)
            {
                return "信息";
            }
            return "事件";
        }

        private static TimeSpan GetBucketSize(ChartRange range)
        {
            if (range == ChartRange.FiveMinutes)
            {
                return TimeSpan.FromSeconds(5);
            }
            if (range == ChartRange.SixHours)
            {
                return TimeSpan.FromMinutes(6);
            }
            return TimeSpan.FromMinutes(1);
        }

        private static string GetRangeSubtitle(ChartRange range)
        {
            if (range == ChartRange.FiveMinutes)
            {
                return "最近 5 分钟 · 每 5 秒事件数 · 悬停查看";
            }
            if (range == ChartRange.SixHours)
            {
                return "最近 6 小时 · 每 6 分钟事件数 · 悬停查看";
            }
            return "最近 60 分钟 · 每分钟事件数 · 悬停查看";
        }

        private static string GetRangeTotalLabel(ChartRange range)
        {
            if (range == ChartRange.FiveMinutes)
            {
                return "最近 5 分钟";
            }
            if (range == ChartRange.SixHours)
            {
                return "最近 6 小时";
            }
            return "最近 60 分钟";
        }

        private static string GetCurrentBucketPrefix(ChartRange range)
        {
            if (range == ChartRange.FiveMinutes)
            {
                return "本 5 秒";
            }
            if (range == ChartRange.SixHours)
            {
                return "本 6 分钟";
            }
            return "本分钟";
        }

        private static DateTime GetWindowStart(DateTime now, TimeSpan bucketSize)
        {
            long bucketTicks = bucketSize.Ticks;
            long startTicks = now.Ticks - (now.Ticks % bucketTicks);
            return new DateTime(startTicks, now.Kind)
                .AddTicks(-(BucketCount - 1) * bucketTicks);
        }

        private IntPtr WindowProc(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message == 0x0024) // WM_GETMINMAXINFO
            {
                IntPtr monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
                if (monitor != IntPtr.Zero)
                {
                    MonitorInfo info = new MonitorInfo();
                    info.Size = Marshal.SizeOf(typeof(MonitorInfo));
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        MinMaxInfo limits = (MinMaxInfo)Marshal.PtrToStructure(
                            lParam, typeof(MinMaxInfo));

                        limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
                        limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
                        limits.MaxSize.X = info.Work.Right - info.Work.Left;
                        limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;

                        Marshal.StructureToPtr(limits, lParam, true);
                    }
                }
            }

            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attribute,
            ref int attributeValue,
            int attributeSize);
    }
}
