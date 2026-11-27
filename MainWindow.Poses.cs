using AvataRunway.Core;
using AvataRunway.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AvataRunway
{
    public sealed partial class MainWindow
    {
        private readonly PoseFileService poseFileService = new();
        private readonly List<string> poseBoneNames = [];
        private bool isUpdatingPoseControls;
        private bool isPoseOperationActive;
        private bool isPoseModelLoading;
        private string selectedPoseBone = string.Empty;
        private string selectedPoseHand = "left";
        private readonly HashSet<string> availablePoseHands = new(StringComparer.Ordinal);
        private readonly HashSet<string> availablePoseFingers = new(StringComparer.Ordinal);
        private bool isPoseFingerSpreadAvailable;
        private Task poseFingerCurlBeginTask = Task.CompletedTask;
        private Task poseFingerSpreadBeginTask = Task.CompletedTask;
        private string? poseStatusJapaneseTitle;
        private string? poseStatusEnglishTitle;
        private string? poseStatusJapaneseMessage;
        private string? poseStatusEnglishMessage;

        private async Task TrySetPoseModeAsync(bool enabled)
        {
            if (PreviewWebView.CoreWebView2 is null)
            {
                return;
            }

            try
            {
                await InvokeViewerAsync("setPoseMode", enabled ? "true" : "false");
            }
            catch (Exception exception)
            {
                await applicationLogService.LogErrorAsync(
                    currentVrmFilePath,
                    nameof(MainWindow),
                    nameof(TrySetPoseModeAsync),
                    exception.ToString());
            }
        }

        private void HandlePoseState(JsonElement message)
        {
            isUpdatingPoseControls = true;
            try
            {
                if (message.TryGetProperty("bones", out var bonesElement) &&
                    bonesElement.ValueKind == JsonValueKind.Array)
                {
                    var incomingBones = bonesElement.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Cast<string>()
                        .ToArray();
                    if (!poseBoneNames.SequenceEqual(incomingBones, StringComparer.Ordinal))
                    {
                        poseBoneNames.Clear();
                        poseBoneNames.AddRange(incomingBones);
                        PoseBoneComboBox.ItemsSource = null;
                        PoseBoneComboBox.ItemsSource = poseBoneNames;
                    }
                }

                if (TryGetString(message, "selectedBone", out var selectedBone))
                {
                    if (poseBoneNames.Contains(selectedBone, StringComparer.Ordinal))
                    {
                        selectedPoseBone = selectedBone;
                        PoseBoneComboBox.SelectedItem = selectedBone;
                        SelectedBoneNameText.Text = selectedBone;
                    }
                    else
                    {
                        selectedPoseBone = string.Empty;
                        PoseBoneComboBox.SelectedItem = null;
                        SelectedBoneNameText.Text = "-";
                    }
                }

                if (message.TryGetProperty("rotation", out var rotation) &&
                    rotation.ValueKind == JsonValueKind.Object &&
                    TryReadFiniteDouble(rotation, "x", out var x) &&
                    TryReadFiniteDouble(rotation, "y", out var y) &&
                    TryReadFiniteDouble(rotation, "z", out var z))
                {
                    PoseRotationXNumberBox.Value = x;
                    PoseRotationYNumberBox.Value = y;
                    PoseRotationZNumberBox.Value = z;
                }

                if (message.TryGetProperty("hands", out var hands) &&
                    hands.ValueKind == JsonValueKind.Object)
                {
                    UpdatePoseHandsState(hands);
                }
            }
            finally
            {
                isUpdatingPoseControls = false;
            }
            UpdatePoseUiAvailability();
        }

        private void UpdatePoseHandsState(JsonElement hands)
        {
            availablePoseHands.Clear();
            foreach (var side in new[] { "left", "right" })
            {
                if (hands.TryGetProperty(side, out var hand) &&
                    hand.ValueKind == JsonValueKind.Object &&
                    hand.TryGetProperty("available", out var available) &&
                    available.ValueKind == JsonValueKind.True)
                {
                    availablePoseHands.Add(side);
                }
            }

            if (TryGetString(hands, "selectedHand", out var selectedHand) &&
                availablePoseHands.Contains(selectedHand))
            {
                selectedPoseHand = selectedHand;
            }
            else if (!availablePoseHands.Contains(selectedPoseHand))
            {
                selectedPoseHand = availablePoseHands.Contains("left") ? "left" : "right";
            }

            LeftPoseHandButton.IsChecked = selectedPoseHand == "left";
            RightPoseHandButton.IsChecked = selectedPoseHand == "right";
            availablePoseFingers.Clear();
            isPoseFingerSpreadAvailable = false;
            if (!hands.TryGetProperty(selectedPoseHand, out var selectedHandState) ||
                selectedHandState.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (selectedHandState.TryGetProperty("fingers", out var fingers) &&
                fingers.ValueKind == JsonValueKind.Object)
            {
                foreach (var finger in new[] { "thumb", "index", "middle", "ring", "little" })
                {
                    var slider = GetFingerSlider(finger);
                    if (fingers.TryGetProperty(finger, out var state) &&
                        state.ValueKind == JsonValueKind.Object &&
                        state.TryGetProperty("available", out var available) &&
                        available.ValueKind == JsonValueKind.True &&
                        TryReadFiniteDouble(state, "value", out var value))
                    {
                        availablePoseFingers.Add(finger);
                        slider.Value = Math.Clamp(value, 0, 1);
                    }
                    else
                    {
                        slider.Value = 0;
                    }
                }
            }

            if (TryReadFiniteDouble(selectedHandState, "fingerSpread", out var fingerSpread))
            {
                FingerSpreadSlider.Value = Math.Clamp(fingerSpread, 0, 1);
            }
            isPoseFingerSpreadAvailable =
                selectedHandState.TryGetProperty("fingerSpreadAvailable", out var spreadAvailable) &&
                spreadAvailable.ValueKind == JsonValueKind.True;
        }

        private void HandlePoseModelLoading(JsonElement message)
        {
            isPoseModelLoading = message.TryGetProperty("isLoading", out var loading) &&
                loading.ValueKind == JsonValueKind.True;
            UpdatePoseUiAvailability();
        }

        private async void PoseBoneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isUpdatingPoseControls ||
                PoseBoneComboBox.SelectedItem is not string boneName ||
                !poseBoneNames.Contains(boneName, StringComparer.Ordinal))
            {
                return;
            }

            try
            {
                await InvokeViewerAsync("selectPoseBone", boneName);
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(PoseBoneComboBox_SelectionChanged), exception);
            }
        }

        private async void PoseRotationNumberBox_ValueChanged(
            object sender,
            NumberBoxValueChangedEventArgs args)
        {
            if (isUpdatingPoseControls ||
                string.IsNullOrWhiteSpace(selectedPoseBone) ||
                !double.IsFinite(PoseRotationXNumberBox.Value) ||
                !double.IsFinite(PoseRotationYNumberBox.Value) ||
                !double.IsFinite(PoseRotationZNumberBox.Value))
            {
                return;
            }

            try
            {
                await InvokeViewerAsync(
                    "setPoseBoneEuler",
                    selectedPoseBone,
                    PoseRotationXNumberBox.Value.ToString("R", CultureInfo.InvariantCulture),
                    PoseRotationYNumberBox.Value.ToString("R", CultureInfo.InvariantCulture),
                    PoseRotationZNumberBox.Value.ToString("R", CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(PoseRotationNumberBox_ValueChanged), exception);
            }
        }

        private async void ResetPoseBoneButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(selectedPoseBone))
            {
                return;
            }

            try
            {
                await InvokeViewerAsync("resetPoseBone", selectedPoseBone);
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(ResetPoseBoneButton_Click), exception);
            }
        }

        private async void ResetAllPoseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isVrmLoaded)
            {
                return;
            }

            try
            {
                await InvokeViewerAsync("resetPoseAll");
                ShowLocalizedPoseStatus(
                    "ポーズをリセットしました",
                    "Pose reset",
                    "すべてのボーンをニュートラル姿勢へ戻しました。",
                    "All bones were restored to their neutral rotations.",
                    InfoBarSeverity.Success);
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(ResetAllPoseButton_Click), exception);
            }
        }

        private async void PoseHandButton_Click(object sender, RoutedEventArgs e)
        {
            if (isUpdatingPoseControls ||
                sender is not FrameworkElement { Tag: string side } ||
                !availablePoseHands.Contains(side))
            {
                return;
            }

            selectedPoseHand = side;
            LeftPoseHandButton.IsChecked = side == "left";
            RightPoseHandButton.IsChecked = side == "right";
            try
            {
                await InvokeViewerAsync("selectPoseHand", side);
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(PoseHandButton_Click), exception);
            }
        }

        private void FingerCurlSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Slider { Tag: string finger } slider ||
                !availablePoseFingers.Contains(finger))
            {
                return;
            }
            slider.CapturePointer(e.Pointer);
            poseFingerCurlBeginTask = BeginPoseFingerInteractionAsync(finger);
        }

        private async Task BeginPoseFingerInteractionAsync(string finger)
        {
            try
            {
                await InvokeViewerAsync("beginFingerCurl", selectedPoseHand, finger);
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(BeginPoseFingerInteractionAsync), exception);
            }
        }

        private async void FingerCurlSlider_ValueChanged(
            object sender,
            RangeBaseValueChangedEventArgs e)
        {
            if (isUpdatingPoseControls ||
                sender is not Slider { Tag: string finger } ||
                !availablePoseFingers.Contains(finger) ||
                !double.IsFinite(e.NewValue))
            {
                return;
            }
            try
            {
                await poseFingerCurlBeginTask;
                await InvokeViewerAsync(
                    "setFingerCurl",
                    selectedPoseHand,
                    finger,
                    e.NewValue.ToString("R", CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(FingerCurlSlider_ValueChanged), exception);
            }
        }

        private async void FingerCurlSlider_PointerEnded(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Slider { Tag: string finger } slider)
            {
                return;
            }
            slider.ReleasePointerCapture(e.Pointer);
            try
            {
                await poseFingerCurlBeginTask;
                await InvokeViewerAsync("endFingerCurl", selectedPoseHand, finger);
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(FingerCurlSlider_PointerEnded), exception);
            }
        }

        private void FingerSpreadSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            FingerSpreadSlider.CapturePointer(e.Pointer);
            poseFingerSpreadBeginTask =
                InvokeViewerAsync("beginPoseFingerSpread", selectedPoseHand);
        }

        private async void FingerSpreadSlider_ValueChanged(
            object sender,
            RangeBaseValueChangedEventArgs e)
        {
            if (isUpdatingPoseControls || !double.IsFinite(e.NewValue))
            {
                return;
            }
            try
            {
                await poseFingerSpreadBeginTask;
                await InvokeViewerAsync(
                    "setPoseFingerSpread",
                    selectedPoseHand,
                    e.NewValue.ToString("R", CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(FingerSpreadSlider_ValueChanged), exception);
            }
        }

        private async void FingerSpreadSlider_PointerEnded(
            object sender,
            PointerRoutedEventArgs e)
        {
            FingerSpreadSlider.ReleasePointerCapture(e.Pointer);
            try
            {
                await poseFingerSpreadBeginTask;
                await InvokeViewerAsync("endPoseFingerSpread", selectedPoseHand);
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(FingerSpreadSlider_PointerEnded), exception);
            }
        }

        private async void CopyPoseFingersButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await InvokeViewerAsync("copyPoseFingers", selectedPoseHand);
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(CopyPoseFingersButton_Click), exception);
            }
        }

        private async void ResetPoseHandFingersButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await InvokeViewerAsync("resetPoseHandFingers", selectedPoseHand);
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(ResetPoseHandFingersButton_Click), exception);
            }
        }

        private Slider GetFingerSlider(string finger) => finger switch
        {
            "thumb" => ThumbCurlSlider,
            "index" => IndexCurlSlider,
            "middle" => MiddleCurlSlider,
            "ring" => RingCurlSlider,
            "little" => LittleCurlSlider,
            _ => throw new ArgumentOutOfRangeException(nameof(finger))
        };

        private async void ExportPoseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isVrmLoaded || isPoseOperationActive)
            {
                return;
            }

            SetPoseOperationBusy(true);
            try
            {
                var poseJson = await InvokeViewerResultAsync("getCurrentPose");
                var pose = poseFileService.Deserialize(Encoding.UTF8.GetBytes(poseJson));
                var outputBytes = poseFileService.Serialize(pose);
                var outputPath = await PickNewOutputPathAsync(
                    ".avpose",
                    $"{Path.GetFileNameWithoutExtension(GetCurrentVrmFileName())}-pose.avpose");
                if (outputPath is null)
                {
                    return;
                }

                await atomicFileWriter.WriteNewAsync(outputPath, outputBytes);
                ShowLocalizedPoseStatus(
                    "ポーズをエクスポートしました",
                    "Pose exported",
                    outputPath,
                    outputPath,
                    InfoBarSeverity.Success);
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(ExportPoseButton_Click), exception);
            }
            finally
            {
                SetPoseOperationBusy(false);
            }
        }

        private async void ImportPoseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isVrmLoaded || isPoseOperationActive)
            {
                return;
            }

            SetPoseOperationBusy(true);
            try
            {
                if (appWindow is null)
                {
                    throw new InvalidOperationException("The parent window is unavailable for the file picker.");
                }

                var picker = new FileOpenPicker(appWindow.Id);
                picker.FileTypeFilter.Add(".avpose");
                var file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    return;
                }

                // Parsing and validation finish before the viewer is allowed to mutate the pose.
                var pose = await poseFileService.LoadAsync(file.Path);
                var normalizedJson = Encoding.UTF8.GetString(poseFileService.Serialize(pose));
                var ignoredJson = await InvokeViewerResultAsync("applyPose", normalizedJson);
                using var ignoredDocument = JsonDocument.Parse(ignoredJson);
                var ignoredBones = ignoredDocument.RootElement.ValueKind == JsonValueKind.Array
                    ? ignoredDocument.RootElement.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Cast<string>()
                        .ToArray()
                    : [];

                if (ignoredBones.Length > 0)
                {
                    ShowLocalizedPoseStatus(
                        "未対応ボーンを無視しました",
                        "Unsupported bones ignored",
                        string.Join(", ", ignoredBones),
                        string.Join(", ", ignoredBones),
                        InfoBarSeverity.Warning);
                }
                else
                {
                    ShowLocalizedPoseStatus(
                        "ポーズをインポートしました",
                        "Pose imported",
                        file.Path,
                        file.Path,
                        InfoBarSeverity.Success);
                }
            }
            catch (Exception exception)
            {
                await ReportPoseErrorAsync(nameof(ImportPoseButton_Click), exception);
            }
            finally
            {
                SetPoseOperationBusy(false);
            }
        }

        private async Task<string> InvokeViewerResultAsync(string methodName, params string[] arguments)
        {
            if (PreviewWebView.CoreWebView2 is null)
            {
                throw new InvalidOperationException("The VRM viewer is not ready.");
            }
            var serializedArguments = string.Join(",", arguments.Select(
                argument => $"\"{JsonEncodedText.Encode(argument)}\""));
            var result = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.avatarRunway?.{methodName}({serializedArguments})");
            if (string.IsNullOrWhiteSpace(result) || string.Equals(result, "null", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The VRM viewer returned no pose data.");
            }
            return result;
        }

        private void UpdatePoseUiAvailability()
        {
            var hasPose = isVrmLoaded && poseBoneNames.Count > 0;
            var canEdit = hasPose && !isPoseOperationActive && !isPoseModelLoading;
            PosesEmptyStateText.Visibility = hasPose ? Visibility.Collapsed : Visibility.Visible;
            PoseEditorScrollViewer.Visibility = hasPose ? Visibility.Visible : Visibility.Collapsed;
            ImportPoseButton.IsEnabled = canEdit;
            ExportPoseButton.IsEnabled = canEdit;
            ResetAllPoseButton.IsEnabled = canEdit;
            ResetPoseBoneButton.IsEnabled = canEdit &&
                !string.IsNullOrWhiteSpace(selectedPoseBone);
            PoseBoneComboBox.IsEnabled = canEdit;
            PoseRotationXNumberBox.IsEnabled = canEdit;
            PoseRotationYNumberBox.IsEnabled = canEdit;
            PoseRotationZNumberBox.IsEnabled = canEdit;
            PoseHandsSection.Visibility = hasPose && availablePoseHands.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            LeftPoseHandButton.IsEnabled = canEdit && availablePoseHands.Contains("left");
            RightPoseHandButton.IsEnabled = canEdit && availablePoseHands.Contains("right");
            foreach (var finger in new[] { "thumb", "index", "middle", "ring", "little" })
            {
                GetFingerSlider(finger).IsEnabled = canEdit &&
                    availablePoseFingers.Contains(finger);
            }
            FingerSpreadSlider.IsEnabled = canEdit && isPoseFingerSpreadAvailable;
            CopyPoseFingersButton.IsEnabled = canEdit && availablePoseHands.Count > 1;
            ResetPoseHandFingersButton.IsEnabled = canEdit && availablePoseFingers.Count > 0;
        }

        private void SetPoseOperationBusy(bool isBusy)
        {
            isPoseOperationActive = isBusy;
            UpdatePoseUiAvailability();
        }

        private void UpdatePoseLocalization()
        {
            var commands = InspectorLocalization.Pose(settingsService.Current.Language);
            PosesHeadingText.Text = commands.Heading;
            ImportPoseButton.Content = commands.Import;
            ExportPoseButton.Content = commands.Export;
            ResetAllPoseButton.Content = commands.ResetAll;
            SelectedBoneLabel.Text = IsJapanese ? "選択中のボーン" : "Selected Bone";
            PoseBoneComboBox.Header = IsJapanese ? "Humanoidボーン" : "Humanoid Bone";
            RotationHeadingText.Text = IsJapanese ? "回転（XYZ、度）" : "Rotation (XYZ degrees)";
            ResetPoseBoneButton.Content = IsJapanese ? "ボーンをリセット" : "Reset Bone";
            PosesEmptyStateText.Text = commands.NoAvatar;
            PoseHandsHeadingText.Text = IsJapanese ? "手" : "Hands";
            LeftPoseHandButton.Content = IsJapanese ? "左手" : "Left Hand";
            RightPoseHandButton.Content = IsJapanese ? "右手" : "Right Hand";
            FingerSpreadSlider.Header = IsJapanese ? "指の間隔" : "Finger Spread";
            CopyPoseFingersButton.Content = IsJapanese
                ? "指だけを反対の手へコピー"
                : "Copy fingers to other hand";
            ResetPoseHandFingersButton.Content = IsJapanese ? "手の指をリセット" : "Reset fingers";

            AutomationProperties.SetName(ImportPoseButton, ImportPoseButton.Content.ToString());
            AutomationProperties.SetName(ExportPoseButton, ExportPoseButton.Content.ToString());
            AutomationProperties.SetName(ResetAllPoseButton, ResetAllPoseButton.Content.ToString());
            AutomationProperties.SetName(PoseBoneComboBox, PoseBoneComboBox.Header.ToString());
            AutomationProperties.SetName(ResetPoseBoneButton, ResetPoseBoneButton.Content.ToString());
            AutomationProperties.SetName(LeftPoseHandButton, LeftPoseHandButton.Content.ToString());
            AutomationProperties.SetName(RightPoseHandButton, RightPoseHandButton.Content.ToString());
            AutomationProperties.SetName(
                FingerSpreadSlider,
                FingerSpreadSlider.Header.ToString());
            AutomationProperties.SetName(CopyPoseFingersButton, CopyPoseFingersButton.Content.ToString());
            AutomationProperties.SetName(
                ResetPoseHandFingersButton,
                ResetPoseHandFingersButton.Content.ToString());
            var fingerNames = IsJapanese
                ? new[] { "親指", "人差し指", "中指", "薬指", "小指" }
                : new[] { "Thumb", "Index finger", "Middle finger", "Ring finger", "Little finger" };
            var fingerKeys = new[] { "thumb", "index", "middle", "ring", "little" };
            for (var index = 0; index < fingerKeys.Length; index++)
            {
                AutomationProperties.SetName(GetFingerSlider(fingerKeys[index]), fingerNames[index]);
            }

            if (PoseInfoBar.IsOpen &&
                poseStatusJapaneseTitle is not null &&
                poseStatusEnglishTitle is not null &&
                poseStatusJapaneseMessage is not null &&
                poseStatusEnglishMessage is not null)
            {
                PoseInfoBar.Title = InspectorLocalization.Select(
                    settingsService.Current.Language,
                    poseStatusJapaneseTitle,
                    poseStatusEnglishTitle);
                PoseInfoBar.Message = InspectorLocalization.Select(
                    settingsService.Current.Language,
                    poseStatusJapaneseMessage,
                    poseStatusEnglishMessage);
            }
        }

        private async Task ReportPoseErrorAsync(string methodName, Exception exception)
        {
            ShowLocalizedPoseStatus(
                "ポーズの処理に失敗しました",
                "Pose operation failed",
                exception.Message,
                exception.Message,
                InfoBarSeverity.Error);
            await applicationLogService.LogErrorAsync(
                currentVrmFilePath,
                nameof(MainWindow),
                methodName,
                exception.ToString());
        }

        private void ShowLocalizedPoseStatus(
            string japaneseTitle,
            string englishTitle,
            string japaneseMessage,
            string englishMessage,
            InfoBarSeverity severity)
        {
            poseStatusJapaneseTitle = japaneseTitle;
            poseStatusEnglishTitle = englishTitle;
            poseStatusJapaneseMessage = japaneseMessage;
            poseStatusEnglishMessage = englishMessage;
            PoseInfoBar.Title = InspectorLocalization.Select(
                settingsService.Current.Language,
                japaneseTitle,
                englishTitle);
            PoseInfoBar.Message = InspectorLocalization.Select(
                settingsService.Current.Language,
                japaneseMessage,
                englishMessage);
            PoseInfoBar.Severity = severity;
            PoseInfoBar.IsOpen = true;
        }

        private bool IsJapanese => settingsService.Current.Language == AppLanguage.Japanese;

        private static bool TryReadFiniteDouble(JsonElement element, string propertyName, out double value)
        {
            value = 0;
            return element.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.Number &&
                property.TryGetDouble(out value) &&
                double.IsFinite(value);
        }
    }
}
