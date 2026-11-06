using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Volume Mixer Setup")]
[assembly: AssemblyDescription("Volume Mixer installer")]
[assembly: AssemblyCompany("Yogiero")]
[assembly: AssemblyProduct("Volume Mixer Setup")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class SetupProgram
{
    private static string LocalPrograms { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"); } }
    private static string InstallDirectory { get { return Path.Combine(LocalPrograms, "Volume Mixer"); } }
    private static string AppPath { get { return Path.Combine(InstallDirectory, "Volume Mixer.exe"); } }
    private static string UninstallerPath { get { return Path.Combine(InstallDirectory, "Volume Mixer Uninstall.exe"); } }
    private static string StartMenuDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft\\Windows\\Start Menu\\Programs\\Volume Mixer"); } }
    private static string StartupDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft\\Windows\\Start Menu\\Programs\\Startup"); } }
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Yogiero.VolumeMixer";
    private const string ResourceName = "VolumeMixer.exe";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && String.Equals(args[0], "--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            Uninstall(args.Length > 1 && String.Equals(args[1], "--quiet", StringComparison.OrdinalIgnoreCase));
            return;
        }
        if (args.Length > 0 && String.Equals(args[0], "--quiet", StringComparison.OrdinalIgnoreCase))
        {
            try { Install(true, true, true); }
            catch (Exception ex) { MessageBox.Show("Volume Mixer could not be installed.\n\n" + ex.Message, "Volume Mixer Setup", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
    }

    public static void Install(bool startWithWindows, bool desktopShortcut, bool launchAfterInstall)
    {
        MigrateLegacyInstallation();
        StopProcessAtPath(AppPath);
        Directory.CreateDirectory(InstallDirectory);
        using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
        {
            if (resource == null) throw new InvalidOperationException("The Volume Mixer application payload is missing.");
            using (FileStream file = new FileStream(AppPath, FileMode.Create, FileAccess.Write, FileShare.None)) resource.CopyTo(file);
        }
        File.Copy(Application.ExecutablePath, UninstallerPath, true);
        Directory.CreateDirectory(StartMenuDirectory);
        CreateShortcut(Path.Combine(StartMenuDirectory, "Volume Mixer.lnk"), AppPath, "Volume Mixer");
        CreateShortcut(Path.Combine(StartMenuDirectory, "Uninstall Volume Mixer.lnk"), UninstallerPath, "Uninstall Volume Mixer", "--uninstall");

        string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Volume Mixer.lnk");
        if (desktopShortcut) CreateShortcut(desktopLink, AppPath, "Volume Mixer"); else TryDelete(desktopLink);
        string startupLink = Path.Combine(StartupDirectory, "Volume Mixer.lnk");
        if (startWithWindows) CreateShortcut(startupLink, AppPath, "Volume Mixer"); else TryDelete(startupLink);
        TryDelete(Path.Combine(StartupDirectory, "Hızlı Ses Karıştırıcısı.lnk"));

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "Volume Mixer", RegistryValueKind.String);
            key.SetValue("DisplayVersion", "1.0.0", RegistryValueKind.String);
            key.SetValue("Publisher", "Yogiero", RegistryValueKind.String);
            key.SetValue("InstallLocation", InstallDirectory, RegistryValueKind.String);
            key.SetValue("DisplayIcon", AppPath + ",0", RegistryValueKind.String);
            key.SetValue("UninstallString", "\"" + UninstallerPath + "\" --uninstall", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        if (launchAfterInstall) Process.Start(AppPath);
    }

    private static void MigrateLegacyInstallation()
    {
        string oldDirectory = Path.Combine(LocalPrograms, "HizliSesKaristirici");
        string oldApp = Path.Combine(oldDirectory, "Hızlı Ses Karıştırıcısı.exe");
        StopProcessAtPath(oldApp);
        string oldExclusions = Path.Combine(oldDirectory, "excluded-apps.txt");
        string newExclusions = Path.Combine(InstallDirectory, "excluded-apps.txt");
        if (File.Exists(oldExclusions) && !File.Exists(newExclusions))
        {
            Directory.CreateDirectory(InstallDirectory);
            File.Copy(oldExclusions, newExclusions, false);
        }
        TryDelete(Path.Combine(StartupDirectory, "Hızlı Ses Karıştırıcısı.lnk"));
        TryDelete(oldApp);
        TryDelete(Path.Combine(oldDirectory, "HizliSesKaristirici.log"));
        TryDelete(oldExclusions);
        try
        {
            if (Directory.Exists(oldDirectory) && Directory.GetFiles(oldDirectory).Length == 0 && Directory.GetDirectories(oldDirectory).Length == 0)
                Directory.Delete(oldDirectory, false);
        }
        catch { }
    }

    private static void StopProcessAtPath(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        foreach (Process process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (String.Equals(process.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(4000);
                    }
                }
                catch { }
            }
        }
    }

    private static void CreateShortcut(string path, string target, string description, string arguments = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("Windows Script Host is unavailable.");
        object shell = Activator.CreateInstance(shellType);
        object shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            Type type = shortcut.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(target) });
            type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
            type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { target + ",0" });
            if (!String.IsNullOrEmpty(arguments)) type.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    private static void Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Remove Volume Mixer and its shortcuts?", "Uninstall Volume Mixer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        StopProcessAtPath(AppPath);
        TryDelete(Path.Combine(StartupDirectory, "Volume Mixer.lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Volume Mixer.lnk"));
        TryDelete(Path.Combine(StartMenuDirectory, "Volume Mixer.lnk"));
        TryDelete(Path.Combine(StartMenuDirectory, "Uninstall Volume Mixer.lnk"));
        try { if (Directory.Exists(StartMenuDirectory) && Directory.GetFileSystemEntries(StartMenuDirectory).Length == 0) Directory.Delete(StartMenuDirectory); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        TryDelete(AppPath);
        TryDelete(Path.Combine(InstallDirectory, "excluded-apps.txt"));
        TryDelete(Path.Combine(InstallDirectory, "VolumeMixer.log"));
        try { MoveFileEx(UninstallerPath, null, 4); } catch { }
        if (!quiet) MessageBox.Show("Volume Mixer has been removed. The uninstaller file will be cleared after Windows releases it.", "Uninstall Volume Mixer", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string existing, string destination, int flags);
}

internal sealed class SetupForm : Form
{
    private readonly CheckBox startup = new CheckBox();
    private readonly CheckBox desktop = new CheckBox();
    private readonly Button install = new Button();

    public SetupForm()
    {
        Text = "Volume Mixer Setup";
        ClientSize = new System.Drawing.Size(500, 290);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var title = new Label { Text = "Install Volume Mixer", Font = new System.Drawing.Font("Segoe UI", 18, System.Drawing.FontStyle.Bold), AutoSize = true, Location = new System.Drawing.Point(24, 24) };
        var subtitle = new Label { Text = "Per-app audio controls, one click away in your system tray.", Font = new System.Drawing.Font("Segoe UI", 9), AutoSize = true, Location = new System.Drawing.Point(27, 67) };
        var locationLabel = new Label { Text = "Install for the current Windows user", Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold), AutoSize = true, Location = new System.Drawing.Point(27, 112) };
        var pathLabel = new Label { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs\\Volume Mixer"), ForeColor = System.Drawing.Color.DimGray, AutoSize = true, Location = new System.Drawing.Point(27, 135) };
        startup.Text = "Start Volume Mixer when I sign in to Windows"; startup.Checked = true; startup.AutoSize = true; startup.Location = new System.Drawing.Point(27, 177);
        desktop.Text = "Create a desktop shortcut"; desktop.Checked = true; desktop.AutoSize = true; desktop.Location = new System.Drawing.Point(27, 202);
        install.Text = "Install"; install.Size = new System.Drawing.Size(100, 32); install.Location = new System.Drawing.Point(274, 244); install.Click += InstallClick;
        var cancel = new Button { Text = "Cancel", Size = new System.Drawing.Size(100, 32), Location = new System.Drawing.Point(384, 244), DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { title, subtitle, locationLabel, pathLabel, startup, desktop, install, cancel });
        AcceptButton = install; CancelButton = cancel;
    }

    private void InstallClick(object sender, EventArgs e)
    {
        install.Enabled = false;
        try
        {
            SetupProgram.Install(startup.Checked, desktop.Checked, true);
            MessageBox.Show("Volume Mixer is installed and running in the system tray.", "Volume Mixer Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            install.Enabled = true;
            MessageBox.Show("Installation failed.\n\n" + ex.Message, "Volume Mixer Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
