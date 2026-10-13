using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using TicTack;

namespace Corpus
{
    // --build-media options: exactly one sink (--staging-dir | --usb |
    // --iso). Drivers repeatable. Initialize arms the --usb format path.
    // PayloadDir skips the re-publish (ADK box without the SDK: hand it a
    // `dotnet publish -r win-x64 --self-contained` dir; it is never deleted).
    public sealed record MediaBuildOptions(
        string? AdkDir = null,
        string? IncludeStore = null,
        string? DbPath = null,
        string[]? Drivers = null,
        string? StagingDir = null,
        string? UsbDrive = null,
        string? IsoPath = null,
        bool Initialize = false,
        string? PayloadDir = null);

    // WinPE recovery-media builder (fetch -> assemble -> write). Only
    // StagePayload is Linux-runnable (pure file copy + JSON); ISO/USB
    // packaging needs the Windows ADK and fails loudly elsewhere.
    public static class MediaBuilder
    {
        public static void Build(MediaBuildOptions opts, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(opts);
            ArgumentNullException.ThrowIfNull(log);
            var sinks = (opts.StagingDir != null ? 1 : 0) + (opts.UsbDrive != null ? 1 : 0) + (opts.IsoPath != null ? 1 : 0);
            if (sinks != 1)
                throw new InvalidDataException("exactly one of --staging-dir, --usb, --iso is required");
            string publishDir;
            var ownPublish = opts.PayloadDir == null;
            if (ownPublish)
            {
                publishDir = RunDotnetPublish(log);
            }
            else
            {
                publishDir = opts.PayloadDir!;
                if (!File.Exists(Path.Combine(publishDir, "Corpus.exe")))
                    throw new InvalidDataException($"--payload has no Corpus.exe in {publishDir} (want a `dotnet publish -r win-x64 --self-contained` dir)");
            }
            try
            {
                var stageDir = Directory.CreateTempSubdirectory("corpus-media-").FullName;
                try
                {
                    StagePayload(stageDir, publishDir, opts, log);
                    if (opts.StagingDir != null)
                    {
                        CopyDir(stageDir, opts.StagingDir);
                        log.Info($"staged recovery media in {opts.StagingDir} (no ADK packaging: copy onto FAT32/NTFS media or finish on Windows)");
                        return;
                    }
                    if (!OperatingSystem.IsWindows())
                        throw new PlatformNotSupportedException("--iso/--usb need the Windows ADK; use --staging-dir on Linux");
                    var adk = ResolveAdk(opts.AdkDir);
                    var build = AdkBuild(adk);
                    if (build < MediaLayout.AdksMinBuild)
                        throw new InvalidDataException($"ADK build {build} is below the floor {MediaLayout.AdksMinBuild} — install the Windows 11 24H2+ ADK + WinPE add-on");
                    // copype refuses an existing destination: reserve the temp
                    // NAME without creating it (CreateTempSubdirectory would
                    // make copype fail every time).
                    var peWork = Path.Combine(Path.GetTempPath(), "corpus-pe-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        RunCopype(adk, peWork, log);
                        // One WIM mount serves drivers + the startnet hook:
                        // startnet.cmd lives INSIDE boot.wim (no Windows dir
                        // exists at the copype media root).
                        var wim = Path.Combine(peWork, "media", "sources", "boot.wim");
                        var mount = MountBootWim(wim, peWork, log);
                        try
                        {
                            foreach (var driver in opts.Drivers ?? [])
                                AddDriverToMount(mount, driver, log);
                            AppendStartnetHook(Path.Combine(mount, "Windows", "System32", "startnet.cmd"));
                        }
                        catch
                        {
                            // A failed image must never commit: best-effort
                            // discard, then the original error propagates.
                            UnmountBootWim(mount, peWork, commit: false, log);
                            throw;
                        }
                        UnmountBootWim(mount, peWork, commit: true, log);
                        CopyDir(stageDir, Path.Combine(peWork, "media"));
                        // Record the real ADK build (staging bakes 0: no ADK there).
                        var mediaDir = Path.Combine(peWork, "media");
                        var infoPath = Path.Combine(mediaDir, "Corpus", "media-info.json");
                        (MediaInfo.Load(infoPath) with { AdkBuild = build }).Save(infoPath);
                        if (opts.IsoPath != null)
                            RunOscdimg(adk, mediaDir, opts.IsoPath, log);
                        else
                            WriteUsb(mediaDir, opts.UsbDrive!, opts.Initialize, log);
                    }
                    finally
                    {
                        DeleteTree(peWork);
                    }
                }
                finally
                {
                    DeleteTree(stageDir);
                }
            }
            finally
            {
                // A handed-in --payload dir is the caller's: never delete it.
                if (ownPublish)
                    DeleteTree(publishDir);
            }
        }

