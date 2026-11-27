using System;
using System.IO;

namespace AvataRunway.Services
{
    public sealed class WebViewPageService
    {
        public static string WebRootPath => Path.Combine(ApplicationPaths.ApplicationDirectoryPath, "app", "web");

        public string GetViewerPageUri()
        {
            var viewerPath = Path.Combine(WebRootPath, "viewer.html");
            if (!File.Exists(viewerPath))
            {
                throw new FileNotFoundException($"Viewer HTML file not found at path: {viewerPath}");
            }

            var bundlePath = Path.Combine(WebRootPath, "viewer-bundle.js");
            if (!File.Exists(bundlePath))
            {
                throw new FileNotFoundException($"Viewer JavaScript bundle not found at path: {bundlePath}");
            }

            return "https://appassets.local/viewer.html";
        }
    }
}
