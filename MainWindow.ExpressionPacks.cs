using AvataRunway.Core;
using AvataRunway.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Windows.Storage.Pickers;

namespace AvataRunway
{
    public sealed partial class MainWindow
    {
        private readonly ExpressionPackService expressionPackService = new();
        private readonly Vrm1ExpressionImportService vrm1ExpressionImportService = new();
        private readonly Vrm0ExpressionImportService vrm0ExpressionImportService = new();
        private readonly AtomicFileWriter atomicFileWriter = new();
        private static readonly HashSet<string> Vrm1ReservedExpressionNames = new(StringComparer.Ordinal)
        {
            "happy", "angry", "sad", "relaxed", "surprised",
            "aa", "ih", "ou", "ee", "oh",
            "blink", "blinkLeft", "blinkRight",
            "lookUp", "lookDown", "lookLeft", "lookRight", "neutral"
        };
        private readonly Dictionary<string, byte[]> memoryVrmResources = new(StringComparer.Ordinal);
        private VrmSourceSnapshot? currentVrmSnapshot;
        private VrmSourceSnapshot? preparedAutoLoadSnapshot;
        private ExpressionPackImportTransaction? importTransaction;
        private bool isExpressionPackOperationActive;
        private bool isVrm0ExpressionInspector;
        private string? expressionStatusJapaneseTitle;
        private string? expressionStatusEnglishTitle;
        private string? expressionStatusJapaneseMessage;
        private string? expressionStatusEnglishMessage;

        private string RegisterMemoryVrm(string token, byte[] bytes)
        {
            memoryVrmResources[token] = bytes;
            return $"https://{MemoryVrmHostName}/{Uri.EscapeDataString(token)}.vrm";
        }

        private CoreWebView2WebResourceResponse CreateMemoryVrmResponse(CoreWebView2 sender, Uri uri)
        {
            var fileName = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            var token = fileName.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^4]
                : string.Empty;
            if (!memoryVrmResources.TryGetValue(token, out var bytes))
            {
                _ = applicationLogService.LogWarningAsync(
                    nameof(MainWindow),
                    nameof(CreateMemoryVrmResponse),
                    $"Memory VRM resource was not found. Token={token}");
                return sender.Environment.CreateWebResourceResponse(
                    null,
                    404,
                    "Not Found",
                    "Content-Type: text/plain");
            }

            _ = applicationLogService.LogInfoAsync(
                nameof(MainWindow),
                nameof(CreateMemoryVrmResponse),
                $"Serving memory VRM resource. Token={token} Length={bytes.Length}");

            return sender.Environment.CreateWebResourceResponse(
                WindowsRuntimeStreamExtensions.AsRandomAccessStream(new MemoryStream(bytes, writable: false)),
                200,
                "OK",
                "Content-Type: model/gltf-binary\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: https://appassets.local");
        }

