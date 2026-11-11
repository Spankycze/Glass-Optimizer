using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SpankyhoOptimizer.ModernSetup;

internal sealed partial class InstallerWindow : Window
{
	private readonly FrameworkElement _ui;

	private readonly TextBox _path;

	private readonly CheckBox _desktop;

	private readonly CheckBox _launch;

	private readonly ProgressBar _progress;

	private readonly TextBlock _status;

	private readonly TextBlock _phase;

	private readonly Button _install;

	private readonly Button _browse;

	private readonly Button _close;
	private readonly Button _minimize;
	private readonly TextBlock _progressPercent;

	private readonly Border _dotNetCard;

	private readonly Border _winAppCard;

	private readonly Border _dotNetDot;

	private readonly Border _winAppDot;

	private readonly TextBlock _dotNetState;

	private readonly TextBlock _winAppState;

	private readonly TextBox _liveLog;

	private readonly Border _stepInstall;

	private readonly Border _stepComponents;

	private readonly Border _stepDone;

	private readonly TextBlock _stepInstallText;

	private readonly TextBlock _stepComponentsText;

	private readonly TextBlock _stepDoneText;

	private readonly StackPanel _finalActions;

	private readonly Button _runButton;

	private readonly Button _openFolderButton;

	private readonly Button _finishCloseButton;

	private readonly TextBlock _footerHint;

	private readonly Button _cancelButton;

	private readonly FrameworkElement _progressArea;

	private readonly FrameworkElement _readySection;

	private readonly Border _efficiencyCard;


	private string? _installedTarget;

	private bool _busy;
	private readonly Queue<string> _liveLogLines = new(40);
	private int _lastProgressValue = -1;
	private string _lastProgressPhase = string.Empty;
	private string _lastProgressStatus = string.Empty;
	private readonly object _pendingProgressLock = new();
	private bool _progressDispatchQueued;
	private long _progressRevision;
	private int _queuedProgressValue;
	private string _queuedProgressPhase = string.Empty;
	private string _queuedProgressStatus = string.Empty;
	private long _queuedProgressRevision;
	private int _queuedProgressAttempt = -1;
	// Count dispatcher posts in isolated UI tests, not incoming file callbacks.
	private int _progressDispatchScheduledCount;

	private static readonly SolidColorBrush StepSelectedBackground = FreezeBrush(Color.FromArgb(78, 58, 142, 188));
	private static readonly SolidColorBrush StepCompletedBackground = FreezeBrush(Color.FromArgb(56, 38, 108, 128));
	private static readonly SolidColorBrush StepIdleBackground = FreezeBrush(Color.FromArgb(32, 42, 76, 98));
	private static readonly SolidColorBrush StepSelectedBorder = FreezeBrush(Color.FromArgb(160, 126, 211, 245));
	private static readonly SolidColorBrush StepCompletedBorder = FreezeBrush(Color.FromArgb(132, 88, 185, 202));
	private static readonly SolidColorBrush StepIdleBorder = FreezeBrush(Color.FromArgb(96, 112, 158, 188));
	private static readonly SolidColorBrush StepSelectedText = FreezeBrush(Color.FromRgb(245, 240, 250));
	private static readonly SolidColorBrush StepCompletedText = FreezeBrush(Color.FromRgb(104, 224, 158));
	private static readonly SolidColorBrush StepIdleText = FreezeBrush(Color.FromRgb(174, 180, 194));
	private static readonly SolidColorBrush DependencyReady = FreezeBrush(Color.FromRgb(65, 221, 151));
	private static readonly SolidColorBrush DependencyWarning = FreezeBrush(Color.FromRgb(255, 185, 84));
	private static readonly SolidColorBrush DependencyWorking = FreezeBrush(Color.FromRgb(126, 211, 255));
	private static readonly SolidColorBrush DependencyReadyBorder = FreezeBrush(Color.FromArgb(150, 74, 154, 130));
	private static readonly SolidColorBrush DependencyWarningBorder = FreezeBrush(Color.FromArgb(150, 170, 120, 68));
	private static readonly SolidColorBrush DependencyWorkingBorder = FreezeBrush(Color.FromArgb(150, 92, 176, 224));