        // Linux-runnable pure part: publish closure + baked config + assets
        // (+ optional store copy) + media-info.json, then AssertLayout.
        // Writes nothing else, so LayoutExact can assert the set.
        public static void StagePayload(string stageDir, string publishDir, MediaBuildOptions opts, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(stageDir);
            ArgumentNullException.ThrowIfNull(publishDir);
            ArgumentNullException.ThrowIfNull(opts);
            ArgumentNullException.ThrowIfNull(log);
            if (!Directory.Exists(publishDir))
                throw new InvalidDataException($"publish dir missing: {publishDir}");
            var corpusDir = Path.Combine(stageDir, "Corpus");
            Directory.CreateDirectory(corpusDir);
            CopyDir(publishDir, corpusDir);
            File.WriteAllText(Path.Combine(corpusDir, "config_corpus.yaml"), BakedConfig(opts));
            var assets = FindAssetsDir();
            foreach (var file in new[] { "restore.cmd", "README-restore.txt" })
                File.Copy(Path.Combine(assets, file), Path.Combine(corpusDir, file), overwrite: true);
            var hasStore = opts.IncludeStore != null;
            int[] gens = [];
            if (hasStore)
            {
                CopyStore(opts, corpusDir, log);
                gens = ReadStoreGens(opts.IncludeStore!);
            }
            new MediaInfo(0, CorpusVersion(), ["base"], opts.Drivers ?? [], gens, DateTime.UtcNow.ToString("o"))
                .Save(Path.Combine(corpusDir, "media-info.json"));
            MediaLayout.AssertLayout(stageDir, hasStore);
            log.Info($"staged recovery payload in {stageDir}" + (hasStore ? $" ({gens.Length} store generations)" : ""));
        }

        // Baked stick config: store_path 'store' resolves beside Corpus.exe
        // at restore time (drive letter unknown at build); empty without
        // --include-store (operator passes --store; README says so).
        internal static string BakedConfig(MediaBuildOptions opts) =>
            "# Baked by Corpus --build-media (WinPE stick). Do not hand-edit:\n"
            + "# store comes from the stick (or pass --store <path> at restore).\n"
            + (opts.IncludeStore != null ? "store_path: 'store'\n" : "store_path: ''\n")
            + "state_db_path: ''\nchunk_mib: 4\ncompression:\n  level: 3\nlogging:\n  level: info\n  path: ''\n  console: true\n";

        internal static string CorpusVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString() ?? "unknown";
        }

        public static string ResolveAdk(string? dir)
        {
            var adk = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Assessment and Deployment Kit");
            if (!Directory.Exists(adk))
                throw new InvalidDataException($"ADK not found at {adk} (pass --adk <dir>; needs the ADK + WinPE add-on)");
            foreach (var rel in new[] { Path.Combine("Windows Preinstallation Environment", "copype.cmd"), Path.Combine("Deployment Tools", "amd64", "Oscdimg", "oscdimg.exe") })
            {
                if (!File.Exists(Path.Combine(adk, rel)))
                    throw new InvalidDataException($"ADK at {adk} is missing {rel} — install the WinPE add-on (copype drift fails closed)");
            }
            return adk;
        }

        // ADK build from the inbox DISM version (tracks the Windows build).
        public static int AdkBuild(string adkDir)
        {
            ArgumentNullException.ThrowIfNull(adkDir);
            var dism = Path.Combine(adkDir, "Deployment Tools", "amd64", "DISM", "dism.exe");
            if (!File.Exists(dism))
                throw new InvalidDataException($"cannot determine ADK build (no {dism}) — install the Windows 11 24H2+ ADK + WinPE add-on");
            return FileVersionInfo.GetVersionInfo(dism).FileBuildPart;
        }

