using System.Diagnostics;
using System.IO;
using System.Net.Http;
using KeySwitch.App.Localization;
using KeySwitch.Core.Updates;
using Microsoft.Win32;

namespace KeySwitch.App.Services;

internal sealed record InstalledUpdate(string DirectoryPath, bool AllUsers)
{
    // The historical Inno Setup AppId ends in two literal closing braces.
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{3C9F1A72-5B4D-4E88-9A17-6D2B8E4F1C93}}_is1";
    private const string AppFileName = "KeySwitchDeepSeek.exe";

    public static InstalledUpdate? Find()
    {
        string? processPath = Environment.ProcessPath;
        if (processPath is null || !Path.GetFileName(processPath).Equals(AppFileName, StringComparison.OrdinalIgnoreCase))
            return null;

        string directory = Path.GetDirectoryName(processPath)!;
        return Find(directory, RegistryHive.CurrentUser) ?? Find(directory, RegistryHive.LocalMachine);
    }

    private static InstalledUpdate? Find(string directory, RegistryHive hive)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey? entry = root.OpenSubKey(UninstallKey);
        if (entry?.GetValue("InstallLocation") is string installedDirectory &&
            Path.IsPathFullyQualified(installedDirectory) &&
            string.Equals(directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(installedDirectory)),
                StringComparison.OrdinalIgnoreCase))
        {
            return new InstalledUpdate(directory, hive == RegistryHive.LocalMachine);
        }

        return null;
    }

    public async Task DownloadAndStartAsync(UpdatePackage package)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var client = new HttpClient();
        using HttpResponseMessage response = await client.GetAsync(
            package.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != package.Size)
            throw new InvalidDataException(Loc.T("update.invalidPackage"));

        await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        string tempDirectory = Directory.CreateTempSubdirectory("KeySwitchDeepSeek-update-").FullName;
        string installer = Path.Combine(tempDirectory, "KeySwitchDeepSeek-update.exe");
        try
        {
            await using (var file = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                if (!await package.CopyVerifiedAsync(source, file, timeout.Token).ConfigureAwait(false))
                    throw new InvalidDataException(Loc.T("update.invalidPackage"));
            }

            // The image loader needs a read-only file. Verify it under the read lock held during launch.
            await using FileStream verified = File.OpenRead(installer);
            if (!await package.CopyVerifiedAsync(verified, Stream.Null, timeout.Token).ConfigureAwait(false))
                throw new InvalidDataException(Loc.T("update.invalidPackage"));

            var start = new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = tempDirectory };
            start.ArgumentList.Add(AllUsers ? "/ALLUSERS" : "/CURRENTUSER");
            start.ArgumentList.Add($"/DIR={DirectoryPath}");
            start.ArgumentList.Add($"/KeySwitchPid={Environment.ProcessId}");
            start.ArgumentList.Add("/SP-");
            start.ArgumentList.Add("/SILENT");
            start.ArgumentList.Add("/NORESTART");
            start.ArgumentList.Add("/NOCLOSEAPPLICATIONS");
            start.ArgumentList.Add("/LOG");

            using Process process = Process.Start(start) ?? throw new InvalidOperationException(Loc.T("update.launchFailed"));
        }
        catch
        {
            try
            {
                File.Delete(installer);
                Directory.Delete(tempDirectory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            throw;
        }
    }
}