	private static readonly CubicEase IosEase = CreateIosEase();

	private static CubicEase CreateIosEase()
	{
		var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
		if (ease.CanFreeze) ease.Freeze();
		return ease;
	}

	private static SolidColorBrush FreezeBrush(Color color)
	{
		var brush = new SolidColorBrush(color);
		if (brush.CanFreeze) brush.Freeze();
		return brush;
	}

	private static void AnimateDouble(DependencyObject target, DependencyProperty property, double to, int milliseconds)
	{
		var animation = new DoubleAnimation
		{
			To = to,
			Duration = TimeSpan.FromMilliseconds(milliseconds),
			EasingFunction = IosEase,
			FillBehavior = FillBehavior.HoldEnd
		};
		if (target is UIElement ui)
			ui.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
		else if (target is Animatable animatable)
			animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
	}

	private static void AttachIosMotion(FrameworkElement element, double hoverScale = 1.012, double pressedScale = 0.985)
	{
		// Respect Windows "Animation effects" and reduce event handlers/GC
		// allocations entirely when the user chooses reduced motion.
		if (!SystemParameters.ClientAreaAnimation) return;
		if (element.RenderTransform is not ScaleTransform scale)
		{
			scale = new ScaleTransform(1, 1);
			element.RenderTransform = scale;
			element.RenderTransformOrigin = new Point(0.5, 0.5);
		}

		void ScaleTo(double value, int ms)
		{
			AnimateDouble(scale, ScaleTransform.ScaleXProperty, value, ms);
			AnimateDouble(scale, ScaleTransform.ScaleYProperty, value, ms);
		}

		element.MouseEnter += (_, _) => { ScaleTo(hoverScale, 145); AnimateDouble(element, OpacityProperty, 1.0, 120); };
		element.MouseLeave += (_, _) => { ScaleTo(1.0, 170); AnimateDouble(element, OpacityProperty, 0.98, 140); };
		element.PreviewMouseLeftButtonDown += (_, _) => ScaleTo(pressedScale, 90);
		element.PreviewMouseLeftButtonUp += (_, _) => ScaleTo(element.IsMouseOver ? hoverScale : 1.0, 150);
	}

	private void AttachInstallerMotion()
	{
		AttachIosMotion(_close, 1.06, 0.92);
		AttachIosMotion(_minimize, 1.04, 0.94);
		AttachIosMotion(_browse);
		AttachIosMotion(_install, 1.018, 0.975);
		AttachIosMotion(_cancelButton);
		AttachIosMotion(_runButton, 1.018, 0.975);
		AttachIosMotion(_openFolderButton);
		AttachIosMotion(_finishCloseButton);
		AttachIosMotion(_desktop, 1.008, 0.985);
		AttachIosMotion(_launch, 1.008, 0.985);

		// Avoid hover-triggered layout/animation work on large glass panels.
		// Only real interactive controls receive lightweight one-shot motion.
	}

	private void AnimateInstallerIn()
	{
		if (!SystemParameters.ClientAreaAnimation) return;
		// A single lightweight entrance transform avoids repainting multiple
		// translucent panels independently inside the layered WPF window.
		_ui.Opacity = 0.97;
		var translate = new TranslateTransform(0, 4);
		_ui.RenderTransform = translate;
		AnimateDouble(_ui, OpacityProperty, 1.0, 145);
		AnimateDouble(translate, TranslateTransform.YProperty, 0, 175);
	}

	private void Ui(Action action, DispatcherPriority priority = DispatcherPriority.Background)
	{
		if (base.Dispatcher.CheckAccess()) action();
		else _ = base.Dispatcher.BeginInvoke(priority, action);
	}

