using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpankyhoOptimizer.ModernSetup;

namespace InstallerUiTests;
internal static class Entry
{
    static readonly List<object> Results = new();
    static int failures;
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    static object? Call(object instance, string method, params object?[] args) =>
        (instance.GetType().GetMethod(method, Flags) ?? throw new Exception("Missing production method: " + method)).Invoke(instance, args);
    static T Field<T>(object instance, string field) => (T)(instance.GetType().GetField(field, Flags)?.GetValue(instance) ?? throw new Exception("Missing field " + field));
    static T Find<T>(InstallerWindow w, string name) where T : FrameworkElement =>
        (T)(((FrameworkElement)w.Content).FindName(name) ?? throw new Exception("Missing UI element: " + name));
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static InstallerWindow NewWindow()
    {
        var w = new InstallerWindow();
        Find<CheckBox>(w,"LaunchAfter").IsChecked=false;
        Find<TextBox>(w,"InstallPath").Text=Path.Combine(Path.GetTempPath(),"spanky-ui-fixture","Spankyho Optimizer");
        return w;
    }
    static Task Run(InstallerWindow w, Func<string,bool,Action<int,string>,Task> operation) => (Task)Call(w,"RunInstallAsync",operation)!;
    static Task Fail(InstallerWindow w, Exception error) => Run(w,(_,_,_)=>Task.FromException(error));
    static async Task Test(string name, Func<InstallerWindow,Task> action)
    {
        InstallerWindow? w=null;
        try { w=NewWindow(); await action(w); Results.Add(new{name,outcome="PASS"}); }
        catch(Exception ex) { failures++; Results.Add(new{name,outcome="FAIL",error=ex.GetBaseException().Message}); }
        finally { if(w!=null){var busy=w.GetType().GetField("_busy",Flags);busy?.SetValue(w,false);w.Close();} Environment.ExitCode=0; }
    }
    // Pixel-accurate off-screen WPF layout previews. No installation or
    // visible window is created, so QA does not disturb the user's desktop.
    private static int RenderPreview(string directory)
    {
        Directory.CreateDirectory(directory);
        _ = new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var w = NewWindow();
        try
        {
            void Save(string name)
            {
                var view=(FrameworkElement)w.Content;
                view.Measure(new Size(w.Width,w.Height));
                view.Arrange(new Rect(0,0,w.Width,w.Height));
                view.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)w.Width,(int)w.Height,96,96,PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string file=Path.Combine(directory,"installer-"+name+".png");
                using var stream=File.Create(file);
                encoder.Save(stream);
                Console.WriteLine("INSTALLER_VISUAL_RENDER_OK "+name+" "+file);
            }
            Save("ready");
            Call(w,"SetProgress",67,"INSTALACE","Rozbaluji a ověřuji soubory…");
            Call(w,"SetHeader","Instaluji Spankyho Optimizer","Bez přerušení a bez ztráty nastavení","PROBÍHÁ");
            Save("progress");
            Call(w,"ShowError",new IOException("Testovací chyba přístupu k souboru."),"C:\\Tests\\Spankyho Optimizer",true);
            Save("error");
        }
        finally { w.Close(); }
        return 0;
    }

    [STAThread] public static int Main(string[] args)
    {
        if (args.Length==2 && args[0]=="--render-preview")
            return RenderPreview(args[1]);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
          await Test("setup log stays bounded with tail retention and two archives",w=>{
            string dir=Path.Combine(Path.GetTempPath(),"spanky-log-fixture-"+Guid.NewGuid().ToString("N"));
            try {
              Directory.CreateDirectory(dir);
              string path=Path.Combine(dir,"setup.log");
              string tail="RECENT_TAIL_EVENT_"+Guid.NewGuid().ToString("N");
              File.WriteAllText(path,new string('X',16000)+tail);
              Require(Program.RotateSetupLogIfNeeded(path,4096),"oversized setup log not rotated");
              string archive=Path.Combine(dir,"setup.1.log");
              Require(File.Exists(archive) && new FileInfo(archive).Length<=4096,"setup archive exceeds budget");
              Require(File.ReadAllText(archive).EndsWith(tail,StringComparison.Ordinal),"latest diagnostic record lost");
              Require(!File.Exists(path),"active log should restart after bounded archiving");
              File.WriteAllText(path,"tiny record");
              Require(!Program.RotateSetupLogIfNeeded(path,4096),"small log rotated unnecessarily");
              Require(File.Exists(archive),"rotation removed the retained diagnostic history");
            } finally {if(Directory.Exists(dir))Directory.Delete(dir,true);}
            return Task.CompletedTask;
          });
          await Test("release bundle only accepts a fresh isolated directory",w=>{
            string destination=Path.Combine(Path.GetTempPath(),
                "SpankyhoOptimizer-ReleaseGate-Guard-"+Guid.NewGuid().ToString("N"));
            Require(Program.IsFreshIsolatedReleaseBundleDestination(destination),
                "fresh unique TEMP release bundle was rejected");
            Require(!Program.IsFreshIsolatedReleaseBundleDestination(Program.DefaultDirectory),
                "real installation path was accepted for release fixture");
            Require(!Program.IsFreshIsolatedReleaseBundleDestination(Path.GetTempPath()),
                "TEMP root itself was accepted for release fixture");
            return Task.CompletedTask;
          });
          await Test("existing TEMP directory is never cleared by release bundle",w=>{
            string destination=Path.Combine(Path.GetTempPath(),
                "SpankyhoOptimizer-ReleaseGate-Owned-"+Guid.NewGuid().ToString("N"));
            try {
              Directory.CreateDirectory(destination);
              string marker=Path.Combine(destination,"unrelated-user-file.marker");
              File.WriteAllText(marker,"PRESERVE");
              Require(!Program.IsFreshIsolatedReleaseBundleDestination(destination),
                  "existing user's TEMP directory was accepted");
              Require(File.ReadAllText(marker)=="PRESERVE",
                  "freshness check altered existing user data");
            } finally {if(Directory.Exists(destination)) Directory.Delete(destination,true);}
            return Task.CompletedTask;
          });
          await Test("recovery permits predecessor release within same major but rejects downgrade or foreign major",w=>{
            var installed=new Version(16,139,0,0);
            var upgrading=new Version(16,140,0,0);
            Require(Program.IsCompatibleRecoveryVersion(installed,upgrading),
              "16.139 predecessor was rejected for 16.140");
            Require(Program.IsCompatibleRecoveryVersion(upgrading,upgrading),
              "current release was rejected");
            Require(!Program.IsCompatibleRecoveryVersion(new Version(16,141,0,0),upgrading),
              "newer version was accepted as predecessor");
            Require(!Program.IsCompatibleRecoveryVersion(new Version(15,4,1474,0),upgrading),
              "previous major was accepted");
            Require(!Program.IsCompatibleRecoveryVersion(installed,new Version(15,4,1474,0)),
              "future major was accepted");
            Require(!Program.IsCompatibleRecoveryVersion(null,upgrading) &&
                    !Program.IsCompatibleRecoveryVersion(installed,null),
              "missing version was accepted");
            return Task.CompletedTask;
          });
          await Test("backup pruning never selects side-by-side or legacy backups",w=>{
            string sandbox=Path.Combine(Path.GetTempPath(),
                "SpankyhoOptimizer-ReleaseGate-PruneScope-"+Guid.NewGuid().ToString("N"));
            try {
              Directory.CreateDirectory(sandbox);
              string primary=Path.Combine(sandbox,"main-install");
              string secondary=Path.Combine(sandbox,"other-install");
              string foreign=Program.BackupDirectoryForTarget(secondary,"preserve");
              string legacy=Path.Combine(sandbox,".SpankyhoOptimizer-backup-legacy");
              Directory.CreateDirectory(foreign);
              Directory.CreateDirectory(legacy);
              File.WriteAllText(Path.Combine(foreign,"SpankyhoOptimizer.exe"),"foreign");
              File.WriteAllText(Path.Combine(legacy,"SpankyhoOptimizer.exe"),"legacy");
              var owned=new List<string>();
              for(int i=0;i<3;i++) {
                string dir=Program.BackupDirectoryForTarget(primary,"owned"+i);
                owned.Add(dir);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir,"SpankyhoOptimizer.exe"),"own"+i);
                Directory.SetLastWriteTimeUtc(dir,DateTime.UtcNow.AddMinutes(i-10));
              }
              Require(Program.GetPrunableBackups(primary,fanHealthVerified:false).Length==0,
                  "unverified fan state could prune a backup");
              var prune=Program.GetPrunableBackups(primary,fanHealthVerified:true);
              Require(prune.Length==2 && prune.All(owned.Contains) &&
                  !prune.Contains(foreign) && !prune.Contains(legacy),
                  "prune selection included an unowned backup");
              Require(owned.All(Directory.Exists) && Directory.Exists(foreign) &&
                  Directory.Exists(legacy), "read-only prune selection mutated backup files");
            } finally {if(Directory.Exists(sandbox)) Directory.Delete(sandbox,true);}
            return Task.CompletedTask;
          });
          await Test("destination-bound backup recovery restores only its rightful target",w=>{
            string sandbox=Path.Combine(Path.GetTempPath(),
                "SpankyhoOptimizer-ReleaseGate-ScopedBackup-"+Guid.NewGuid().ToString("N"));
            try {
              Directory.CreateDirectory(sandbox);
              string primary=Path.Combine(sandbox,"main-install");
              string secondary=Path.Combine(sandbox,"another-install");
              string primaryBackup=Program.BackupDirectoryForTarget(primary,"fixture");
              string secondaryBackup=Program.BackupDirectoryForTarget(secondary,"fixture");
              Require(!string.Equals(primaryBackup,secondaryBackup,StringComparison.OrdinalIgnoreCase),
                  "two different installation targets share the same backup name");
              Directory.CreateDirectory(primaryBackup);
              Directory.CreateDirectory(secondaryBackup);
              File.Copy(typeof(Program).Assembly.Location,
                  Path.Combine(primaryBackup,"SpankyhoOptimizer.exe"));
              File.WriteAllText(Path.Combine(primaryBackup,"owner.marker"),"PRIMARY");
              File.Copy(typeof(Program).Assembly.Location,
                  Path.Combine(secondaryBackup,"SpankyhoOptimizer.exe"));
              File.WriteAllText(Path.Combine(secondaryBackup,"owner.marker"),"SECONDARY");
              Program.RecoverInterruptedInstall(primary,allowSyntheticTestExecutables:true);
              Require(File.Exists(Path.Combine(primary,"owner.marker")) &&
                  File.ReadAllText(Path.Combine(primary,"owner.marker"))=="PRIMARY" &&
                  !Directory.Exists(primaryBackup) &&
                  File.ReadAllText(Path.Combine(secondaryBackup,"owner.marker"))=="SECONDARY" &&
                  !Directory.Exists(secondary),
                  "backup recovery did not respect destination ownership");
            } finally {if(Directory.Exists(sandbox))Directory.Delete(sandbox,true);}
            return Task.CompletedTask;
          });
          await Test("recovery never adopts a valid-looking backup from another target",w=>{
            string sandbox=Path.Combine(Path.GetTempPath(),
                "SpankyhoOptimizer-ReleaseGate-BackupOwner-"+Guid.NewGuid().ToString("N"));
            try {
              Directory.CreateDirectory(sandbox);
              string target=Path.Combine(sandbox,"first-install");
              string foreign=Path.Combine(sandbox,".SpankyhoOptimizer-backup-other-install");
              Directory.CreateDirectory(foreign);
              File.Copy(typeof(Program).Assembly.Location,
                  Path.Combine(foreign,"SpankyhoOptimizer.exe"));
              File.WriteAllText(Path.Combine(foreign,"other-owner.marker"),"KEEP");
              Program.RecoverInterruptedInstall(target,allowSyntheticTestExecutables:true);
              Require(!Directory.Exists(target) &&
                  File.Exists(Path.Combine(foreign,"other-owner.marker")),
                  "recovery moved an unrelated backup into another installation");
            } finally {if(Directory.Exists(sandbox))Directory.Delete(sandbox,true);}
            return Task.CompletedTask;
          });
          await Test("synthetic recovery never deletes another installer staging directory",w=>{
            string sandbox=Path.Combine(Path.GetTempPath(),
              "SpankyhoOptimizer-ReleaseGate-SyntheticStaging-"+Guid.NewGuid().ToString("N"));
            try {
              Directory.CreateDirectory(sandbox);
              string target=Path.Combine(sandbox,"installed");
              string unrelated=Path.Combine(sandbox,".SpankyhoOptimizer-staging-other-installer");
              Directory.CreateDirectory(unrelated);
              string marker=Path.Combine(unrelated,"other-installation.marker");
              File.WriteAllText(marker,"PRESERVE_UNRELATED_STAGING");
              Program.RecoverInterruptedInstall(target,allowSyntheticTestExecutables:true);
              Require(File.Exists(marker) && File.ReadAllText(marker)=="PRESERVE_UNRELATED_STAGING",
                "synthetic recovery deleted another installer's staging files");
            } finally {if(Directory.Exists(sandbox)) Directory.Delete(sandbox,true);}
            return Task.CompletedTask;
          });
          await Test("existing TEMP file is never accepted as release bundle root",w=>{
            string destination=Path.Combine(Path.GetTempPath(),
                "SpankyhoOptimizer-ReleaseGate-File-"+Guid.NewGuid().ToString("N"));
            try {
              File.WriteAllText(destination,"PRESERVE");
              Require(!Program.IsFreshIsolatedReleaseBundleDestination(destination),
                  "existing file was accepted for extraction");
              Require(File.ReadAllText(destination)=="PRESERVE",
                  "existing file was modified by release path validation");
            } finally {if(File.Exists(destination)) File.Delete(destination);}
            return Task.CompletedTask;
          });
          await Test("embedded installer payload accepts only exact SHA256 of resource bytes",w=>{
            byte[] bytes=System.Text.Encoding.UTF8.GetBytes("embedded-payload-fixture");
            string trusted=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            using var original=new MemoryStream(bytes);
            Require(Program.MatchesEmbeddedPayloadSha256(original,trusted),"correct embedded payload hash rejected");
            return Task.CompletedTask;
          });
          await Test("embedded payload hash rejects altered same-length bytes",w=>{
            byte[] bytes=System.Text.Encoding.UTF8.GetBytes("embedded-payload-fixture");
            string trusted=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            bytes[0]^=0x01;
            using var changed=new MemoryStream(bytes);
            Require(!Program.MatchesEmbeddedPayloadSha256(changed,trusted),"tampered embedded payload accepted");
            return Task.CompletedTask;
          });
          await Test("embedded payload hash rejects invalid and missing expected digest",w=>{
            foreach(string? digest in new string?[]{null,"00",new string('X',64)})
            {
              using var stream=new MemoryStream(new byte[]{1,2,3});
              Require(!Program.MatchesEmbeddedPayloadSha256(stream,digest),"invalid expected digest accepted");
            }
            return Task.CompletedTask;
          });
          await Test("installer results require safe exits and installed version readback",w=>{
            foreach(int code in new[]{0,1641,3010}) Require(Program.IsSuccessfulInstallerExitCode(code),"valid installer code rejected");
            foreach(int code in new[]{-1,1,5,1603,1618,1638}) Require(!Program.IsSuccessfulInstallerExitCode(code),"failure code accepted");
            string target=Path.Combine(Path.GetTempPath(),"spanky-update-readback-"+Guid.NewGuid().ToString("N"));
            try {
              Directory.CreateDirectory(target);
              File.Copy(typeof(Program).Assembly.Location,Path.Combine(target,"SpankyhoOptimizer.exe"));
              File.Copy(typeof(Program).Assembly.Location,Path.Combine(target,"SpankyhoOptimizer.dll"));
              Require(Program.VerifyUpdateReadback(target,Program.Version,target),"matching installed payload and registration rejected");
              Require(!Program.VerifyUpdateReadback(target,"0.0.0",target),"wrong uninstall version accepted");
              Require(!Program.VerifyUpdateReadback(target,Program.Version,target+"-other"),"foreign uninstall path accepted");
              File.WriteAllText(Path.Combine(target,"SpankyhoOptimizer.dll"),"invalid payload");
              Require(!Program.VerifyUpdateReadback(target,Program.Version,target),"invalid installed DLL accepted");
            } finally { if(Directory.Exists(target))Directory.Delete(target,true); }
            return Task.CompletedTask;
          });
          await Test("dependency downloads enforce strict size bounds before disk writes",w=>{
            Program.ValidateDownloadBounds(0,null);
            Program.ValidateDownloadBounds(Program.MaxDependencyDownloadBytes,Program.MaxDependencyDownloadBytes);
            foreach (var item in new (long,long?)[] {(-1,null),(Program.MaxDependencyDownloadBytes+1,null),(0,Program.MaxDependencyDownloadBytes+1),(8,7),(0,-1) }) {
              bool rejected=false;
              try { Program.ValidateDownloadBounds(item.Item1,item.Item2); } catch(InvalidDataException) { rejected=true; }
              Require(rejected,"unsafe dependency byte count accepted");
            }
            return Task.CompletedTask;
          });
          await Test("corrupted dependency bytes fail pinned SHA512 integrity",w=>{
            string file=Path.Combine(Path.GetTempPath(),"spanky-hash-"+Guid.NewGuid().ToString("N"));
            try {
              File.WriteAllText(file,"official fixture");
              using var sha=System.Security.Cryptography.SHA512.Create();
              string expected=Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file)));
              Program.VerifySha512(file,expected);
              File.AppendAllText(file,"tampered");
              bool rejected=false;
              try { Program.VerifySha512(file,expected); } catch(InvalidDataException) { rejected=true; }
              Require(rejected,"tampered runtime accepted");
            } finally {if(File.Exists(file))File.Delete(file);}
            return Task.CompletedTask;
          });
          await Test("dependency downloader refuses HTTP, missing and credentialed redirect targets",w=>{
            Program.RequireHttpsDownload(new Uri("https://builds.dotnet.microsoft.com/runtime.exe"));
            foreach(var uri in new Uri?[] {null,new Uri("http://example.com/file.exe"),new Uri("https://user:secret@example.com/file.exe")}) {
              bool rejected=false;
              try { Program.RequireHttpsDownload(uri); } catch(InvalidDataException) { rejected=true; }
              Require(rejected,"unsafe source accepted");
            }
            return Task.CompletedTask;
          });
          await Test("installer prerequisite does not mistake plain dotnet runtime for desktop",w=>{
            string root=Path.Combine(Path.GetTempPath(),"spanky-prereq-"+Guid.NewGuid().ToString("N"));
            try {
              Directory.CreateDirectory(Path.Combine(root,"shared","Microsoft.NETCore.App","10.0.12"));
              Require(!InstallerPrerequisites.HasDesktopRuntime10(out _,root),"plain dotnet runtime incorrectly accepted");
              Directory.CreateDirectory(Path.Combine(root,"shared","Microsoft.WindowsDesktop.App","9.0.13"));
              Require(!InstallerPrerequisites.HasDesktopRuntime10(out _,root),"desktop 9 incorrectly accepted");
              Directory.CreateDirectory(Path.Combine(root,"shared","Microsoft.WindowsDesktop.App","10.0.12"));
              Require(InstallerPrerequisites.HasDesktopRuntime10(out var version,root) && version=="10.0.12","desktop 10 not accepted");
            } finally { Directory.Delete(root,true); }
            return Task.CompletedTask;
          });
          await Test("compact 712x486 glass window retains frameless geometry",w=>{
            double fit=InstallerWindow.ComputeCompactWindowScale(SystemParameters.WorkArea.Width,SystemParameters.WorkArea.Height);
            Require(Math.Abs(w.Width-712*fit)<0.01 && Math.Abs(w.Height-486*fit)<0.01,"window dimensions regressed");
            Require(w.ResizeMode==ResizeMode.NoResize && w.WindowStyle==WindowStyle.None,"native chrome appeared");
            var root=(FrameworkElement)w.Content;
            root.Measure(new Size(w.Width,w.Height));
            root.Arrange(new Rect(new Point(0,0),new Size(w.Width,w.Height)));
            root.UpdateLayout();
            Require(root.DesiredSize.Width<=712 && root.DesiredSize.Height<=486,"layout needs more space than viewport");
            return Task.CompletedTask;
          });
          await Test("installation layout reserves progress without main scrollbar",w=>{
            var ui=(FrameworkElement)w.Content;
            Require(ui.FindName("InstallerScroll")==null,"old installer scrollbar reintroduced");
            Require(Find<FrameworkElement>(w,"ProgressArea").Visibility==Visibility.Visible,"progress must be permanently reserved");
            var rows=Find<Grid>(w,"InstallerContent").RowDefinitions;
            Require(rows.Count==6,"compact six-zone layout broken");
            Require(rows.Sum(x=>x.Height.Value)==452,"fixed zones no longer fit compact window");
            return Task.CompletedTask;
          });
          await Test("dark readable installer retains functional frameless titlebar controls",w=>{
            var ui=(FrameworkElement)w.Content;
            var frame=(Border)ui;
            var tint=(System.Windows.Media.SolidColorBrush)frame.Background;
            Require(tint.Color.A >= 90 && tint.Color.A <= 170,"outer installer shell must be moderately translucent");
            Require(Find<Button>(w,"MinimizeButton").IsEnabled,"minimize button missing");
            Require(ui.FindName("InstallerVersionLabel")==null,
                    "unwanted MODERN SETUP version caption was reintroduced");
            Require(ui.FindName("DotNetVisibleStatus")==null && ui.FindName("WebViewVisibleStatus")==null,
                    "unwanted runtime status labels were reintroduced to main window");
            Require(Find<TextBlock>(w,"ProgressPercent").Text=="0 %","initial progress readout missing");
            Require((bool)Call(w,"CanDragFromCaption",Find<Grid>(w,"DragArea"))!,"caption must drag");
            Require(!(bool)Call(w,"CanDragFromCaption",Find<Button>(w,"CloseButton"))!,
                    "close button click bubbles into dragging");
            Require(!(bool)Call(w,"CanDragFromCaption",Find<Button>(w,"MinimizeButton"))!,
                    "minimize button click bubbles into dragging");
            return Task.CompletedTask;
          });
          await Test("dark matte installer keeps rounded layered shell readable",w=>{
            Require(w.AllowsTransparency && w.WindowStyle==WindowStyle.None,
                    "installer reverted to non-layered opaque WPF host");
            Require(w.Background==Brushes.Transparent,"HWND root paints an opaque sheet");
            var shell=(Border)w.Content;
            var tint=((SolidColorBrush)shell.Background).Color;
            Require(tint.A>=90 && tint.A<=170,"outer glass layer should be gently translucent, not opaque or invisible");
            Require(shell.BorderThickness.Left==0 && shell.BorderThickness.Top==0,
                    "bright perimeter stroke was reintroduced");
            var sheet=Find<Border>(w,"SingleGlassSurface");
            Require(sheet.Background is SolidColorBrush glass && glass.Color.A>=48 && glass.Color.A<=125,
                    "main glass sheet should visibly reveal the desktop without sacrificing contrast");
            var hero=Find<Border>(w,"InstallCard");
            Require(hero.Background is SolidColorBrush heroBrush && heroBrush.Color.A==0,
                    "opaque hero card stack was reintroduced");
            Require(((FrameworkElement)w.Content).FindName("HardwareCard")==null,
                    "unnecessary hardware information was reintroduced");
            Require(Find<Border>(w,"EfficiencyCard").Background is SolidColorBrush progress &&
                    progress.Color.A>=200 && progress.Color.A<=245,
                    "large progress panel should remain darker than the main glass while retaining some transparency");
            Require(Find<FrameworkElement>(w,"SingleGlassSurface").IsHitTestVisible==false,
                    "glass overlay blocks installer controls");
            // Actual rendered alpha, not just declared paint colors. A WPF
            // layered window must leave enough alpha for desktop pixels through it.
            var view=(FrameworkElement)w.Content;
            view.Measure(new Size(w.Width,w.Height));
            view.Arrange(new Rect(0,0,w.Width,w.Height));
            view.UpdateLayout();
            int width=(int)w.Width, height=(int)w.Height;
            var rendered=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
            rendered.Render(view);
            var pixels=new byte[width*height*4];
            rendered.CopyPixels(pixels,width*4,0);
            byte AlphaAt(int x,int y)=>pixels[((y*width+x)*4)+3];
            Require(AlphaAt(100,250)>=170 && AlphaAt(100,250)<=228,
                    $"glass installer core should reveal the wallpaper moderately (alpha={AlphaAt(100,250)})");
            Require(AlphaAt(4,240)>=155 && AlphaAt(4,240)<=215,
                    $"installer edge should remain visibly translucent (alpha={AlphaAt(4,240)})");
            return Task.CompletedTask;
          });
          await Test("path text remains editable with dark focus outline",w=>{
            var path=Find<TextBox>(w,"InstallPath");
            path.Text=@"C:\Program Files\Spankyho Optimizer";
            Require(path.ApplyTemplate(),"native text editor template missing");
            path.UpdateLayout();
            Require(path.Template.FindName("PART_ContentHost",path) is ScrollViewer,
                    "native WPF text host missing, path could be clipped");
            Require(path.Text.EndsWith("Spankyho Optimizer",StringComparison.Ordinal),
                    "path value corrupted by text style");
            var rim=path.BorderBrush as SolidColorBrush;
            Require(rim is not null && rim.Color.R<130 && rim.Color.G<175,
                    "path border is bright white instead of muted blue");
            return Task.CompletedTask;
          });
          await Test("glass error sheet stays readable and chevron expands details",w=>{
            var overlay=Find<Grid>(w,"ErrorOverlay");
            var card=Find<Border>(w,"ErrorCard");
            var scrim=(SolidColorBrush)overlay.Background;
            var surface=(SolidColorBrush)card.Background;
            Require(scrim.Color.A>=144,"error scrim no longer hides underlying text");
            Require(surface.Color.A>=220,"error panel translucent enough to leak underlying text");
            var more=Find<Expander>(w,"ErrorMore");
            Require(more.ApplyTemplate(),"modern expander template was not applied");
            var details=more.Template.FindName("ExpandSite",more) as FrameworkElement;
            Require(details is not null,"modern expandable details missing");
            more.IsExpanded=true;
            more.UpdateLayout();
            Require(details!.Visibility==Visibility.Visible,"details did not expand");
            more.IsExpanded=false;
            more.UpdateLayout();
            Require(details.Visibility==Visibility.Collapsed,"details did not collapse");
            return Task.CompletedTask;
          });
          await Test("installer progress percent mirrors actual progress phase",async w=>{
            Call(w,"SetProgress",37,"INSTALACE","Rozbaluji soubory");
            Require(Find<TextBlock>(w,"ProgressPercent").Text=="37 %","progress percent did not update");
            Call(w,"SetProgress",100,"HOTOVO","Nainstalováno");
            Require(Find<TextBlock>(w,"ProgressPercent").Text=="100 %","completion percent did not update");
            var popup=new UpdatePopupWindow(Path.GetTempPath(),false);
            var gate=new TaskCompletionSource<bool>();
            Action<int,string>? report=null;
            Task run=popup.RunUpdateAsync(p=>{report=p;p(73,"Instaluji");return gate.Task;});
            Require(Field<TextBlock>(popup,"_percent").Text=="73 %","automatic popup lost actual install percentage");
            report!(60,"stale phase");
            Require(Field<TextBlock>(popup,"_percent").Text=="73 %","automatic progress moved backwards");
            Require(!popup.ShowActivated && !popup.ShowInTaskbar,"automatic popup steals focus or adds taskbar UI");
            Require(Field<Border>(popup,"_fill").Background is SolidColorBrush kick && kick.Color==Color.FromRgb(83,252,24),"automatic install lost Kick green");
            gate.SetResult(true);
            await run;
            report(51,"late progress");
            Require(Field<TextBlock>(popup,"_percent").Text=="100 %","late install progress overwrote success");
            var failed=new UpdatePopupWindow(Path.GetTempPath(),false);
            try {
              await failed.RunUpdateAsync(_=>Task.FromException(new IOException("isolated update failure")));
              Require(Field<TextBlock>(failed,"_percent").Text=="CHYBA","failed automatic update claimed success");
              Require(Field<Button>(failed,"_close").Visibility==Visibility.Visible,"automatic install failure cannot be dismissed");
            } finally { failed.Close(); }
          });
          await Test("large native progress retains measured value and rounded blue indicator",w=>{
            var bar=Find<ProgressBar>(w,"InstallProgress");
            Require(bar.Height>=20,"installer progress bar is too small");
            Require(bar.ApplyTemplate(),"installer progress template did not load");
            Require(bar.Template.FindName("PART_Indicator",bar) is Border,
                    "rounded progress indicator visual missing");
            Require(((FrameworkElement)w.Content).FindName("HardwareCard")==null,
                    "unnecessary hardware summary returned");
            Require(w.GetType().GetMethod("ReadSystemSpecificationsAsync",Flags)==null,
                    "unnecessary PowerShell hardware probe still exists");
            Call(w,"SetProgress",43,"INSTALACE","Ověřuji obsah");
            double actual=(double)bar.GetAnimationBaseValue(
                System.Windows.Controls.Primitives.RangeBase.ValueProperty);
            Require(Math.Abs(actual-43)<.01,
                    "animated progress does not retain actual worker-reported percentage");
            Require(Find<TextBlock>(w,"ProgressPercent").Text=="43 %",
                    "numeric progress does not match worker percentage");
            Call(w,"SetProgress",100,"HOTOVO","Instalace dokončena");
            Require(bar.Value==100 && Find<TextBlock>(w,"ProgressPercent").Text=="100 %",
                    "terminal completion should show 100% immediately");
            Call(w,"SetProgress",0,"CHYBA","Instalace nebyla dokončena");
            Require(bar.Value==0,"failure must reset progress without delayed fake movement");
            return Task.CompletedTask;
          });
          await Test("installer titlebar minimize action works without resizing",w=>{
            Find<Button>(w,"MinimizeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(w.WindowState==WindowState.Minimized,"minimize click was ignored");
            Require(w.Width<=712 && w.Height<=486,"minimize enlarged compact geometry");
            w.WindowState=WindowState.Normal;
            return Task.CompletedTask;
          });
          await Test("small-DPI screens fit while desktop size never grows",w=>{
            double large=InstallerWindow.ComputeCompactWindowScale(1920,1080);
            double small=InstallerWindow.ComputeCompactWindowScale(640,420);
            Require(large==1,"large screens must remain 712x486");
            Require(small<1 && small>=0.5,"small screen was not scaled");
            Require(712*small<=640-20 && 486*small<=420-20,"scaled layout overflows work area");
            Require(InstallerWindow.ComputeCompactWindowScale(0,0)==1,"invalid desktop size must safely fallback");
            return Task.CompletedTask;
          });
          await Test("native glass prefers host-verified acrylic with safe fallback",w=>{
            Require(InstallerWindow.ShouldPreferAcrylic(false),"working native acrylic not selected");
            Require(!InstallerWindow.ShouldPreferAcrylic(true),"high contrast acrylic should be off");
            Require(!InstallerWindow.ShouldUseDwmFallback(true,false),"never stack DWM over acrylic");
            Require(InstallerWindow.ShouldUseDwmFallback(false,false),"DWM fallback lost when acrylic fails");
            Require(!InstallerWindow.ShouldUseDwmFallback(false,true),"high contrast material must be off");
            return Task.CompletedTask;
          });
          await Test("progress burst queues exactly one UI dispatch",async w=>{
            // Block this UI thread while a worker produces 5000 updates; verify
            // only one pending dispatch, then allow Render to drain it.
            Task.Run(()=>{
                for(int i=0;i<5000;i++)
                    Call(w,"SetProgress",25,"STAHOVÁNÍ","Soubor "+i);
                Call(w,"SetProgress",77,"INSTALACE","Poslední soubor");
            }).GetAwaiter().GetResult();
            Require(Field<bool>(w,"_progressDispatchQueued"),"progress dispatcher never scheduled");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(Find<TextBlock>(w,"ProgressPercent").Text=="77 %","latest progress was lost");
            Require(Find<TextBlock>(w,"StatusText").Text=="Poslední soubor","older progress replaced latest");
            Require(!Field<bool>(w,"_progressDispatchQueued"),"dispatch queue did not drain");
          });
          await Test("real install worker 5000 file reports use one dispatcher post",async w=>{
            var complete=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<int,string>? captured=null;
            var running=Run(w,(_,_,report)=>{
                captured=report;
                // Intentionally hold the WPF UI thread while the extractor
                // produces its entire burst. There must be NO per-file UI work.
                Task.Run(()=>{
                    for(int i=0;i<5000;i++)
                        report(60 + i*23/5000, "Rozbaluji soubor "+i);
                    report(83, "Poslední ověřený soubor");
                }).GetAwaiter().GetResult();
                return complete.Task;
            });
            Require(Field<int>(w,"_progressDispatchScheduledCount")==1,
                    "one dispatcher callback per file was scheduled instead of one coalesced post");
            Require(Field<bool>(w,"_progressDispatchQueued"),
                    "live worker progress was not batched for the render dispatcher");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(Find<TextBlock>(w,"ProgressPercent").Text=="83 %",
                    "last worker progress percent was lost");
            Require(Find<TextBlock>(w,"StatusText").Text=="Poslední ověřený soubor",
                    "last worker progress status was lost");
            Require(!Field<bool>(w,"_progressDispatchQueued"),"pending progress was not drained");
            complete.SetResult();
            await running;
            Require(Find<TextBlock>(w,"ProgressPercent").Text=="100 %",
                    "coalesced worker progress overwrote completion");
            Task.Run(()=>captured!(93,"OPOŽDĚNÝ SOUBOR")).GetAwaiter().GetResult();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(Find<TextBlock>(w,"ProgressPercent").Text=="100 %" &&
                    Find<TextBlock>(w,"PhaseText").Text=="HOTOVO",
                    "late worker report replaced committed install completion");
          });
          await Test("stale worker progress cannot overwrite terminal success",async w=>{
            Task.Run(()=>Call(w,"SetProgress",68,"INSTALACE","Old queued progress")).GetAwaiter().GetResult();
            Call(w,"SetProgress",100,"HOTOVO","Instalace dokončena");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(Find<TextBlock>(w,"ProgressPercent").Text=="100 %","stale worker overwrote terminal success");
            Require(Find<TextBlock>(w,"StatusText").Text=="Instalace dokončena",
                "stale progress overwrote terminal description");
          });
          await Test("installation progress and completion keep stable geometry",async w=>{
            var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running=Run(w,(_,_,report)=>{report(72,"Rozbaluji soubory");return finish.Task;});
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(Find<FrameworkElement>(w,"ProgressArea").Visibility==Visibility.Visible,"progress disappears while busy");
            Require(w.Width<=712 && w.Height<=486,"busy phase resized");
            finish.SetResult();
            await running;
            Require(Find<FrameworkElement>(w,"ProgressArea").Visibility==Visibility.Visible,"progress disappears when done");
            Require(w.Width<=712 && w.Height<=486,"completion phase resized");
          });
          await Test("compact error sheet fits installer viewport",async w=>{
            await Fail(w,new IOException("fixture"));
            var card=Find<Border>(w,"ErrorCard");
            double scale=InstallerWindow.ComputeCompactWindowScale(SystemParameters.WorkArea.Width,SystemParameters.WorkArea.Height);
            Require(card.Width*scale<=w.Width-50*scale && card.MaxHeight*scale<=w.Height-50*scale,"error overlay exceeds compact viewport");
            Require(Find<Grid>(w,"ErrorOverlay").Visibility==Visibility.Visible,"error overlay missing");
          });
          await Test("zstd archive extracts through bounded streaming",w=>{
            string dest=Path.Combine(Path.GetTempPath(),"spanky-zstd-ui-"+Guid.NewGuid().ToString("N"));
            try {
              int progress=-1;
              using var content=new MemoryStream(PayloadFixture.Build("SpankyhoOptimizer.exe","test"));
              PayloadArchive.Extract(content,dest,(pct,_)=>progress=pct);
              Require(File.ReadAllText(Path.Combine(dest,"SpankyhoOptimizer.exe"))=="test","file contents damaged");
              Require(progress==90,"final progress not reported");
            } finally {if(Directory.Exists(dest))Directory.Delete(dest,true);}
            return Task.CompletedTask;
          });
          await Test("zstd archive rejects traversal before writing",w=>{
            string dest=Path.Combine(Path.GetTempPath(),"spanky-zstd-ui-"+Guid.NewGuid().ToString("N"));
            try {
              using var content=new MemoryStream(PayloadFixture.Build("../escape.txt"));
              bool blocked=false;
              try {PayloadArchive.Extract(content,dest,null);} catch(InvalidDataException){blocked=true;}
              Require(blocked,"traversal not rejected");
              Require(!File.Exists(Path.Combine(Path.GetDirectoryName(dest)!,"escape.txt")),"traversal escaped");
            } finally {if(Directory.Exists(dest))Directory.Delete(dest,true);}
            return Task.CompletedTask;
          });
          await Test("zstd archive rejects symbolic links and non-file entries",w=>{
            string dest=Path.Combine(Path.GetTempPath(),"spanky-zstd-ui-"+Guid.NewGuid().ToString("N"));
            try {
              using var content=new MemoryStream(PayloadFixture.Build("danger",asSymlink:true));
              bool blocked=false;
              try {PayloadArchive.Extract(content,dest,null);} catch(InvalidDataException){blocked=true;}
              Require(blocked,"symbolic link accepted");
            } finally {if(Directory.Exists(dest))Directory.Delete(dest,true);}
            return Task.CompletedTask;
          });
          await Test("zstd archive rejects mismatched payload manifest",w=>{
            string dest=Path.Combine(Path.GetTempPath(),"spanky-zstd-ui-"+Guid.NewGuid().ToString("N"));
            try {
              using var content=new MemoryStream(PayloadFixture.Build("SpankyhoOptimizer.exe", extraManifestFiles:1));
              bool blocked=false;
              try {PayloadArchive.Extract(content,dest,null);} catch(InvalidDataException){blocked=true;}
              Require(blocked,"incomplete archive accepted");
            } finally {if(Directory.Exists(dest))Directory.Delete(dest,true);}
            return Task.CompletedTask;
          });
          await Test("zstd refuses reparse root even before reading the payload",w=>{
            Require(PayloadArchive.IsSafeRootAttributes(FileAttributes.Directory),"ordinary staging folder rejected");
            Require(!PayloadArchive.IsSafeRootAttributes(FileAttributes.Directory|FileAttributes.ReparsePoint),
                    "junction/symlink staging root accepted");
            return Task.CompletedTask;
          });
          await Test("zstd rejects mismatched product version before writing",w=>{
            string dest=Path.Combine(Path.GetTempPath(),"spanky-zstd-ui-"+Guid.NewGuid().ToString("N"));
            try {
              using var content=new MemoryStream(PayloadFixture.Build("SpankyhoOptimizer.exe",
                                          versionOverride:"0.0.0"));
              bool blocked=false;
              try {PayloadArchive.Extract(content,dest,null);} catch(InvalidDataException){blocked=true;}
              Require(blocked,"other-version payload accepted");
              Require(Directory.GetFiles(dest,"*",SearchOption.AllDirectories).Length==0,
                      "other-version payload was written");
            } finally {if(Directory.Exists(dest))Directory.Delete(dest,true);}
            return Task.CompletedTask;
          });
          await Test("zstd allocation-free path validation accepts legitimate nested file names",w=>{
            foreach(var name in new[]{"SpankyhoOptimizer.exe","Assets/Changelog.md","fonts/žlutý font.ttf",
                                      "_/file...txt","one/two/three/four.json"})
              Require(PayloadArchive.IsSafeEntryName(name),"valid file name rejected: "+name);
            return Task.CompletedTask;
          });
          await Test("zstd path validator blocks empty, dot, drive, UNC, ADS and backslash",w=>{
            foreach(var name in new[]{"","/",".","..","./a","../a","a/..","a/.","a/../b",
                                      "a/./b","a//b","a/","/a","C:/Windows/test",
                                      @"C:\Windows\test",@"\\server\share\file","a:stream","x/y:zone"})
              Require(!PayloadArchive.IsSafeEntryName(name),"unsafe file name accepted: "+name);
            return Task.CompletedTask;
          });
          await Test("zstd safely reuses existing nested folder during extraction",w=>{
            string dest=Path.Combine(Path.GetTempPath(),"spanky-zstd-ui-"+Guid.NewGuid().ToString("N"));
            try {
              using var content=new MemoryStream(PayloadFixture.Build(
                "SpankyhoOptimizer.exe", data:"test", secondName:"Assets/Changelog.md"));
              PayloadArchive.Extract(content,dest,null);
              Require(File.ReadAllText(Path.Combine(dest,"Assets","Changelog.md"))=="test",
                      "nested directory extraction failed");
              Require(Directory.GetFiles(dest,"*",SearchOption.AllDirectories).Length==2,
                      "unexpected number of files");
            } finally {if(Directory.Exists(dest))Directory.Delete(dest,true);}
            return Task.CompletedTask;
          });
          await Test("zstd invalid copy buffer fails before writing target files",w=>{
            string dest=Path.Combine(Path.GetTempPath(),"spanky-zstd-ui-"+Guid.NewGuid().ToString("N"));
            try {
              using var content=new MemoryStream(PayloadFixture.Build("SpankyhoOptimizer.exe"));
              bool blocked=false;
              try {PayloadArchive.Extract(content,dest,null,copyBufferSize:2048);}
              catch(ArgumentOutOfRangeException){blocked=true;}
              Require(blocked,"too-small buffer accepted");
              Require(!Directory.Exists(dest),"destination was modified for invalid buffer");
            } finally {if(Directory.Exists(dest))Directory.Delete(dest,true);}
            return Task.CompletedTask;
          });
          await Test("zstd refuses duplicate TAR target even with matching manifest",w=>{
            string dest=Path.Combine(Path.GetTempPath(),"spanky-zstd-ui-"+Guid.NewGuid().ToString("N"));
            try {
              using var payload=new MemoryStream(PayloadFixture.Build("SpankyhoOptimizer.exe",
                                                     data:"fixture", secondName:"SpankyhoOptimizer.exe"));
              bool refused=false;
              try { PayloadArchive.Extract(payload,dest,null); }
              catch(IOException){ refused=true; }
              Require(refused,"duplicate archive overwrote an earlier payload file");
              Require(File.ReadAllText(Path.Combine(dest,"SpankyhoOptimizer.exe"))=="fixture",
                      "duplicate file unexpectedly changed contents");
            } finally {if(Directory.Exists(dest)) Directory.Delete(dest,true);}
            return Task.CompletedTask;
          });
          await Test("zstd refuses to overwrite existing staging files",w=>{
            string dest=Path.Combine(Path.GetTempPath(),"spanky-zstd-ui-"+Guid.NewGuid().ToString("N"));
            try {
              Directory.CreateDirectory(dest);
              string existing=Path.Combine(dest,"SpankyhoOptimizer.exe");
              File.WriteAllText(existing,"existing-protected");
              using var payload=new MemoryStream(PayloadFixture.Build("SpankyhoOptimizer.exe"));
              bool refused=false;
              try { PayloadArchive.Extract(payload,dest,null); }
              catch(IOException){ refused=true; }
              Require(refused,"preexisting staging file was overwritten");
              Require(File.ReadAllText(existing)=="existing-protected","preexisting file changed");
            } finally {if(Directory.Exists(dest)) Directory.Delete(dest,true);}
            return Task.CompletedTask;
          });
          await Test("zstd oversized entry is refused before any file write",w=>{
            string dest=Path.Combine(Path.GetTempPath(),"spanky-zstd-ui-"+Guid.NewGuid().ToString("N"));
            try {
              using var archive=new MemoryStream(PayloadFixture.Build("SpankyhoOptimizer.exe",
                                                     data:"fixture", extraManifestBytes:-6));
              bool refused=false;
              try {PayloadArchive.Extract(archive,dest,null);}
              catch(InvalidDataException){refused=true;}
              Require(refused,"oversized archive entry accepted");
              Require(Directory.GetFiles(dest,"*",SearchOption.AllDirectories).Length==0,
                      "oversized entry wrote data before manifest bounds validation");
            } finally {if(Directory.Exists(dest))Directory.Delete(dest,true);}
            return Task.CompletedTask;
          });
          await Test("ready header is named and localized",w=>{Require(Find<TextBlock>(w,"InstallHeading").Text=="Připraveno k instalaci","heading");Require(Find<TextBlock>(w,"InstallBadge").Text=="PŘIPRAVENO","badge");return Task.CompletedTask;});
          await Test("failure restores controls before error presentation",async w=>{
            await Fail(w,new IOException("fixture",unchecked((int)0x80070020)));
            Require(!Field<bool>(w,"_busy"),"busy remains true");
            foreach(var id in new[]{"InstallButton","BrowseButton","CancelButton","CloseButton"})Require(Find<Button>(w,id).IsEnabled,id+" disabled");
            Require(Find<TextBox>(w,"InstallPath").IsEnabled,"path disabled");Require(Find<TextBlock>(w,"InstallBadge").Text=="CHYBA","stale READY badge");
            Require(Find<Grid>(w,"ErrorOverlay").Visibility==Visibility.Visible,"missing glass error");Require(Environment.ExitCode==1,"failure exit code");
          });
          await Test("access denied is not mislabeled as file lock",async w=>{await Fail(w,new UnauthorizedAccessException("fixture"));Require(Find<TextBlock>(w,"ErrorHeading").Text=="Přístup do složky byl odepřen","wrong access diagnosis");});
          await Test("inner Win32 access-denied code overrides generic outer IO",async w=>{await Fail(w,new IOException("outer",new IOException("inner",unchecked((int)0x80070005))));Require(Find<TextBlock>(w,"ErrorHeading").Text.Contains("odepřen"),"inner error ignored");});
          await Test("sharing violation receives explicit recovery",async w=>{await Fail(w,new IOException("fixture",unchecked((int)0x80070020)));Require(Find<TextBlock>(w,"ErrorHeading").Text=="Soubory právě používá jiný program","wrong lock diagnosis");Require(Find<TextBlock>(w,"ErrorGuidance").Text.Contains("opakovat"),"no retry guidance");});
          await Test("disk-full receives different recovery",async w=>{await Fail(w,new IOException("fixture",unchecked((int)0x80070070)));Require(Find<TextBlock>(w,"ErrorHeading").Text=="Na disku není dost místa","disk full ignored");});
          await Test("network errors retain network recovery",async w=>{await Fail(w,new System.Net.Http.HttpRequestException("offline"));Require(Find<TextBlock>(w,"ErrorHeading").Text.Contains("Stažení"),"network message");});
          await Test("failure details remain text and preserve Unicode",async w=>{var value="Příliš žluťoučký kůň <script> & 🧪 "+new string('Ž',8000);await Fail(w,new InvalidOperationException(value));Require(Find<TextBox>(w,"ErrorDetails").Text.Contains(value),"exception text lost/parsed");Require(Find<TextBox>(w,"ErrorDetails").IsReadOnly,"details editable");});
          await Test("dismiss removes overlay and re-enables the form",async w=>{await Fail(w,new IOException("fixture"));Find<Button>(w,"ErrorDismiss").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(Find<Grid>(w,"ErrorOverlay").Visibility==Visibility.Collapsed,"overlay still mounted");Require(Find<Grid>(w,"InstallerContent").IsEnabled,"form blocked");});
          await Test("escape dismisses the error panel",async w=>{await Fail(w,new IOException("fixture"));using var keySource=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("key-test"){Width=1,Height=1});var ev=new KeyEventArgs(Keyboard.PrimaryDevice,keySource,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};w.RaiseEvent(ev);Require(Find<Grid>(w,"ErrorOverlay").Visibility==Visibility.Collapsed,"Escape ineffective");});
          await Test("retry success clears failure exit and stale header",async w=>{await Fail(w,new IOException("fixture"));await Run(w,(_,_,p)=>{p(70,"fixture");return Task.CompletedTask;});Require(Environment.ExitCode==0,"stale error exit");Require(Find<TextBlock>(w,"InstallBadge").Text=="HOTOVO","stale error header");Require(Find<Grid>(w,"ErrorOverlay").Visibility==Visibility.Collapsed,"stale overlay");Require(Find<StackPanel>(w,"FinalActions").Visibility==Visibility.Visible,"no completion controls");});
          await Test("duplicate install invocation never starts a second operation",async w=>{var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;var first=Run(w,(_,_,_)=>{calls++;return finish.Task;});await Run(w,(_,_,_)=>{calls++;return Task.CompletedTask;});Require(calls==1,"duplicate install");Require(Find<TextBlock>(w,"InstallBadge").Text=="PROBÍHÁ","busy badge");finish.SetResult();await first;});
          await Test("late progress cannot replace terminal failure",async w=>{Action<int,string>? delayed=null;await Run(w,(_,_,p)=>{delayed=p;return Task.FromException(new IOException("fixture"));});delayed!(80,"STALE_PROGRESS");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Require(Find<TextBlock>(w,"PhaseText").Text=="CHYBA","late progress overwrote failure");});
          await Test("old attempt progress cannot corrupt retry",async w=>{Action<int,string>? old=null;await Run(w,(_,_,p)=>{old=p;return Task.FromException(new IOException("fixture"));});var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var next=Run(w,(_,_,p)=>{p(65,"CURRENT");return finish.Task;});old!(80,"STALE");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Require(Find<TextBlock>(w,"StatusText").Text!="STALE","cross-attempt progress");finish.SetResult();await next;});
          await Test("repeated failure does not accumulate dialogs",async w=>{for(int i=0;i<3;i++)await Fail(w,new IOException("failure "+i));Require(Application.Current.Windows.Count==1,"extra native modal windows");Require(Find<TextBox>(w,"ErrorDetails").Text.Contains("failure 2"),"stale error details");});
          await Test("invalid target fails before installation operation",async w=>{Find<TextBox>(w,"InstallPath").Text="C:\\not-the-product";int calls=0;await Run(w,(_,_,_)=>{calls++;return Task.CompletedTask;});Require(calls==0,"invalid path executed");Require(Find<Grid>(w,"ErrorOverlay").Visibility==Visibility.Visible,"validation error missing");});
          await Test("fan agent restarts for committed default install",w=>{int calls=0;bool ok=SpankyhoOptimizer.ModernSetup.Program.ResumeFanAgentForInstalledTarget(SpankyhoOptimizer.ModernSetup.Program.DefaultDirectory,()=>{calls++;return true;});Require(ok && calls==1,"registered task not requested");return Task.CompletedTask;});
          await Test("fan agent is not started for isolated test targets",w=>{int calls=0;bool ok=SpankyhoOptimizer.ModernSetup.Program.ResumeFanAgentForInstalledTarget(Path.Combine(Path.GetTempPath(),"spanky-test-install"),()=>{calls++;return true;});Require(!ok && calls==0,"test fixture attempted to start user fan task");return Task.CompletedTask;});
          await Test("failed fan restart does not invalidate committed install",w=>{bool ok=SpankyhoOptimizer.ModernSetup.Program.ResumeFanAgentForInstalledTarget(SpankyhoOptimizer.ModernSetup.Program.DefaultDirectory,()=>throw new IOException("fixture task denial"));Require(!ok,"start failure reported as success");return Task.CompletedTask;});
          var report=new{passed=failures==0,count=Results.Count,failures,results=Results};
          Console.WriteLine(JsonSerializer.Serialize(report));
          app.Shutdown(failures==0?0:1);
        }));
        return app.Run();
    }

}
