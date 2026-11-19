namespace SkyrimMCP.Saves;

// FEAT-265: reads the Papyrus section (global data 1001) of an SE/AE save, following ReSaver's read path (FallrimTools by Mark
// Fairchild, Apache-2.0). SE uses 32-bit string indices and 64-bit object handles; thread, message and stack IDs are 32-bit.
// Only what the scan reports is kept; everything else is read past.
internal sealed class PapyrusReader(EssScanner.Buf b, SaveScan scan, List<string> plugins, List<string> lightPlugins, int[] formIds)
{
    public string Stage { get; private set; } = "header";

    string[] _strings = [];
    readonly Dictionary<string, string> _scriptParent = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<long, (string script, int refId)> _instances = new();
    readonly Dictionary<long, int> _arrayLength = new();
    readonly List<int> _threadIds = [];
    readonly Dictionary<int, Thread> _threads = new();
    readonly Dictionary<string, int> _queued = new();
    readonly Dictionary<string, string?> _queuedPlugin = new();
    readonly Dictionary<string, int> _messages = new();
    readonly Dictionary<string, string?> _messagePlugin = new();
    readonly HashSet<int> _suspendedIds = [];

    sealed class Thread
    {
        public string? Entry, Top;
        public long OwnerHandle;
        public bool Undefined, Terminated;
    }

    static readonly HashSet<string> Native = new(StringComparer.OrdinalIgnoreCase)
    {
        "ActiveMagicEffect", "Alias", "Debug", "Form", "Game", "Input", "Math", "ModEvent", "SKSE", "StringUtil", "UI", "Utility",
        "CommonArrayFunctions", "ScriptObject", "InputEnableLayer",
    };

