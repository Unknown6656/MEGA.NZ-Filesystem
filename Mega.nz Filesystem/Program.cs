using System.Collections.Generic;
using System.Xml.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;
using System.Drawing;
using System.Linq;
using System.Text;
using System.IO;
using System;

using CG.Web.MegaApiClient.Serialization;
using CG.Web.MegaApiClient;

using DokanNet;

namespace MegaFileSystem
{
    public static class Program
    {
        internal const string REG_BASE = "Software\\Microsoft\\CurrentVersion\\Explorer\\DriveIcons\\";
        internal const string CONFIG_PATH = "./mega.config";

        public static readonly MegaApiClient mega = new MegaApiClient();
        public static RegistryKey regkey;
        public static FileSystem filesys;
        public static char mountpoint;


        public static int Main(string[] args)
        {
            Win32.SetConsoleCtrlHandler(e =>
            {
                if (Enum.IsDefined(typeof(CtrlTypes), e))
                {
                    OnExit();

                    Console.WriteLine($"The application forcefully exited due to: {e}");
                }

                return true;
            }, true);

            Directory.SetCurrentDirectory(new FileInfo(typeof(Program).Assembly.Location).Directory.FullName);
            Application.EnableVisualStyles();
            Application.DoEvents();
            Console.Title = "MEGA.NZ Filesystem";

            FileInfo conf = new FileInfo(CONFIG_PATH);
            Settings settings = new Settings();

            if (conf.Exists)
                try
                {
                    using (FileStream fs = conf.OpenRead())
                        settings = Settings.Deserialize(fs);
                }
                catch
                {
                }

            try
            {
                settings.LastToken = null;
                settings.Init();

                if (settings != null)
                {
                    if (conf.Exists)
                        conf.Delete();

                    if (settings.Save)
                        using (FileStream fs = conf.Create())
                            settings.Serialize(fs);

                    settings.Connect();

                    Thread thread = settings.Mount();

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("Type 'quit' to unmount the drive and exit the application.");
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Title = $"[{(mega.IsLoggedIn ? $"'{settings.Email}' mounted on {settings.Drive}:\\" : "offline")}] MEGA.NZ Filesystem";
                    Thread.Sleep(5000);

                    thread.Start();

                    while (thread.IsAlive && (Console.ReadLine() != "quit"))
                        Console.WriteLine("Type 'quit' to unmount the drive and exit the application.");

                    if (thread.IsAlive)
                        thread.Abort();

                    thread = null;

                    Console.Write("Unmounted forcefully by user.");

                    settings.Unmount();
                }
            }
            catch (Exception ex)
            {
                StringBuilder sb = new StringBuilder();

                sb.AppendLine("An CRITICAL error occured:");

                while (ex != null)
                {
                    sb.Insert(0, $"{ex.Message}:\n{ex.StackTrace}");

                    ex = ex.InnerException;
                }

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(sb);
                Console.ForegroundColor = ConsoleColor.White;
            }

            if (Debugger.IsAttached)
            {
                Thread.Sleep(1000);
                Console.WriteLine("Press any key to exit ...");
                Console.ReadKey(true);
            }

            return 0;
        }

        private static void Connect(this Settings settings)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.CursorVisible = true;

            if (settings.LastToken is null)
                Console.WriteLine("Failed to log in.");
            else
                Console.WriteLine($@"Successfully logged in with the following data:

    Email:      {settings.Email}
    Hash:       {settings.Hash}
    Session:    {settings.LastToken.SessionId}
    AKey:       {string.Join(" ", from b in settings.AES select b.ToString("X2"))}
    MKey:       {string.Join(" ", from b in settings.LastToken.MasterKey select b.ToString("X2"))}
");
        }