        private static void CopyStore(MediaBuildOptions opts, string corpusDir, ILogger log)
        {
            var src = opts.IncludeStore!;
            if (!Directory.Exists(src))
                throw new InvalidDataException($"--include-store missing: {src}");
            var dst = Path.Combine(corpusDir, "store");
            Directory.CreateDirectory(dst);
            // The index does NOT live in the store (Store ctor takes dbPath
            // from StateRoot), so a bare store copy is unrestorable — the db
            // ships next to the copy and restore prefers it (§1).
            foreach (var sub in new[] { "packs", "manifests" })
            {
                var s = Path.Combine(src, sub);
                if (Directory.Exists(s))
                    CopyDir(s, Path.Combine(dst, sub));
                else
                    Directory.CreateDirectory(Path.Combine(dst, sub));
            }
            var idkey = Path.Combine(src, "id.key");
            if (!File.Exists(idkey))
                throw new InvalidDataException($"--include-store {src} has no id.key (not a Corpus store?)");
            File.Copy(idkey, Path.Combine(dst, "id.key"), overwrite: true);
            var db = opts.DbPath ?? DefaultIndexPath();
            if (!File.Exists(db))
                throw new InvalidDataException($"chunk index missing: {db} (pass --db <path to corpus-chunks.db>)");
            File.Copy(db, Path.Combine(dst, "corpus-chunks.db"), overwrite: true);
            log.Info($"packed store {src} (index {db})");
        }

        internal static string DefaultIndexPath() =>
            Path.Combine(OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Corpus")
                : "/var/lib/corpus", "corpus-chunks.db");

        internal static int[] ReadStoreGens(string storeRoot)
        {
            var manifests = Path.Combine(storeRoot, "manifests");
            var gens = new System.Collections.Generic.List<int>();
            if (Directory.Exists(manifests))
            {
                foreach (var path in Directory.EnumerateFiles(manifests, "*.json"))
                {
                    var name = Path.GetFileName(path);
                    if (name.Length > 7 && name[6] == '-' && int.TryParse(name.AsSpan(0, 6), out var gen))
                        gens.Add(gen);
                }
            }
            gens.Sort();
            return gens.ToArray();
        }