        private void ReleaseMemoryVrm(string token)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                memoryVrmResources.Remove(token);
            }
        }

        private async void ExportExpressionPackButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentVrmSnapshot is null || importTransaction is not null)
            {
                return;
            }

            BeginExpressionPackOperation(
                "表情パックをエクスポートしています",
                "Exporting Expression Pack",
                "表情と新しい出力ファイルを選択してください。",
                "Select expressions and a new output file.");
            try
            {
                var document = await Task.Run(() => GlbDocument.Parse(currentVrmSnapshot.CopyBytes()));
                var choices = await Task.Run(() => GetExportChoices(document));
                var selectedNames = await ShowExportSelectionDialogAsync(choices);
                if (selectedNames is null || selectedNames.Count == 0)
                {
                    return;
                }

                var outputPath = await PickNewOutputPathAsync(
                    ".avexp",
                    $"{Path.GetFileNameWithoutExtension(GetCurrentVrmFileName())}-expressions.avexp");
                if (outputPath is null)
                {
                    return;
                }

                var outputBytes = await Task.Run(() =>
                {
                    using var output = new MemoryStream();
                    if (IsVrm1(document))
                    {
                        expressionPackService.ExportVrm1Custom(document, selectedNames, output);
                    }
                    else
                    {
                        expressionPackService.ExportVrm0Unknown(document, selectedNames, output);
                    }
                    return output.ToArray();
                });
                await atomicFileWriter.WriteNewAsync(outputPath, outputBytes);
                ShowLocalizedExpressionPackStatus(
                    "表情パックをエクスポートしました",
                    "Expression Pack exported",
                    outputPath,
                    outputPath,
                    InfoBarSeverity.Success);
            }
            catch (Exception exception)
            {
                await ReportExpressionPackErrorAsync(nameof(ExportExpressionPackButton_Click), exception);
            }
            finally
            {
                EndExpressionPackOperationIfNoPreview();
            }
        }

        private async void ImportExpressionPackButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentVrmSnapshot is null || importTransaction is not null)
            {
                return;
            }

            BeginExpressionPackOperation(
                "表情パックをインポートしています",
                "Importing Expression Pack",
                "表情パックを選択して解析します。",
                "Select and analyze an Expression Pack.");
            try
            {
                if (appWindow is null)
                {
                    throw new InvalidOperationException(Ui(
                        "ファイル選択に必要な親ウィンドウを利用できません。",
                        "The parent window is unavailable for the file picker."));
                }
                var picker = new FileOpenPicker(appWindow.Id);
                picker.FileTypeFilter.Add(".avexp");
                var file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    return;
                }

                var pack = await Task.Run(() =>
                {
                    using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return expressionPackService.Read(stream);
                });
                var (target, analysis) = await Task.Run(() =>
                {
                    var parsedTarget = GlbDocument.Parse(currentVrmSnapshot.CopyBytes());
                    return (parsedTarget, AnalyzeImport(pack, parsedTarget));
                });
                var selection = await ShowImportPlanDialogAsync(analysis);
                if (selection is null || selection.Names.Count == 0)
                {
                    return;
                }

                var candidateSnapshot = await Task.Run(() =>
                {
                    var candidate = analysis.Generation == VrmGeneration.Vrm1
                        ? vrm1ExpressionImportService.ImportNewExpressions(
                            pack,
                            target,
                            selection.GeometryDifferencesConfirmed,
                            selection.Names)
                        : vrm0ExpressionImportService.ImportNewExpressions(
                            pack,
                            target,
                            selection.GeometryDifferencesConfirmed,
                            selection.Names);
                    return VrmSourceSnapshot.Create(candidate.Write());
                });
                importTransaction = new ExpressionPackImportTransaction(
                    currentVrmSnapshot,
                    candidateSnapshot,
                    GetCurrentVrmFileName(),
                    selection.Names);
                SetExpressionPackBusyState(true);
                importTransaction.ActiveLoadToken = $"pack-preview-{Guid.NewGuid():N}";
                var url = RegisterMemoryVrm(importTransaction.ActiveLoadToken, candidateSnapshot.CopyBytes());
                await InvokeViewerAsync(
                    "loadExternalVrm",
                    url,
                    BuildCandidateFileName(importTransaction.OriginalFileName),
                    importTransaction.ActiveLoadToken,
                    Ui(
                        "インポートしたVRM候補を読み込めませんでした。元の対象VRMは変更されていません。",
                        "The imported VRM candidate could not be loaded. The original Target VRM is unchanged."));
            }
            catch (Exception exception)
            {
                AbortImportTransactionWithoutReload();
                await ReportExpressionPackErrorAsync(nameof(ImportExpressionPackButton_Click), exception);
            }
            finally
            {
                EndExpressionPackOperationIfNoPreview();
            }
        }

        private ImportAnalysis AnalyzeImport(LoadedExpressionPack pack, GlbDocument target)
        {
            if (IsVrm1(target) && pack.Manifest.Source.VrmGeneration == "1.0")
            {
                var plan = vrm1ExpressionImportService.Analyze(pack, target);
                return new ImportAnalysis(VrmGeneration.Vrm1, plan.Expressions);
            }
            if (IsVrm0(target) && pack.Manifest.Source.VrmGeneration == "0.x")
            {
                var plan = vrm0ExpressionImportService.Analyze(pack, target);
                return new ImportAnalysis(VrmGeneration.Vrm0, plan.Expressions);
            }
            throw new InvalidVrmDataException("MVP only supports VRM 1.0 to VRM 1.0 and VRM 0.x to VRM 0.x imports.");
        }

        private async void SaveExpressionPackPreviewButton_Click(object sender, RoutedEventArgs e)
        {
            var transaction = importTransaction;
            if (transaction is null || !transaction.PreviewLoaded)
            {
                return;
            }

            try
            {
                var outputPath = await PickNewOutputPathAsync(
                    ".vrm",
                    BuildCandidateFileName(transaction.OriginalFileName));
                if (outputPath is null)
                {
                    return;
                }
                var candidateBytes = transaction.CandidateSnapshot.CopyBytes();
                var savedSnapshot = await Task.Run(() => VrmSourceSnapshot.Create(candidateBytes, outputPath));
                await atomicFileWriter.WriteNewAsync(outputPath, candidateBytes);
                PromoteSavedExpressionPackCandidate(savedSnapshot, outputPath);
                FinishImportTransaction();
                ShowLocalizedExpressionPackStatus(
                    "インポートしたVRMを保存しました",
                    "Imported VRM saved",
                    outputPath,
                    outputPath,
                    InfoBarSeverity.Success);
            }
            catch (Exception exception)
            {
                await ReportExpressionPackErrorAsync(nameof(SaveExpressionPackPreviewButton_Click), exception);
            }
        }

        private async void CancelExpressionPackPreviewButton_Click(object sender, RoutedEventArgs e)
        {
            var transaction = importTransaction;
            if (transaction is null)
            {
                return;
            }

            try
            {
                transaction.IsRestoringOriginal = true;
                transaction.ActiveLoadToken = $"pack-cancel-{Guid.NewGuid():N}";
                var url = RegisterMemoryVrm(transaction.ActiveLoadToken, transaction.OriginalSnapshot.CopyBytes());
                await InvokeViewerAsync(
                    "loadExternalVrm",
                    url,
                    string.IsNullOrWhiteSpace(transaction.OriginalFileName) ? "original.vrm" : transaction.OriginalFileName,
                    transaction.ActiveLoadToken,
                    Ui(
                        "元の対象VRMをViewerへ復元できませんでした。保存済みファイルは変更されていません。",
                        "The original Target VRM could not be restored in the Viewer. The saved files are unchanged."));
            }
            catch (Exception exception)
            {
                await ReportExpressionPackErrorAsync(nameof(CancelExpressionPackPreviewButton_Click), exception);
            }
        }

        private void HandleExpressionPackLoadSuccess(JsonElement message)
        {
            if (importTransaction is null ||
                !TryGetString(message, "token", out var token) ||
                !string.Equals(token, importTransaction.ActiveLoadToken, StringComparison.Ordinal))
            {
                ExpressionPackInfoBar.IsOpen = false;
                ExpressionPackPreviewActions.Visibility = Visibility.Collapsed;
                UpdateExpressionPackButtons();
                return;
            }

            ReleaseMemoryVrm(token);
            if (importTransaction.IsRestoringOriginal)
            {
                FinishImportTransaction();
                ShowLocalizedExpressionPackStatus(
                    "表情パックのプレビューをキャンセルしました",
                    "Expression Pack preview cancelled",
                    "元の対象VRMを復元しました。",
                    "The original Target VRM was restored.",
                    InfoBarSeverity.Informational);
                return;
            }

            importTransaction.PreviewLoaded = true;
            ShowLocalizedExpressionPackStatus(
                "表情パックのプレビュー",
                "Expression Pack preview",
                $"{importTransaction.SelectedNames.Count}件の新しい表情をメモリ上で適用しました。［名前を付けて保存］で新しいVRMを作成し、［キャンセル］で元の対象VRMを復元します。",
                $"{importTransaction.SelectedNames.Count} new expression(s) applied in memory. Save As creates a new VRM; Cancel restores the original Target.",
                InfoBarSeverity.Warning);
            ExpressionPackPreviewActions.Visibility = Visibility.Visible;
            SaveExpressionPackPreviewButton.IsEnabled = true;
            CancelExpressionPackPreviewButton.IsEnabled = true;
        }

        private bool HandleExpressionPackLoadError(string token)
        {
            if (importTransaction is null ||
                !string.Equals(token, importTransaction.ActiveLoadToken, StringComparison.Ordinal))
            {
                return false;
            }
            ReleaseMemoryVrm(token);
            if (!importTransaction.PreviewLoaded)
            {
                AbortImportTransactionWithoutReload();
            }
            return true;
        }

        private void SetExpressionPackBusyState(bool isBusy)
        {
            isExpressionPackOperationActive = isBusy;
            SetWebViewInputEnabled(!isBusy && !isSettingsModalOpen);
            ExportExpressionPackButton.IsEnabled = !isBusy && isVrmLoaded && currentVrmSnapshot is not null;
            ImportExpressionPackButton.IsEnabled = !isBusy && isVrmLoaded && currentVrmSnapshot is not null;
            ExpressionPackPreviewActions.Visibility = Visibility.Collapsed;
            ExpressionPackInfoBar.IsOpen = isBusy;
            if (isBusy)
            {
                ShowLocalizedExpressionPackStatus(
                    "プレビューを準備しています",
                    "Preparing preview",
                    "インポート候補をViewerで検証しています。",
                    "The imported candidate is being validated by the Viewer.",
                    InfoBarSeverity.Informational);
            }
        }

        private void FinishImportTransaction()
        {
            if (importTransaction is not null)
            {
                ReleaseMemoryVrm(importTransaction.ActiveLoadToken);
            }
            importTransaction = null;
            isExpressionPackOperationActive = false;
            ExpressionPackPreviewActions.Visibility = Visibility.Collapsed;
            SetWebViewInputEnabled(!isSettingsModalOpen);
            UpdateExpressionPackButtons();
        }

        private void AbortImportTransactionWithoutReload()
        {
            if (importTransaction is not null)
            {
                ReleaseMemoryVrm(importTransaction.ActiveLoadToken);
            }
            importTransaction = null;
            isExpressionPackOperationActive = false;
            ExpressionPackPreviewActions.Visibility = Visibility.Collapsed;
            SetWebViewInputEnabled(!isSettingsModalOpen);
            UpdateExpressionPackButtons();
        }

        private void UpdateExpressionPackButtons()
        {
            var enabled = isVrmLoaded && currentVrmSnapshot is not null &&
                importTransaction is null && !isExpressionPackOperationActive;
            ExportExpressionPackButton.IsEnabled = enabled;
            ImportExpressionPackButton.IsEnabled = enabled;
        }

        private void BeginExpressionPackOperation(
            string japaneseTitle,
            string englishTitle,
            string japaneseMessage,
            string englishMessage)
        {
            isExpressionPackOperationActive = true;
            SetWebViewInputEnabled(false);
            ExportExpressionPackButton.IsEnabled = false;
            ImportExpressionPackButton.IsEnabled = false;
            ShowLocalizedExpressionPackStatus(
                japaneseTitle,
                englishTitle,
                japaneseMessage,
                englishMessage,
                InfoBarSeverity.Informational);
        }

        private void EndExpressionPackOperationIfNoPreview()
        {
            if (importTransaction is not null)
            {
                return;
            }
            isExpressionPackOperationActive = false;
            SetWebViewInputEnabled(!isSettingsModalOpen);
            UpdateExpressionPackButtons();
            if (ExpressionPackInfoBar.Severity == InfoBarSeverity.Informational &&
                ExpressionPackPreviewActions.Visibility == Visibility.Collapsed)
            {
                ExpressionPackInfoBar.IsOpen = false;
            }
        }

        private IReadOnlyList<ExportChoice> GetExportChoices(GlbDocument document)
        {
            var result = new List<ExportChoice>();
            if (IsVrm1(document))
            {
                var expressions = document.Root["extensions"]?["VRMC_vrm"]?["expressions"]?.AsObject();
                foreach (var pair in expressions?["custom"]?.AsObject() ?? new JsonObject())
                {
                    var expression = pair.Value?.AsObject();
                    var reason = GetVrm1UnsupportedReason(pair.Key, expression);
                    result.Add(new ExportChoice(pair.Key, "Custom", reason is null, reason ?? string.Empty));
                }
                foreach (var pair in expressions?["preset"]?.AsObject() ?? new JsonObject())
                {
                    result.Add(new ExportChoice(
                        pair.Key,
                        "Preset",
                        false,
                        Ui("MVPではカスタム表情のみエクスポートできます。", "MVP exports custom expressions only.")));
                }
            }
            else if (IsVrm0(document))
            {
                var groups = document.Root["extensions"]?["VRM"]?["blendShapeMaster"]?["blendShapeGroups"]?.AsArray();
                foreach (var group in groups?.OfType<JsonObject>() ?? [])
                {
                    var name = group["name"]?.GetValue<string>() ?? "(unnamed)";
                    var isUnknown = string.Equals(group["presetName"]?.GetValue<string>(), "unknown", StringComparison.OrdinalIgnoreCase);
                    var hasMaterialValues = group["materialValues"] is JsonArray materialValues && materialValues.Count > 0;
                    var hasBinds = group["binds"] is JsonArray binds && binds.Count > 0;
                    var supported = isUnknown && !hasMaterialValues && hasBinds;
                    var reason = !isUnknown
                        ? Ui("MVPではUnknown BlendShapeClipのみエクスポートできます。", "MVP exports Unknown BlendShapeClips only.")
                        : hasMaterialValues
                            ? Ui("MVPではMaterial Valueバインドをサポートしていません。", "Material Value binds are not supported by the MVP.")
                            : !hasBinds ? Ui("Morph Targetバインドがありません。", "No Morph Target binds.") : string.Empty;
                    result.Add(new ExportChoice(name, isUnknown ? "Unknown" : "Preset", supported, reason));
                }
            }
            else
            {
                throw new InvalidVrmDataException("The loaded GLB is not a supported VRM 0.x or VRM 1.0 file.");
            }
            return result;
        }

        private string? GetVrm1UnsupportedReason(string name, JsonObject? expression)
        {
            if (Vrm1ReservedExpressionNames.Contains(name))
            {
                return Ui(
                    "VRM 1.0のプリセット名は予約済みのため、カスタム表情としてエクスポートできません。",
                    "VRM 1.0 preset names are reserved and cannot be exported as custom expressions.");
            }
            if (expression is null)
            {
                return Ui("表情の定義が不正です。", "The expression definition is invalid.");
            }
            if (expression["materialColorBinds"] is JsonArray materialBinds && materialBinds.Count > 0)
            {
                return Ui("MVPではMaterial Colorバインドをサポートしていません。", "Material Color binds are not supported by the MVP.");
            }
            if (expression["textureTransformBinds"] is JsonArray textureBinds && textureBinds.Count > 0)
            {
                return Ui("MVPではTexture Transformバインドをサポートしていません。", "Texture Transform binds are not supported by the MVP.");
            }
            if (expression["morphTargetBinds"] is not JsonArray morphBinds || morphBinds.Count == 0)
            {
                return Ui("Morph Targetバインドがありません。", "No Morph Target binds.");
            }
            return null;
        }

        private async Task<IReadOnlyList<string>?> ShowExportSelectionDialogAsync(IReadOnlyList<ExportChoice> choices)
        {
            var checkBoxes = new List<CheckBox>();
            var list = new ListView
            {
                MaxHeight = 420,
                SelectionMode = ListViewSelectionMode.None
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(list, "ExpressionPackExportList");
            var choiceIndex = 0;
            foreach (var choice in choices)
            {
                var checkBox = new CheckBox
                {
                    Content = string.IsNullOrEmpty(choice.Reason)
                        ? $"{choice.Name} ({choice.Kind})"
                        : $"{choice.Name} ({choice.Kind}) — {choice.Reason}",
                    IsEnabled = choice.IsSupported,
                    IsChecked = choice.IsSupported,
                    Tag = choice.Name
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(checkBox, $"ExpressionPackExportChoice{choiceIndex++}");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(checkBox, $"{choice.Name}, {choice.Kind}");
                checkBoxes.Add(checkBox);
                list.Items.Add(checkBox);
            }
            if (choices.Count == 0)
            {
                list.Items.Add(new TextBlock
                {
                    Text = Ui("利用できる表情がありません。", "No expressions are available."),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = Ui("表情パックのエクスポート", "Export Expression Pack"),
                Content = list,
                PrimaryButtonText = Ui("エクスポート", "Export"),
                CloseButtonText = Ui("キャンセル", "Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = choices.Any(choice => choice.IsSupported)
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }
            return checkBoxes
                .Where(checkBox => checkBox.IsEnabled && checkBox.IsChecked == true)
                .Select(checkBox => (string)checkBox.Tag)
                .ToArray();
        }

        private async Task<ImportSelection?> ShowImportPlanDialogAsync(ImportAnalysis analysis)
        {
            var newChecks = new List<(CheckBox Check, ExpressionImportItem Item)>();
            var panel = new StackPanel { Spacing = 8 };
            var compatibilityText = new TextBlock
            {
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(compatibilityText);
            var results = new ListView
            {
                MaxHeight = 360,
                SelectionMode = ListViewSelectionMode.None
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(results, "ExpressionPackImportResults");
            var resultIndex = 0;
            foreach (var item in analysis.Expressions)
            {
                var check = new CheckBox
                {
                    Content = string.IsNullOrWhiteSpace(item.Reason)
                        ? $"{item.Name} — {FormatImportStatus(item.Status)}"
                        : $"{item.Name} — {FormatImportStatus(item.Status)}: {FormatImportReason(item.Reason)}",
                    IsEnabled = item.Status == ExpressionImportStatus.New,
                    IsChecked = item.Status == ExpressionImportStatus.New,
                    Tag = item.Name
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(check, $"ExpressionPackImportChoice{resultIndex++}");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, $"{item.Name}, {item.Status}");
                if (item.Status == ExpressionImportStatus.New)
                {
                    newChecks.Add((check, item));
                }
                results.Items.Add(check);
            }
            panel.Children.Add(results);

            CheckBox? geometryConfirmation = null;
            if (newChecks.Any(entry => entry.Item.Compatibility == MeshCompatibilityGrade.CompatibleWithGeometryDifferences))
            {
                geometryConfirmation = new CheckBox
                {
                    Content = Ui(
                        "POSITIONは異なりますが、トポロジー、インデックス、UV、スキンの識別情報が一致していることを確認しました。",
                        "I understand that POSITION differs, while topology, indices, UV, and skin identity match."),
                    IsChecked = false
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(geometryConfirmation, "ExpressionPackGeometryConfirmation");
                panel.Children.Add(geometryConfirmation);
            }
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = Ui("表情パックのインポート", "Import Expression Pack"),
                Content = panel,
                PrimaryButtonText = Ui("プレビューを適用", "Apply Preview"),
                CloseButtonText = Ui("キャンセル", "Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false
            };

            void UpdateSelectionState()
            {
                var selectedItems = newChecks
                    .Where(entry => entry.Check.IsChecked == true)
                    .Select(entry => entry.Item)
                    .ToArray();
                if (selectedItems.Length == 0)
                {
                    compatibilityText.Text = Ui("新しい表情が選択されていません。", "No new expressions selected.");
                    if (geometryConfirmation is not null)
                    {
                        geometryConfirmation.Visibility = Visibility.Collapsed;
                    }
                    dialog.IsPrimaryButtonEnabled = false;
                    return;
                }

                var selectedCompatibility = selectedItems
                    .Select(item => item.Compatibility)
                    .Aggregate(MeshCompatibilityGrade.Exact, MinCompatibility);
                compatibilityText.Text = Ui(
                    $"選択したメッシュの互換性：{FormatCompatibility(selectedCompatibility)}",
                    $"Selected mesh compatibility: {FormatCompatibility(selectedCompatibility)}");
                var requiresGeometryConfirmation = selectedCompatibility == MeshCompatibilityGrade.CompatibleWithGeometryDifferences;
                if (geometryConfirmation is not null)
                {
                    geometryConfirmation.Visibility = requiresGeometryConfirmation ? Visibility.Visible : Visibility.Collapsed;
                }
                dialog.IsPrimaryButtonEnabled = selectedCompatibility != MeshCompatibilityGrade.Incompatible &&
                    (!requiresGeometryConfirmation || geometryConfirmation?.IsChecked == true);
            }

            foreach (var entry in newChecks)
            {
                entry.Check.Checked += (_, _) => UpdateSelectionState();
                entry.Check.Unchecked += (_, _) => UpdateSelectionState();
            }
            if (geometryConfirmation is not null)
            {
                geometryConfirmation.Checked += (_, _) => UpdateSelectionState();
                geometryConfirmation.Unchecked += (_, _) => UpdateSelectionState();
            }
            UpdateSelectionState();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }
            var names = newChecks
                .Where(entry => entry.Check.IsChecked == true)
                .Select(entry => (string)entry.Check.Tag)
                .ToArray();
            return new ImportSelection(names, geometryConfirmation?.IsChecked == true || geometryConfirmation is null);
        }

        private async Task<string?> PickNewOutputPathAsync(string extension, string suggestedFileName)
        {
            if (appWindow is null)
            {
                throw new InvalidOperationException(Ui(
                    "フォルダー選択に必要な親ウィンドウを利用できません。",
                    "The parent window is unavailable for the folder picker."));
            }
            var folderPicker = new FolderPicker(appWindow.Id);
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder is null)
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(folder.Path) || !Path.IsPathRooted(folder.Path) || !Directory.Exists(folder.Path))
            {
                throw new IOException(Ui(
                    "選択した出力フォルダーから書き込み可能なファイルシステムのパスを取得できません。",
                    "The selected output folder does not expose a writable filesystem path."));
            }

            var fileNameBox = new TextBox
            {
                Header = Ui("新しいファイル名", "New file name"),
                Text = EnsureExtension(string.IsNullOrWhiteSpace(suggestedFileName) ? $"avatar{extension}" : suggestedFileName, extension)
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(fileNameBox, "ExpressionPackOutputFileName");
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = Ui("名前を付けて保存（新規ファイルのみ）", "Save As (new file only)"),
                Content = fileNameBox,
                PrimaryButtonText = Ui("保存", "Save"),
                CloseButtonText = Ui("キャンセル", "Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }

            var fileName = EnsureExtension(fileNameBox.Text.Trim(), extension);
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            {
                throw new IOException(Ui("出力ファイル名が不正です。", "The output file name is invalid."));
            }
            var path = Path.Combine(folder.Path, fileName);
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new IOException(Ui(
                    "選択した出力先は既に存在します。名前を付けて保存ではファイルを上書きしません。",
                    "The selected output path already exists. Save As never overwrites any file."));
            }
            return path;
        }

        private async Task ReportExpressionPackErrorAsync(string methodName, Exception exception)
        {
            ShowLocalizedExpressionPackStatus(
                "表情パックの処理に失敗しました",
                "Expression Pack operation failed",
                exception.Message,
                exception.Message,
                InfoBarSeverity.Error);
            await applicationLogService.LogErrorAsync(currentVrmFilePath, nameof(MainWindow), methodName, exception.ToString());
        }

        private void ShowLocalizedExpressionPackStatus(
            string japaneseTitle,
            string englishTitle,
            string japaneseMessage,
            string englishMessage,
            InfoBarSeverity severity)
        {
            expressionStatusJapaneseTitle = japaneseTitle;
            expressionStatusEnglishTitle = englishTitle;
            expressionStatusJapaneseMessage = japaneseMessage;
            expressionStatusEnglishMessage = englishMessage;
            ExpressionPackInfoBar.Title = Ui(japaneseTitle, englishTitle);
            ExpressionPackInfoBar.Message = Ui(japaneseMessage, englishMessage);
            ExpressionPackInfoBar.Severity = severity;
            ExpressionPackInfoBar.IsOpen = true;
        }

        private void UpdateExpressionLocalization()
        {
            var commands = InspectorLocalization.Expressions(
                settingsService.Current.Language,
                isVrm0ExpressionInspector);
            ExpressionsHeadingText.Text = commands.Heading;
            ImportExpressionPackButton.Content = commands.Import;
            ExportExpressionPackButton.Content = commands.Export;
            ResetExpressionsButton.Content = commands.ResetAll;
            ExpressionsEmptyStateText.Text = isVrmLoaded ? commands.Empty : commands.NoAvatar;
            SaveExpressionPackPreviewButton.Content = Ui("名前を付けて保存", "Save As");
            CancelExpressionPackPreviewButton.Content = Ui("キャンセル", "Cancel");

            AutomationProperties.SetName(ImportExpressionPackButton, ImportExpressionPackButton.Content.ToString());
            AutomationProperties.SetName(ExportExpressionPackButton, ExportExpressionPackButton.Content.ToString());
            AutomationProperties.SetName(ResetExpressionsButton, ResetExpressionsButton.Content.ToString());
            AutomationProperties.SetName(
                SaveExpressionPackPreviewButton,
                SaveExpressionPackPreviewButton.Content.ToString());
            AutomationProperties.SetName(
                CancelExpressionPackPreviewButton,
                CancelExpressionPackPreviewButton.Content.ToString());

            if (ExpressionPackInfoBar.IsOpen &&
                expressionStatusJapaneseTitle is not null &&
                expressionStatusEnglishTitle is not null &&
                expressionStatusJapaneseMessage is not null &&
                expressionStatusEnglishMessage is not null)
            {
                ExpressionPackInfoBar.Title = Ui(
                    expressionStatusJapaneseTitle,
                    expressionStatusEnglishTitle);
                ExpressionPackInfoBar.Message = Ui(
                    expressionStatusJapaneseMessage,
                    expressionStatusEnglishMessage);
            }
        }

        private string Ui(string japanese, string english) =>
            InspectorLocalization.Select(settingsService.Current.Language, japanese, english);

        private static bool IsVrm1(GlbDocument document) => document.Root["extensions"]?["VRMC_vrm"] is JsonObject;
        private static bool IsVrm0(GlbDocument document) => document.Root["extensions"]?["VRM"] is JsonObject;
        private static string EnsureExtension(string fileName, string extension) =>
            fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + extension;
        private static string BuildCandidateFileName(string originalFileName) =>
            $"{Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(originalFileName) ? "avatar" : originalFileName)}-expressions.vrm";
        private void PromoteSavedExpressionPackCandidate(VrmSourceSnapshot savedSnapshot, string outputPath)
        {
            currentVrmSnapshot = savedSnapshot;
            currentVrmFilePath = outputPath;
            InspectorFileNameText.Text = Path.GetFileName(outputPath);
            applicationLogService.SetCurrentVrmFilePath(outputPath);
        }
        private string GetCurrentVrmFileName() => !string.IsNullOrWhiteSpace(currentVrmFilePath)
            ? Path.GetFileName(currentVrmFilePath)
            : string.IsNullOrWhiteSpace(InspectorFileNameText.Text) || InspectorFileNameText.Text == "-"
                ? "avatar.vrm"
                : InspectorFileNameText.Text;
        private string FormatCompatibility(MeshCompatibilityGrade compatibility) => compatibility switch
        {
            MeshCompatibilityGrade.Exact => Ui("完全一致", "Exact"),
            MeshCompatibilityGrade.CompatibleWithGeometryDifferences => Ui(
                "形状差ありで互換（確認が必要）",
                "Compatible with geometry differences (confirmation required)"),
            _ => Ui("非互換", "Incompatible")
        };
        private string FormatImportStatus(ExpressionImportStatus status) => status switch
        {
            ExpressionImportStatus.New => Ui("新規", "New"),
            ExpressionImportStatus.AlreadyExists => Ui("既存", "Already exists"),
            ExpressionImportStatus.NameConflict => Ui("名前の競合", "Name conflict"),
            ExpressionImportStatus.Incompatible => Ui("非互換", "Incompatible"),
            _ => Ui("未対応", "Unsupported")
        };
        private string FormatImportReason(string reason) => reason switch
        {
            "Only VRM 0.x Unknown BlendShapeClips are supported." =>
                Ui("VRM 0.xのUnknown BlendShapeClipのみ対応しています。", reason),
            "Only VRM 1.0 custom expressions are supported." =>
                Ui("VRM 1.0のカスタム表情のみ対応しています。", reason),
            "The target mesh identity could not be verified safely or the mesh mapping is not one-to-one." =>
                Ui("対象メッシュの同一性を安全に確認できないか、メッシュの対応が1対1ではありません。", reason),
            "The target node mapping is ambiguous." =>
                Ui("対象ノードの対応が曖昧です。", reason),
            "The target mesh is controlled by a Morph Weight Animation." =>
                Ui("対象メッシュはMorph Weight Animationで制御されています。", reason),
            "The target primitive is missing a required base POSITION, NORMAL, or TANGENT attribute." =>
                Ui("対象プリミティブに必要なPOSITION、NORMAL、またはTANGENT属性がありません。", reason),
            "The same expression and Morph Target data already exist." =>
                Ui("同じ表情とMorph Targetデータが既に存在します。", reason),
            "The same Unknown BlendShapeClip and Morph Target data already exist." =>
                Ui("同じUnknown BlendShapeClipとMorph Targetデータが既に存在します。", reason),
            "An expression with the same name has different content." =>
                Ui("同名の表情に異なる内容があります。", reason),
            "A BlendShapeClip with the same name has different content." =>
                Ui("同名のBlendShapeClipに異なる内容があります。", reason),
            "VRM 1.0 preset expression names are reserved and cannot be used by custom expressions." =>
                Ui("VRM 1.0のプリセット表情名は予約済みで、カスタム表情には使用できません。", reason),
            "A preset expression with the same reserved name already exists." =>
                Ui("同じ予約名のプリセット表情が既に存在します。", reason),
            _ => reason
        };
        private static MeshCompatibilityGrade MinCompatibility(MeshCompatibilityGrade left, MeshCompatibilityGrade right) =>
            (MeshCompatibilityGrade)Math.Min((int)left, (int)right);

        private sealed record ExportChoice(string Name, string Kind, bool IsSupported, string Reason);
        private sealed record ImportAnalysis(VrmGeneration Generation, IReadOnlyList<ExpressionImportItem> Expressions);
        private sealed record ImportSelection(IReadOnlyList<string> Names, bool GeometryDifferencesConfirmed);
        private enum VrmGeneration { Vrm0, Vrm1 }

        private sealed class ExpressionPackImportTransaction(
            VrmSourceSnapshot originalSnapshot,
            VrmSourceSnapshot candidateSnapshot,
            string originalFileName,
            IReadOnlyList<string> selectedNames)
        {
            public VrmSourceSnapshot OriginalSnapshot { get; } = originalSnapshot;
            public VrmSourceSnapshot CandidateSnapshot { get; } = candidateSnapshot;
            public string OriginalFileName { get; } = originalFileName;
            public IReadOnlyList<string> SelectedNames { get; } = selectedNames;
            public string ActiveLoadToken { get; set; } = string.Empty;
            public bool PreviewLoaded { get; set; }
            public bool IsRestoringOriginal { get; set; }
        }
    }
}