	// Unit-testable, monotone "fit only" policy with enough reserved space
	// for Windows borders, taskbar and potential remote-desktop work areas.
	internal static double ComputeCompactWindowScale(double availableWidth, double availableHeight)
	{
		if (!double.IsFinite(availableWidth) || !double.IsFinite(availableHeight) ||
			availableWidth <= 0 || availableHeight <= 0) return 1;
		return Math.Clamp(Math.Min((availableWidth - 24) / 712.0, (availableHeight - 24) / 486.0),
			0.50, 1.0);
	}

	internal InstallerWindow()
	{
		base.Title = $"Glass Optimizer V{Program.DisplayVersion} Setup";
		// Keep the 712x486 layout but shrink proportionally if a small laptop
		// display, elevated DPI, or constrained remote desktop cannot fit it.
		// Never enlarge: oversized installer windows were a frequent regression.
		double fit = ComputeCompactWindowScale(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
		base.Width = 712.0 * fit;
		base.Height = 486.0 * fit;
		base.ResizeMode = ResizeMode.NoResize;
		base.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		base.WindowStyle = WindowStyle.None;
		// Retain per-pixel alpha for clean rounded HWND corners; the visible
		// installer content is now dark and readable, not see-through wallpaper.
		base.AllowsTransparency = true;
		base.Background = Brushes.Transparent;
		base.UseLayoutRounding = true;
		base.SnapsToDevicePixels = true;
		// AccentPolicy/DWM acrylic do not reliably compose with a layered WPF
		// window. Do not stack incompatible backdrops over real per-pixel alpha.
		if (!base.AllowsTransparency)
			base.SourceInitialized += (_, _) => TryEnableGlassBackdrop();
		_ui = Program.LoadUi();
		if (fit < 1.0)
			_ui.LayoutTransform = new ScaleTransform(fit, fit);
		base.Content = _ui;
		_path = Find<TextBox>("InstallPath");
		_desktop = Find<CheckBox>("DesktopShortcut");
		_launch = Find<CheckBox>("LaunchAfter");
		_progress = Find<ProgressBar>("InstallProgress");
		_status = Find<TextBlock>("StatusText");
		_phase = Find<TextBlock>("PhaseText");
		_install = Find<Button>("InstallButton");
		_browse = Find<Button>("BrowseButton");
		_close = Find<Button>("CloseButton");
		_minimize = Find<Button>("MinimizeButton");
		_progressPercent = Find<TextBlock>("ProgressPercent");
		_dotNetCard = Find<Border>("DotNetCard");
		_winAppCard = Find<Border>("WinAppCard");
		_dotNetDot = Find<Border>("DotNetDot");
		_winAppDot = Find<Border>("WinAppDot");
		_dotNetState = Find<TextBlock>("DotNetState");
		_winAppState = Find<TextBlock>("WinAppState");
		_liveLog = Find<TextBox>("LiveLog");
		_stepInstall = Find<Border>("StepInstall");
		_stepComponents = Find<Border>("StepComponents");
		_stepDone = Find<Border>("StepDone");
		_stepInstallText = Find<TextBlock>("StepInstallText");
		_stepComponentsText = Find<TextBlock>("StepComponentsText");
		_stepDoneText = Find<TextBlock>("StepDoneText");
		_finalActions = Find<StackPanel>("FinalActions");
		_runButton = Find<Button>("RunButton");
		_openFolderButton = Find<Button>("OpenFolderButton");
		_finishCloseButton = Find<Button>("FinishCloseButton");
		_footerHint = Find<TextBlock>("FooterHint");
		_cancelButton = Find<Button>("CancelButton");
		_progressArea = Find<FrameworkElement>("ProgressArea");
		_readySection = Find<FrameworkElement>("ReadySection");
		_efficiencyCard = Find<Border>("EfficiencyCard");
		_path.Text = Program.DefaultDirectory;
		_desktop.IsChecked = true;
		_launch.IsChecked = true;
		_close.Click += delegate
		{
			if (!_busy)
			{
				Close();
			}
		};
		_minimize.Click += (_, _) => WindowState = WindowState.Minimized;
		_browse.Click += BrowseClick;
		_install.Click += InstallClick;
		_cancelButton.Click += delegate { if (!_busy) Close(); };
		// The progress region is a fixed-size part of the layout in every phase.
		// Never add another row during installation (avoids overflow/scrollbars).
		_progressArea.Visibility = Visibility.Visible;
		AttachInstallerMotion();
		_runButton.Click += delegate
		{
			if (TryLaunchInstalledApp())
				Close();
		};
		_openFolderButton.Click += delegate { OpenInstalledFolder(); };
		_finishCloseButton.Click += delegate { if (!_busy) Close(); };
		AppendLiveLog("Setup V" + Program.DisplayVersion + " připraven.");
		SetStepState(0);
		Find<FrameworkElement>("DragArea").MouseLeftButtonDown += (_, e) =>
		{
			if (e.LeftButton != MouseButtonState.Pressed ||
				!CanDragFromCaption(e.OriginalSource as DependencyObject)) return;
			try { DragMove(); }
			catch (InvalidOperationException) { } // window may be closing mid-drag
		};
		PreviewKeyDown += (_, e) =>
		{
			if (e.Handled || e.Key != Key.Escape || _busy ||
				Find<Grid>("ErrorOverlay").Visibility == Visibility.Visible) return;
			e.Handled = true;
			Close();
		};
		base.Loaded += delegate
		{
			AnimateInstallerIn();
			base.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
			{
				RefreshDependencies();
			}));
		};
		InitializeErrorSurface();
		base.Closing += OnClosing;
	}

	// Bubble events from Close/Minimize were previously dragging the entire
	// frameless window and could swallow button clicks. Only true caption
	// surfaces may start a window drag.
	private static bool CanDragFromCaption(DependencyObject? source)
	{
		for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
		{
			if (current is System.Windows.Controls.Primitives.ButtonBase ||
				current is TextBox) return false;
		}
		return source != null;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Margins
	{
		public int Left;
		public int Right;
		public int Top;
		public int Bottom;
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

	[StructLayout(LayoutKind.Sequential)]
	private struct AccentPolicy
	{
		public int AccentState;
		public int AccentFlags;
		public int GradientColor;
		public int AnimationId;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WindowCompositionAttributeData
	{
		public int Attribute;
		public IntPtr Data;
		public int SizeOfData;
	}

	[DllImport("user32.dll")]
	private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

	private static bool TryEnableAcrylicFallback(IntPtr hwnd)
	{
		const int WCA_ACCENT_POLICY = 19;
		const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
		var policy = new AccentPolicy
		{
			AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
			AccentFlags = 2,
			// AABBGGRR: low-alpha cool blue. The acrylic should reveal the desktop instead of
			// reading as an opaque charcoal sheet when WPF layers over the DWM backdrop.
			// AABBGGRR: electric azure tint at low alpha; never desaturated beige-gray.
			GradientColor = unchecked((int)0x14ED880B),
			AnimationId = 0
		};

		IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
		try
		{
			Marshal.StructureToPtr(policy, ptr, false);
			var data = new WindowCompositionAttributeData
			{
				Attribute = WCA_ACCENT_POLICY,
				Data = ptr,
				SizeOfData = Marshal.SizeOf<AccentPolicy>()
			};
			return SetWindowCompositionAttribute(hwnd, ref data) != 0;
		}
		finally
		{
			Marshal.FreeHGlobal(ptr);
		}
	}

	// Verified on the user's Windows 11 with a real screen capture: DWM
	// transient backdrop returned S_OK but rendered as a flat gray WPF sheet.
	// AccentPolicy acrylic instead showed the wallpaper through the window.
	// Never layer both and never force glass when High Contrast is enabled.
	internal static bool ShouldPreferAcrylic(bool highContrast) => !highContrast;
	internal static bool ShouldUseDwmFallback(bool acrylicApplied, bool highContrast) =>
		!acrylicApplied && !highContrast;

	private void TryEnableGlassBackdrop()
	{
		try
		{
			var helper = new WindowInteropHelper(this);
			IntPtr hwnd = helper.Handle;
			if (hwnd == IntPtr.Zero) return;

			if (HwndSource.FromHwnd(hwnd) is HwndSource source && source.CompositionTarget != null)
				source.CompositionTarget.BackgroundColor = Colors.Transparent;

			var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
			_ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

			const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
			const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
			const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
			int enabled = 1;
			int round = 2;
			_ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
			_ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
			// The actual host screenshot showed AccentPolicy acrylic is visibly
			// translucent, while the successful DWM backdrop looked opaque.
			int noBackdrop = 0;
			_ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref noBackdrop, sizeof(int));
			bool acrylicApplied = false;
			if (ShouldPreferAcrylic(SystemParameters.HighContrast))
			{
				try { acrylicApplied = TryEnableAcrylicFallback(hwnd); }
				catch (Exception) { acrylicApplied = false; }
			}
			if (ShouldUseDwmFallback(acrylicApplied, SystemParameters.HighContrast))
			{
				int transientWindowBackdrop = 3;
				_ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE,
					ref transientWindowBackdrop, sizeof(int));
			}
		}
		catch
		{
			// The translucent XAML fallback stays active on systems without the Windows 11 backdrop API.
		}
	}

	private T Find<T>(string name) where T : FrameworkElement
	{
		return (_ui.FindName(name) as T) ?? throw new InvalidOperationException("Prvek instalátoru nebyl nalezen: " + name);
	}

	private void AppendLiveLog(string text)
	{
		Program.Log(text);
		Ui(() =>
		{
			string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
			if (_liveLogLines.Count == 40) _liveLogLines.Dequeue();
			_liveLogLines.Enqueue(line);
			if (!_liveLog.IsVisible) return;
			_liveLog.Text = string.Join(Environment.NewLine, _liveLogLines);
			_liveLog.CaretIndex = _liveLog.Text.Length;
			_liveLog.ScrollToEnd();
		});
	}

	private void SetStepState(int active)
	{
		Ui(() =>
		{
			Border[] borders = { _stepInstall, _stepComponents, _stepDone };
			TextBlock[] labels = { _stepInstallText, _stepComponentsText, _stepDoneText };
			for (int i = 0; i < borders.Length; i++)
			{
				bool selected = i == active;
				bool completed = i < active;
				borders[i].Background = selected ? StepSelectedBackground : completed ? StepCompletedBackground : StepIdleBackground;
				borders[i].BorderBrush = selected ? StepSelectedBorder : completed ? StepCompletedBorder : StepIdleBorder;
				labels[i].Foreground = selected ? StepSelectedText : completed ? StepCompletedText : StepIdleText;
			}
		});
	}

	private bool TryLaunchInstalledApp()
	{
		try
		{
			if (string.IsNullOrWhiteSpace(_installedTarget))
				throw new InvalidOperationException("Cílová instalace není připravená.");
			string exe = Path.Combine(_installedTarget, "SpankyhoOptimizer.exe");
			if (!File.Exists(exe))
				throw new FileNotFoundException("SpankyhoOptimizer.exe nebyl po instalaci nalezen.", exe);

			Process? process = Process.Start(new ProcessStartInfo(exe)
			{
				WorkingDirectory = _installedTarget,
				UseShellExecute = true
			});
			if (process == null)
                throw new InvalidOperationException("Glass Optimizer se nepodařilo spustit.");

            AppendLiveLog($"Glass Optimizer spuštěn • PID {process.Id}.");
			return true;
		}
		catch (Exception ex)
		{
			Program.Log("LAUNCH ERROR " + ex);
			AppendLiveLog("CHYBA SPUŠTĚNÍ: " + ex.Message);
			_footerHint.Text = "Instalace je hotová • aplikaci se nepodařilo spustit";
			ShowError(ex, _installedTarget ?? string.Empty, false);
			return false;
		}
	}

	private void OpenInstalledFolder()
	{
		if (string.IsNullOrWhiteSpace(_installedTarget) || !Directory.Exists(_installedTarget)) return;
		Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _installedTarget + "\"") { UseShellExecute = true });
	}

	private void OnClosing(object? sender, CancelEventArgs e)
	{
		if (_busy)
		{
			e.Cancel = true;
		}
	}

	private void BrowseClick(object? sender, RoutedEventArgs e)
	{
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
            Title = "Vyber složku pro Glass Optimizer",
			InitialDirectory = (Directory.Exists(_path.Text) ? _path.Text : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
		};
		if (openFolderDialog.ShowDialog(this) == true)
		{
			string folderName = openFolderDialog.FolderName;
			_path.Text = (string.Equals(Path.GetFileName(folderName), "Spankyho Optimizer", StringComparison.OrdinalIgnoreCase) ? folderName : Path.Combine(folderName, "Spankyho Optimizer"));
		}
	}

	private void RefreshDependencies()
	{
		bool dotnet = Program.HasDotNet10(out string? desktopVersion);
		bool webview = InstallerPrerequisites.HasWebView2(out string? webviewVersion);
		SetDependency(_dotNetCard, _dotNetDot, _dotNetState, dotnet,
			dotnet ? ".NET Desktop Runtime 10: " + desktopVersion : ".NET Desktop 10 se doinstaluje");
		SetDependency(_winAppCard, _winAppDot, _winAppState, ready: true,
			"Windows App SDK: součástí aplikace");
		AppendLiveLog(dotnet ? ".NET Desktop 10 připraven: " + desktopVersion :
			".NET Desktop 10 bude doinstalován, pokud chybí.");
		AppendLiveLog(webview ? "WebView2 připraven: " + webviewVersion :
			"Microsoft WebView2 bude doinstalován, pokud chybí.");
		AppendLiveLog("Windows App SDK je součástí instalovaného balíčku.");
	}

	private static void SetDependency(Border card, Border dot, TextBlock text, bool ready, string state)
	{
		SolidColorBrush color = ready ? DependencyReady : DependencyWarning;
		dot.Background = color;
		text.Foreground = color;
		text.Text = state;
		card.BorderBrush = ready ? DependencyReadyBorder : DependencyWarningBorder;
	}

	private void SetProgress(int value, string phase, string status) =>
		SetProgressCore(value, phase, status, attempt: -1);

	// Only reports belonging to the current installation may update UI. The
	// generation is checked again when the coalesced dispatcher work actually
	// executes, so an in-flight worker callback cannot overwrite success/retry.
	private void SetInstallProgress(int value, string phase, string status, int attempt) =>
		SetProgressCore(value, phase, status, attempt);

	private void SetProgressCore(int value, string phase, string status, int attempt)
	{
		if (attempt >= 0 &&
			(attempt != System.Threading.Volatile.Read(ref _attemptGeneration) || _windowClosed))
			return;
		int clamped = Math.Clamp(value, 0, 100);
		long revision = System.Threading.Interlocked.Increment(ref _progressRevision);
		if (Dispatcher.CheckAccess())
		{
			if (attempt < 0 || (_busy && attempt == _attemptGeneration))
				ApplyProgress(clamped, phase, status);
			return;
		}
		lock (_pendingProgressLock)
		{
			if (revision < _queuedProgressRevision) return; // concurrent producer completed out of order
			_queuedProgressValue = clamped;
			_queuedProgressPhase = phase;
			_queuedProgressStatus = status;
			_queuedProgressRevision = revision;
			_queuedProgressAttempt = attempt;
			if (_progressDispatchQueued) return;
			_progressDispatchQueued = true;
		}
		// Thousands of streamed file updates result in only one pending
		// dispatcher callback. Never schedule per-file work on the UI thread.
		System.Threading.Interlocked.Increment(ref _progressDispatchScheduledCount);
		_ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
		{
			int latest, latestAttempt;
			string latestPhase, latestStatus;
			long latestRevision;
			lock (_pendingProgressLock)
			{
				latest = _queuedProgressValue;
				latestPhase = _queuedProgressPhase;
				latestStatus = _queuedProgressStatus;
				latestRevision = _queuedProgressRevision;
				latestAttempt = _queuedProgressAttempt;
				_progressDispatchQueued = false;
			}
			if (!_windowClosed &&
				latestRevision == System.Threading.Interlocked.Read(ref _progressRevision) &&
				(latestAttempt < 0 || (_busy && latestAttempt == _attemptGeneration)))
				ApplyProgress(latest, latestPhase, latestStatus);
		}));
	}

	private void ApplyProgress(int clamped, string phase, string status)
	{
		if (clamped == _lastProgressValue &&
			string.Equals(phase, _lastProgressPhase, StringComparison.Ordinal) &&
			string.Equals(status, _lastProgressStatus, StringComparison.Ordinal)) return;
		_lastProgressValue = clamped;
		_lastProgressPhase = phase;
		_lastProgressStatus = status;
		_progressArea.Visibility = Visibility.Visible;
		bool showProgressStage = clamped > 0 ||
			(!string.Equals(phase, "PŘIPRAVENO", StringComparison.Ordinal) &&
			 !string.Equals(phase, "CHYBA", StringComparison.Ordinal));
		var installCard = Find<FrameworkElement>("InstallCard");
		installCard.Opacity = showProgressStage ? 0.0 : 1.0;
		installCard.IsHitTestVisible = !showProgressStage;
		_readySection.Opacity = showProgressStage ? 0.0 : 1.0;
		_readySection.IsHitTestVisible = !showProgressStage;
		_efficiencyCard.Opacity = showProgressStage ? 1.0 : 0.0;
		_efficiencyCard.IsHitTestVisible = showProgressStage;
		if (_ui.FindName("ProgressLogoBadge") is FrameworkElement progressLogo)
			progressLogo.Opacity = Math.Clamp(0.32 + (0.68 * (clamped / 100.0)), 0.32, 1.0);
		if (showProgressStage && clamped < 100)
		{
			_install.Visibility = Visibility.Collapsed;
			_cancelButton.Visibility = Visibility.Collapsed;
		}
		else if (!showProgressStage)
		{
			_install.Visibility = Visibility.Visible;
			_cancelButton.Visibility = Visibility.Visible;
		}
		// The reported percent is authoritative. Animate only the short visible
		// transition toward that value; never run a fake progress timer or
		// delay the final 100% completion and error/reset states.
		double prior = _progress.Value;
		_progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
		_progress.Value = clamped;
		if (SystemParameters.ClientAreaAnimation && clamped > prior && clamped < 100)
		{
			var transition = new DoubleAnimation
			{
				From = prior,
				To = clamped,
				Duration = TimeSpan.FromMilliseconds(170),
				EasingFunction = IosEase,
				FillBehavior = FillBehavior.Stop
			};
			_progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
				transition, HandoffBehavior.SnapshotAndReplace);
		}
		_progressPercent.Text = clamped.ToString(System.Globalization.CultureInfo.InvariantCulture) + " %";
		_phase.Text = phase;
		_status.Text = status;
	}

	private void SetDependencyWorking(Border card, Border dot, TextBlock text, string state)
	{
		Ui(() =>
		{
			dot.Background = DependencyWorking;
			text.Foreground = DependencyWorking;
			text.Text = state;
			card.BorderBrush = DependencyWorkingBorder;
		});
	}

	private async void InstallClick(object? sender, RoutedEventArgs e) => await RunInstallAsync();

	private async Task EnsurePrerequisites()
	{
		AppendLiveLog("Kontroluji .NET Desktop 10, Microsoft WebView2 a Windows App SDK.");
		await Program.EnsureRuntimePrerequisitesAsync(
			(percentage, message) => SetProgress(percentage, "ZÁVISLOSTI", message),
			(kind, message) =>
			{
				AppendLiveLog(message);
				switch (kind)
				{
					case "dotnet":
						SetDependency(_dotNetCard, _dotNetDot, _dotNetState,
							message.StartsWith(".NET Desktop 10:", StringComparison.Ordinal), message);
						break;
					case "webview2":
						break;
				}
			});
		SetDependency(_winAppCard, _winAppDot, _winAppState, ready: true,
			"Windows App SDK: součástí aplikace");
		SetProgress(51, "SYSTÉM PŘIPRAVEN", "Všechny požadované součásti jsou připravené.");
	}
}
