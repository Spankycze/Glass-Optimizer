using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace SpankyhoOptimizer.ModernSetup;

internal sealed class InstallerWindow : Window
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

	private readonly TextBlock _processorValue;

	private readonly TextBlock _graphicsValue;

	private readonly TextBlock _memoryValue;

	private readonly TextBlock _motherboardValue;

	private readonly TextBlock _secureBootValue;

	private string? _installedTarget;

	private bool _busy;

	internal InstallerWindow()
	{
		base.Title = $"Spankyho Optimizer V{Program.Version} Setup";
		base.Width = 930.0;
		base.Height = 603.0;
		base.ResizeMode = ResizeMode.NoResize;
		base.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		base.WindowStyle = WindowStyle.None;
		base.AllowsTransparency = true;
		base.Background = Brushes.Transparent;
		_ui = Program.LoadUi();
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
		_processorValue = Find<TextBlock>("ProcessorValue");
		_graphicsValue = Find<TextBlock>("GraphicsValue");
		_memoryValue = Find<TextBlock>("MemoryValue");
		_motherboardValue = Find<TextBlock>("MotherboardValue");
		_secureBootValue = Find<TextBlock>("SecureBootValue");
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
		_browse.Click += BrowseClick;
		_install.Click += InstallClick;
		_cancelButton.Click += delegate { if (!_busy) Close(); };
		_progressArea.Visibility = Visibility.Collapsed;
		_runButton.Click += delegate
		{
			if (TryLaunchInstalledApp())
				Close();
		};
		_openFolderButton.Click += delegate { OpenInstalledFolder(); };
		_finishCloseButton.Click += delegate { if (!_busy) Close(); };
		AppendLiveLog("Setup V" + Program.Version + " připraven.");
		SetStepState(0);
		Find<FrameworkElement>("DragArea").MouseLeftButtonDown += delegate
		{
			if (Mouse.LeftButton == MouseButtonState.Pressed)
			{
				DragMove();
			}
		};
		base.Loaded += delegate
		{
			RefreshDependencies();
			SafeLoadSystemSpecifications();
		};
		base.Closing += OnClosing;
	}

	private T Find<T>(string name) where T : FrameworkElement
	{
		return (_ui.FindName(name) as T) ?? throw new InvalidOperationException("Prvek instalátoru nebyl nalezen: " + name);
	}

	private void AppendLiveLog(string text)
	{
		base.Dispatcher.Invoke(delegate
		{
			string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
			var lines = (_liveLog.Text ?? string.Empty).Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
			var merged = lines.Concat(new[] { line }).TakeLast(40);
			_liveLog.Text = string.Join(Environment.NewLine, merged);
			_liveLog.CaretIndex = _liveLog.Text.Length;
			_liveLog.ScrollToEnd();
		});
	}

	private void SetStepState(int active)
	{
		base.Dispatcher.Invoke(delegate
		{
			Border[] borders = { _stepInstall, _stepComponents, _stepDone };
			TextBlock[] labels = { _stepInstallText, _stepComponentsText, _stepDoneText };
			for (int i = 0; i < borders.Length; i++)
			{
				bool selected = i == active;
				bool completed = i < active;
				borders[i].Background = new SolidColorBrush(selected ? Color.FromRgb(31, 111, 235) : completed ? Color.FromRgb(22, 61, 45) : Color.FromRgb(23, 27, 33));
				borders[i].BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(59, 130, 246) : completed ? Color.FromRgb(47, 129, 93) : Color.FromRgb(48, 54, 61));
				labels[i].Foreground = new SolidColorBrush(selected ? Colors.White : completed ? Color.FromRgb(88, 214, 141) : Color.FromRgb(139, 148, 158));
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
				throw new InvalidOperationException("Spankyho Optimizer se nepodařilo spustit.");

			AppendLiveLog($"Spankyho Optimizer spuštěn • PID {process.Id}.");
			return true;
		}
		catch (Exception ex)
		{
			Program.Log("LAUNCH ERROR " + ex);
			AppendLiveLog("CHYBA SPUŠTĚNÍ: " + ex.Message);
			_footerHint.Text = "Instalace je hotová • aplikaci se nepodařilo spustit";
			MessageBox.Show(this, ex.Message, "Spankyho Optimizer Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
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

	private async void SafeLoadSystemSpecifications()
	{
		try
		{
			string[] values = await ReadSystemSpecificationsAsync();
			if (values.Length >= 5)
			{
				_processorValue.Text = values[0];
				_graphicsValue.Text = values[1];
				_memoryValue.Text = values[2];
				_motherboardValue.Text = values[3];
				_secureBootValue.Text = values[4];
			}
		}
		catch
		{
			_processorValue.Text = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown";
			_graphicsValue.Text = "Unknown";
			_memoryValue.Text = "Unknown";
			_motherboardValue.Text = "Unknown";
			_secureBootValue.Text = "Unknown";
		}
	}

	private static async Task<string[]> ReadSystemSpecificationsAsync()
	{
		string ps = "$cpu=((Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name).Trim()); " +
			"$gpu=((Get-CimInstance Win32_VideoController | Where-Object {$_.Name -notmatch 'Remote|Basic'} | Select-Object -First 1 -ExpandProperty Name).Trim()); " +
			"$ram=Get-CimInstance Win32_PhysicalMemory; $gb=[math]::Round((($ram | Measure-Object Capacity -Sum).Sum)/1GB); $mhz=($ram | Measure-Object ConfiguredClockSpeed -Maximum).Maximum; $mem=\"$gb GB @ $mhz MHz\"; " +
			"$bb=Get-CimInstance Win32_BaseBoard | Select-Object -First 1; $board=(($bb.Manufacturer+' '+$bb.Product).Trim()); " +
			"try{$sb=if(Confirm-SecureBootUEFI){'Enabled'}else{'Disabled'}}catch{$sb='Unsupported'}; " +
			"@($cpu,$gpu,$mem,$board,$sb) -join \"`n\"";
		using Process? process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "\\\"") + "\"")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		});
		if (process == null) throw new InvalidOperationException("System information process failed.");
		Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
		Task<string> errorTask = process.StandardError.ReadToEndAsync();
		using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
		try
		{
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (timeout.IsCancellationRequested)
		{
			try { process.Kill(entireProcessTree: true); } catch { }
			throw new TimeoutException("System information query timed out.");
		}
		string output = await outputTask.ConfigureAwait(false);
		string error = await errorTask.ConfigureAwait(false);
		if (process.ExitCode != 0) throw new InvalidOperationException("System information query failed: " + error.Trim());
		return output.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
	}

	private void BrowseClick(object? sender, RoutedEventArgs e)
	{
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
			Title = "Vyber složku pro Spankyho Optimizer",
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
		bool flag = Program.HasDotNet10(out string? version);
		SetDependency(_dotNetCard, _dotNetDot, _dotNetState, flag, flag ? (".NET 10 nalezen: " + version) : ".NET 10 se doinstaluje");
		SetDependency(_winAppCard, _winAppDot, _winAppState, ready: true, "Windows App Runtime 1.8 je součástí aplikace");
		AppendLiveLog(flag ? ".NET 10 připraven: " + version : ".NET 10 bude doinstalován.");
		AppendLiveLog("Windows App Runtime 1.8 je součástí balíku.");
	}

	private static void SetDependency(Border card, Border dot, TextBlock text, bool ready, string state)
	{
		Color color = (ready ? Color.FromRgb(65, 221, 151) : Color.FromRgb(byte.MaxValue, 185, 84));
		dot.Background = new SolidColorBrush(color);
		text.Foreground = new SolidColorBrush(color);
		text.Text = state;
		card.BorderBrush = new SolidColorBrush(ready ? Color.FromRgb(41, 86, 75) : Color.FromRgb(91, 68, 42));
	}

	private void SetProgress(int value, string phase, string status)
	{
		base.Dispatcher.Invoke(delegate
		{
			_progressArea.Visibility = Visibility.Visible;
			_progress.Value = Math.Max(0, Math.Min(100, value));
			_phase.Text = phase;
			_status.Text = status;
		});
	}

	private void SetDependencyWorking(Border card, Border dot, TextBlock text, string state)
	{
		base.Dispatcher.Invoke(delegate
		{
			Color color = Color.FromRgb(146, 113, byte.MaxValue);
			dot.Background = new SolidColorBrush(color);
			text.Foreground = new SolidColorBrush(color);
			text.Text = state;
			card.BorderBrush = new SolidColorBrush(Color.FromRgb(79, 61, 137));
		});
	}

	private async void InstallClick(object? sender, RoutedEventArgs e)
	{
		string target = string.Empty;
		try
		{
			target = Path.GetFullPath(_path.Text.Trim()).TrimEnd(Path.DirectorySeparatorChar);
			if (!string.Equals(Path.GetFileName(target), "Spankyho Optimizer", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Cílová složka musí končit názvem ‚Spankyho Optimizer‘.");
			}
			_busy = true;
			_cancelButton.IsEnabled = false;
			_installedTarget = null;
			_finalActions.Visibility = Visibility.Collapsed;
			_install.Visibility = Visibility.Visible;
			_footerHint.Text = "Probíhá bezpečná transakční instalace…";
			SetStepState(1);
			AppendLiveLog("Instalace spuštěna do: " + target);
			Button install = _install;
			Button browse = _browse;
			bool flag = (_path.IsEnabled = false);
			bool isEnabled = (browse.IsEnabled = flag);
			install.IsEnabled = isEnabled;
			_install.Content = "PRACUJI…";
			SetProgress(3, "PŘÍPRAVA", "Kontroluji systém a potřebné součásti…");
			Program.Log("INSTALL START " + target);
			await EnsurePrerequisites();
			AppendLiveLog("Runtime kontrola dokončena.");
			bool createDesktopShortcut = _desktop.IsChecked == true;
			SetProgress(52, "INSTALACE", "Připravuji transakční staging aplikace…");
			AppendLiveLog("Připravuji staging a rollback bod.");
			await Task.Run(delegate
			{
				Program.WaitForUpdateSourceExit();
				Program.StopRunningOptimizer();
				Program.InstallPayloadTransactional(target, createDesktopShortcut, delegate(int pct, string text)
				{
					SetProgress(pct, "INSTALACE", text);
					AppendLiveLog(text);
				});
			});
			SetProgress(93, "DOKONČENÍ", "Ověřuji novou instalaci a cleanup backupu…");
			AppendLiveLog("Ověřuji finální instalaci.");
			SetProgress(100, "HOTOVO", $"Spankyho Optimizer V{Program.Version} je připraven.");
			Program.Log("INSTALL OK");
			_installedTarget = target;
			SetStepState(2);
			AppendLiveLog("Instalace úspěšně dokončena.");
			_install.Visibility = Visibility.Collapsed;
			_finalActions.Visibility = Visibility.Visible;
			_footerHint.Text = "Instalace dokončena • vyber další akci";
			_busy = false;
			_close.IsEnabled = true;
			if (_launch.IsChecked == true)
			{
				AppendLiveLog("Spouštím Spankyho Optimizer.");
				_footerHint.Text = "Hotovo • spouštím aplikaci";
				if (TryLaunchInstalledApp())
				{
					Close();
					return;
				}
			}
			else
			{
				_footerHint.Text = "Hotovo • instalace dokončena";
			}
		}
		catch (Exception ex)
		{
			Program.Log("INSTALL ERROR " + ex);
			AppendLiveLog("CHYBA: " + ex.Message);
			SetStepState(0);
			_finalActions.Visibility = Visibility.Collapsed;
			_install.Visibility = Visibility.Visible;
			_footerHint.Text = "Instalace selhala • můžeš ji zkusit znovu";
			SetProgress(0, "CHYBA", "Instalace se nezdařila. Zkontroluj log instalátoru a zkus to znovu.");
			MessageBox.Show(this, ex.Message, "Spankyho Optimizer Setup", MessageBoxButton.OK, MessageBoxImage.Hand);
			_busy = false;
			_cancelButton.IsEnabled = true;
			_install.Content = "ZKUSIT ZNOVU";
			Button install2 = _install;
			Button browse2 = _browse;
			bool flag = (_path.IsEnabled = true);
			bool isEnabled = (browse2.IsEnabled = flag);
			install2.IsEnabled = isEnabled;
			Environment.ExitCode = 1;
		}
	}

	private async Task EnsurePrerequisites()
	{
		AppendLiveLog("Kontroluji systémové komponenty.");
		if (!Program.HasDotNet10(out string? version))
		{
			string file = Path.Combine(Path.GetTempPath(), "spanky-dotnet10-x64.exe");
			AppendLiveLog(".NET 10 chybí — stahuji ověřený runtime.");
			SetDependencyWorking(_dotNetCard, _dotNetDot, _dotNetState, "Stahuji .NET 10…");
			await Program.DownloadAsync(Program.DotNetUrl, file, delegate(int p)
			{
				SetProgress(5 + p / 5, "ZÁVISLOSTI", "Stahuji .NET 10…");
			});
			Program.VerifySha512(file, Program.DotNetSha512);
			SetDependencyWorking(_dotNetCard, _dotNetDot, _dotNetState, "Instaluji .NET 10…");
			AppendLiveLog("Instaluji .NET 10 runtime.");
			await Program.RunPackageAsync(file, "/install /quiet /norestart");
			try
			{
				File.Delete(file);
			}
			catch
			{
			}
			if (!Program.HasDotNet10(out version))
			{
				throw new InvalidOperationException(".NET 10 se nepodařilo nainstalovat.");
			}
		}
		SetDependency(_dotNetCard, _dotNetDot, _dotNetState, ready: true, ".NET 10: " + version);
		SetDependency(_winAppCard, _winAppDot, _winAppState, ready: true, "Windows App Runtime 1.8 je součástí aplikace");
		AppendLiveLog("Systémové komponenty jsou připravené.");
		SetProgress(51, "SYSTÉM PŘIPRAVEN", "Potřebné runtime součásti jsou připravené.");
	}
}