        private static Thread Mount(this Settings settings)
        {
            Thread mounter = new Thread(() =>
            {
                IAccountInformation acc = mega.GetAccountInformation();

                Console.WriteLine($@"Mounting the filesystem to \\{mountpoint = settings.Drive}:\ with the following data:

    Base driver version: {Dokan.DriverVersion:x8} ({Dokan.DriverVersion})
    Dokan API version:   {Dokan.Version:x8} ({Dokan.Version})
    Used bytes:          {acc.UsedQuota}
    Total bytes:         {acc.TotalQuota}
");

                try
                {
                    filesys = new FileSystem(new DirectoryInfo($"./tmp/{settings.Email}/"), mega, settings.Email, settings.CacheSz);
                    regkey = Registry.LocalMachine.CreateSubKey($"{REG_BASE}\\{mountpoint}", true);

                    string iconpath = $"{mountpoint}:\\{FileSystem.CFILE_ICON},0";

                    using (RegistryKey defico = regkey.CreateSubKey("DefaultIcon", true))
                    {
                        defico.SetValue("", iconpath);
                        defico.Flush();
                    }

                    using (RegistryKey deflbl = regkey.CreateSubKey("DefaultLabel", true))
                    {
                        deflbl.SetValue("", FileSystem.LABEL);
                        deflbl.Flush();
                    }

                    regkey.Flush();

                    Win32.SHUpdateImage(iconpath, 0, 0x0202, 0);

                    filesys.Mount($"{settings.Drive}:\\", DokanOptions.StderrOutput);
                }
                catch (Exception ex)
                {
                    StringBuilder sb = new StringBuilder();

                    sb.AppendLine("An error occured during the mounting of the file system:");

                    while (ex != null)
                    {
                        sb.Insert(0, $"{ex.Message}:\n{ex.StackTrace}");

                        ex = ex.InnerException;
                    }

                    Console.WriteLine(sb);

                    Unmount(settings);
                }
            });

            return mounter;
        }

        private static void OnExit()
        {
            if (mega.IsLoggedIn)
            {
                mega.Logout();

                Console.WriteLine("Logged out.");
            }
        }

        private static void Unmount(this Settings settings)
        {
            try
            {
                Dokan.Unmount(mountpoint);

                Console.WriteLine($"Unmounted {mountpoint}:\\");
            }
            catch
            {
            }
            finally
            {
                if (regkey != null)
                    try
                    {
                        regkey.Close();

                        Registry.LocalMachine.OpenSubKey(REG_BASE).DeleteSubKeyTree($"{mountpoint}", false);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        regkey.Dispose();
                        regkey = null;
                    }

                if (settings.DeleteCache && (filesys != null))
                    try
                    {
                        if (filesys.TemporaryDirectory.Exists)
                            filesys.TemporaryDirectory.Delete(true);
                    }
                    catch
                    {
                    }

                filesys = null;

                OnExit();
            }
        }

        private static void Init(this Settings set)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($@"
------------- THE MEGA.NZ FILESYSTEM DRIVER -------------
    Written by Unknown6656, based on the DOKAN-Project
    Copyright (c) {DateTime.Now.Year}, Unknown6656
---------------------------------------------------------
");
                Console.CursorVisible = false;

                IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;

                using (Graphics g = Graphics.FromHwnd(hwnd))
                    g.DrawImage(Properties.Resources.logo, 50, 100, 100, 100);
            }
            catch
            {
            }
            
            using (InitForm win = new InitForm(set, mega))
                win.ShowDialog();
        }
    }

    [Serializable]
    public sealed class Settings
    {
        private static XmlSerializer _ser = new XmlSerializer(typeof(Settings));

        public byte[] AES { set; get; }
        public string Hash { set; get; }
        public string Email { set; get; }
        public char Drive { set; get; }
        public bool Save { set; get; } = true;
        public int CacheSz { set; get; } = 128;
        public bool DeleteCache { set; get; } = true;
        public MegaApiClient.LogonSessionToken LastToken { set; get; } = null;


        public void Serialize(Stream s) => _ser.Serialize(s, this);

        public static Settings Deserialize(Stream s) => _ser.Deserialize(s) as Settings;
    }
}
