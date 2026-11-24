using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyVersion("1.14.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.14.0.0")]

internal static class Launcher
{
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowStyle(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowStyle(IntPtr window,int index,IntPtr style);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct ClientRect {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window,out ClientRect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool SetWindowText(IntPtr window,string text);
    private sealed class WindowSettings
    {
        public WindowSettings(){}
        public int Width=1600,Height=1000;
        public bool Maximized;
    }
    private static WindowSettings ReadWindowSettings()
    {
        try
        {
            string path=Path.Combine(Root,"viewer-window-settings.json");
            if(File.Exists(path))
            {
                var settings=new JavaScriptSerializer().Deserialize<WindowSettings>(File.ReadAllText(path));
                if(settings!=null&&settings.Width>=320&&settings.Width<=16384&&settings.Height>=240&&settings.Height<=16384)return settings;
            }
        }catch(Exception error){File.WriteAllText(Path.Combine(Root,"window-settings.log"),error.Message);}
        return new WindowSettings();
    }
    private static void RememberWindowSize(Process player,WindowSettings settings)
    {
        string lastSaved=null;
        while(!player.WaitForExit(250))
        {
            try
            {
                player.Refresh();if(player.HasExited)break;
                ProcessMeshPicker(player);
                var window=player.MainWindowHandle;if(window==IntPtr.Zero||IsIconic(window))continue;
                settings.Maximized=IsZoomed(window);
                if(!settings.Maximized)
                {
                    ClientRect rect;if(!GetClientRect(window,out rect))continue;
                    int width=rect.Right-rect.Left,height=rect.Bottom-rect.Top;
                    if(width<320||height<240)continue;settings.Width=width;settings.Height=height;
                }
                string json=new JavaScriptSerializer().Serialize(settings);if(json==lastSaved)continue;
                string path=Path.Combine(Root,"viewer-window-settings.json"),temporary=path+".tmp";
                File.WriteAllText(temporary,json,new UTF8Encoding(false));
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
                lastSaved=json;
            }
            catch(Exception error)
            {if(player.HasExited)break;File.WriteAllText(Path.Combine(Root,"window-settings.log"),error.Message);}
        }
    }
    private sealed class WindowOwner:IWin32Window
    {
        public IntPtr Handle{get;private set;}
        internal WindowOwner(IntPtr handle){Handle=handle;}
    }
    private static void ProcessMeshPicker(Process player)
    {
        string requestFile=Path.Combine(Root,"mesh-import-request.json");if(!File.Exists(requestFile))return;
        string id=null;var response=new Dictionary<string,object>();
        try
        {
            var serializer=new JavaScriptSerializer();var request=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(requestFile));
            id=Convert.ToString(request["Id"]);Guid value;if(!Guid.TryParseExact(id,"N",out value))throw new InvalidDataException("Invalid file picker request");
            File.Delete(requestFile);response["Id"]=id;
            using(var picker=new OpenFileDialog())
            {
                picker.Title="Blender 수정 폴더의 mesh-overrides.json 선택";picker.Filter="StarSavior 모드 JSON (*.json)|*.json";picker.FileName="mesh-overrides.json";picker.CheckFileExists=true;picker.Multiselect=false;
                object initial;if(request.TryGetValue("InitialDirectory",out initial)&&Directory.Exists(Convert.ToString(initial)))picker.InitialDirectory=Convert.ToString(initial);
                var result=picker.ShowDialog(new WindowOwner(player.MainWindowHandle));
                if(result==DialogResult.OK)response["Path"]=picker.FileName;else response["Cancelled"]=true;
            }
        }
        catch(Exception error){response["Id"]=id;response["Error"]=error.Message;}
        File.WriteAllText(Path.Combine(Root,"mesh-import-response.json"),new JavaScriptSerializer().Serialize(response),new UTF8Encoding(false));
    }
    private static void EnableWindowResize(Process player)
    {
        try
        {
            var until=DateTime.UtcNow.AddSeconds(12);
            do
            {
                player.Refresh();if(player.HasExited)return;
                var window=player.MainWindowHandle;
                if(window!=IntPtr.Zero)
                {
                    long style=GetWindowStyle(window,-16).ToInt64();
                    SetWindowStyle(window,-16,new IntPtr(style|0x00040000L|0x00010000L|0x00020000L));
                    SetWindowText(window,"StarSavior 의상 편집 뷰어 · v1.14.0");
                    SetWindowPos(window,IntPtr.Zero,0,0,0,0,0x0027);return;
                }
                Thread.Sleep(100);
            }while(DateTime.UtcNow<until);
        }catch(Exception error){File.WriteAllText(Path.Combine(Root,"window-resize.log"),error.Message);}
    }
    private static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    private static bool PublicEdition
    {
        get
        {
            string file=Path.Combine(Root,"viewer-edition.json");
            return File.Exists(file)?File.ReadAllText(file).Contains("Public"):File.Exists(Path.Combine(Root,"runtime-patcher","RuntimePatcher.exe"));
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--link-helper"))
            {
                int index = Array.IndexOf(args, "--source");
                if (index < 0 || index + 1 >= args.Length) throw new IOException("Missing source folder.");
                string requested = ResolveSource(args[index + 1]);
                ValidateSource(requested);
                Prepare(requested);
                return 0;
            }
            bool acquired;
            using (var mutex = new Mutex(true, "Local\\StarSaviorCostumeViewer18766", out acquired))
            {
            if (!acquired) { MessageBox.Show("의상 뷰어가 이미 실행 중입니다.", "의상 뷰어"); return 0; }
            string errorPath = Path.Combine(Root,"launcher-error.txt"); if(File.Exists(errorPath)) File.Delete(errorPath);
            string config = Path.Combine(Root, "client-path.txt");
            int sourceIndex = Array.IndexOf(args, "--source");
            string source = sourceIndex >= 0 && sourceIndex + 1 < args.Length ? args[sourceIndex + 1] :
                File.Exists(config) ? File.ReadAllText(config).Trim() : "";
            source = ResolveSource(source);
            bool confirmed = File.Exists(Path.Combine(Root, "direct-source-confirmed.txt"));
            if (!ValidSource(source) || source.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase))
            {
                source = FindSteamGame() ?? "";
                confirmed = false;
            }
            if ((!confirmed || !ValidSource(source)) && !args.Contains("--prepare-only") && !args.Contains("--smoke"))
                source = ConfirmSource(source);
            if (!ValidSource(source)) throw new IOException("Steam의 StarSavior 설치 폴더를 찾을 수 없습니다.");
            ValidateSource(source);
            string engine;
            try { engine = Prepare(source); }
            catch (LinkPermissionException)
            {
                var helper = new ProcessStartInfo(Application.ExecutablePath,
                    "--link-helper --prepare-only --source \"" + source + "\"")
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var process = Process.Start(helper))
                {
                    while (!process.WaitForExit(100)) Application.DoEvents();
                    if (process.ExitCode != 0) throw new IOException("리소스 연결을 완료하지 못했습니다. launcher-error.txt를 확인해 주세요.");
                }
                engine = Prepare(source);
            }
            File.WriteAllText(config, Path.GetFullPath(source), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Root, "direct-source-confirmed.txt"), "1.12");
            if (args.Contains("--prepare-only")) return 0;
            // Native initialization reads local settings and never starts an asset server.
            {

                foreach(string file in new[]{"mesh-import-request.json","mesh-import-response.json"}){string pending=Path.Combine(Root,file);if(File.Exists(pending))File.Delete(pending);}
                var windowSettings=ReadWindowSettings();
                var start = new ProcessStartInfo(Path.Combine(engine, "StarSavior.exe"),
                    "-screen-fullscreen 0 -screen-width "+windowSettings.Width+" -screen-height "+windowSettings.Height+" -logFile \"" + Path.Combine(Root, "viewer.log") + "\"")
                    { WorkingDirectory = engine, UseShellExecute = false };
                start.EnvironmentVariables["STARSAVIOR_VIEWER_ROOT"] = Root.TrimEnd(Path.DirectorySeparatorChar);
                start.EnvironmentVariables["STARSAVIOR_VIEWER_PUBLIC"] = PublicEdition ? "1" : "0";
                if (args.Contains("--smoke")) start.EnvironmentVariables["STARSAVIOR_VIEWER_SMOKE"] = "1";
                foreach (var key in start.EnvironmentVariables.Keys.Cast<string>().Where(key => key.StartsWith("STARSAVIOR_TEST_")).ToArray())
                    start.EnvironmentVariables.Remove(key);
                using (var player = Process.Start(start))
                {
                    EnableWindowResize(player);player.Refresh();
                    if(!player.HasExited&&windowSettings.Maximized&&player.MainWindowHandle!=IntPtr.Zero)ShowWindow(player.MainWindowHandle,3);
                    RememberWindowSize(player,windowSettings);
                }
            }
            return 0;
            }
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(Root, "launcher-error.txt"), error.ToString());
            if (!args.Contains("--prepare-only") && !args.Contains("--smoke")) MessageBox.Show(error.Message, "의상 뷰어", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

    }
    private static bool ValidSource(string source)
    {
        return File.Exists(Path.Combine(source, "StarSavior.exe")) && Directory.Exists(Path.Combine(source, "Data", "eb"));
    }
    private static string ResolveSource(string source)
    {
        return ValidSource(source) ? Path.GetFullPath(source) : source;
    }
    private static void ValidateSource(string source)
    {
        foreach (string file in new[] { "UnityPlayer.dll", "Data/globalgamemanagers", "Data/Managed/NKC.dll",
            "Data/StreamingAssets/aa/catalog.json" })
            if (!File.Exists(Path.Combine(source, file))) throw new IOException("필수 파일이 없습니다: " + file);
        // Resources may be updated independently of the supported Mono runtime.
        var runtime = FileVersionInfo.GetVersionInfo(Path.Combine(source, "UnityPlayer.dll"));
        if (runtime.FileMajorPart != 6000 || runtime.FileMinorPart != 0)
            throw new IOException("이 클라이언트의 Unity 실행 버전은 아직 지원하지 않습니다.");
    }

    private static string FindSteamGame()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
        {
            foreach (string value in new[] { "SteamPath", "InstallPath" })
            {
                var path = Registry.GetValue(key, value, null) as string;
                if (!string.IsNullOrEmpty(path)) roots.Add(path);
            }
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        foreach (string root in roots.ToArray())
        {
            string libraries = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraries)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(libraries), @"""path""\s*""([^""]+)"""))
                roots.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
        }
        foreach (string root in roots)
        {
            string game = Path.Combine(root, "steamapps", "common", "StarSavior");
            if (ValidSource(game)) return Path.GetFullPath(game);
            string apps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(manifest);
                var name = Regex.Match(text, @"""installdir""\s*""([^""]+)""");
                if (!name.Success || name.Groups[1].Value.IndexOf("StarSavior", StringComparison.OrdinalIgnoreCase) < 0) continue;
                game = Path.Combine(apps, "common", name.Groups[1].Value);
                if (ValidSource(game)) return Path.GetFullPath(game);
            }
        }
        return null;
    }

    internal static string InstalledCatalog(string source)
    {
        return Path.Combine(source, "Data", "StreamingAssets", "aa", "catalog.json");
    }

    private static string ConfirmSource(string detected)
    {
        Application.EnableVisualStyles();
        using (var form = new Form { Text = "게임 폴더 확인", Width = 630, Height = 195,
            StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false })
        {
            var label = new Label { Left = 18, Top = 18, Width = 575, Height = 26, Text = "StarSavior 리소스를 읽을 폴더를 확인해 주세요." };
            var path = new TextBox { Left = 18, Top = 51, Width = 480, Text = detected };
            var browse = new Button { Left = 506, Top = 48, Width = 88, Text = "변경…" };
            var accept = new Button { Left = 402, Top = 96, Width = 90, Text = "확인" };
            var cancelButton = new Button { Left = 504, Top = 96, Width = 90, Text = "취소", DialogResult = DialogResult.Cancel };
            browse.Click += (sender, e) =>
            {
                using (var picker = new FolderBrowserDialog { SelectedPath = path.Text, ShowNewFolderButton = false })
                    if (picker.ShowDialog(form) == DialogResult.OK) path.Text = picker.SelectedPath;
            };
            accept.Click += (sender, e) =>
            {
                string selected = ResolveSource(path.Text.Trim());
                if (!ValidSource(selected)) { MessageBox.Show(form, "StarSavior.exe와 Data/eb가 있는 게임 폴더를 선택해 주세요."); return; }
                path.Text = selected;
                form.DialogResult = DialogResult.OK;
            };
            form.Controls.AddRange(new Control[] { label, path, browse, accept, cancelButton });
            form.AcceptButton = accept; form.CancelButton = cancelButton;
            if (form.ShowDialog() != DialogResult.OK) throw new OperationCanceledException();
            return path.Text;
        }
    }

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CreateSymbolicLink(string link, string target, int flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CreateHardLink(string link, string target, IntPtr reserved);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file, uint command, byte[] input,
        int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    private static void LinkDirectory(string link, string target)
    {
        if (Directory.Exists(link))
        {
            if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) throw new IOException("기존 실행 폴더의 링크가 아닙니다: " + link);
            Directory.Delete(link, false);
        }
        // NTFS junctions work across local drives without enabling Developer Mode.
        Directory.CreateDirectory(link);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        byte[] display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] data = new byte[16 + substitute.Length + 2 + display.Length + 2];
        using (var stream = new MemoryStream(data)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0xA0000003u); writer.Write((ushort)(data.Length - 8)); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write((ushort)substitute.Length);
            writer.Write((ushort)(substitute.Length + 2)); writer.Write((ushort)display.Length);
            writer.Write(substitute); writer.Write((ushort)0); writer.Write(display); writer.Write((ushort)0);
        }
        using (var handle = CreateFile(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            int returned;
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                throw new IOException("리소스 폴더 연결 실패: " + target);
        }
    }

    private sealed class LinkPermissionException : IOException
    {
        internal LinkPermissionException(string message) : base(message) { }
    }
    private static bool reuseLinks;
    private static readonly string[] ModBootstrapLibraries =
        { "winhttp.dll", "version.dll", "doorstop.dll", "dinput8.dll", "dxgi.dll", "d3d11.dll", "winmm.dll" };

    private static string ViewerBootConfiguration(string source)
    {
        var lines = File.ReadAllLines(Path.Combine(source, "Data", "boot.config"))
            .Where(line => !new[] { "single-instance", "build-guid" }.Contains(
                line.Split('=')[0].Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
        using (var hash = MD5.Create())
            lines.Add("build-guid=" + BitConverter.ToString(hash.ComputeHash(
                Encoding.UTF8.GetBytes("StarStyleX:" + Path.GetFullPath(Root).ToUpperInvariant()))).Replace("-", "").ToLowerInvariant());
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static void WritePrivateText(string target, string content)
    {
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0 &&
            File.ReadAllText(target) == content) return;
        string temporary = target + ".new";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            File.Delete(target);
        // Replace the directory entry, including an older symbolic/hard link.
        // Never write through the old link into the installed game.
        if (File.Exists(target)) File.Replace(temporary, target, null);
        else File.Move(temporary, target);
    }

    private static void LinkFile(string link, string target)
    {
        if (File.Exists(link))
        {
            if (reuseLinks && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) return;
            File.Delete(link);
        }
        if (CreateSymbolicLink(link, target, 2) || CreateSymbolicLink(link, target, 0)) return;
        if (CreateHardLink(link, target, IntPtr.Zero)) return;
        throw new LinkPermissionException("리소스 파일 연결 권한이 필요합니다. Windows 개발자 모드를 켜거나 뷰어를 관리자 권한으로 실행해 주세요.\n" + target);
    }

    private static void PrivateFile(string source, string target)
    {
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            File.Delete(target);
        if (File.Exists(target) && new FileInfo(source).Length == new FileInfo(target).Length &&
            File.GetLastWriteTimeUtc(source) == File.GetLastWriteTimeUtc(target)) return;
        string temporary = target + ".new";
        File.Copy(source, temporary, true);
        if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
        copiedFiles++;
        copiedBytes += new FileInfo(source).Length;
    }

    private static int copiedFiles;
    private static long copiedBytes;

    private static string Prepare(string source)
    {
        var timing = Stopwatch.StartNew();
        copiedFiles = 0; copiedBytes = 0;
        string engine = Path.Combine(Root, "viewer-runtime");
        Directory.CreateDirectory(engine);
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var settings = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(
            Path.Combine(source, "Data", "StreamingAssets", "aa", "settings.json")));
        var locations = (System.Collections.IEnumerable)settings["m_CatalogLocations"];
        var main = locations.Cast<Dictionary<string, object>>().Single(location =>
            Convert.ToString(location["m_Provider"]).EndsWith(".ContentCatalogProvider", StringComparison.Ordinal));
        main["m_InternalId"] = InstalledCatalog(source).Replace('\\', '/');
        main["m_Dependencies"] = new string[0];
        settings["m_CatalogLocations"] = new[] { main };
        settings["m_DisableCatalogUpdateOnStart"] = true;
        WritePrivateText(Path.Combine(Root, "local-addressables-settings.json"), serializer.Serialize(settings));
        string sourceMarker = Path.Combine(engine, "direct-source.txt");
        reuseLinks = File.Exists(sourceMarker) && string.Equals(File.ReadAllText(sourceMarker), source, StringComparison.OrdinalIgnoreCase);
        string data = Path.Combine(engine, "Data");
        Directory.CreateDirectory(data);
        string managed = Path.Combine(data, "Managed");
        Directory.CreateDirectory(managed);
        var overrides = Directory.GetFiles(Path.Combine(Root, "engine-overrides"))
            .Where(file=>!PublicEdition||new[]{"StarSavior.CostumeViewer.dll","StarSavior.ModMeshRuntime.dll","public-symbols.json"}.Contains(Path.GetFileName(file),StringComparer.OrdinalIgnoreCase))
            .ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        string patcher=Path.Combine(Root,"runtime-patcher","RuntimePatcher.exe");
        var patchFiles=new[]{"NKC.dll","UnityEngine.CoreModule.dll","Bs.Windows.dll"};
        string patchFingerprint="";
        bool reusePatches=false;
        string patchMarker=Path.Combine(engine,"runtime-patch-version.txt");
        if(PublicEdition&&File.Exists(patcher))
        {
            patchFingerprint=string.Join("\n",patchFiles.Select(name=>Path.Combine(source,"Data","Managed",name))
                .Concat(new[]{patcher,Path.Combine(Root,"engine-overrides","StarSavior.CostumeViewer.dll")})
                .Select(path=>path+"|"+new FileInfo(path).Length+"|"+File.GetLastWriteTimeUtc(path).Ticks));
            reusePatches=reuseLinks&&File.Exists(patchMarker)&&File.ReadAllText(patchMarker)==patchFingerprint;
        }
        Directory.CreateDirectory(Path.Combine(Root,"native-runtime"));
        var nativeLibraries = new HashSet<string>(Directory.GetFiles(Path.Combine(Root, "native-runtime"), "*.dll")
            .Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(source))
            if ((Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) == "StarSavior.exe") &&
                !ModBootstrapLibraries.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase) &&
                !nativeLibraries.Contains(Path.GetFileName(file)))
                LinkFile(Path.Combine(engine, Path.GetFileName(file)), file);
        foreach (string name in ModBootstrapLibraries)
        {
            string proxy = Path.Combine(engine, name);
            if (File.Exists(proxy)) File.Delete(proxy);
        }
        foreach (string directory in Directory.GetDirectories(Path.Combine(source, "Data")))
            if (Path.GetFileName(directory) != "Managed") LinkDirectory(Path.Combine(data, Path.GetFileName(directory)), directory);
        if (Directory.Exists(Path.Combine(source, "MonoBleedingEdge")))
            LinkDirectory(Path.Combine(engine, "MonoBleedingEdge"), Path.Combine(source, "MonoBleedingEdge"));
        foreach (string file in Directory.GetFiles(Path.Combine(source, "Data", "Managed")))
            if (!overrides.ContainsKey(Path.GetFileName(file))&&!(reusePatches&&patchFiles.Contains(Path.GetFileName(file)))) PrivateFile(file, Path.Combine(managed, Path.GetFileName(file)));
        if(PublicEdition)
        {
            var expected=new HashSet<string>(Directory.GetFiles(Path.Combine(source,"Data","Managed")).Select(Path.GetFileName),StringComparer.OrdinalIgnoreCase);
            expected.UnionWith(overrides.Keys);
            foreach(string file in Directory.GetFiles(managed,"*.dll"))if(!expected.Contains(Path.GetFileName(file)))File.Delete(file);
        }
        foreach (string file in Directory.GetFiles(Path.Combine(source, "Data")))
        {
            string name = Path.GetFileName(file);
            if (name == "globalgamemanagers" || name == "app.info" || name == "boot.config") continue;
            LinkFile(Path.Combine(data, name), file);
        }
        WritePrivateText(Path.Combine(data, "boot.config"), ViewerBootConfiguration(source));
        foreach (string file in overrides.Values)
            PrivateFile(file, Path.Combine(managed, Path.GetFileName(file)));
        if(PublicEdition&&File.Exists(patcher)&&!reusePatches)
        {
            var start=new ProcessStartInfo(patcher,"\""+managed+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
            using(var process=Process.Start(start))
            {
                string error=process.StandardError.ReadToEnd();process.WaitForExit();
                if(process.ExitCode!=0)throw new IOException("뷰어 런타임 구성 실패: "+error);
            }
            WritePrivateText(patchMarker,patchFingerprint);
        }
        foreach (string file in Directory.GetFiles(Path.Combine(Root, "native-runtime"), "*.dll"))
        {
            string target = Path.Combine(engine, Path.GetFileName(file));
            PrivateFile(file, target);
        }
        byte[] bytes = File.ReadAllBytes(Path.Combine(source, "Data", "globalgamemanagers"));
        if (bytes.Length < 1330) throw new InvalidDataException("지원하지 않는 Unity 설정 파일입니다.");
        string company = Encoding.UTF8.GetString(bytes, 1304, 11);
        if (company != "StudioBside" && company != "OfflineLabs") throw new InvalidDataException("지원하지 않는 Unity 설정 파일입니다.");
        Buffer.BlockCopy(Encoding.UTF8.GetBytes("ViewerLabsX"), 0, bytes, 1304, 11);
        Buffer.BlockCopy(Encoding.UTF8.GetBytes("StarStyleX"), 0, bytes, 1320, 10);
        File.WriteAllBytes(Path.Combine(data, "globalgamemanagers"), bytes);
        File.WriteAllText(Path.Combine(data, "app.info"), "ViewerLabsX\nStarStyleX");
        File.WriteAllText(sourceMarker, source);
        File.WriteAllText(Path.Combine(Root, "startup-prepare-timing.json"), serializer.Serialize(new {
            PrepareMs = timing.ElapsedMilliseconds, CopiedFiles = copiedFiles, CopiedBytes = copiedBytes }));
        return engine;
    }
}
