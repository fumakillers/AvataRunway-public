using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AvataRunway.Services;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AvataRunway
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private const uint MessageBoxIconError = 0x00000010;

        private Window? _window;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(
            IntPtr windowHandle,
            string text,
            string caption,
            uint type);

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                ApplicationPaths.EnsurePortableStorageWritable();
            }
            catch (Exception exception)
            {
                MessageBoxW(
                    IntPtr.Zero,
                    $"AvataRunwayの起動に必要なフォルダーを作成または書き込みできませんでした。\n\n" +
                    "AvataRunwayをユーザーが書き込み可能なフォルダーへ移動して再実行してください。\n\n" +
                    $"エラー: {exception.GetType().Name}\n{exception.Message}",
                    "AvataRunway - 起動エラー",
                    MessageBoxIconError);
                Exit();
                return;
            }

            _window = new MainWindow();
            _window.Activate();
        }
    }
}
