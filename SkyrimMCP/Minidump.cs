using System.Buffers.Binary;

namespace SkyrimMCP;

/// <summary>
/// Reads a Windows minidump of Skyrim (FEAT-279): crashes Crash Logger doesn't catch (shutdown, returning to the main menu,
/// a fail-fast) only leave the .dmp Windows writes when LocalDumps is set up. Gives the exception, the faulting module and
/// offset, and a stack scan of the crashing thread (values on its stack that point into loaded modules), like Crash
/// Logger's stack scan. No symbols, so these are leads, not a call stack. Written from Microsoft's published minidump
/// format; scripts/minidump-summary.py is the same reader for use from a shell.
/// </summary>
public static class Minidump
{
    const int ExceptionStream = 6, ModuleList = 4, MemoryList = 5, Memory64List = 9;

    static readonly Dictionary<uint, string> Codes = new()
    {
        [0xC0000005] = "access violation",
        [0xC0000374] = "heap corruption",
        [0xC0000409] = "fail-fast (the runtime stopped the game on purpose: corrupted memory or abort)",
        [0xC00000FD] = "stack overflow",
        [0xE06D7363] = "unhandled C++ exception",
        [0xC000001D] = "illegal instruction",
        [0x80000003] = "breakpoint",
    };

    /// <summary>Folders Windows writes Skyrim dumps to: LocalDumps' DumpFolder for SkyrimSE.exe or globally, else the default.</summary>
    public static IEnumerable<string> DumpFolders()
    {
        var folders = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\SkyrimSE.exe", @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps" })
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                    if (k?.GetValue("DumpFolder") is string f) folders.Add(Environment.ExpandEnvironmentVariables(f));
                }
                catch (Exception) { }
            }
        }
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"));
        return folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static List<FileInfo> FindDumps() =>
        DumpFolders().SelectMany(f => new DirectoryInfo(f).EnumerateFiles("SkyrimSE.exe*.dmp")).OrderByDescending(f => f.LastWriteTime).ToList();

    /// <summary>The folder of the game exe the dump was written for (Steam or GOG), from its module list; null if unreadable.</summary>
    public static string? GameFolder(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != 0x504D444D) return null;
            r.ReadUInt32();
            var count = r.ReadUInt32();
            var dirRva = r.ReadUInt32();
            for (var i = 0; i < count; i++)
            {
                fs.Seek(dirRva + i * 12, SeekOrigin.Begin);
                var type = r.ReadUInt32(); r.ReadUInt32(); var rva = r.ReadUInt32();
                if (type != ModuleList) continue;
                fs.Seek(rva, SeekOrigin.Begin);
                var n = r.ReadUInt32();
                for (var m = 0; m < n; m++)
                {
                    fs.Seek(rva + 4 + m * 108 + 20, SeekOrigin.Begin);
                    var nameRva = r.ReadUInt32();
                    fs.Seek(nameRva, SeekOrigin.Begin);
                    var name = System.Text.Encoding.Unicode.GetString(r.ReadBytes((int)r.ReadUInt32()));
                    if (Path.GetFileName(name).Equals("SkyrimSE.exe", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(name);
                }
            }
        }
        catch (Exception) { }
        return null;
    }

    public static object Summarise(string path, int stackEntries = 30)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] Read(long at, int n)
        {
            var b = new byte[n];
            fs.Seek(at, SeekOrigin.Begin);
            var got = fs.Read(b, 0, n);
            return got == n ? b : b[..got];
        }
        static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));

        var head = Read(0, 32);
        if (U32(head, 0) != 0x504D444D) return new { error = "not a minidump" };
        var count = (int)U32(head, 8);
        var dirRva = U32(head, 12);
        var dir = Read(dirRva, count * 12);
        var streams = new Dictionary<uint, (uint size, uint rva)>();
        for (var i = 0; i < count; i++) streams.TryAdd(U32(dir, i * 12), (U32(dir, i * 12 + 4), U32(dir, i * 12 + 8)));

        var modules = new List<(ulong start, ulong size, string name)>();
        if (streams.TryGetValue(ModuleList, out var ml))
        {
            var n = (int)U32(Read(ml.rva, 4), 0);
            var block = Read(ml.rva + 4, n * 108);
            for (var i = 0; i < n && (i + 1) * 108 <= block.Length; i++)
            {
                var start = U64(block, i * 108);
                var size = U32(block, i * 108 + 8);
                var nameRva = U32(block, i * 108 + 20);
                var len = (int)U32(Read(nameRva, 4), 0);
                var name = System.Text.Encoding.Unicode.GetString(Read(nameRva + 4, len));
                modules.Add((start, size, Path.GetFileName(name)));
            }
        }
        modules.Sort((a, b) => a.start.CompareTo(b.start));
        string? Where(ulong a)
        {
            foreach (var (start, size, name) in modules)
                if (a >= start && a < start + size) return $"{name}+0x{a - start:X}";
            return null;
        }

        var ranges = new List<(ulong start, ulong size, long offset)>();
        if (streams.TryGetValue(Memory64List, out var m64))
        {
            var h = Read(m64.rva, 16);
            var n = (long)U64(h, 0);
            var offset = (long)U64(h, 8);
            var list = Read(m64.rva + 16, (int)Math.Min(n * 16, int.MaxValue));
            for (var i = 0; i < n && (i + 1) * 16 <= list.Length; i++)
            {
                var start = U64(list, i * 16);
                var size = U64(list, i * 16 + 8);
                ranges.Add((start, size, offset));
                offset += (long)size;
            }
        }
        else if (streams.TryGetValue(MemoryList, out var mlist))
        {
            var n = (int)U32(Read(mlist.rva, 4), 0);
            var list = Read(mlist.rva + 4, n * 16);
            for (var i = 0; i < n && (i + 1) * 16 <= list.Length; i++)
                ranges.Add((U64(list, i * 16), U32(list, i * 16 + 8), U32(list, i * 16 + 12)));
        }
        byte[] Memory(ulong at, int n)
        {
            foreach (var (start, size, offset) in ranges)
                if (at >= start && at < start + size) return Read(offset + (long)(at - start), (int)Math.Min((ulong)n, start + size - at));
            return [];
        }

        if (!streams.TryGetValue(ExceptionStream, out var ex)) return new { file = path, note = "No exception in this dump (taken by hand, not on a crash)." };
        var e = Read(ex.rva, 168);
        var thread = U32(e, 0);
        var code = U32(e, 8);
        var address = U64(e, 24);
        var nParams = (int)U32(e, 32);
        var info = Enumerable.Range(0, Math.Min(nParams, 15)).Select(i => U64(e, 40 + i * 8)).ToArray();
        var ctxRva = U32(e, 164);
        var ctx = Read(ctxRva, 0x100);
        var rsp = ctx.Length >= 0x100 ? U64(ctx, 0x98) : 0;
        string[] regNames = ["Rax", "Rcx", "Rdx", "Rbx", "Rsp", "Rbp", "Rsi", "Rdi", "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15"];
        var regs = ctx.Length >= 0x100
            ? regNames.Select((r, i) => (r, w: Where(U64(ctx, 0x78 + i * 8)))).Where(x => x.w != null).Select(x => $"{x.r} = {x.w}").ToList()
            : [];

        var stack = Memory(rsp, 0x4000);
        var hits = new List<string>();
        for (var o = 0; o + 8 <= stack.Length; o += 8)
            if (Where(U64(stack, o)) is string w) hits.Add($"[RSP+{o:X}] {w}");
        var byModule = hits.Select(h => h[(h.IndexOf(']') + 2)..].Split('+')[0]).GroupBy(m => m)
            .OrderByDescending(g => g.Count()).Select(g => new { module = g.Key, entries = g.Count() }).Take(10).ToList();

        string? access = code == 0xC0000005 && info.Length >= 2 ? $"{(info[0] == 1 ? "write" : info[0] == 8 ? "execute" : "read")} at 0x{info[1]:X}" : null;
        var systemOnly = new[] { "SkyrimSE.exe", "ucrtbase.dll", "ntdll.dll", "KERNELBASE.dll", "kernel32.dll" };
        var mods = byModule.Select(b => b.module).Where(m => !systemOnly.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        return new
        {
            file = path,
            written = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss"),
            exception = $"0x{code:X8} {Codes.GetValueOrDefault(code, "")}".Trim(),
            at = Where(address) ?? $"0x{address:X}",
            access,
            thread,
            registersIntoModules = regs,
            stackScan = hits.Take(Math.Clamp(stackEntries, 5, 200)),
            modulesOnStack = byModule,
            reading = (code == 0xC0000409 ? "A fail-fast: the game stopped itself on purpose, often while tearing down (quitting or returning to the main menu). " : "") +
                      (mods.Count > 0 ? $"DLLs other than the game on the crashing thread's stack: {string.Join(", ", mods)} (leads, not proof: a memory-manager plugin appears on most stacks). " : "Only the game and Windows are on the stack. ") +
                      "No symbols: module+offset values are leads to compare with other crashes, not a call stack.",
        };
    }
}
