using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace DisplaySwitcher;

internal static class BundledComponents
{
    private const string ResourceName = "DisplaySwitcher.Components.zip";
    private static readonly Lazy<string> DirectoryPath = new(Extract, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string Root => DirectoryPath.Value;

    private static string Extract()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (resource is null)
            return AppContext.BaseDirectory;

        string version = Convert.ToHexString(SHA256.HashData(resource));
        resource.Position = 0;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DisplaySwitcher", "components", version);
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        using var gate = new Mutex(false, @"Local\DisplaySwitcher.Components." + version);
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
            throw new TimeoutException("正在准备内置组件，请稍后重新启动。");
        try
        {
            using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("内置组件路径无效。");
                if (entry.Name.Length == 0)
                    continue;
                using var content = entry.Open();
                byte[] expected = SHA256.HashData(content);
                if (File.Exists(destination))
                {
                    using var existing = File.OpenRead(destination);
                    if (CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(existing)))
                        continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var source = entry.Open())
                    using (var output = File.Create(temporary))
                        source.CopyTo(output);
                    File.Move(temporary, destination, true);
                }
                finally { File.Delete(temporary); }
            }
            if (!File.Exists(Path.Combine(root, "FanWorker.exe")) ||
                !File.Exists(Path.Combine(root, "XunleiWorker.exe")) ||
                !File.Exists(Path.Combine(root, "MultiMonitorTool", "MultiMonitorTool.exe")))
                throw new InvalidDataException("内置运行组件不完整，请重新下载程序。");
            return root;
        }
        finally { gate.ReleaseMutex(); }
    }
}
