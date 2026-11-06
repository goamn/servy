using Servy.Core.Helpers;
using Servy.Core.Logging;
using Servy.Manager.Config;
using Servy.Manager.Models;
using Servy.Manager.Resources;
using Servy.Manager.Services;
using Servy.Manager.Utils;
using Servy.Manager.ViewModels;
using Servy.UI.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows.Automation;
using System.Windows;
using Wpf.Ui.Appearance;
using Button = Wpf.Ui.Controls.Button;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using MenuItem = Wpf.Ui.Controls.MenuItem;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Servy.Manager.Views
{
    /// <summary>
    /// Interaction logic for <see cref="MainWindow"/>.
    /// Represents the main window of the Servy Manager application.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public partial class MainWindow : FluentWindow
    {
        /// <summary>
        /// Specifies the active tab scope used to coordinate background activity teardown during tab transitions.
        /// </summary>
        private enum TabScope
        {
            /// <summary>The primary dashboard service management tab.</summary>
            Main,

            /// <summary>The real-time performance monitoring tab.</summary>
            Performance,

            /// <summary>The console output tab.</summary>
            Console,

            /// <summary>The service dependency tree visualization tab.</summary>
            Dependencies,

            /// <summary>The log search tab.</summary>
            Logs
        }

        private readonly IMessageBoxService _messageBoxService;
        private readonly IProcessKiller _processKiller;
        private readonly ThemePreferenceStore _themePreferenceStore;
        private readonly ColumnVisibilityPreferences _columnVisibilityPreferences;
        private readonly List<DataGridColumn> _hardwareColumns = new();
        private readonly List<DataGridColumn> _gpuUsageColumns = new();
        private readonly List<DataGridColumn> _gpuAdditionalColumns = new();
        private readonly List<DataGridColumn> _diskAdditionalColumns = new();
        private DataGridColumn? _primaryDiskActivityColumn;
        private ScrollViewer? _servicesScrollViewer;
        private bool _isSynchronizingActionsScroll;
        private bool _hardwareColumnsBuilt;
        private bool _isWatchingSystemTheme;

        private const double ActionsColumnWidth = 160;
        private const double ColumnResizeHitTargetWidth = 14;
        private const double ColumnGroupArrowSlotWidth = 20;
        private const double ColumnGroupArrowLabelGap = 2;
        private const double ColumnGroupMenuContentInset = 16;

        /// <summary>
        /// Initializes a new instance of the <see cref="MainWindow"/> class using constructor injection.
        /// </summary>
        /// <param name="mainViewModel">The primary DataContext for the application.</param>
        /// <param name="messageBoxService">Service for displaying UI dialogs.</param>
        /// <param name="processKiller">Service for terminating child processes on application exit.</param>
        public MainWindow(
            MainViewModel mainViewModel,
            IMessageBoxService messageBoxService,
            IProcessKiller processKiller
            )
        {
            Logger.Info("Servy Manager main window construction started.");

            try
            {
                InitializeComponent();
                ApplicationThemeManager.Apply(this);
                _themePreferenceStore = new ThemePreferenceStore();
                ApplicationThemeManager.Changed += ApplicationThemeManager_Changed;

                _columnVisibilityPreferences = new ColumnVisibilityPreferences();
                ApplySavedColumnVisibilityToExistingColumns();
                _messageBoxService = messageBoxService ?? throw new ArgumentNullException(nameof(messageBoxService));
                DataContext = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
                _processKiller = processKiller ?? throw new ArgumentNullException(nameof(processKiller));
                mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;

                Logger.Info("Servy Manager main window construction completed.");
            }
            catch (Exception ex)
            {
                Logger.Error("Servy Manager main window construction failed.", ex);
                throw;
            }
        }

        /// <summary>
        /// Handles the <see cref="Window.Loaded"/> event.
        /// Acts as a synchronous wrapper that fire-and-forgets the asynchronous window initialization.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data.</param>
        private void Window_Loaded(object sender, RoutedEventArgs e)
            => _ = RunAsync(() => Window_LoadedAsync(sender, e));

        private void ServicesDataGrid_Loaded(object sender, RoutedEventArgs e)
        {
            _servicesScrollViewer ??= FindVisualChild<ScrollViewer>(ServicesDataGrid);
            if (_servicesScrollViewer != null)
            {
                _servicesScrollViewer.ScrollChanged -= ServicesScrollViewer_ScrollChanged;
                _servicesScrollViewer.ScrollChanged += ServicesScrollViewer_ScrollChanged;
                UpdateActionsColumnFade();
            }
        }

        private void ServicesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isSynchronizingActionsScroll)
                return;

            UpdateActionsColumnFade();
            if (Math.Abs(e.VerticalChange) < 0.01) return;

            try
            {
                _isSynchronizingActionsScroll = true;
                ActionsScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
            }
            finally
            {
                _isSynchronizingActionsScroll = false;
            }
        }

        private void UpdateActionsColumnFade()
        {
            if (_servicesScrollViewer is null) return;

            // The fade separates scrolling content from the fixed Actions overlay. At the
            // horizontal end there is no more content sliding underneath it, so remove the
            // gradient and leave the final column fully legible.
            ActionsColumnFade.Visibility = ShouldShowActionsColumnFade(
                _servicesScrollViewer.ScrollableWidth,
                _servicesScrollViewer.HorizontalOffset)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        internal static bool ShouldShowActionsColumnFade(double scrollableWidth, double horizontalOffset) =>
            double.IsFinite(scrollableWidth) &&
            double.IsFinite(horizontalOffset) &&
            scrollableWidth - horizontalOffset > 1;

        private void ActionsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isSynchronizingActionsScroll || _servicesScrollViewer == null || Math.Abs(e.VerticalChange) < 0.01)
                return;

            try
            {
                _isSynchronizingActionsScroll = true;
                _servicesScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
            }
            finally
            {
                _isSynchronizingActionsScroll = false;
            }
        }

        private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }

        /// <summary>
        /// Performs asynchronous initialization when the window is loaded.
        /// Executes the initial service search via the <see cref="MainViewModel.SearchCommand"/>.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task Window_LoadedAsync(object sender, RoutedEventArgs e)
        {
            Logger.Info("Servy Manager window loaded; starting the initial service-only search.");

            if (_themePreferenceStore.Preference == ThemePreference.System)
                StartSystemThemeWatcher();
            else
                UpdateThemeToggleButton(ApplicationThemeManager.GetAppTheme());

            try
            {
                // Trigger the global search command to populate the dashboard
                // the moment the shell is ready for interaction. Hardware discovery is
                // deliberately not part of this path.
                if (DataContext is MainViewModel vm)
                {
                    await vm.SearchCommand.ExecuteAsync(null);
                    Logger.Info("Initial service-only search completed; manager shell is interactive.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Initial manager window load failed.", ex);
                throw;
            }
        }

        private void StartSystemThemeWatcher()
        {
            if (_isWatchingSystemTheme) return;

            try
            {
                SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.None, updateAccents: true);
                _isWatchingSystemTheme = true;
                var currentTheme = ApplicationThemeManager.GetAppTheme();
                SynchronizeTheme(currentTheme);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Windows theme monitoring could not be started; the current theme will remain active. {ex.Message}");
            }
        }

        private void StopSystemThemeWatcher()
        {
            if (!_isWatchingSystemTheme) return;

            try
            {
                SystemThemeWatcher.UnWatch(this);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Windows theme monitoring could not be stopped cleanly. {ex.Message}");
            }
            finally
            {
                _isWatchingSystemTheme = false;
            }
        }

        private void ApplicationThemeManager_Changed(ApplicationTheme theme, Color accent)
        {
            SynchronizeTheme(theme);
        }

        private void SynchronizeTheme(ApplicationTheme theme)
        {
            // WPF-UI copies application resources into each window. Refresh that copy,
            // then explicitly update both palette scopes so no copied light brushes can
            // linger beside the newly applied dark (or light) WPF-UI control resources.
            ApplicationThemeManager.Apply(this);
            ApplyThemePalette(theme);
            ThemePalette.Apply(Resources, theme);
            UpdateThemeToggleButton(theme);
        }

        private static void ApplyThemePalette(ApplicationTheme theme)
        {
            if (Application.Current is { } application)
                ThemePalette.Apply(application, theme);
        }

        private void UpdateThemeToggleButton(ApplicationTheme theme)
        {
            var isDark = ThemePalette.IsDark(theme);
            var label = isDark ? "Switch to light mode" : "Switch to dark mode";
            ThemeToggleButton.Icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = isDark ? Wpf.Ui.Controls.SymbolRegular.WeatherSunny24 : Wpf.Ui.Controls.SymbolRegular.WeatherMoon24
            };
            ThemeToggleButton.ToolTip = label;
            AutomationProperties.SetName(ThemeToggleButton, label);
        }

        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var nextTheme = ThemePalette.IsDark(ApplicationThemeManager.GetAppTheme())
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark;
            SetThemePreference(nextTheme == ApplicationTheme.Dark ? ThemePreference.Dark : ThemePreference.Light, nextTheme);
        }

        private void FollowSystemTheme_Click(object sender, RoutedEventArgs e)
        {
            SetThemePreference(ThemePreference.System, ThemePalette.Resolve(ThemePreference.System));
            StartSystemThemeWatcher();
        }

        private void SetThemePreference(ThemePreference preference, ApplicationTheme theme)
        {
            _themePreferenceStore.SetPreference(preference);
            if (preference == ThemePreference.System)
            {
                SystemThemeManager.UpdateSystemThemeCache();
                theme = ThemePalette.Resolve(ThemePreference.System);
            }
            else
            {
                StopSystemThemeWatcher();
            }

            ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.None, updateAccent: true);
            SynchronizeTheme(theme);
        }

        private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MainViewModel vm ||
                e.PropertyName is not (nameof(MainViewModel.IsHardwareMetricsInitialized)
                    or nameof(MainViewModel.Gpus)
                    or nameof(MainViewModel.Disks)))
            {
                return;
            }

            BuildHardwareColumns(vm);
        }

        /// <summary>
        /// Adds optional GPU and disk columns after the hardware provider has completed discovery.
        /// The fixed columns remain declared in XAML; only hardware-dependent columns are created here.
        /// </summary>
        private void BuildHardwareColumns(MainViewModel vm)
        {
            if (_hardwareColumnsBuilt || !vm.IsHardwareMetricsInitialized) return;

            _hardwareColumnsBuilt = true;

            if (vm.Gpus.Count == 0 && vm.Disks.Count == 0)
            {
                Logger.Info("No optional GPU or disk telemetry was detected; hardware columns omitted.");
                return;
            }

            var insertIndex = Math.Max(1, ServicesDataGrid.Columns.Count - 1);

            for (var i = 0; i < vm.Gpus.Count; i++)
            {
                var gpu = vm.Gpus[i];
                var gpuPrefix = vm.Gpus.Count == 1 ? "GPU" : $"GPU {gpu.Index}";
                var usageLabel = vm.Gpus.Count == 1 ? "GPU usage (%)" : $"{gpuPrefix} usage (%)";
                var powerLabel = vm.Gpus.Count == 1 ? "GPU power (W)" : $"{gpuPrefix} power (W)";
                var vramLabel = vm.Gpus.Count == 1
                    ? "VRAM"
                    : $"VRAM {gpu.Index}";
                var usageColumn = CreateMetricColumn(
                    usageLabel,
                    $"GpuMetrics[{i}].UsageDisplay",
                    $"GpuMetrics[{i}].UsageDetailsDisplay",
                    160);
                _gpuUsageColumns.Add(usageColumn);
                _hardwareColumns.Add(usageColumn);
                var powerColumn = CreateMetricColumn(
                    powerLabel,
                    $"GpuMetrics[{i}].PowerDisplay",
                    $"GpuMetrics[{i}].PowerDetailsDisplay",
                    112,
                    Visibility.Hidden);
                _gpuAdditionalColumns.Add(powerColumn);
                _hardwareColumns.Add(powerColumn);
                var vramColumn = CreateMetricColumn(
                    vramLabel,
                    $"GpuMetrics[{i}].VramDisplay",
                    $"GpuMetrics[{i}].DetailsDisplay",
                    104,
                    Visibility.Hidden);
                _gpuAdditionalColumns.Add(vramColumn);
                _hardwareColumns.Add(vramColumn);
            }

            if (vm.Disks.Count > 0)
            {
                var cDriveIndex = -1;
                for (var i = 0; i < vm.Disks.Count; i++)
                {
                    if (string.Equals(vm.Disks[i].Label.TrimEnd('\\'), "C:", StringComparison.OrdinalIgnoreCase))
                    {
                        cDriveIndex = i;
                        break;
                    }
                }
                if (cDriveIndex < 0) cDriveIndex = 0;

                var diskActivityColumns = new List<DataGridColumn>();
                for (var i = 0; i < vm.Disks.Count; i++)
                {
                    var driveLabel = vm.Disks[i].Label.TrimEnd('\\');
                    var diskColumn = CreateMetricColumn(
                        $"Disk {driveLabel} activity",
                        $"DiskMetrics[{i}].UsageDisplay",
                        $"DiskMetrics[{i}].DetailsDisplay",
                        160,
                        i == cDriveIndex ? Visibility.Visible : Visibility.Hidden);
                    diskActivityColumns.Add(diskColumn);
                    _hardwareColumns.Add(diskColumn);
                }

                _primaryDiskActivityColumn = diskActivityColumns[cDriveIndex];
                _diskAdditionalColumns.AddRange(diskActivityColumns.Where((_, index) => index != cDriveIndex));

                if (vm.Disks.Count > 1)
                {
                    var allDisksColumn = CreateMetricColumn(
                        "All disks activity",
                        $"DiskMetrics[{vm.Disks.Count}].UsageDisplay",
                        $"DiskMetrics[{vm.Disks.Count}].DetailsDisplay",
                        160,
                        Visibility.Hidden);
                    _diskAdditionalColumns.Add(allDisksColumn);
                    _hardwareColumns.Add(allDisksColumn);
                }

                var overallDiskIndex = vm.Disks.Count > 1 ? vm.Disks.Count : 0;
                var diskLabel = vm.Disks.Count > 1 ? "All disks" : $"Disk {vm.Disks[0].Label.TrimEnd('\\')}";

                var readColumn = CreateMetricColumn(
                    $"{diskLabel} read",
                    $"DiskMetrics[{overallDiskIndex}].ReadDisplay",
                    $"DiskMetrics[{overallDiskIndex}].ReadDetailsDisplay",
                    120,
                    Visibility.Hidden);
                _diskAdditionalColumns.Add(readColumn);
                _hardwareColumns.Add(readColumn);
                var writeColumn = CreateMetricColumn(
                    $"{diskLabel} write",
                    $"DiskMetrics[{overallDiskIndex}].WriteDisplay",
                    $"DiskMetrics[{overallDiskIndex}].WriteDetailsDisplay",
                    120,
                    Visibility.Hidden);
                _diskAdditionalColumns.Add(writeColumn);
                _hardwareColumns.Add(writeColumn);
            }

            foreach (var column in _hardwareColumns)
            {
                column.CanUserReorder = false;
                column.HeaderStyle = (Style)FindResource("ServicesColumnHeaderStyle");
                ApplySavedColumnVisibility(column);
                ServicesDataGrid.Columns.Insert(insertIndex++, column);
            }

            Logger.Info($"Added {_hardwareColumns.Count} optional hardware columns.");
        }

        private static DataGridTemplateColumn CreateMetricColumn(
            string header,
            string bindingPath,
            string tooltipPath,
            double width,
            Visibility visibility = Visibility.Visible)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.MarginProperty, new Thickness(5, 0, 0, 0));
            text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            text.SetBinding(TextBlock.TextProperty, new Binding(bindingPath));
            text.SetBinding(TextBlock.ToolTipProperty, new Binding(tooltipPath));

            return new DataGridTemplateColumn
            {
                Header = header,
                Width = new DataGridLength(width),
                MinWidth = Math.Min(72, width),
                CanUserResize = true,
                Visibility = visibility,
                SortMemberPath = bindingPath,
                CellTemplate = new DataTemplate { VisualTree = text }
            };
        }

        /// <summary>
        /// Handles PreviewMouseLeftButtonDown for row action buttons (Start, Stop, Restart).
        /// Executes the associated command manually.
        /// </summary>
        private void ActionButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (sender is Button btn && btn.Command != null)
            {
                var parameter = btn.CommandParameter ?? DataContext;
                if (btn.Command.CanExecute(parameter))
                    btn.Command.Execute(parameter);
            }
        }

        /// <summary>
        /// Handles PreviewMouseLeftButtonDown for menu buttons (⋮).
        /// Opens the associated ContextMenu and sets its DataContext.
        /// </summary>
        private void MenuButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.DataContext = btn.DataContext;

                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.Placement = PlacementMode.Bottom;
                btn.ContextMenu.IsOpen = true;
            }
        }

        /// <summary>
        /// Handles the PreviewKeyDown event on the window to provide global keyboard shortcuts.
        /// Acts as a synchronous entry point that fire-and-forgets the asynchronous shortcut logic.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data containing the key and modifiers.</param>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
            => _ = RunAsync(() => Window_PreviewKeyDownAsync(sender, e));

        /// <summary>
        /// Asynchronously processes global keyboard shortcuts.
        /// Supports F5 for contextual refreshing across different tabs and Ctrl+A for service selection.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data containing the key and modifiers.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task Window_PreviewKeyDownAsync(object sender, KeyEventArgs e)
        {
            // F5: Context-aware refresh logic based on the active tab
            if (e.Key == Key.F5)
            {
                if (MainTab.IsSelected && DataContext is MainViewModel vm && vm.SearchCommand.CanExecute(null))
                {
                    await vm.SearchCommand.ExecuteAsync(null);
                }
                else if (LogsTab.IsSelected && LogsTab.Content is LogsView logsView && logsView.DataContext is LogsViewModel lvm && lvm.SearchCommand.CanExecute(null))
                {
                    await lvm.SearchCommand.ExecuteAsync(null);
                }
                else if (DependenciesTab.IsSelected && DependenciesTab.Content is DependenciesView dependenciesView && dependenciesView.DataContext is DependenciesViewModel dvm)
                {
                    await dvm.RefreshCommand.ExecuteAsync(null);
                }
            }

            // Ctrl + A: Select all services in the DataGrid (Dashboard only)
            if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && MainTab.IsSelected)
            {
                ServicesDataGrid.Focus();
                ServicesDataGrid.SelectAll();

                // Mark event as handled to prevent standard text-box 'Select All' if a search field has focus
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handles the click event for the Configuration menu item.
        /// Acts as a synchronous wrapper that fire-and-forgets the asynchronous configuration sequence.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data.</param>
        private void Menu_ConfigClick(object sender, RoutedEventArgs e)
            => _ = RunAsync(() => Menu_ConfigClickAsync(sender, e));

        /// <summary>
        /// Asynchronously initiates the application configuration sequence.
        /// Executes the <see cref="MainViewModel.ConfigureCommand"/> to open the settings interface.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task Menu_ConfigClickAsync(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                await vm.ConfigureCommand.ExecuteAsync(null);
            }
        }

        /// <summary>
        /// Handles the <see cref="SelectionChanged"/> event of the main <see cref="TabControl"/>.
        /// Acts as a synchronous wrapper that fire-and-forgets the asynchronous tab transition logic.
        /// </summary>
        /// <param name="sender">The <see cref="TabControl"/> that raised the event.</param>
        /// <param name="e">The <see cref="SelectionChangedEventArgs"/> instance containing event data.</param>
        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => _ = RunAsync(() => MainTabControl_SelectionChangedAsync(sender, e));

        /// <summary>
        /// Asynchronously manages the lifecycle and state transitions when switching between application tabs.
        /// Cancels background tasks or timers for inactive tabs and triggers context-specific searches or
        /// data loading for the newly selected tab.
        /// </summary>
        /// <param name="sender">The <see cref="TabControl"/> that raised the event.</param>
        /// <param name="e">The <see cref="SelectionChangedEventArgs"/> instance containing event data.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <remarks>
        /// This method ensures that high-resource operations (like real-time performance monitoring or
        /// log tailing) are throttled or stopped when the user navigates away from the respective tab.
        /// It uses the <see cref="SelectionChangedEventArgs.OriginalSource"/> check to ensure it only
        /// reacts to the main TabControl and not nested elements.
        /// </remarks>
        private async Task MainTabControl_SelectionChangedAsync(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                // Only react if the TabControl itself fired the event (prevents bubbling issues)
                if (!ReferenceEquals(sender, e.OriginalSource))
                    return;

                if (DataContext is MainViewModel vm)
                {
                    if (MainTab.IsSelected)
                        await HandleMainTabSelected(vm);
                    else if (PerformanceTab.IsSelected)
                        await HandleMonitoringTabSelected(vm, GetPerformanceVm(), TabScope.Performance);
                    else if (ConsoleTab.IsSelected)
                        await HandleMonitoringTabSelected(vm, GetConsoleVm(), TabScope.Console);
                    else if (DependenciesTab.IsSelected)
                        await HandleMonitoringTabSelected(vm, GetDependenciesVm(), TabScope.Dependencies);
                    else if (LogsTab.IsSelected)
                        await HandleLogsTabSelected(vm, GetLogsVm());
                }
            }
            catch (OperationCanceledException)
            {
                // Expected - a newer tab navigation or search has superseded this one.
            }
            catch (Exception ex)
            {
                Logger.Error("Tab selection failed.", ex);
                await _messageBoxService.ShowErrorAsync(Strings.Msg_MainTabControl_SelectionChangedError, UiAppConfig.Caption);
            }
        }

        /// <summary>
        /// Retrieves the <see cref="PerformanceViewModel"/> instance bound to the <see cref="PerformanceTab"/> content, if available.
        /// </summary>
        /// <returns>
        /// The <see cref="PerformanceViewModel"/> instance if the <see cref="PerformanceTab"/> has content and its DataContext
        /// is a <see cref="PerformanceViewModel"/>; otherwise, <c>null</c>.
        /// </returns>
        private PerformanceViewModel? GetPerformanceVm()
            => PerformanceTab.Content is PerformanceView perfView ? perfView.DataContext as PerformanceViewModel : null;

        /// <summary>
        /// Retrieves the current console view model associated with the console tab, if available.
        /// </summary>
        /// <returns>A <see cref="ConsoleViewModel"/> instance representing the data context of the console view; or <see
        /// langword="null"/> if the console view is not present or its data context is not a <see
        /// cref="ConsoleViewModel"/>.</returns>
        private ConsoleViewModel? GetConsoleVm()
            => ConsoleTab.Content is ConsoleView consoleView ? consoleView.DataContext as ConsoleViewModel : null;

        /// <summary>
        /// Retrieves the <see cref="DependenciesViewModel"/> instance bound to the <see cref="DependenciesTab"/> content, if available.
        /// </summary>
        /// <returns>
        /// The <see cref="DependenciesViewModel"/> instance if the <see cref="DependenciesTab"/> has content and its DataContext
        /// is a <see cref="DependenciesViewModel"/>; otherwise, <c>null</c>.
        /// </returns>
        private DependenciesViewModel? GetDependenciesVm()
            => DependenciesTab.Content is DependenciesView depsView ? depsView.DataContext as DependenciesViewModel : null;

        /// <summary>
        /// Retrieves the <see cref="LogsViewModel"/> instance bound to the <see cref="LogsTab"/> content, if available.
        /// </summary>
        /// <returns>
        /// The <see cref="LogsViewModel"/> instance if the <see cref="LogsTab"/> has content and its DataContext
        /// is a <see cref="LogsViewModel"/>; otherwise, <c>null</c>.
        /// </returns>
        private LogsViewModel? GetLogsVm()
            => LogsTab.Content is LogsView logsView ? logsView.DataContext as LogsViewModel : null;

        /// <summary>
        /// Centralized teardown utility that deactivates background monitoring, timers, and searches
        /// across all tabs except for the explicitly specified active scope.
        /// </summary>
        /// <param name="vm">The main <see cref="MainViewModel"/> instance.</param>
        /// <param name="activeTab">The tab scope identifier that should skip deactivation teardown.</param>
        private void DeactivateAllExcept(MainViewModel vm, TabScope activeTab)
        {
            if (activeTab != TabScope.Main) vm.StopRefreshTimer();
            if (activeTab != TabScope.Performance) GetPerformanceVm()?.StopMonitoring();
            if (activeTab != TabScope.Console) GetConsoleVm()?.StopMonitoring();
            if (activeTab != TabScope.Dependencies) GetDependenciesVm()?.StopMonitoring();
            if (activeTab != TabScope.Logs) GetLogsVm()?.CancelSearch();
        }

        /// <summary>
        /// Handles tasks when the Main tab is selected: stops background monitoring and searches in
        /// every other tab, triggers a service search if the list is empty, and starts the periodic
        /// refresh timer.
        /// </summary>
        /// <param name="vm">The main <see cref="MainViewModel"/> instance.</param>
        private async Task HandleMainTabSelected(MainViewModel vm)
        {
            // Stop background workers on all other interfaces, bypassing the Main refresh timer
            DeactivateAllExcept(vm, TabScope.Main);

            if (!vm.HasSearched)
                await vm.SearchCommand.ExecuteAsync(null);

            vm.CreateAndStartTimer();
        }

        /// <summary>
        /// Handles state transitions and resource management when a monitoring-based tab (Performance, Console, or Dependencies) is selected.
        /// </summary>
        /// <remarks>
        /// This method ensures that only the specified monitoring tab is active by stopping or cleaning up background
        /// activities across all other tabs.
        /// </remarks>
        /// <param name="vm">The main <see cref="MainViewModel"/> instance.</param>
        /// <param name="monitoringVm">The target <see cref="MonitoringViewModelBase"/> instance, or <c>null</c> if unavailable.</param>
        /// <param name="scope">The active <see cref="TabScope"/> corresponding to the selected monitoring tab.</param>
        private Task HandleMonitoringTabSelected(MainViewModel vm, MonitoringViewModelBase? monitoringVm, TabScope scope)
        {
            DeactivateAllExcept(vm, scope);

            monitoringVm?.StartMonitoring();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles tasks when the Logs tab is selected: stops background monitoring and timers in
        /// every other tab, and triggers a search for logs if the logs collection is empty.
        /// </summary>
        /// <param name="vm">The main <see cref="MainViewModel"/> instance.</param>
        /// <param name="logsVm">
        /// The <see cref="LogsViewModel"/> instance for the logs tab, or <c>null</c> if unavailable.
        /// </param>
        private async Task HandleLogsTabSelected(MainViewModel vm, LogsViewModel? logsVm)
        {
            DeactivateAllExcept(vm, TabScope.Logs);

            if (logsVm != null && !logsVm.HasSearched)
            {
                logsVm.ResetDateWindowToNow();
                await logsVm.SearchCommand.ExecuteAsync(null);
            }
        }

        /// <summary>
        /// Handles the PreviewMouseLeftButtonDown event for the DataGrid "Check All" header checkbox.
        /// Toggles the SelectAll property in the MainViewModel when the user clicks the header checkbox.
        /// </summary>
        /// <param name="sender">The DataGridColumnHeader that raised the event.</param>
        /// <param name="e">Mouse button event arguments.</param>
        private void ColumnHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && sender is DataGridColumnHeader resizedHeader && resizedHeader.Column.CanUserResize)
            {
                var pointerX = e.GetPosition(resizedHeader).X;
                var clickedGripper = e.OriginalSource is DependencyObject resizeSource &&
                    FindVisualAncestor<Thumb>(resizeSource) is { Name: "PART_RightHeaderGripper" };
                var clickedDividerArea = pointerX >= resizedHeader.ActualWidth - ColumnResizeHitTargetWidth;

                if (clickedGripper || clickedDividerArea)
                {
                    AutoSizeColumn(resizedHeader.Column, resizedHeader);
                    e.Handled = true;
                    return;
                }
            }

            if (sender is DataGridColumnHeader header &&
                header.Content is CheckBox cb &&
                ServicesDataGrid.DataContext is MainViewModel vm)
            {
                // Toggle the SelectAll value manually (true if unchecked or indeterminate, false if checked)
                bool newValue = cb.IsChecked != true;
                vm.SelectAll = newValue;
                e.Handled = true;
            }
        }

        private void AutoSizeColumn(DataGridColumn column, DataGridColumnHeader header)
        {
            try
            {
                var desiredWidth = column.MinWidth;
                var headerText = new System.Windows.Controls.TextBlock
                {
                    Text = Convert.ToString(column.Header) ?? string.Empty,
                    FontFamily = header.FontFamily,
                    FontSize = header.FontSize,
                    FontWeight = header.FontWeight,
                    FontStyle = header.FontStyle,
                    TextWrapping = TextWrapping.NoWrap
                };
                headerText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                desiredWidth = Math.Max(
                    desiredWidth,
                    headerText.DesiredSize.Width + header.Padding.Left + header.Padding.Right + 24);

                if (column is DataGridTemplateColumn { CellTemplate: { } template })
                {
                    foreach (var item in ServicesDataGrid.Items)
                    {
                        if (ReferenceEquals(item, CollectionView.NewItemPlaceholder)) continue;

                        // Host the template in a ContentPresenter so WPF establishes the
                        // normal Content/DataContext inheritance before measuring bindings.
                        // Calling LoadContent() directly leaves some template bindings
                        // unresolved, which silently measures only the header width.
                        var cellPresenter = new ContentPresenter
                        {
                            Content = item,
                            ContentTemplate = template
                        };
                        cellPresenter.ApplyTemplate();
                        RemoveCellMeasureConstraints(cellPresenter);
                        cellPresenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        if (double.IsFinite(cellPresenter.DesiredSize.Width))
                            desiredWidth = Math.Max(desiredWidth, cellPresenter.DesiredSize.Width + 16);
                    }
                }

                column.Width = new DataGridLength(Math.Ceiling(desiredWidth));
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not auto-size the '{GetColumnTitle(column)}' service column.", ex);
            }
        }

        private static void RemoveCellMeasureConstraints(DependencyObject root)
        {
            if (root is FrameworkElement element)
            {
                BindingOperations.ClearBinding(element, FrameworkElement.MaxWidthProperty);
                element.ClearValue(FrameworkElement.MaxWidthProperty);
            }

            if (root is System.Windows.Controls.TextBlock textBlock)
            {
                textBlock.TextWrapping = TextWrapping.NoWrap;
                textBlock.TextTrimming = TextTrimming.None;
            }

            if (root is Visual or Visual3D)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                    RemoveCellMeasureConstraints(VisualTreeHelper.GetChild(root, i));
            }
            else if (root is FrameworkContentElement contentElement)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(contentElement))
                {
                    if (child is DependencyObject dependencyObject)
                        RemoveCellMeasureConstraints(dependencyObject);
                }
            }
        }

        private static T? FindVisualAncestor<T>(DependencyObject source) where T : DependencyObject
        {
            for (DependencyObject? current = source; current is not null;)
            {
                if (current is T match) return match;
                current = current is Visual or Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return null;
        }

        /// <summary>
        /// Selects the row before opening its context menu so row commands always target the
        /// entry that was right-clicked, including when another row was selected previously.
        /// </summary>
        private void DataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.DataContext is ServiceRowViewModel service)
            {
                row.IsSelected = true;
                ServicesDataGrid.SelectedItem = service;
                ServicesDataGrid.Focus();
            }
        }

        /// <summary>
        /// Builds a compact column chooser for the header that was right-clicked.
        /// The selection and action columns stay visible so this menu remains reachable.
        /// </summary>
        private void ServicesColumnHeader_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not DataGridColumnHeader header) return;

            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem
            {
                Header = "Show / hide columns",
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold
            });

            var firstColumn = ServicesDataGrid.Columns.FirstOrDefault();
            var actionColumn = ServicesDataGrid.Columns.LastOrDefault();
            var groupedColumns = new HashSet<DataGridColumn>();

            if (_gpuUsageColumns.Count > 0)
            {
                var primaryGpuUsage = _gpuUsageColumns[0];
                var gpuSecondaryColumns = _gpuAdditionalColumns.ToArray();
                groupedColumns.Add(primaryGpuUsage);
                foreach (var column in gpuSecondaryColumns) groupedColumns.Add(column);
                AddColumnGroupMenuItems(menu, CreateColumnGroupMenuItem(
                    primaryGpuUsage,
                    gpuSecondaryColumns,
                    "Show more columns"));

                // Every detected GPU keeps its own process-utilization column visible by default.
                // Additional devices stay individually configurable without duplicating the expander.
                foreach (var column in _gpuUsageColumns.Skip(1))
                {
                    groupedColumns.Add(column);
                    menu.Items.Add(CreateColumnVisibilityMenuItem(column));
                }
            }

            if (_primaryDiskActivityColumn is not null)
            {
                groupedColumns.Add(_primaryDiskActivityColumn);
                foreach (var column in _diskAdditionalColumns) groupedColumns.Add(column);
                AddColumnGroupMenuItems(menu, CreateColumnGroupMenuItem(
                    _primaryDiskActivityColumn,
                    _diskAdditionalColumns,
                    "Show more columns"));
            }

            if (groupedColumns.Count > 0)
                menu.Items.Add(new Separator());

            foreach (var column in ServicesDataGrid.Columns.OrderBy(c => c.DisplayIndex))
            {
                if (ReferenceEquals(column, firstColumn) ||
                    ReferenceEquals(column, actionColumn) ||
                    groupedColumns.Contains(column))
                {
                    continue;
                }

                menu.Items.Add(CreateColumnVisibilityMenuItem(column));
            }

            menu.Items.Add(new Separator());
            var showAll = new MenuItem { Header = "Show all columns" };
            showAll.Click += (_, _) =>
            {
                foreach (var column in ServicesDataGrid.Columns)
                {
                    // The legacy in-grid actions column is the data source for the
                    // fixed right overlay and must never be made visible again.
                    if (!ReferenceEquals(column, actionColumn))
                        SetColumnVisibility(column, isVisible: true);
                }
            };
            menu.Items.Add(showAll);

            header.ContextMenu = menu;
            menu.PlacementTarget = header;
            e.Handled = true;
            menu.IsOpen = true;
        }

        private void ColumnVisibilityMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && item.Tag is DataGridColumn column)
                SetColumnVisibility(column, item.IsChecked);
        }

        private MenuItem CreateColumnVisibilityMenuItem(DataGridColumn column)
        {
            var item = new MenuItem
            {
                Header = GetColumnTitle(column),
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                Tag = column
            };
            item.Click += ColumnVisibilityMenuItem_Click;
            return item;
        }

        private ColumnGroupMenuItems CreateColumnGroupMenuItem(
            DataGridColumn primaryColumn,
            IReadOnlyList<DataGridColumn> secondaryColumns,
            string expandToolTip)
        {
            var arrow = new System.Windows.Controls.TextBlock
            {
                Text = "▶",
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 12,
                Width = ColumnGroupArrowSlotWidth,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0)
            };
            var expandButton = new System.Windows.Controls.Button
            {
                Content = arrow,
                Width = ColumnGroupArrowSlotWidth,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, ColumnGroupArrowLabelGap, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                ToolTip = expandToolTip,
                Cursor = Cursors.Hand,
                Focusable = false
            };
            AutomationProperties.SetName(expandButton, expandToolTip);
            var label = new System.Windows.Controls.TextBlock
            {
                Text = GetColumnTitle(primaryColumn),
                VerticalAlignment = VerticalAlignment.Center
            };
            var content = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(expandButton);
            content.Children.Add(label);

            var primaryItem = new MenuItem
            {
                Header = content,
                IsCheckable = true,
                IsChecked = primaryColumn.Visibility == Visibility.Visible,
                Tag = primaryColumn
            };
            primaryItem.Click += ColumnVisibilityMenuItem_Click;

            var secondaryItems = new List<MenuItem>(secondaryColumns.Count);
            foreach (var column in secondaryColumns)
            {
                var item = CreateColumnVisibilityMenuItem(column);
                // Include the menu's leading content inset as well as the primary
                // label's arrow slot and gap so expanded labels line up visually.
                item.Padding = new Thickness(
                    ColumnGroupMenuContentInset + ColumnGroupArrowSlotWidth + ColumnGroupArrowLabelGap,
                    4,
                    12,
                    4);
                item.Visibility = Visibility.Collapsed;
                secondaryItems.Add(item);
            }

            var state = new ColumnGroupMenuState(arrow, secondaryItems);
            expandButton.Tag = state;
            expandButton.Click += ColumnGroupExpander_Click;

            return new ColumnGroupMenuItems(primaryItem, secondaryItems);
        }

        private static void AddColumnGroupMenuItems(ContextMenu menu, ColumnGroupMenuItems group)
        {
            menu.Items.Add(group.Primary);
            foreach (var item in group.SecondaryItems)
            {
                menu.Items.Add(item);
            }
        }

        private void ColumnGroupExpander_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: ColumnGroupMenuState state }) return;
            e.Handled = true;

            state.IsExpanded = !state.IsExpanded;
            var animation = new DoubleAnimation(
                state.IsExpanded ? 0 : 90,
                state.IsExpanded ? 90 : 0,
                TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            state.Arrow.BeginAnimation(RotateTransform.AngleProperty, animation);

            foreach (var item in state.SecondaryItems)
            {
                item.BeginAnimation(UIElement.OpacityProperty, null);
                item.Visibility = Visibility.Visible;
                item.Opacity = state.IsExpanded ? 0 : 1;
                var offset = new TranslateTransform(0, state.IsExpanded ? -5 : 0);
                item.RenderTransform = offset;

                var reveal = new DoubleAnimation(
                    state.IsExpanded ? 0 : 1,
                    state.IsExpanded ? 1 : 0,
                    TimeSpan.FromMilliseconds(145))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                if (!state.IsExpanded)
                {
                    reveal.Completed += (_, _) =>
                    {
                        if (state.IsExpanded) return;
                        item.Visibility = Visibility.Collapsed;
                        item.Opacity = 1;
                        item.RenderTransform = Transform.Identity;
                    };
                }

                item.BeginAnimation(UIElement.OpacityProperty, reveal);
                offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(
                    state.IsExpanded ? -5 : 0,
                    state.IsExpanded ? 0 : -5,
                    TimeSpan.FromMilliseconds(145))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
        }

        private void SetColumnVisibility(DataGridColumn column, bool isVisible)
        {
            column.Visibility = isVisible ? Visibility.Visible : Visibility.Hidden;
            if (GetColumnPreferenceKey(column) is { } key)
                _columnVisibilityPreferences.SetVisibility(key, isVisible);
        }

        private void ApplySavedColumnVisibilityToExistingColumns()
        {
            foreach (var column in ServicesDataGrid.Columns)
                ApplySavedColumnVisibility(column);
        }

        private void ApplySavedColumnVisibility(DataGridColumn column)
        {
            if (GetColumnPreferenceKey(column) is not { } key ||
                !_columnVisibilityPreferences.TryGetVisibility(key, out var isVisible))
            {
                return;
            }

            column.Visibility = isVisible ? Visibility.Visible : Visibility.Hidden;
        }

        private static string? GetColumnPreferenceKey(DataGridColumn column)
        {
            return string.IsNullOrWhiteSpace(column.SortMemberPath) ? null : column.SortMemberPath;
        }

        private sealed class ColumnGroupMenuState(
            System.Windows.Controls.TextBlock arrow,
            IReadOnlyList<MenuItem> secondaryItems)
        {
            public System.Windows.Controls.TextBlock Arrow { get; } = arrow;
            public IReadOnlyList<MenuItem> SecondaryItems { get; } = secondaryItems;
            public bool IsExpanded { get; set; }
        }

        private sealed record ColumnGroupMenuItems(MenuItem Primary, IReadOnlyList<MenuItem> SecondaryItems);

        private string GetColumnTitle(DataGridColumn column)
        {
            if (column.Header is string text && !string.IsNullOrWhiteSpace(text))
                return text;

            if (ReferenceEquals(column, ServicesDataGrid.Columns.FirstOrDefault()))
                return "Select";

            if (ReferenceEquals(column, ServicesDataGrid.Columns.LastOrDefault()))
                return "Actions";

            return $"Column {column.DisplayIndex + 1}";
        }

        /// <summary>
        /// Handles the PreviewMouseLeftButtonDown event for cells in the checkbox column.
        /// Toggles the IsChecked property of the corresponding ServiceRowViewModel when the user clicks the checkbox cell.
        /// Also ensures that IsSelected is cleared if the checkbox is checked.
        /// </summary>
        /// <param name="sender">The DataGridCell that raised the event.</param>
        /// <param name="e">Mouse button event arguments.</param>
        private void CheckBoxCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only toggle for the first column, which contains the checkbox
            if (sender is DataGridCell cell && cell.DataContext is ServiceRowViewModel vm && cell.Column.DisplayIndex == 0)
            {
                vm.IsChecked = !vm.IsChecked;
                if (vm.IsChecked) vm.IsSelected = false;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handles the <see cref="DataGridRow.MouseDoubleClick"/> event.
        /// Toggles the <see cref="ServiceRowViewModel.IsChecked"/> property when a row is double-clicked,
        /// which visually changes the row background through a style trigger.
        /// </summary>
        /// <param name="sender">The source of the event, expected to be a <see cref="DataGridRow"/>.</param>
        /// <param name="e">The <see cref="MouseButtonEventArgs"/> instance containing event data.</param>
        private void DataGridRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.DataContext is ServiceRowViewModel vm)
            {
                vm.IsChecked = !vm.IsChecked;

                // consume the double-click so it does not reach the row's default handling
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handles mouse clicks on the window and clears the DataGrid selection
        /// if the click occurred outside the DataGrid.
        /// </summary>
        /// <param name="sender">The source of the event (typically the Window).</param>
        /// <param name="e">Mouse button event arguments containing click information.</param>
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && !IsDescendantOf(source, ServicesDataGrid))
            {
                ServicesDataGrid.SelectedItems.Clear();
            }
        }

        /// <summary>
        /// Helper to check if a visual element is a child of a parent
        /// </summary>
        private bool IsDescendantOf(DependencyObject source, DependencyObject parent)
        {
            while (source != null)
            {
                if (source == parent)
                    return true;

                source = (source is Visual || source is Visual3D)
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }
            return false;
        }

        /// <summary>
        /// Handles the <see cref="Window.Closed"/> event.
        /// </summary>
        /// <param name="e">An <see cref="EventArgs"/> object that contains the event data.</param>
        /// <remarks>
        /// This override ensures that when the main window is closed:
        /// 1. All child processes spawned by Servy are terminated to prevent orphans.
        /// 2. All ViewModels are explicitly cleaned up to stop DispatcherTimers and cancel
        ///    async background tasks.
        /// Secure data and the global logger are disposed later, in <see cref="App.OnExit"/> -
        /// see #2027; shutting the logger down here suppresses disposal warnings.
        /// </remarks>
        protected override void OnClosed(EventArgs e)
        {
            ApplicationThemeManager.Changed -= ApplicationThemeManager_Changed;
            StopSystemThemeWatcher();

            // 1. Terminate orphaned child processes
            try
            {
                int currentPID;
                using (var current = Process.GetCurrentProcess())
                {
                    currentPID = current.Id;
                }
                _processKiller.KillChildren(currentPID);
            }
            catch (Exception ex)
            {
                Logger.Error("Error killing child processes.", ex);
            }

            // 2. Explicitly stop timers and cancel background work
            // This is CRITICAL to prevent memory leaks and "zombie" ticks
            try
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.PropertyChanged -= MainViewModel_PropertyChanged;
                    vm.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error during ViewModel cleanup in OnClosed.", ex);
            }

            if (_servicesScrollViewer != null)
                _servicesScrollViewer.ScrollChanged -= ServicesScrollViewer_ScrollChanged;

            ActionsScrollViewer.ScrollChanged -= ActionsScrollViewer_ScrollChanged;

            base.OnClosed(e);
        }

        /// <summary>
        /// Ensures the entire application process is terminated when the main window is closed.
        /// </summary>
        /// <param name="e">
        /// Provides data for the closing event.
        /// </param>
        /// <remarks>
        /// This explicitly calls <see cref="Application.Current.Shutdown"/> to guarantee
        /// that no background threads, timers, or hidden windows keep the process alive.
        /// </remarks>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            // No handler currently sets e.Cancel, so the guard is a placeholder for a future "confirm before closing" feature rather than an active check today.
            if (!e.Cancel)
            {
                Application.Current.Shutdown();
            }
        }

        /// <summary>
        /// Executes an asynchronous task in a fire-and-forget manner, typically for UI event handlers.
        /// </summary>
        /// <param name="action">The asynchronous work to execute.</param>
        /// <param name="callerName">The automatically captured member name of the calling UI event handler, used for context-aware error logging.</param>
        /// <remarks>
        /// <para>
        /// This method returns <see cref="Task"/> but is invoked as fire-and-forget via
        /// <c>_ = RunAsync(...)</c> from synchronous UI event handlers. Because the
        /// returned task is discarded, exceptions would otherwise be observed only on
        /// finalizer/GC. The top-level try-catch inside <see cref="UiTaskRunner"/> ensures all failures are recorded by
        /// the <see cref="Logger"/> instead.
        /// </para>
        /// <para>
        /// <b>Note:</b> Use this only for top-level event handlers where the caller does not
        /// need to know when the operation completes.
        /// </para>
        /// </remarks>
        private Task RunAsync(Func<Task> action, [CallerMemberName] string callerName = "")
        {
            return UiTaskRunner.RunAsync(action, $"MainWindow.{callerName}");
        }
    }
}
