using AvataRunway.Services;
using AvataRunway.Models;
using AvataRunway.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;

namespace AvataRunway
{
    /// <summary>
    /// Main shell layout for the AvataRunway viewer.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const int NavigationOpenWidth = 240;
        private const int NavigationCompactWidth = 48;
        private const int MinimumWindowWidthInDips = 1280;
        private const int MinimumWindowHeightInDips = 720;
        private const double InitialInspectorPaneWidth = 400;
        private const double MinimumInspectorPaneWidth = 400;
        private const double MaximumInspectorPaneWidth = 800;
        private const double InspectorPaneResizeGripWidth = 8;
        private const uint DefaultDpi = 96;
        private const string ViewerHostName = "appassets.local";
        private const string AutoLoadHostName = "modelassets.local";
        private const string MemoryVrmHostName = "memoryvrm.local";
        private const int MaximumBlockedWebResourceWarnings = 32;
        private static readonly IReadOnlyDictionary<string, LocalizedText> MetadataEnumDisplayValues =
            new Dictionary<string, LocalizedText>(StringComparer.Ordinal)
            {
                ["onlyAuthor"] = new("Only Author", "作者のみに許可"),
                ["onlySeparatelyLicensedPerson"] = new("Only Separately Licensed Person", "個別に許諾された人に許可"),
                ["everyone"] = new("Everyone", "全員に許可"),
                ["personalNonProfit"] = new("Personal Non-Profit", "個人（非商用）のみ"),
                ["personalProfit"] = new("Personal Profit", "個人（商用）"),
                ["corporation"] = new("Corporation", "個人・法人（商用）"),
                ["required"] = new("Required", "必要"),
                ["unnecessary"] = new("Unnecessary", "不要"),
                ["prohibited"] = new("Prohibited", "改変禁止"),
                ["allowModification"] = new("Allow Modification", "改変を許可"),
                ["allowModificationRedistribution"] = new("Allow Modification and Redistribution", "改変したモデルの再配布を許可"),
                ["OnlyAuthor"] = new("Only Author", "アバター作者のみに許可"),
                ["ExplicitlyLicensedPerson"] = new("Explicitly Licensed Person", "明確に許可された人のみに許可"),
                ["Everyone"] = new("Everyone", "全員に許可"),
                ["Disallow"] = new("Not Allowed", "不許可"),
                ["Allow"] = new("Allowed", "許可"),
                ["Redistribution_Prohibited"] = new("Redistribution Prohibited", "再配布禁止"),
                ["CC0"] = new("CC0", "著作権放棄（CC0）"),
                ["CC_BY"] = new("CC BY", "Creative Commons CC BYライセンス"),
                ["CC_BY_NC"] = new("CC BY-NC", "Creative Commons CC BY NCライセンス"),
                ["CC_BY_SA"] = new("CC BY-SA", "Creative Commons CC BY SAライセンス"),
                ["CC_BY_NC_SA"] = new("CC BY-NC-SA", "Creative Commons CC BY NC SAライセンス"),
                ["CC_BY_ND"] = new("CC BY-ND", "Creative Commons CC BY NDライセンス"),
                ["CC_BY_NC_ND"] = new("CC BY-NC-ND", "Creative Commons CC BY NC NDライセンス"),
                ["Other"] = new("Other", "その他")
            };
        private static readonly MetadataFieldDefinition[] Vrm1MetadataFields =
        [
            new("Copyright", "著作権者", "copyrightInformation", MetadataValueKind.Text),
            new("Contact", "連絡先", "contactInformation", MetadataValueKind.Text),
            new("References", "親作品", "references", MetadataValueKind.StringArray),
            new("Third-party Licenses", "サードパーティライセンス表記", "thirdPartyLicenses", MetadataValueKind.Text),
            new("VRM License URL", "VRMライセンス文書へのURL", "licenseUrl", MetadataValueKind.Text),
            new("Avatar Permission", "人格を与えることの許諾範囲", "avatarPermission", MetadataValueKind.Enum),
            new("Excessively Violent Usage", "過剰な暴力表現を含むコンテンツでの利用を許可するか", "allowExcessivelyViolentUsage", MetadataValueKind.Permission),
            new("Excessively Sexual Usage", "過剰な性的表現を含むコンテンツでの利用を許可するか", "allowExcessivelySexualUsage", MetadataValueKind.Permission),
            new("Commercial Usage", "商用利用の許可範囲", "commercialUsage", MetadataValueKind.Enum),
            new("Political / Religious Usage", "政治・宗教用途での利用を許可するか", "allowPoliticalOrReligiousUsage", MetadataValueKind.Permission),
            new("Antisocial / Hate Usage", "反社会的・憎悪表現を含むコンテンツでの利用を許可するか", "allowAntisocialOrHateUsage", MetadataValueKind.Permission),
            new("Credit Notation", "クレジット表記の指定", "creditNotation", MetadataValueKind.Enum),
            new("Redistribution", "再配布を許可するか", "allowRedistribution", MetadataValueKind.Permission),
            new("Modification", "モデルの改変の許可範囲", "modification", MetadataValueKind.Enum),
            new("Other License URL", "その他のライセンス条件", "otherLicenseUrl", MetadataValueKind.Text)
        ];
        private static readonly MetadataFieldDefinition[] Vrm0MetadataFields =
        [
            new("Contact", "連絡先", "contactInformation", MetadataValueKind.Text),
            new("Reference", "参照（親作品）", "reference", MetadataValueKind.Text),
            new("Allowed User", "アバターに人格を与えることの許諾範囲", "allowedUserName", MetadataValueKind.Enum),
            new("Violent Usage", "このアバターを用いて暴力表現を演じることの許可", "violentUssageName", MetadataValueKind.Enum),
            new("Sexual Usage", "このアバターを用いて性的表現を演じることの許可", "sexualUssageName", MetadataValueKind.Enum),
            new("Commercial Usage", "商用利用の許可", "commercialUssageName", MetadataValueKind.Enum),
            new("Other Permission URL", "その他のライセンス条件（使用許諾）", "otherPermissionUrl", MetadataValueKind.Text),
            new("License Type", "ライセンスタイプ", "licenseName", MetadataValueKind.Enum),
            new("Other License URL", "その他のライセンス条件（再配布・改変）", "otherLicenseUrl", MetadataValueKind.Text)
        ];
        private const string AutoLoadFailureMessage = "前回のVRMを自動読み込みできませんでした。ファイルを選び直してください。";
        private const string WebViewDiagnosticBootstrapScript = """
            (() => {
                const postDiagnostic = (stage, detail = '') => {
                    window.chrome?.webview?.postMessage({
                        type: 'diagnostic',
                        stage,
                        detail
                    });
                };
                window.addEventListener('error', (event) => {
                    const detail = event.error?.stack ?? event.message ?? 'Unknown WebView document startup error.';
                    const status = document.getElementById('status');
                    if (status) {
                        status.textContent = 'WebViewページの初期化に失敗しました。ログを確認してください。';
                        status.className = 'error';
                        status.hidden = false;
                    }

                    window.chrome?.webview?.postMessage({
                        type: 'load-error',
                        fileName: '',
                        className: 'WebViewDocument',
                        methodName: 'initializeDocument',
                        userMessage: 'WebViewページの初期化に失敗しました。',
                        detail
                    });
                });
                window.addEventListener('unhandledrejection', (event) => {
                    const reason = event.reason;
                    const detail = reason?.stack ?? reason?.message ?? String(reason ?? 'Unknown viewer promise rejection.');
                    window.chrome?.webview?.postMessage({
                        type: 'load-error',
                        fileName: '',
                        className: 'WebViewDocument',
                        methodName: 'initializeDocument',
                        userMessage: 'WebViewページの初期化に失敗しました。',
                        detail
                    });
                });
                postDiagnostic('document-bootstrap-executed');
            })();
            """;

        private readonly WebViewPageService webViewPageService = new();
        private readonly SettingsService settingsService = new();
        private readonly ApplicationLogService applicationLogService;

        private bool isNavigationOpen;
        private InspectorView activeInspectorView = InspectorView.Viewer;
        private bool isVrmLoaded;
        private bool isDropDecisionPending;
        private bool isSettingsModalOpen;
        private bool isInitializingSettingsControls = true;
        private bool isSettingsErrorDialogOpen;
        private bool isAppearanceSettingsSelected;
        private bool isWebViewDropEnabled;
        private bool isInspectorPaneResizing;
        private uint inspectorPaneResizePointerId;
        private double inspectorPaneResizeStartPointerX;
        private double inspectorPaneResizeStartWidth;
        private JsonElement? currentVrmMetadata;
        private Control? focusBeforeSettings;
        private string currentVrmFilePath = string.Empty;
        private string autoLoadVrmFilePath = string.Empty;
        private string autoLoadToken = string.Empty;
        private string preparedAutoLoadUrl = string.Empty;
        private string preparedAutoLoadFileName = string.Empty;
        private string autoLoadPreparationError = string.Empty;
        private bool isAutoLoadHostMappingActive;
        private readonly Dictionary<string, PendingDropState> pendingDrops = new(StringComparer.Ordinal);
        private readonly Dictionary<Slider, TextBlock> expressionValueTexts = new();
        private readonly HashSet<string> blockedWebResourceWarningUris = new(StringComparer.OrdinalIgnoreCase);
        private readonly Microsoft.UI.Xaml.Media.Brush selectedNavigationItemBackground;
        private readonly Microsoft.UI.Xaml.Media.Brush unselectedNavigationItemBackground;
        private bool isResettingExpressionSliders;
        private bool hasLoggedBlockedWebResourceWarningSuppression;
        private DropSourceCapture? activeDropSourceCapture;
        private long nextDropGeneration;
        private AppWindow? appWindow;
        private IntPtr windowHandle;
        private IntPtr windowIconHandle;
        private uint minimumWindowSizeDpi;
        private CoreWebView2DevToolsProtocolEventReceiver? runtimeConsoleReceiver;
        private CoreWebView2DevToolsProtocolEventReceiver? logEntryReceiver;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(
            string filePath,
            int iconIndex,
            IntPtr[] largeIconHandles,
            IntPtr[] smallIconHandles,
            uint iconCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr iconHandle);