    // Opcode argument counts (pex): negative = that many fixed arguments then a count of extra ones.
    static readonly int[] OpArgs =
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 1, 2, 2, -3, -2, -3, 1, 3, 3, 3, 2, 2, 3, 3, 4, 4, 3, 1, 3, 3, 5,
        3, 3, 3, 1, 3, 1, 6,
    ];

    string Str()
    {
        var i = b.I32();
        if (i < 0 || i >= _strings.Length) throw new EssScanner.FormatException($"string index {i} out of {_strings.Length}");
        return _strings[i];
    }

    bool IsUndefinedScript(string name) =>
        _scriptParent.TryGetValue(name, out var parent) ? string.IsNullOrEmpty(parent) && !Native.Contains(name) : !Native.Contains(name);

    public void Read()
    {
        b.U16();
        Stage = "string table";
        var n = b.I32();
        if (n < 0 || n > 10_000_000) throw new EssScanner.FormatException($"string count {n}");
        _strings = new string[n];
        for (var i = 0; i < n; i++) _strings[i] = b.WStr();
        scan.Strings = n;

        Stage = "scripts";
        n = b.I32();
        for (var i = 0; i < n; i++)
        {
            var name = Str();
            var parent = Str();
            _scriptParent[name] = parent;
            var members = b.I32();
            List<string[]>? list = scan.ScriptMembers != null ? new List<string[]>(members) : null;
            for (var m = 0; m < members; m++) { var mn = Str(); var mt = Str(); list?.Add([mn, mt]); }
            if (list != null) scan.ScriptMembers![name] = list;
        }
        scan.Scripts = n;

        Stage = "script instances";
        n = b.I32();
        var instanceOrder = new List<long>(n);
        for (var i = 0; i < n; i++)
        {
            var id = b.I64();
            var script = Str();
            b.U16(); b.S16();
            var refId = b.RefId();
            b.U8();
            _instances[id] = (script, refId);
            instanceOrder.Add(id);
        }
        scan.ScriptInstances = n;

        Stage = "references";
        var refCount = b.I32();
        for (var i = 0; i < refCount; i++) { b.I64(); Str(); }
        scan.References = refCount;

        Stage = "arrays";
        var arrCount = b.I32();
        for (var i = 0; i < arrCount; i++)
        {
            var id = b.I64();
            var t = ReadType();
            if (IsRefType(t)) Str();
            _arrayLength[id] = b.I32();
        }
        scan.Arrays = arrCount;

        b.I32();   // Papyrus runtime

        Stage = "active scripts";
        var threadCount = b.I32();
        for (var i = 0; i < threadCount; i++) { _threadIds.Add(b.I32()); b.U8(); }
        scan.ActiveScripts = threadCount;

        Stage = "script instance data";
        for (var i = 0; i < n; i++) ReadObjectData();
        Stage = "reference data";
        for (var i = 0; i < refCount; i++) ReadObjectData();
        Stage = "array data";
        for (var i = 0; i < arrCount; i++)
        {
            var id = b.I64();
            if (!_arrayLength.TryGetValue(id, out var len)) throw new EssScanner.FormatException($"array data for unknown array {id:X}");
            for (var v = 0; v < len; v++) ReadVariable();
        }

        Stage = "active script data";
        for (var i = 0; i < threadCount; i++) ReadThread();

        Stage = "function messages";
        var fm = b.I32();
        for (var i = 0; i < fm; i++)
        {
            var flag = b.S8();
            if (flag <= 2) b.I32();
            if (b.U8() != 0) Count(_messages, _messagePlugin, ReadMessageData());
        }
        scan.FunctionMessages = fm;

        Stage = "suspended stacks";
        scan.SuspendedStacks1 = ReadSuspended();
        scan.SuspendedStacks2 = ReadSuspended();
        Stage = "done";
    }

    int ReadSuspended()
    {
        var count = b.I32();
        for (var i = 0; i < count; i++)
        {
            _suspendedIds.Add(b.I32());
            if (b.U8() != 0)
            {
                var (name, self) = ReadMessageData();
                Count(_queued, _queuedPlugin, (name, self));
            }
        }
        return count;
    }

    void Count(Dictionary<string, int> into, Dictionary<string, string?>? plugin, (string name, long self) call)
    {
        into[call.name] = into.GetValueOrDefault(call.name) + 1;
        if (plugin != null && !plugin.ContainsKey(call.name)) plugin[call.name] = PluginOfHandle(call.self);
    }

    (string name, long self) ReadMessageData()
    {
        b.U8();
        var script = Str();
        var ev = Str();
        var self = ReadVariable();
        var count = b.I32();
        for (var i = 0; i < count; i++) ReadVariable();
        return ($"{script}.{ev}", self);
    }

    void ReadObjectData()
    {
        b.I64();
        var flag = b.U8();
        Str();
        b.I32();
        if ((flag & 0x04) != 0) b.I32();
        var count = b.I32();
        for (var i = 0; i < count; i++) ReadVariable();
    }

    void ReadThread()
    {
        var id = b.I32();
        var t = new Thread();
        _threads[id] = t;
        b.U8(); b.U8();
        ReadVariable();
        var flag = b.U8();
        b.U8();
        if ((flag & 0x01) != 0) b.I32();
        var kind = b.S8();
        if (kind is >= 1 and <= 3)
        {
            if (kind != 2)
            {
                var len = b.I32();
                var name = System.Text.Encoding.UTF8.GetString(b.Bytes(len));
                switch (name)
                {
                    case "QuestStage": b.RefId(); b.S16(); b.U8(); break;
                    case "ScenePhaseResults":
                    case "SceneActionResults": b.RefId(); b.I32(); break;
                    case "SceneResults": b.RefId(); break;
                    case "TerminalRunResults": b.U8(); b.I32(); b.RefId(); Str(); break;
                    case "TopicInfo": break;
                    default: throw new EssScanner.FormatException($"unknown fragment type '{name}'");
                }
            }
            if (kind is 2 or 3) ReadVariable();
        }
        var frames = b.I32();
        if (frames < 0 || frames > 100_000) throw new EssScanner.FormatException($"thread {id:X}: {frames} frames");
        var allZeroedOrNative = true;
        var firstNonNativeZeroed = false;
        for (var f = 0; f < frames; f++)
        {
            var (name, native, zeroed, owner, script) = ReadFrame();
            // Frame 0 is where the thread started (the event handler); the last frame is where it is now (often a native
            // call it waits in). Checked on the Aniya flood save: frame 0 = OnMagicEffectApply, last = MagicEffect.HasKeyword.
            if (f == 0) { t.Entry = name; firstNonNativeZeroed = !native && zeroed; }
            t.Top = name;
            if (t.OwnerHandle == 0 && owner != 0) t.OwnerHandle = owner;
            // ReSaver: a frame is undefined only when its script is in the save's definitions and is undefined there (a frame
            // whose script isn't listed doesn't count). Counting unlisted ones reported 7 threads ReSaver doesn't clean.
            if (!native && _scriptParent.TryGetValue(script, out var parent) && string.IsNullOrEmpty(parent) && !Native.Contains(script)) t.Undefined = true;
            if (!zeroed && !native) allZeroedOrNative = false;
        }
        t.Terminated = frames > 0 && firstNonNativeZeroed && allZeroedOrNative;
        if (frames > 0) b.U8();
    }

    (string name, bool native, bool zeroed, long owner, string script) ReadFrame()
    {
        var varCount = b.I32();
        if (varCount < 0 || varCount > 50000) throw new EssScanner.FormatException($"stack frame with {varCount} variables");
        var flag = b.U8();
        var fnType = ReadType();
        var script = Str();
        Str();   // base name
        var ev = Str();
        if ((flag & 1) == 0 && fnType == 0) Str();   // status
        b.U8(); b.U8();
        Str(); Str();   // return type, doc string
        b.I32();
        var fnFlags = b.U8();
        var native = ((fnFlags >> 1) & 1) != 0;
        int pc = b.U16();
        for (var i = 0; i < pc; i++) { Str(); Str(); }
        int lc = b.U16();
        for (var i = 0; i < lc; i++) { Str(); Str(); }
        int oc = b.U16();
        var allNop = oc > 0;
        for (var i = 0; i < oc; i++) if (ReadOpcode() != 0) allNop = false;
        b.I32();   // instruction pointer
        var owner = ReadVariable();
        for (var i = 0; i < varCount; i++) ReadVariable();
        return ($"{script}.{ev}", native, !native && allNop, owner, script);
    }

    int ReadOpcode()
    {
        int code = b.S8();
        if (code < 0 || code >= OpArgs.Length) throw new EssScanner.FormatException($"opcode {code}");
        var args = OpArgs[code];
        var fixedCount = args < 0 ? 1 - args : args;
        int? lastInt = null;
        for (var i = 0; i < fixedCount; i++) lastInt = ReadParameter();
        if (args < 0 && fixedCount > 0)
        {
            if (lastInt is not int extra) throw new EssScanner.FormatException($"opcode {code}: argument count isn't an integer");
            for (var i = 0; i < extra; i++) ReadParameter();
        }
        return code;
    }

    // Returns the value when the parameter is an integer (an opcode's extra-argument count).
    int? ReadParameter()
    {
        var t = b.U8();
        switch (t)
        {
            case 0: return null;
            case 1: case 2: case 8: Str(); return null;
            case 3: return b.I32();
            case 4: b.F32(); return null;
            case 5: b.U8(); return null;
            default: throw new EssScanner.FormatException($"opcode parameter type {t}");
        }
    }

    byte ReadType()
    {
        var t = b.U8();
        if (t > 17 || t is 8 or 9 or 10) throw new EssScanner.FormatException($"variable type {t}");
        return t;
    }

    static bool IsRefType(byte t) => t is 1 or 7 or 11 or 17;

    // Returns the object handle when the variable is an object reference (a frame's or a call's self), else 0.
    long ReadVariable()
    {
        var t = ReadType();
        switch (t)
        {
            case 0: case 3: case 4: case 5: b.I32(); return 0;
            case 1: Str(); return b.I64();
            case 2: Str(); return 0;
            case 6: return ReadVariable();
            case 7: Str(); b.I64(); return 0;
            default:   // arrays
                if (IsRefType(t)) Str();
                b.I64();
                return 0;
        }
    }

    string? PluginOfHandle(long handle)
    {
        if (handle == 0 || !_instances.TryGetValue(handle, out var inst)) return null;
        return PluginOfRef(inst.refId);
    }

    string? PluginOfRef(int refId)
    {
        if (refId == 0) return null;
        var value = refId & 0x3FFFFF;
        switch ((refId >> 22) & 3)
        {
            case 0:
                return value > 0 && value - 1 < formIds.Length ? EssScanner.PluginFor(formIds[value - 1], plugins, lightPlugins) : null;
            case 1: return plugins.Count > 0 ? plugins[0] : null;
            case 2: return "(created in game)";
            default: return null;
        }
    }

    /// <summary>Fills the findings from whatever was read (a partial read still reports what it got).</summary>
    public void Summarise()
    {
        scan.UndefinedScripts = _scriptParent.Keys.Where(IsUndefinedScript).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var undefinedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unattachedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (script, refId) in _instances.Values)
        {
            if (IsUndefinedScript(script)) undefinedBy[script] = undefinedBy.GetValueOrDefault(script) + 1;
            if (refId == 0) unattachedBy[script] = unattachedBy.GetValueOrDefault(script) + 1;
        }
        foreach (var (script, _) in _instances.Values) scan.InstancesByScript[script] = scan.InstancesByScript.GetValueOrDefault(script) + 1;
        foreach (var (_, refId) in _instances.Values)
        {
            var owner = PluginOfRef(refId) ?? (refId == 0 ? "(unattached)" : "(unresolved)");
            scan.InstancesByPlugin[owner] = scan.InstancesByPlugin.GetValueOrDefault(owner) + 1;
        }
        scan.UndefinedInstances = undefinedBy.Values.Sum();
        scan.UndefinedInstancesByScript = Top(undefinedBy, 25);
        scan.UnattachedInstances = unattachedBy.Values.Sum();
        scan.UnattachedInstancesByScript = Top(unattachedBy, 25);

        scan.SuspendedThreads = _threads.Keys.Count(_suspendedIds.Contains);
        scan.TerminatedThreads = _threads.Count(kv => kv.Value.Terminated && !_suspendedIds.Contains(kv.Key));
        scan.UndefinedThreads = _threads.Values.Count(t => t.Undefined && !t.Terminated);

        var byEntry = new Dictionary<string, int>();
        var entryPlugin = new Dictionary<string, string?>();
        var byTop = new Dictionary<string, int>();
        foreach (var t in _threads.Values)
        {
            if (t.Entry != null)
            {
                byEntry[t.Entry] = byEntry.GetValueOrDefault(t.Entry) + 1;
                if (!entryPlugin.TryGetValue(t.Entry, out var p) || p == null) entryPlugin[t.Entry] = PluginOfHandle(t.OwnerHandle);
            }
            if (t.Top != null) byTop[t.Top] = byTop.GetValueOrDefault(t.Top) + 1;
        }
        scan.ThreadsByEntryPoint = Top(byEntry, 300, entryPlugin);
        scan.ThreadsByTopFrame = Top(byTop, 20);
        scan.QueuedCalls = Top(_queued, 300, _queuedPlugin);
        scan.FunctionMessageCalls = Top(_messages, 10, _messagePlugin);
        // A pile-up can be spread over many handlers of one mod (SimpleItemSpawnerChest1..16.OnItemRemoved, ~700 each): totals
        // per plugin show it as one problem.
        scan.QueuedByPlugin = ByPlugin(_queued, _queuedPlugin);
        scan.ThreadsByPlugin = ByPlugin(byEntry, entryPlugin);
        scan.FunctionMessagesByPlugin = ByPlugin(_messages, _messagePlugin);
    }

    static List<ModCount> ByPlugin(Dictionary<string, int> d, Dictionary<string, string?> plugin)
    {
        var totals = new Dictionary<string, (int count, int handlers, string first, int firstCount)>();
        foreach (var (name, count) in d)
        {
            var p = plugin.GetValueOrDefault(name) ?? "(mod unknown)";
            var cur = totals.TryGetValue(p, out var had) ? had : (count: 0, handlers: 0, first: name, firstCount: 0);
            totals[p] = (cur.count + count, cur.handlers + 1, count > cur.firstCount ? name : cur.first, Math.Max(cur.firstCount, count));
        }
        return totals.OrderByDescending(kv => kv.Value.count).Take(10)
            .Select(kv => new ModCount(kv.Key, kv.Value.count, kv.Value.handlers > 1 ? $"{kv.Value.handlers} handlers, most in {kv.Value.first}" : kv.Value.first))
            .ToList();
    }

    static List<NameCount> Top(Dictionary<string, int> d, int n, Dictionary<string, string?>? plugin = null) =>
        d.OrderByDescending(kv => kv.Value).Take(n)
            .Select(kv => new NameCount(kv.Key, kv.Value, plugin != null && plugin.TryGetValue(kv.Key, out var p) ? p : null)).ToList();
}