        // Assets ship beside the exe (csproj None-include); dev/test trees
        // resolve by walking up to corpus/recovery/assets.
        internal static string FindAssetsDir()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var c in new[] { Path.Combine(baseDir, "recovery", "assets"), Path.Combine(baseDir, "assets") })
            {
                if (File.Exists(Path.Combine(c, "restore.cmd")))
                    return c;
            }
            var dir = new DirectoryInfo(baseDir);
            for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                foreach (var rel in new[] { Path.Combine("corpus", "recovery", "assets"), Path.Combine("recovery", "assets") })
                {
                    var c = Path.Combine(dir.FullName, rel);
                    if (File.Exists(Path.Combine(c, "restore.cmd")))
                        return c;
                }
            }
            throw new InvalidDataException("recovery assets not found (recovery/assets/restore.cmd missing beside Corpus.exe and in the dev tree)");
        }

        internal static void CopyDir(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dest, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static string RunDotnetPublish(ILogger log)
        {
            var csproj = FindProject();
            var outDir = Directory.CreateTempSubdirectory("corpus-pub-").FullName;
            log.Info($"publishing Corpus win-x64 self-contained to {outDir}");
            var r = ProcessRunner.RunAsync($"dotnet publish \"{csproj}\" -c Release -r win-x64 --self-contained -o \"{outDir}\"",
                Path.GetDirectoryName(csproj)!, redirect: true, timeoutMs: 600000).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0 || !File.Exists(Path.Combine(outDir, "Corpus.exe")))
            {
                DeleteTree(outDir);
                throw new InvalidDataException($"dotnet publish failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
            }
            return outDir;
        }

        private static string FindProject()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Corpus.csproj");
                if (File.Exists(candidate))
                    return candidate;
            }
            throw new InvalidDataException("Corpus.csproj not found above the exe — build media from a Corpus source checkout");
        }

        // copype.cmd ships in the WinPE add-on subtree, NOT under
        // Deployment Tools (older notes say otherwise; proven on-box).
        internal static string CopypeCmd(string adk) =>
            Path.Combine(adk, "Windows Preinstallation Environment", "copype.cmd");

        private static void RunCopype(string adk, string peWork, ILogger log)
        {
            var copype = CopypeCmd(adk);
            // copype reads %WinPERoot%/%OSCDImgRoot%/%DISMROOT%: without the
            // Deployment Tools env it fails to stage, so run it inside a
            // DandISetEnv session (same box, same call).
            var diEnv = Path.Combine(adk, "Deployment Tools", "DandISetEnv.bat");
            if (!File.Exists(diEnv))
                throw new InvalidDataException($"ADK at {adk} is missing Deployment Tools\\DandISetEnv.bat — reinstall the ADK");
            log.Info($"running copype amd64 {peWork}");
            var r = ProcessRunner.RunAsync($"call \"{diEnv}\" >NUL && \"{copype}\" amd64 \"{peWork}\"",
                Path.GetDirectoryName(diEnv)!, redirect: true, timeoutMs: 300000).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0 || !Directory.Exists(Path.Combine(peWork, "media")))
                throw new InvalidDataException($"copype failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
        }

        private static string MountBootWim(string wim, string peWork, ILogger log)
        {
            if (!File.Exists(wim))
                throw new InvalidDataException($"copype output missing {wim} (copype drift fails closed)");
            var mount = Directory.CreateTempSubdirectory("corpus-mnt-").FullName;
            log.Info($"mounting boot.wim {mount}");
            var m = ProcessRunner.RunAsync($"dism /Mount-Image /ImageFile:\"{wim}\" /Index:1 /MountDir:\"{mount}\"",
                peWork, redirect: true, timeoutMs: 300000).GetAwaiter().GetResult();
            if (m.TimedOut || m.ExitCode != 0)
            {
                DeleteTree(mount);
                throw new InvalidDataException($"dism mount failed (exit {m.ExitCode}): {Tail(m.Stderr + m.Stdout)} (needs elevation)");
            }
            return mount;
        }

        private static void UnmountBootWim(string mount, string peWork, bool commit, ILogger log)
        {
            var verb = commit ? "/Commit" : "/Discard";
            var u = ProcessRunner.RunAsync($"dism /Unmount-Image /MountDir:\"{mount}\" {verb}",
                peWork, redirect: true, timeoutMs: 300000).GetAwaiter().GetResult();
            DeleteTree(mount);
            if (u.TimedOut || u.ExitCode != 0)
                throw new InvalidDataException($"dism unmount/{verb.TrimStart('/').ToLowerInvariant()} failed (exit {u.ExitCode}): {Tail(u.Stderr + u.Stdout)}");
        }

        private static void AddDriverToMount(string mount, string driverDir, ILogger log)
        {
            if (!Directory.Exists(driverDir))
                throw new InvalidDataException($"--driver missing: {driverDir}");
            log.Info($"injecting driver {driverDir} into boot.wim");
            var a = ProcessRunner.RunAsync($"dism /Image:\"{mount}\" /Add-Driver /Driver:\"{driverDir}\" /Recurse",
                Path.GetDirectoryName(mount)!, redirect: true, timeoutMs: 300000).GetAwaiter().GetResult();
            if (a.TimedOut || a.ExitCode != 0)
                throw new InvalidDataException($"dism add-driver failed (exit {a.ExitCode}): {Tail(a.Stderr + a.Stdout)}");
        }

        // Spec-verbatim hook: scan C..Z for \Corpus\restore.cmd, call it.
        internal static void AppendStartnetHook(string startnetPath)
        {
            if (!File.Exists(startnetPath))
                throw new InvalidDataException($"copype output missing {startnetPath} (copype drift fails closed)");
            File.AppendAllText(startnetPath,
                "\r\n@echo off\r\nfor %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if exist %%D:\\Corpus\\restore.cmd call %%D:\\Corpus\\restore.cmd %%D:\r\n");
        }

        private static void RunOscdimg(string adk, string mediaDir, string isoPath, ILogger log)
        {
            var oscdimg = Path.Combine(adk, "Deployment Tools", "amd64", "Oscdimg", "oscdimg.exe");
            var peParent = Directory.GetParent(mediaDir)!.FullName;
            // 24H2 copype stages firmware files under bootbins\; older ADKs
            // used fwfiles\. Prefer the new layout, accept the old.
            string? fwDir = null;
            foreach (var d in new[] { "bootbins", "fwfiles" })
            {
                var cand = Path.Combine(peParent, d);
                if (File.Exists(Path.Combine(cand, "etfsboot.com")) && File.Exists(Path.Combine(cand, "efisys.bin")))
                {
                    fwDir = cand;
                    break;
                }
            }
            if (fwDir == null)
                throw new InvalidDataException($"copype output has no firmware files (looked in {peParent}\\bootbins and {peParent}\\fwfiles) (copype drift fails closed)");
            var bootData = $"-bootdata:2#p0,e,b\"{Path.Combine(fwDir, "etfsboot.com")}\"#pEF,e,b\"{Path.Combine(fwDir, "efisys.bin")}\"";
            log.Info($"writing ISO {isoPath}");
            // `call` prefix: cmd /c strips a LEADING quoted program path
            // (`C:\Program Files...`), mis-parsing it as a command.
            var r = ProcessRunner.RunAsync($"call \"{oscdimg}\" -m -o -u2 -udfver102 {bootData} \"{mediaDir}\" \"{isoPath}\"",
                peParent, redirect: true, timeoutMs: 600000).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0)
                throw new InvalidDataException($"oscdimg failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
            log.Info($"wrote ISO {isoPath}");
        }

        private static void WriteUsb(string mediaDir, string usbDrive, bool initialize, ILogger log)
        {
            var letter = NormalizeUsbDrive(usbDrive);
            if (initialize)
            {
                ConfirmErase(letter);
                RequireAdmin();
                var disk = DiskNumberForLetter(letter);
                var script = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(script,
                        $"select disk {disk}\nclean\nconvert gpt\ncreate partition primary\nformat fs=fat32 quick label=CORPUS\nassign letter={letter}\nactive\nexit\n");
                    log.Info($"initializing {letter}: (disk {disk} WILL BE ERASED)");
                    var r = ProcessRunner.RunAsync($"diskpart /s \"{script}\"", Path.GetTempPath(), redirect: true, timeoutMs: 300000).GetAwaiter().GetResult();
                    if (r.TimedOut || r.ExitCode != 0)
                        throw new InvalidDataException($"diskpart failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
                }
                finally
                {
                    try { File.Delete(script); } catch (IOException) { }
                }
            }
            else
            {
                DriveInfo drive;
                try
                {
                    drive = new DriveInfo(letter + ":\\");
                    if (!drive.IsReady)
                        throw new InvalidDataException($"USB target {letter}:\\ is not ready");
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException($"USB target {letter}:\\ is not a drive", ex);
                }
                if (!string.Equals(drive.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"USB target {letter}:\\ is {drive.DriveFormat} — plain copy needs FAT32 or NTFS (or pass --initialize to format)");
                var bytes = DirSize(mediaDir);
                if (bytes > drive.AvailableFreeSpace)
                    throw new InvalidDataException($"USB target {letter}:\\ needs {bytes} bytes, has {drive.AvailableFreeSpace}");
            }
            CopyDir(mediaDir, letter + ":\\");
            log.Info($"wrote recovery media to {letter}:\\ (no format: plain copy)");
        }

        internal static string NormalizeUsbDrive(string usb)
        {
            ArgumentNullException.ThrowIfNull(usb);
            var letter = usb.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':').ToUpperInvariant();
            if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
                throw new InvalidDataException($"--usb wants a drive like E:\\ (got: {usb})");
            return letter;
        }

        // --initialize is destructive: interactive typed ERASE <letter>, no
        // flag-only path (mirrors the restore-side token posture).
        private static void ConfirmErase(string letter)
        {
            if (Console.IsInputRedirected)
                throw new InvalidDataException($"--initialize needs an interactive console to type ERASE {letter}");
            Console.Write($"Type ERASE {letter} to wipe {letter}:\\ and write recovery media: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), $"ERASE {letter}", StringComparison.Ordinal))
                throw new InvalidDataException("--initialize aborted (token mismatch)");
        }

        private static void RequireAdmin()
        {
            try
            {
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidDataException("--initialize needs an elevated console");
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("--initialize needs an elevated console", ex);
            }
        }

        private static int DiskNumberForLetter(string letter)
        {
            var r = ProcessRunner.RunAsync($"powershell -NoProfile -Command \"(Get-Partition -DriveLetter {letter}).DiskNumber\"",
                Path.GetTempPath(), redirect: true, timeoutMs: 60000).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0 || !int.TryParse(r.Stdout.Trim(), out var disk))
                throw new InvalidDataException($"cannot map {letter}:\\ to a disk (needs an existing partition): {Tail(r.Stderr + r.Stdout)}");
            return disk;
        }

        private static long DirSize(string dir)
        {
            var total = 0L;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;
            return total;
        }

        private static void DeleteTree(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        private static string Tail(string s) =>
            s.Length <= 500 ? s : s.Substring(s.Length - 500);
    }
}