        public MainWindow()
        {
            applicationLogService = new ApplicationLogService(settingsService.Current.LogLevel);
            InitializeComponent();
            InspectorPane.Width = InitialInspectorPaneWidth;
            InspectorPaneResizeGrip.Width = InspectorPaneResizeGripWidth;
            selectedNavigationItemBackground = ViewerNavigationItem.Background;
            unselectedNavigationItemBackground = ExpressionsNavigationItem.Background;
            SettingsModalOverlay.AddHandler(
                UIElement.KeyDownEvent,
                new KeyEventHandler(SettingsModalOverlay_KeyDown),
                true);
            InitializeSettingsControls();
            RenderVrmInfo();
            ConfigureTitleBar();
            ConfigureWindowSize();
            UpdateMinimumWindowSize();
            PreviewWebView.Loaded += PreviewWebView_Loaded;
            PreviewWebView.DragEnter += PreviewWebView_DragEnter;
            PreviewWebView.Drop += PreviewWebView_Drop;
        }

        private void ConfigureTitleBar()
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDragRegion);

            windowHandle = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            appWindow = AppWindow.GetFromWindowId(windowId);
            ConfigureWindowIcon();

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
                appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                appWindow.Changed += AppWindow_Changed;
                UpdateCaptionButtonInset();
            }
        }

        private void ConfigureWindowIcon()
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                throw new InvalidOperationException("The executable path could not be resolved for the window icon.");
            }

            var largeIconHandles = new IntPtr[1];
            var smallIconHandles = new IntPtr[1];
            if (ExtractIconEx(processPath, 0, largeIconHandles, smallIconHandles, 1) == 0)
            {
                throw new InvalidOperationException("The embedded application icon could not be extracted.");
            }

            var selectedIconHandle = largeIconHandles[0] != IntPtr.Zero
                ? largeIconHandles[0]
                : smallIconHandles[0];
            var unusedIconHandle = selectedIconHandle == largeIconHandles[0]
                ? smallIconHandles[0]
                : largeIconHandles[0];

            if (selectedIconHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("The embedded application icon is invalid.");
            }

            try
            {
                appWindow!.SetIcon(Win32Interop.GetIconIdFromIcon(selectedIconHandle));
                windowIconHandle = selectedIconHandle;
                Closed += MainWindow_Closed;
            }
            catch
            {
                _ = DestroyIcon(selectedIconHandle);
                throw;
            }
            finally
            {
                if (unusedIconHandle != IntPtr.Zero)
                {
                    _ = DestroyIcon(unusedIconHandle);
                }
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            SaveSessionSettingsForShutdown();

            if (windowIconHandle != IntPtr.Zero)
            {
                _ = DestroyIcon(windowIconHandle);
                windowIconHandle = IntPtr.Zero;
            }
        }

        private void SaveSessionSettingsForShutdown()
        {
            if (appWindow is null)
            {
                return;
            }

            var position = appWindow.Position;
            var size = appWindow.Size;
            settingsService.Current.WindowPlacement = new WindowPlacementSettings
            {
                X = position.X,
                Y = position.Y,
                Width = size.Width,
                Height = size.Height
            };
            if (settingsService.Current.AutoLoadLastVrm &&
                !string.IsNullOrWhiteSpace(currentVrmFilePath))
            {
                settingsService.Current.LastVrmFilePath = currentVrmFilePath;
            }

            try
            {
                settingsService.SaveForShutdown();
            }
            catch (Exception exception)
            {
                _ = applicationLogService.LogErrorAsync(
                    currentVrmFilePath,
                    nameof(MainWindow),
                    nameof(SaveSessionSettingsForShutdown),
                    exception.ToString());
            }
        }

        private void ConfigureWindowSize()
        {
            if (appWindow is null)
            {
                return;
            }

            if (settingsService.Current.RestoreWindowPlacement &&
                TryGetRestoredWindowRectangle(out var restoredRectangle))
            {
                appWindow.MoveAndResize(restoredRectangle);
                return;
            }

            MoveWindowToDefaultPosition();
        }

        private void MoveWindowToDefaultPosition()
        {
            if (appWindow is null)
            {
                return;
            }

            var initialSize = GetMinimumWindowSizeInPixels();
            var width = initialSize.Width;
            var height = initialSize.Height;

            var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            var x = workArea.X + (workArea.Width - width) / 2;
            var y = workArea.Y + (workArea.Height - height) / 2;

            appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }

        private bool TryGetRestoredWindowRectangle(out RectInt32 rectangle)
        {
            rectangle = default;
            var saved = settingsService.Current.WindowPlacement;
            if (saved is null || appWindow is null || saved.Width <= 0 || saved.Height <= 0)
            {
                return false;
            }

            var displayArea = DisplayArea.GetFromPoint(
                new PointInt32(saved.X, saved.Y),
                DisplayAreaFallback.None);
            if (displayArea is null)
            {
                return false;
            }

            var workArea = displayArea.WorkArea;
            if (saved.X < workArea.X || saved.Y < workArea.Y ||
                saved.X >= workArea.X + workArea.Width ||
                saved.Y >= workArea.Y + workArea.Height)
            {
                return false;
            }

            var minimumSize = GetMinimumWindowSizeInPixels();
            var width = Math.Max(saved.Width, minimumSize.Width);
            var height = Math.Max(saved.Height, minimumSize.Height);
            if (width > workArea.Width || height > workArea.Height)
            {
                return false;
            }

            var x = Math.Min(saved.X, workArea.X + workArea.Width - width);
            var y = Math.Min(saved.Y, workArea.Y + workArea.Height - height);
            rectangle = new RectInt32(x, y, width, height);
            return true;
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            UpdateCaptionButtonInset();
            UpdateMinimumWindowSize();
        }

        private void UpdateMinimumWindowSize()
        {
            if (appWindow?.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            var dpi = GetWindowDpi();
            if (dpi == minimumWindowSizeDpi)
            {
                return;
            }

            minimumWindowSizeDpi = dpi;
            var minimumSize = GetWindowSizeInPixels(dpi);
            presenter.PreferredMinimumWidth = minimumSize.Width;
            presenter.PreferredMinimumHeight = minimumSize.Height;
        }

        private SizeInt32 GetMinimumWindowSizeInPixels()
        {
            return GetWindowSizeInPixels(GetWindowDpi());
        }

        private uint GetWindowDpi()
        {
            var dpi = GetDpiForWindow(windowHandle);
            return dpi == 0 ? DefaultDpi : dpi;
        }

        private static SizeInt32 GetWindowSizeInPixels(uint dpi)
        {
            var scale = dpi / (double)DefaultDpi;
            return new SizeInt32(
                (int)Math.Ceiling(MinimumWindowWidthInDips * scale),
                (int)Math.Ceiling(MinimumWindowHeightInDips * scale));
        }

        private void UpdateCaptionButtonInset()
        {
            if (appWindow is null)
            {
                return;
            }

            // Minimize/restore can temporarily report zero before the system caption
            // buttons are laid out again. Keep the last valid reservation in that case.
            var reportedRightInset = appWindow.TitleBar.RightInset;
            if (reportedRightInset <= 0)
            {
                return;
            }

            var rightInset = Math.Min(reportedRightInset, 240);
            CaptionButtonColumn.Width = new GridLength(rightInset);
        }

        private void TitleBarPaneToggleButton_Click(object sender, RoutedEventArgs e)
        {
            SetNavigationPaneOpen(!isNavigationOpen);
        }

        private void SetNavigationPaneOpen(bool isOpen)
        {
            isNavigationOpen = isOpen;
            var width = isOpen ? NavigationOpenWidth : NavigationCompactWidth;

            SideNavigation.Width = width;
            ViewerNavigationLabel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            ExpressionsNavigationLabel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            PosesNavigationLabel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            SettingsNavigationLabel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ViewerNavigationItem_Click(object sender, RoutedEventArgs e)
        {
            await SelectInspectorViewAsync(InspectorView.Viewer);
        }

        private async void ExpressionsNavigationItem_Click(object sender, RoutedEventArgs e)
        {
            await SelectInspectorViewAsync(InspectorView.Expressions);
        }

        private async void PosesNavigationItem_Click(object sender, RoutedEventArgs e)
        {
            await SelectInspectorViewAsync(InspectorView.Poses);
        }

        private async Task SelectInspectorViewAsync(InspectorView view)
        {
            activeInspectorView = view;
            ViewerInspectorContent.Visibility = view == InspectorView.Viewer ? Visibility.Visible : Visibility.Collapsed;
            ExpressionsInspectorContent.Visibility = view == InspectorView.Expressions ? Visibility.Visible : Visibility.Collapsed;
            PosesInspectorContent.Visibility = view == InspectorView.Poses ? Visibility.Visible : Visibility.Collapsed;

            SetNavigationItemState(
                ViewerNavigationItem,
                ViewerNavigationIndicator,
                ViewerNavigationLabel,
                view == InspectorView.Viewer);
            SetNavigationItemState(
                ExpressionsNavigationItem,
                ExpressionsNavigationIndicator,
                ExpressionsNavigationLabel,
                view == InspectorView.Expressions);
            SetNavigationItemState(
                PosesNavigationItem,
                PosesNavigationIndicator,
                PosesNavigationLabel,
                view == InspectorView.Poses);

            if (view != InspectorView.Viewer && InspectorPane.Visibility != Visibility.Visible)
            {
                SetInspectorVisible(true);
            }

            await TrySetPoseModeAsync(view == InspectorView.Poses);
        }

        private void SetNavigationItemState(
            Button item,
            FrameworkElement indicator,
            TextBlock label,
            bool isSelected)
        {
            item.Background = isSelected ? selectedNavigationItemBackground : unselectedNavigationItemBackground;
            indicator.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            label.FontWeight = isSelected
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }

        private void InitializeSettingsControls()
        {
            AutoLoadLastVrmToggle.IsOn = settingsService.Current.AutoLoadLastVrm;
            RestoreWindowToggle.IsOn = settingsService.Current.RestoreWindowPlacement;
            LogLevelComboBox.SelectedIndex = (int)settingsService.Current.LogLevel;
            LanguageComboBox.SelectedIndex = (int)settingsService.Current.Language;
            var colorSchemeOptions = PreviewColorSchemeOption.CreateAll();
            ColorSchemeComboBox.ItemsSource = colorSchemeOptions;
            ColorSchemeComboBox.SelectedItem = colorSchemeOptions.First(
                option => option.Scheme == settingsService.Current.PreviewColorScheme);
            UpdateSettingsLocalization();
            isInitializingSettingsControls = false;
        }

        private void SystemSettingsCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsCategory(showAppearance: false);
        }

        private void AppearanceSettingsCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsCategory(showAppearance: true);
        }

        private void ShowSettingsCategory(bool showAppearance)
        {
            isAppearanceSettingsSelected = showAppearance;
            SystemSettingsContent.Visibility = showAppearance ? Visibility.Collapsed : Visibility.Visible;
            AppearanceSettingsContent.Visibility = showAppearance ? Visibility.Visible : Visibility.Collapsed;
            SystemSettingsCategoryIndicator.Visibility = showAppearance ? Visibility.Collapsed : Visibility.Visible;
            AppearanceSettingsCategoryIndicator.Visibility = showAppearance ? Visibility.Visible : Visibility.Collapsed;
            SystemSettingsCategoryButton.Background = showAppearance
                ? unselectedNavigationItemBackground
                : selectedNavigationItemBackground;
            AppearanceSettingsCategoryButton.Background = showAppearance
                ? selectedNavigationItemBackground
                : unselectedNavigationItemBackground;
            UpdateSettingsCategoryTitle();
        }

        private void UpdateSettingsLocalization()
        {
            var isJapanese = settingsService.Current.Language == AppLanguage.Japanese;
            SystemSettingsCategoryLabel.Text = isJapanese ? "システム" : "System";
            AppearanceSettingsCategoryLabel.Text = isJapanese ? "画面表示" : "Appearance";
            AutoLoadLastVrmLabel.Text = isJapanese
                ? "起動時に前回のVRMを自動読み込み"
                : "Automatically load the last VRM at startup";
            AutoLoadLastVrmDescription.Text = isJapanese
                ? "最後に読み込んだVRMの元ファイルを次回起動時に開きます。"
                : "Opens the original file of the last loaded VRM the next time the app starts.";
            RestoreWindowLabel.Text = isJapanese
                ? "前回のウィンドウ位置とサイズを復元"
                : "Restore the previous window position and size";
            RestoreWindowDescription.Text = isJapanese
                ? "次回起動時に、最後に閉じた位置とサイズへ戻します。"
                : "Restores the window to its last position and size the next time the app starts.";
            LogLevelLabel.Text = isJapanese ? "ログレベル" : "Log level";
            LanguageLabel.Text = isJapanese ? "言語" : "Language";
            LanguageDescription.Text = isJapanese
                ? "設定画面などの表示言語を切り替えます。"
                : "Changes the display language used in settings and other screens.";
            ColorSchemeLabel.Text = isJapanese ? "配色" : "Color scheme";
            ViewerNavigationLabel.Text = "Viewer";
            ExpressionsNavigationLabel.Text = InspectorLocalization.ExpressionsNavigationText;
            PosesNavigationLabel.Text = InspectorLocalization.PoseNavigationText;
            AutomationProperties.SetName(
                SystemSettingsCategoryButton,
                SystemSettingsCategoryLabel.Text);
            AutomationProperties.SetName(
                AppearanceSettingsCategoryButton,
                AppearanceSettingsCategoryLabel.Text);
            AutomationProperties.SetName(AutoLoadLastVrmToggle, AutoLoadLastVrmLabel.Text);
            AutomationProperties.SetName(RestoreWindowToggle, RestoreWindowLabel.Text);
            AutomationProperties.SetName(LogLevelComboBox, LogLevelLabel.Text);
            AutomationProperties.SetName(LanguageComboBox, LanguageLabel.Text);
            AutomationProperties.SetName(
                ColorSchemeComboBox,
                ColorSchemeLabel.Text);
            AutomationProperties.SetName(ViewerNavigationItem, ViewerNavigationLabel.Text);
            AutomationProperties.SetName(ExpressionsNavigationItem, ExpressionsNavigationLabel.Text);
            AutomationProperties.SetName(PosesNavigationItem, PosesNavigationLabel.Text);
            UpdateExpressionLocalization();
            UpdatePoseLocalization();
            UpdateSettingsCategoryTitle();
        }

        private void UpdateSettingsCategoryTitle()
        {
            SettingsCategoryTitle.Text = isAppearanceSettingsSelected
                ? AppearanceSettingsCategoryLabel.Text
                : SystemSettingsCategoryLabel.Text;
        }

        private void SettingsNavigationItem_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsModal();
        }

        private void OpenSettingsModal()
        {
            if (isSettingsModalOpen)
            {
                return;
            }

            isSettingsModalOpen = true;
            focusBeforeSettings = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as Control;
            SetWebViewInputEnabled(false);
            SettingsModalOverlay.Visibility = Visibility.Visible;
            SettingsCloseButton.Focus(FocusState.Programmatic);
        }

        private void CloseSettingsModal()
        {
            if (!isSettingsModalOpen)
            {
                return;
            }

            SettingsModalOverlay.Visibility = Visibility.Collapsed;
            isSettingsModalOpen = false;
            SetWebViewInputEnabled(!isExpressionPackOperationActive);
            if (focusBeforeSettings is { IsEnabled: true })
            {
                focusBeforeSettings.Focus(FocusState.Programmatic);
            }
            else
            {
                SettingsNavigationItem.Focus(FocusState.Programmatic);
            }

            focusBeforeSettings = null;
        }

        private void SettingsCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseSettingsModal();
        }

        private void SettingsModalOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                CloseSettingsModal();
            }
        }

        private void SettingsModalOverlay_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
        }

        private void SettingsModalOverlay_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
        }

        private void SetWebViewInputEnabled(bool isEnabled)
        {
            PreviewWebView.IsHitTestVisible = isEnabled;
            PreviewWebView.AllowDrop = isEnabled && isWebViewDropEnabled;
            if (PreviewWebView.CoreWebView2 is not null)
            {
                _ = UpdateViewerInputStateAsync(isEnabled);
            }
        }

        private async Task UpdateViewerInputStateAsync(bool isEnabled)
        {
            try
            {
                await InvokeViewerAsync("setInputEnabled", isEnabled ? "true" : "false");
            }
            catch (Exception exception)
            {
                await applicationLogService.LogErrorAsync(
                    currentVrmFilePath,
                    nameof(MainWindow),
                    nameof(UpdateViewerInputStateAsync),
                    exception.ToString());
            }
        }

        private async void AutoLoadLastVrmToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isInitializingSettingsControls)
            {
                return;
            }

            settingsService.Current.AutoLoadLastVrm = AutoLoadLastVrmToggle.IsOn;
            await SaveSettingsChangeAsync(nameof(AutoLoadLastVrmToggle_Toggled));
        }

        private async void RestoreWindowToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isInitializingSettingsControls)
            {
                return;
            }

            settingsService.Current.RestoreWindowPlacement = RestoreWindowToggle.IsOn;
            await SaveSettingsChangeAsync(nameof(RestoreWindowToggle_Toggled));
        }

        private async void LogLevelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isInitializingSettingsControls || LogLevelComboBox.SelectedIndex < 0)
            {
                return;
            }

            settingsService.Current.LogLevel = (AppLogLevel)LogLevelComboBox.SelectedIndex;
            applicationLogService.SetLogLevel(settingsService.Current.LogLevel);
            await SaveSettingsChangeAsync(nameof(LogLevelComboBox_SelectionChanged));
        }

        private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isInitializingSettingsControls || LanguageComboBox.SelectedIndex < 0)
            {
                return;
            }

            settingsService.Current.Language = (AppLanguage)LanguageComboBox.SelectedIndex;
            UpdateSettingsLocalization();
            RenderVrmInfo();
            await SaveSettingsChangeAsync(nameof(LanguageComboBox_SelectionChanged));
        }

        private async void ColorSchemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isInitializingSettingsControls ||
                ColorSchemeComboBox.SelectedItem is not PreviewColorSchemeOption option)
            {
                return;
            }

            settingsService.Current.PreviewColorScheme = option.Scheme;
            await ApplyPreviewColorSchemeAsync(nameof(ColorSchemeComboBox_SelectionChanged));
            await SaveSettingsChangeAsync(nameof(ColorSchemeComboBox_SelectionChanged));
        }

        private async Task ApplyPreviewColorSchemeAsync(string methodName)
        {
            if (PreviewWebView.CoreWebView2 is null)
            {
                return;
            }

            try
            {
                await InvokeViewerAsync(
                    "setColorScheme",
                    settingsService.Current.PreviewColorScheme.ToString());
            }
            catch (Exception exception)
            {
                await applicationLogService.LogErrorAsync(
                    currentVrmFilePath,
                    nameof(MainWindow),
                    methodName,
                    exception.ToString());
            }
        }

        private async Task SaveSettingsChangeAsync(string methodName)
        {
            try
            {
                await settingsService.SaveAsync();
            }
            catch (Exception exception)
            {
                await applicationLogService.LogErrorAsync(
                    string.Empty,
                    nameof(MainWindow),
                    methodName,
                    exception.ToString());
                await ShowSettingsSaveErrorAsync();
            }
        }

        private async Task ShowSettingsSaveErrorAsync()
        {
            if (isSettingsErrorDialogOpen)
            {
                return;
            }

            isSettingsErrorDialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "設定を保存できませんでした",
                    Content = "設定の保存先へ書き込めませんでした。詳細はログを確認してください。",
                    CloseButtonText = "閉じる",
                    DefaultButton = ContentDialogButton.Close
                };
                await dialog.ShowAsync();
            }
            finally
            {
                isSettingsErrorDialogOpen = false;
            }
        }

        private async Task ConfigureWebView2Async()
        {
            var userDataFolder = ApplicationPaths.WebView2UserDataPath;
            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                nameof(ConfigureWebView2Async),
                $"WebView2 configuration started. BaseDirectory={AppContext.BaseDirectory} ProcessPath={Environment.ProcessPath} WebRootPath={WebViewPageService.WebRootPath} ViewerExists={File.Exists(Path.Combine(WebViewPageService.WebRootPath, "viewer.html"))} BundleExists={File.Exists(Path.Combine(WebViewPageService.WebRootPath, "viewer-bundle.js"))} UserDataFolder={userDataFolder}");

            Directory.CreateDirectory(userDataFolder);
            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                nameof(ConfigureWebView2Async),
                "WebView2 user data directory created; creating CoreWebView2Environment.");

            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null);
            if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.UserDataFolder)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(userDataFolder)),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("WebView2 のユーザーデータ保存先が app/temp/webview2 以外に上書きされています。");
            }

            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                nameof(ConfigureWebView2Async),
                "CoreWebView2Environment created; ensuring CoreWebView2.");
            await PreviewWebView.EnsureCoreWebView2Async(environment);
            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                nameof(ConfigureWebView2Async),
                $"CoreWebView2 initialized. BrowserVersion={PreviewWebView.CoreWebView2.Environment.BrowserVersionString}");

            PreviewWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                ViewerHostName,
                WebViewPageService.WebRootPath,
                CoreWebView2HostResourceAccessKind.DenyCors);
            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                nameof(ConfigureWebView2Async),
                $"Virtual host mapping configured. Host={ViewerHostName} Folder={WebViewPageService.WebRootPath}");
            PreviewWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            PreviewWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            PreviewWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            PreviewWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            await ConfigureWebViewDropAsync();

            PreviewWebView.CoreWebView2.WebMessageReceived += PreviewWebView_WebMessageReceived;
            PreviewWebView.CoreWebView2.NavigationStarting += PreviewWebView_NavigationStarting;
            PreviewWebView.CoreWebView2.NavigationCompleted += PreviewWebView_NavigationCompleted;
            PreviewWebView.CoreWebView2.ProcessFailed += PreviewWebView_ProcessFailed;
            PreviewWebView.CoreWebView2.AddWebResourceRequestedFilter(
                "*",
                CoreWebView2WebResourceContext.All);
            PreviewWebView.CoreWebView2.WebResourceRequested += PreviewWebView_WebResourceRequested;

            await ConfigureConsoleLoggingAsync();
            await PreviewWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                WebViewDiagnosticBootstrapScript);
            await TryAutoLoadLastVrmAsync();
            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                nameof(ConfigureWebView2Async),
                "WebMessageReceived, navigation, process, resource, and console handlers registered; document bootstrap script registered. Navigation has not started yet.");
        }

        private async Task ConfigureWebViewDropAsync()
        {
            var valueBefore = PreviewWebView.AllowDrop;
            isWebViewDropEnabled = true;
            PreviewWebView.AllowDrop = !isSettingsModalOpen;
            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                nameof(ConfigureWebViewDropAsync),
                $"WebView UIElement.AllowDrop enabled. Before={valueBefore} After={PreviewWebView.AllowDrop}. Native drag/drop observers capture the source file path without handling the drop.");
        }

        private void PreviewWebView_DragEnter(object sender, DragEventArgs args)
        {
            if (isExpressionPackOperationActive)
            {
                args.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            activeDropSourceCapture = new DropSourceCapture(
                ++nextDropGeneration,
                CaptureDroppedVrmSourceAsync(args.DataView));
        }

        private void PreviewWebView_Drop(object sender, DragEventArgs args)
        {
            if (activeDropSourceCapture is not null)
            {
                activeDropSourceCapture.SourceTask = CaptureDroppedVrmSourceAsync(args.DataView);
            }
        }

        private async Task<CapturedVrmSource?> CaptureDroppedVrmSourceAsync(DataPackageView dataView)
        {
            try
            {
                if (!dataView.Contains(StandardDataFormats.StorageItems))
                {
                    return null;
                }

                var storageItems = await dataView.GetStorageItemsAsync();
                var file = storageItems.OfType<StorageFile>().FirstOrDefault();
                if (file is null)
                {
                    return null;
                }

                await using var source = await file.OpenStreamForReadAsync();
                using var ownedBytes = new MemoryStream();
                await source.CopyToAsync(ownedBytes);
                var bytes = ownedBytes.ToArray();
                var snapshot = await Task.Run(() => VrmSourceSnapshot.Create(bytes, file.Path));
                return new CapturedVrmSource(
                    file.Name,
                    file.Path,
                    snapshot);
            }
            catch (Exception exception)
            {
                await applicationLogService.LogErrorAsync(
                    string.Empty,
                    nameof(MainWindow),
                    nameof(CaptureDroppedVrmSourceAsync),
                    exception.ToString());
                return null;
            }
        }

        private async Task ConfigureConsoleLoggingAsync()
        {
            try
            {
                runtimeConsoleReceiver = PreviewWebView.CoreWebView2.GetDevToolsProtocolEventReceiver(
                    "Runtime.consoleAPICalled");
                runtimeConsoleReceiver.DevToolsProtocolEventReceived += RuntimeConsole_DevToolsProtocolEventReceived;
                logEntryReceiver = PreviewWebView.CoreWebView2.GetDevToolsProtocolEventReceiver(
                    "Log.entryAdded");
                logEntryReceiver.DevToolsProtocolEventReceived += LogEntry_DevToolsProtocolEventReceived;
                await PreviewWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
                await PreviewWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
                await applicationLogService.LogInfoAsync(
                    nameof(MainWindow),
                    nameof(ConfigureConsoleLoggingAsync),
                    "DevTools Protocol console logging enabled for Runtime.consoleAPICalled and Log.entryAdded.");
            }
            catch (Exception exception)
            {
                await applicationLogService.LogErrorAsync(
                    string.Empty,
                    nameof(MainWindow),
                    nameof(ConfigureConsoleLoggingAsync),
                    exception.ToString());
            }
        }

        private async void PreviewWebView_Loaded(object sender, RoutedEventArgs e)
        {
            PreviewWebView.Loaded -= PreviewWebView_Loaded;
            try
            {
                if (settingsService.LoadFailure is not null)
                {
                    await applicationLogService.LogWarningAsync(
                        nameof(SettingsService),
                        "Load",
                        $"Invalid or unreadable settings were ignored. {settingsService.LoadFailure}");
                }

                await ConfigureWebView2Async();
                var pageUri = new Uri(webViewPageService.GetViewerPageUri());
                await applicationLogService.LogInfoAsync(
                    nameof(MainWindow),
                    nameof(PreviewWebView_Loaded),
                    $"Starting WebView navigation after all handlers and the bootstrap script were registered. PageMode=Viewer Uri={pageUri}");
                PreviewWebView.Source = pageUri;
            }
            catch (Exception exception)
            {
                var filePath = autoLoadVrmFilePath;
                ResetAutoLoadState(clearHostMapping: true);
                await applicationLogService.LogErrorAsync(
                    filePath,
                    nameof(MainWindow),
                    nameof(PreviewWebView_Loaded),
                    exception.ToString());
                if (PreviewWebView.CoreWebView2 is null)
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = RootGrid.XamlRoot,
                        Title = "ビューアを初期化できませんでした",
                        Content = "Microsoft Edge WebView2 Evergreen Runtime がインストールされているか確認してください。導入手順は README、失敗の詳細は app/logs を参照してください。",
                        CloseButtonText = "閉じる"
                    };
                    await dialog.ShowAsync();
                    return;
                }

                var logStatusHtml = applicationLogService.LastWriteFailureType is null
                    ? "<p>ログ: &lt;EXE配置先&gt;\\app\\logs</p>"
                    : $"<p>ログ書き込みにも失敗しました（{System.Net.WebUtility.HtmlEncode(applicationLogService.LastWriteFailureType)}）。</p>";
                try
                {
                    PreviewWebView.NavigateToString(
                        $"<!doctype html><meta charset=\"utf-8\"><body style=\"background:#303236;color:white;font-family:Segoe UI;display:grid;place-items:center;height:100vh;margin:0;text-align:center\"><div><p>ビューアを初期化できませんでした（{System.Net.WebUtility.HtmlEncode(exception.GetType().Name)}）。</p>{logStatusHtml}</div></body>");
                }
                catch (Exception fallbackException)
                {
                    await applicationLogService.LogErrorAsync(
                        string.Empty,
                        nameof(MainWindow),
                        nameof(PreviewWebView_Loaded),
                        $"Fallback page navigation failed: {fallbackException}");
                }
            }
        }

        private async void PreviewWebView_NavigationStarting(
            CoreWebView2 sender,
            CoreWebView2NavigationStartingEventArgs args)
        {
            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                nameof(PreviewWebView_NavigationStarting),
                $"NavigationStarting Uri={args.Uri}");

            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Host, ViewerHostName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            args.Cancel = true;
            await applicationLogService.LogErrorAsync(
                string.Empty,
                nameof(MainWindow),
                nameof(PreviewWebView_NavigationStarting),
                $"Blocked navigation to {args.Uri}");
        }

        private async void PreviewWebView_NavigationCompleted(
            CoreWebView2 sender,
            CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess)
            {
                var filePath = autoLoadVrmFilePath;
                ResetAutoLoadState(clearHostMapping: true);
                await applicationLogService.LogErrorAsync(
                    filePath,
                    nameof(MainWindow),
                    nameof(PreviewWebView_NavigationCompleted),
                    $"Navigation failed. WebErrorStatus={args.WebErrorStatus} Source={PreviewWebView.Source}");
                return;
            }

            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                nameof(PreviewWebView_NavigationCompleted),
                $"NavigationCompleted IsSuccess={args.IsSuccess} WebErrorStatus={args.WebErrorStatus} Source={PreviewWebView.Source}");
        }

        private async void PreviewWebView_ProcessFailed(
            CoreWebView2 sender,
            CoreWebView2ProcessFailedEventArgs args)
        {
            var filePath = autoLoadVrmFilePath;
            ResetAutoLoadState(clearHostMapping: true);
            await applicationLogService.LogErrorAsync(
                filePath,
                nameof(MainWindow),
                nameof(PreviewWebView_ProcessFailed),
                $"ProcessFailedKind={args.ProcessFailedKind} Reason={args.Reason} ExitCode={args.ExitCode} Description={args.ProcessDescription}");
        }

        private async void RuntimeConsole_DevToolsProtocolEventReceived(
            CoreWebView2 sender,
            CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        {
            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                "Runtime.consoleAPICalled",
                args.ParameterObjectAsJson);
        }

        private async void LogEntry_DevToolsProtocolEventReceived(
            CoreWebView2 sender,
            CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        {
            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                "Log.entryAdded",
                args.ParameterObjectAsJson);
        }

        private void PreviewWebView_WebResourceRequested(
            CoreWebView2 sender,
            CoreWebView2WebResourceRequestedEventArgs args)
        {
            if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
            {
                if (string.Equals(uri.Host, AutoLoadHostName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (string.Equals(uri.Host, MemoryVrmHostName, StringComparison.OrdinalIgnoreCase))
                {
                    args.Response = CreateMemoryVrmResponse(sender, uri);
                    return;
                }

                if (string.Equals(uri.Host, ViewerHostName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(uri.Scheme, "blob", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(uri.Scheme, "data", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            LogBlockedWebResourceRequest(args.Request.Uri);
            args.Response = sender.Environment.CreateWebResourceResponse(
                null,
                403,
                "Forbidden",
                "Content-Type: text/plain");
        }

        private void LogBlockedWebResourceRequest(string uri)
        {
            if (blockedWebResourceWarningUris.Contains(uri))
            {
                return;
            }

            if (blockedWebResourceWarningUris.Count < MaximumBlockedWebResourceWarnings)
            {
                blockedWebResourceWarningUris.Add(uri);
                _ = applicationLogService.LogWarningAsync(
                    nameof(MainWindow),
                    "WebResourceRequestBlocked",
                    $"Blocked Uri={uri}");
                return;
            }

            if (hasLoggedBlockedWebResourceWarningSuppression)
            {
                return;
            }

            hasLoggedBlockedWebResourceWarningSuppression = true;
            _ = applicationLogService.LogWarningAsync(
                nameof(MainWindow),
                "WebResourceRequestBlocked",
                $"Additional blocked URI warnings are suppressed after {MaximumBlockedWebResourceWarnings} unique requests.");
        }

        private async void PreviewWebView_WebMessageReceived(
            CoreWebView2 sender,
            CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var sourceUri) ||
                    !string.Equals(sourceUri.Host, ViewerHostName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                using var message = JsonDocument.Parse(args.WebMessageAsJson);
                if (!TryGetString(message.RootElement, "type", out var messageType))
                {
                    return;
                }

                var isAutoLoadSuccess = string.Equals(messageType, "load-success", StringComparison.Ordinal) &&
                    TryGetString(message.RootElement, "token", out var successToken) &&
                    string.Equals(successToken, autoLoadToken, StringComparison.Ordinal);
                if (string.Equals(messageType, "load-success", StringComparison.Ordinal))
                {
                    await PromotePendingDropToCurrentAsync(message.RootElement);
                }

                if (!isAutoLoadSuccess &&
                    !string.Equals(messageType, "load-error", StringComparison.Ordinal) &&
                    !string.Equals(messageType, "diagnostic", StringComparison.Ordinal) &&
                    !string.Equals(messageType, "model-loading", StringComparison.Ordinal) &&
                    !string.Equals(messageType, "pose-ready", StringComparison.Ordinal) &&
                    !string.Equals(messageType, "pose-changed", StringComparison.Ordinal))
                {
                    await applicationLogService.LogInfoAsync(
                        nameof(MainWindow),
                        nameof(PreviewWebView_WebMessageReceived),
                        $"WebMessageReceived Type={messageType} Source={args.Source}");
                }

                switch (messageType)
                {
                    case "drop-request":
                        await HandleDropRequestAsync(message.RootElement);
                        break;
                    case "load-success":
                        HandleLoadSuccess(message.RootElement);
                        break;
                    case "load-error":
                        await HandleLoadErrorAsync(message.RootElement);
                        break;
                    case "viewer-ready":
                        await applicationLogService.LogInfoAsync(
                            nameof(MainWindow),
                            "viewer-ready",
                            "Three.js viewer initialization completed.");
                        await ApplyPreviewColorSchemeAsync("viewer-ready");
                        await TrySetPoseModeAsync(activeInspectorView == InspectorView.Poses);
                        await StartPreparedAutoLoadAsync();
                        break;
                    case "pose-ready":
                    case "pose-changed":
                        HandlePoseState(message.RootElement);
                        break;
                    case "model-loading":
                        HandlePoseModelLoading(message.RootElement);
                        break;
                    case "diagnostic":
                        await HandleDiagnosticMessageAsync(message.RootElement);
                        break;
                }
            }
            catch (Exception exception)
            {
                await applicationLogService.LogErrorAsync(
                    string.Empty,
                    nameof(MainWindow),
                    nameof(PreviewWebView_WebMessageReceived),
                    exception.ToString());
            }
        }

        private async Task HandleDiagnosticMessageAsync(JsonElement message)
        {
            _ = TryGetString(message, "stage", out var stage);
            _ = TryGetString(message, "detail", out var detail);
            await applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                string.IsNullOrWhiteSpace(stage) ? "JavaScriptDiagnostic" : stage,
                detail);
        }

        private async Task TryAutoLoadLastVrmAsync()
        {
            if (!settingsService.Current.AutoLoadLastVrm)
            {
                ResetAutoLoadState(clearHostMapping: true);
                return;
            }

            var filePath = settingsService.Current.LastVrmFilePath;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                ResetAutoLoadState(clearHostMapping: true);
                return;
            }

            try
            {
                await applicationLogService.LogInfoAsync(
                    nameof(MainWindow),
                    "AutoLoadStart",
                    $"FilePath={filePath}");
                if (!string.Equals(Path.GetExtension(filePath), ".vrm", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The saved file path does not have a .vrm extension.");
                }

                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException("The previously loaded VRM file is unavailable.", filePath);
                }

                var sourceBytes = await File.ReadAllBytesAsync(filePath);
                preparedAutoLoadSnapshot = await Task.Run(() => VrmSourceSnapshot.Create(sourceBytes, filePath));
                await applicationLogService.LogInfoAsync(
                    nameof(MainWindow),
                    "FileValidationSucceeded",
                    $"FilePath={filePath} Length={new FileInfo(filePath).Length}");

                autoLoadToken = $"auto-{Guid.NewGuid():N}";
                autoLoadVrmFilePath = filePath;
                preparedAutoLoadFileName = Path.GetFileName(filePath);
                preparedAutoLoadUrl = RegisterMemoryVrm(autoLoadToken, preparedAutoLoadSnapshot.CopyBytes());
                await applicationLogService.LogInfoAsync(
                    nameof(MainWindow),
                    "OriginalBytesCaptured",
                    $"FilePath={filePath} Length={preparedAutoLoadSnapshot.Length} Sha256={preparedAutoLoadSnapshot.Sha256}");
            }
            catch (Exception exception)
            {
                ResetAutoLoadState(clearHostMapping: true);
                autoLoadPreparationError = AutoLoadFailureMessage;
                await applicationLogService.LogErrorAsync(
                    filePath,
                    nameof(MainWindow),
                    nameof(TryAutoLoadLastVrmAsync),
                    exception.ToString());
            }
        }

        private async Task StartPreparedAutoLoadAsync()
        {
            if (!string.IsNullOrEmpty(autoLoadPreparationError))
            {
                var message = autoLoadPreparationError;
                ResetAutoLoadState(clearHostMapping: false);
                await TryShowAutoLoadErrorAsync(message);
                return;
            }

            if (string.IsNullOrEmpty(preparedAutoLoadUrl))
            {
                return;
            }

            try
            {
                await InvokeViewerAsync(
                    "loadExternalVrm",
                    preparedAutoLoadUrl,
                    preparedAutoLoadFileName,
                    autoLoadToken);
            }
            catch (Exception exception)
            {
                var filePath = autoLoadVrmFilePath;
                ResetAutoLoadState(clearHostMapping: true);
                await applicationLogService.LogErrorAsync(
                    filePath,
                    nameof(MainWindow),
                    nameof(StartPreparedAutoLoadAsync),
                    exception.ToString());
                await TryShowAutoLoadErrorAsync(AutoLoadFailureMessage);
            }
        }

        private async Task TryShowAutoLoadErrorAsync(string message)
        {
            try
            {
                await InvokeViewerAsync("showAutoLoadError", message);
            }
            catch (Exception exception)
            {
                await applicationLogService.LogWarningAsync(
                    nameof(MainWindow),
                    nameof(TryShowAutoLoadErrorAsync),
                    $"The user-facing auto-load error could not be displayed. {exception}");

                try
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = RootGrid.XamlRoot,
                        Title = "自動読み込みエラー",
                        Content = message,
                        CloseButtonText = "閉じる",
                        DefaultButton = ContentDialogButton.Close
                    };
                    await dialog.ShowAsync();
                }
                catch (Exception fallbackException)
                {
                    await applicationLogService.LogWarningAsync(
                        nameof(MainWindow),
                        nameof(TryShowAutoLoadErrorAsync),
                        $"The native fallback error dialog could not be displayed. {fallbackException}");
                }
            }
        }

        private void ResetAutoLoadState(bool clearHostMapping)
        {
            ReleaseMemoryVrm(autoLoadToken);
            if (clearHostMapping)
            {
                ClearAutoLoadHostMapping();
            }

            autoLoadToken = string.Empty;
            autoLoadVrmFilePath = string.Empty;
            preparedAutoLoadUrl = string.Empty;
            preparedAutoLoadFileName = string.Empty;
            autoLoadPreparationError = string.Empty;
            preparedAutoLoadSnapshot = null;
        }

        private void ClearAutoLoadHostMapping()
        {
            if (!isAutoLoadHostMappingActive)
            {
                return;
            }

            try
            {
                PreviewWebView.CoreWebView2?.ClearVirtualHostNameToFolderMapping(AutoLoadHostName);
                isAutoLoadHostMappingActive = false;
            }
            catch (Exception exception)
            {
                _ = applicationLogService.LogWarningAsync(
                    nameof(MainWindow),
                    nameof(ClearAutoLoadHostMapping),
                    exception.ToString());
            }
        }

        private async Task HandleDropRequestAsync(JsonElement message)
        {
            if (!TryGetString(message, "token", out var token) ||
                !TryGetString(message, "fileName", out var fileName))
            {
                return;
            }

            var pendingDrop = await RegisterPendingDropAsync(token, fileName);
            var isViewerLoading = message.TryGetProperty("isLoading", out var isLoadingProperty) &&
                isLoadingProperty.ValueKind == JsonValueKind.True;

            if (isExpressionPackOperationActive || isPoseOperationActive)
            {
                try
                {
                    await InvokeViewerAsync("rejectPendingLoad", token, "A file operation is active. Finish or cancel it before loading another VRM.");
                }
                finally
                {
                    ClearPendingDrop(pendingDrop);
                }
                return;
            }

            if (!string.Equals(Path.GetExtension(fileName), ".vrm", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await applicationLogService.LogErrorAsync(
                        pendingDrop.FilePathForLogging,
                        nameof(MainWindow),
                        nameof(HandleDropRequestAsync),
                        "The dropped file does not have a .vrm extension.");
                    await InvokeViewerAsync("rejectPendingLoad", token, "VRMファイル（.vrm）を指定してください。");
                }
                finally
                {
                    ClearPendingDrop(pendingDrop);
                }
                return;
            }

            if (pendingDrop.Snapshot is null)
            {
                try
                {
                    await applicationLogService.LogErrorAsync(
                        pendingDrop.FilePathForLogging,
                        nameof(MainWindow),
                        nameof(HandleDropRequestAsync),
                        "The native StorageFile bytes could not be captured; the load was rejected to preserve an owned original snapshot.");
                    await InvokeViewerAsync("rejectPendingLoad", token, "VRM bytes could not be captured safely. Please try the drop again.");
                }
                finally
                {
                    ClearPendingDrop(pendingDrop);
                }
                return;
            }

            if (isViewerLoading)
            {
                try
                {
                    await applicationLogService.LogErrorAsync(
                        pendingDrop.FilePathForLogging,
                        nameof(MainWindow),
                        nameof(HandleDropRequestAsync),
                        "Another VRM load is already in progress.");
                    await InvokeViewerAsync(
                        "rejectPendingLoad",
                        token,
                        "別のVRMを読み込み中です。完了後にもう一度お試しください。");
                }
                finally
                {
                    ClearPendingDrop(pendingDrop);
                }
                return;
            }

            if (isDropDecisionPending)
            {
                try
                {
                    await applicationLogService.LogErrorAsync(
                        pendingDrop.FilePathForLogging,
                        nameof(MainWindow),
                        nameof(HandleDropRequestAsync),
                        "Another replacement confirmation is already in progress.");
                    await InvokeViewerAsync("rejectPendingLoad", token, "別の確認処理が進行中です。");
                }
                finally
                {
                    ClearPendingDrop(pendingDrop);
                }
                return;
            }

            if (!isVrmLoaded)
            {
                await StartPendingLoadAsync(pendingDrop);
                return;
            }

            isDropDecisionPending = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "VRMの入れ替え",
                    Content = "現在のVRMを閉じて、新しいVRMを読み込みますか？",
                    PrimaryButtonText = "読み込む",
                    CloseButtonText = "キャンセル",
                    DefaultButton = ContentDialogButton.Primary
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    await StartPendingLoadAsync(pendingDrop);
                }
                else
                {
                    try
                    {
                        await InvokeViewerAsync("cancelPendingLoad", token);
                    }
                    finally
                    {
                        ClearPendingDrop(pendingDrop);
                    }
                }
            }
            catch (Exception exception)
            {
                try
                {
                    await applicationLogService.LogErrorAsync(
                        pendingDrop.FilePathForLogging,
                        nameof(MainWindow),
                        nameof(HandleDropRequestAsync),
                        exception.ToString());
                    await InvokeViewerAsync(
                        "rejectPendingLoad",
                        token,
                        "確認ダイアログを表示できませんでした。現在のVRMは維持されています。");
                }
                finally
                {
                    ClearPendingDrop(pendingDrop);
                }
            }
            finally
            {
                isDropDecisionPending = false;
            }
        }

        private void HandleLoadSuccess(JsonElement message)
        {
            if (!message.TryGetProperty("metadata", out var metadata))
            {
                return;
            }

            currentVrmMetadata = metadata.Clone();

            BoneTreeView.RootNodes.Clear();
            if (message.TryGetProperty("bones", out var bones) && bones.ValueKind == JsonValueKind.Array)
            {
                foreach (var bone in bones.EnumerateArray())
                {
                    BoneTreeView.RootNodes.Add(CreateBoneTreeNode(bone, true));
                }
            }

            if (BoneTreeView.RootNodes.Count == 0)
            {
                BoneTreeView.RootNodes.Add(new TreeViewNode { Content = "ボーン情報なし" });
            }

            UpdateExpressions(message, metadata);

            isVrmLoaded = true;
            RenderVrmInfo();
            UpdatePoseUiAvailability();
            HandleExpressionPackLoadSuccess(message);
        }

        private void RenderVrmInfo()
        {
            var isJapanese = settingsService.Current.Language == AppLanguage.Japanese;
            InspectorHeadingText.Text = isJapanese ? "VRM情報" : "VRM Info";
            InspectorStatusText.Text = isVrmLoaded
                ? (isJapanese ? "読み込み済み" : "Loaded")
                : (isJapanese ? "VRMが読み込まれていません" : "No avatar loaded");
            VrmInfoScrollViewer.Visibility = isVrmLoaded && currentVrmMetadata.HasValue
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!isVrmLoaded || !currentVrmMetadata.HasValue)
            {
                InspectorVersionMetadataRows.Children.Clear();
                InspectorVersionMetadataRows.RowDefinitions.Clear();
                return;
            }

            var metadata = currentVrmMetadata.Value;
            InspectorFileLabel.Text = isJapanese ? "ファイル" : "File";
            InspectorVrmVersionLabel.Text = isJapanese ? "VRMバージョン" : "VRM Version";
            InspectorTitleLabel.Text = isJapanese ? "モデルの名前" : "Title";
            InspectorAuthorsLabel.Text = isJapanese ? "作者名" : "Author(s)";
            InspectorModelVersionLabel.Text = isJapanese ? "モデルのバージョン" : "Model Version";
            InspectorBonesHeadingText.Text = isJapanese ? "ボーン" : "Bones";

            InspectorFileNameText.Text = GetDisplayValue(metadata, "fileName");
            InspectorVrmVersionText.Text = GetDisplayValue(metadata, "vrmVersion");
            InspectorTitleText.Text = GetDisplayValue(metadata, "title");
            InspectorAuthorsText.Text = metadata.TryGetProperty("authors", out var authors) &&
                authors.ValueKind == JsonValueKind.Array
                ? FormatMetadataArray(authors, ", ")
                : "-";
            InspectorModelVersionText.Text = GetDisplayValue(metadata, "modelVersion");
            UpdateVersionMetadata(metadata, isJapanese);
        }

        private void UpdateVersionMetadata(JsonElement metadata, bool isJapanese)
        {
            InspectorVersionMetadataRows.Children.Clear();
            InspectorVersionMetadataRows.RowDefinitions.Clear();

            if (!TryGetString(metadata, "metaVersion", out var metaVersion))
            {
                return;
            }

            var fields = metaVersion switch
            {
                "0" => Vrm0MetadataFields,
                "1" => Vrm1MetadataFields,
                _ => []
            };

            for (var rowIndex = 0; rowIndex < fields.Length; rowIndex++)
            {
                var field = fields[rowIndex];
                InspectorVersionMetadataRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = isJapanese ? field.JapaneseLabel : field.EnglishLabel,
                    Style = (Style)InspectorVersionMetadataRows.Resources["InspectorMetadataLabelStyle"]
                };
                AutomationProperties.SetAutomationId(label, $"MetadataLabel_{field.PropertyName}");
                Grid.SetRow(label, rowIndex);
                InspectorVersionMetadataRows.Children.Add(label);

                var value = new TextBlock
                {
                    Text = GetMetadataDisplayValue(metadata, field, isJapanese),
                    Style = (Style)InspectorVersionMetadataRows.Resources["InspectorMetadataValueStyle"]
                };
                AutomationProperties.SetAutomationId(value, $"MetadataValue_{field.PropertyName}");
                Grid.SetRow(value, rowIndex);
                Grid.SetColumn(value, 1);
                InspectorVersionMetadataRows.Children.Add(value);
            }
        }

        private static string GetMetadataDisplayValue(
            JsonElement metadata,
            MetadataFieldDefinition field,
            bool isJapanese)
        {
            if (!metadata.TryGetProperty(field.PropertyName, out var value))
            {
                return "-";
            }

            return field.Kind switch
            {
                MetadataValueKind.Permission when value.ValueKind == JsonValueKind.True => isJapanese ? "許可" : "Allowed",
                MetadataValueKind.Permission when value.ValueKind == JsonValueKind.False => isJapanese ? "不許可" : "Not Allowed",
                MetadataValueKind.Enum when value.ValueKind == JsonValueKind.String => FormatMetadataEnum(value.GetString(), isJapanese),
                MetadataValueKind.StringArray when value.ValueKind == JsonValueKind.Array => FormatMetadataArray(value, " | "),
                _ when value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString()!,
                _ => "-"
            };
        }

        private static string FormatMetadataEnum(string? value, bool isJapanese)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "-";
            }

            return MetadataEnumDisplayValues.TryGetValue(value, out var displayValue)
                ? (isJapanese ? displayValue.Japanese : displayValue.English)
                : value;
        }

        private static string FormatMetadataArray(JsonElement value, string separator)
        {
            var items = value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item));
            var displayValue = string.Join(separator, items);
            return string.IsNullOrWhiteSpace(displayValue) ? "-" : displayValue;
        }

        private void UpdateExpressions(JsonElement message, JsonElement metadata)
        {
            isVrm0ExpressionInspector = GetDisplayValue(metadata, "vrmVersion") == "VRM 0.x";
            ExpressionsListPanel.Children.Clear();
            expressionValueTexts.Clear();

            if (message.TryGetProperty("expressions", out var expressions) &&
                expressions.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var expression in expressions.EnumerateArray())
                {
                    if (!TryGetString(expression, "name", out var expressionName) ||
                        string.IsNullOrWhiteSpace(expressionName))
                    {
                        continue;
                    }

                    var value = expression.TryGetProperty("value", out var valueProperty) &&
                        valueProperty.TryGetDouble(out var parsedValue)
                        ? Math.Clamp(parsedValue, 0.0, 1.0)
                        : 0.0;
                    AddExpressionSlider(expressionName, value, index++);
                }
            }

            var hasExpressions = expressionValueTexts.Count > 0;
            ExpressionsEmptyStateText.Visibility = hasExpressions ? Visibility.Collapsed : Visibility.Visible;
            ExpressionsScrollViewer.Visibility = hasExpressions ? Visibility.Visible : Visibility.Collapsed;
            ResetExpressionsButton.IsEnabled = hasExpressions;
            UpdateExpressionLocalization();
            UpdateExpressionPackButtons();
        }

        private void AddExpressionSlider(string expressionName, double value, int index)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock
            {
                Text = expressionName,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var valueGrid = new Grid { ColumnSpacing = 8 };
            valueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            valueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            var slider = new Slider
            {
                Minimum = 0.0,
                Maximum = 1.0,
                SmallChange = 0.01,
                LargeChange = 0.1,
                StepFrequency = 0.01,
                Value = value,
                Tag = expressionName
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(slider, $"ExpressionSlider{index}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, expressionName);
            slider.ValueChanged += ExpressionSlider_ValueChanged;

            var valueText = new TextBlock
            {
                Width = 44,
                HorizontalTextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Text = value.ToString("F2")
            };
            Grid.SetColumn(valueText, 1);
            valueGrid.Children.Add(slider);
            valueGrid.Children.Add(valueText);
            panel.Children.Add(valueGrid);
            ExpressionsListPanel.Children.Add(panel);
            expressionValueTexts[slider] = valueText;
        }

        private async void ExpressionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (sender is not Slider slider || !expressionValueTexts.TryGetValue(slider, out var valueText))
            {
                return;
            }

            valueText.Text = e.NewValue.ToString("F2");
            if (isResettingExpressionSliders || slider.Tag is not string expressionName)
            {
                return;
            }

            try
            {
                await InvokeViewerAsync("setExpressionValue", expressionName, e.NewValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                await applicationLogService.LogErrorAsync(
                    currentVrmFilePath,
                    nameof(MainWindow),
                    nameof(ExpressionSlider_ValueChanged),
                    exception.ToString());
            }
        }

        private async void ResetExpressionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isVrmLoaded || expressionValueTexts.Count == 0)
            {
                return;
            }

            isResettingExpressionSliders = true;
            try
            {
                foreach (var slider in expressionValueTexts.Keys)
                {
                    slider.Value = 0.0;
                }
            }
            finally
            {
                isResettingExpressionSliders = false;
            }

            try
            {
                await InvokeViewerAsync("resetExpressionValues");
            }
            catch (Exception exception)
            {
                await applicationLogService.LogErrorAsync(
                    currentVrmFilePath,
                    nameof(MainWindow),
                    nameof(ResetExpressionsButton_Click),
                    exception.ToString());
            }
        }

        private async Task HandleLoadErrorAsync(JsonElement message)
        {
            _ = TryGetString(message, "token", out var token);
            _ = TryGetString(message, "fileName", out var fileName);
            _ = TryGetString(message, "className", out var className);
            _ = TryGetString(message, "methodName", out var methodName);
            _ = TryGetString(message, "detail", out var detail);

            _ = HandleExpressionPackLoadError(token);

            var pendingDrop = GetPendingDrop(token);
            var isAutoLoad = string.Equals(token, autoLoadToken, StringComparison.Ordinal);
            var logFilePath = isAutoLoad
                ? autoLoadVrmFilePath
                : pendingDrop?.FilePathForLogging ?? fileName;

            if (pendingDrop is not null)
            {
                ClearPendingDrop(pendingDrop);
            }

            if (isAutoLoad)
            {
                ResetAutoLoadState(clearHostMapping: true);
            }

            await applicationLogService.LogErrorAsync(
                logFilePath,
                className,
                methodName,
                detail);
        }

        private async Task<PendingDropState> RegisterPendingDropAsync(string token, string fileName)
        {
            var capture = activeDropSourceCapture;
            var generation = capture?.Generation ?? ++nextDropGeneration;
            var captured = capture is null
                ? null
                : await capture.SourceTask;
            if (ReferenceEquals(activeDropSourceCapture, capture))
            {
                activeDropSourceCapture = null;
            }

            var isVerified = string.Equals(
                captured?.FileName,
                fileName,
                StringComparison.OrdinalIgnoreCase);
            var pendingDrop = new PendingDropState(
                token,
                generation,
                fileName,
                isVerified ? captured?.FilePath ?? string.Empty : string.Empty,
                isVerified ? captured?.Snapshot : null);
            pendingDrops[token] = pendingDrop;
            return pendingDrop;
        }

        private async Task StartPendingLoadAsync(PendingDropState pendingDrop)
        {
            try
            {
                if (pendingDrop.Snapshot is null)
                {
                    throw new InvalidOperationException("The pending VRM has no C#-owned source snapshot.");
                }
                await InvokeViewerAsync("startPendingLoad", pendingDrop.Token);
            }
            catch
            {
                ClearPendingDrop(pendingDrop);
                throw;
            }
        }

        private async Task PromotePendingDropToCurrentAsync(JsonElement message)
        {
            if (!TryGetString(message, "token", out var token))
            {
                return;
            }

            if (importTransaction is not null &&
                string.Equals(token, importTransaction.ActiveLoadToken, StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(token, autoLoadToken, StringComparison.Ordinal))
            {
                currentVrmFilePath = autoLoadVrmFilePath;
                currentVrmSnapshot = preparedAutoLoadSnapshot;
                applicationLogService.SetCurrentVrmFilePath(currentVrmFilePath);
                ResetAutoLoadState(clearHostMapping: true);
                await applicationLogService.LogInfoAsync(
                    nameof(MainWindow),
                    "AutoLoadSucceeded",
                    $"FilePath={currentVrmFilePath}");
                return;
            }

            var pendingDrop = GetPendingDrop(token);
            if (pendingDrop is null)
            {
                return;
            }

            try
            {
                currentVrmSnapshot = pendingDrop.Snapshot;
                currentVrmFilePath = pendingDrop.FilePath;
                applicationLogService.SetCurrentVrmFilePath(currentVrmFilePath);
            }
            finally
            {
                ClearPendingDrop(pendingDrop);
            }
        }

        private PendingDropState? GetPendingDrop(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && pendingDrops.TryGetValue(token, out var pendingDrop)
                ? pendingDrop
                : null;
        }

        private void ClearPendingDrop(PendingDropState pendingDrop)
        {
            if (pendingDrops.TryGetValue(pendingDrop.Token, out var current) &&
                current.Generation == pendingDrop.Generation)
            {
                pendingDrops.Remove(pendingDrop.Token);
                ReleaseMemoryVrm(pendingDrop.Token);
            }
        }

        private sealed class DropSourceCapture(long generation, Task<CapturedVrmSource?> sourceTask)
        {
            public long Generation { get; } = generation;

            public Task<CapturedVrmSource?> SourceTask { get; set; } = sourceTask;
        }

        private sealed record CapturedVrmSource(string FileName, string FilePath, VrmSourceSnapshot Snapshot);

        private sealed class PendingDropState(
            string token,
            long generation,
            string fileName,
            string filePath,
            VrmSourceSnapshot? snapshot)
        {
            public string Token { get; } = token;

            public long Generation { get; } = generation;

            public string FilePath { get; } = filePath;

            public string FileName { get; } = fileName;

            public VrmSourceSnapshot? Snapshot { get; } = snapshot;

            public string FilePathForLogging => string.IsNullOrWhiteSpace(FilePath)
                ? FileName
                : FilePath;
        }

        private static TreeViewNode CreateBoneTreeNode(JsonElement bone, bool isRoot)
        {
            var node = new TreeViewNode
            {
                Content = GetDisplayValue(bone, "name"),
                IsExpanded = isRoot
            };

            if (bone.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    node.Children.Add(CreateBoneTreeNode(child, false));
                }
            }

            return node;
        }

        private async Task InvokeViewerAsync(string methodName, params string[] arguments)
        {
            var serializedArguments = string.Join(",", arguments.Select(
                argument => $"\"{JsonEncodedText.Encode(argument)}\""));
            await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.avatarRunway?.{methodName}({serializedArguments});");
        }

        private static string GetDisplayValue(JsonElement element, string propertyName)
        {
            return TryGetString(element, propertyName, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : "-";
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string value)
        {
            value = string.Empty;
            if (!element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString() ?? string.Empty;
            return true;
        }

        private enum MetadataValueKind
        {
            Text,
            StringArray,
            Permission,
            Enum
        }

        private enum InspectorView
        {
            Viewer,
            Expressions,
            Poses
        }

        private sealed record LocalizedText(string English, string Japanese);

        private sealed record MetadataFieldDefinition(
            string EnglishLabel,
            string JapaneseLabel,
            string PropertyName,
            MetadataValueKind Kind);

        private void InspectorPaneResizeGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointerPoint = e.GetCurrentPoint(RootGrid);
            if (!pointerPoint.Properties.IsLeftButtonPressed)
            {
                return;
            }

            e.Handled = true;
            if (!InspectorPaneResizeGrip.CapturePointer(e.Pointer))
            {
                return;
            }

            isInspectorPaneResizing = true;
            inspectorPaneResizePointerId = e.Pointer.PointerId;
            inspectorPaneResizeStartPointerX = pointerPoint.Position.X;
            inspectorPaneResizeStartWidth = InspectorPane.Width;
        }

        private void InspectorPaneResizeGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!isInspectorPaneResizing || e.Pointer.PointerId != inspectorPaneResizePointerId)
            {
                return;
            }

            var currentPointerX = e.GetCurrentPoint(RootGrid).Position.X;
            var requestedWidth = inspectorPaneResizeStartWidth +
                (inspectorPaneResizeStartPointerX - currentPointerX);
            InspectorPane.Width = Math.Clamp(
                requestedWidth,
                MinimumInspectorPaneWidth,
                MaximumInspectorPaneWidth);
            e.Handled = true;
        }

        private void InspectorPaneResizeGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            CompleteInspectorPaneResize(e, releasePointerCapture: true);
        }

        private void InspectorPaneResizeGrip_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            CompleteInspectorPaneResize(e, releasePointerCapture: true);
        }

        private void InspectorPaneResizeGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            CompleteInspectorPaneResize(e, releasePointerCapture: false);
        }

        private void CompleteInspectorPaneResize(PointerRoutedEventArgs e, bool releasePointerCapture)
        {
            if (!isInspectorPaneResizing || e.Pointer.PointerId != inspectorPaneResizePointerId)
            {
                return;
            }

            isInspectorPaneResizing = false;
            inspectorPaneResizePointerId = 0;
            if (releasePointerCapture)
            {
                InspectorPaneResizeGrip.ReleasePointerCapture(e.Pointer);
            }

            e.Handled = true;
        }

        private void InspectorToggleButton_Click(object sender, RoutedEventArgs e)
        {
            SetInspectorVisible(InspectorToggleButton.IsChecked == true);
        }

        private void SetInspectorVisible(bool isVisible)
        {
            if (isVisible)
            {
                InspectorPane.Visibility = Visibility.Visible;
                InspectorToggleButton.IsChecked = true;
                return;
            }

            InspectorPane.Visibility = Visibility.Collapsed;
            InspectorToggleButton.IsChecked = false;
        }
    }
}
