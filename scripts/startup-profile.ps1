# FEAT-303: which module is the game busy in while it starts? Waits for a new SkyrimSE, then ten times a second looks at the two
# threads that used most CPU in the last few seconds: the module each is executing in and the modules on its stack (a scan for return
# addresses, so stale entries are possible). Also process I/O and the last line of skse64.log, every few seconds. Read only: threads are
# paused for well under a millisecond per sample.
#   powershell -ExecutionPolicy Bypass -File startup-profile.ps1 -Out <folder> [-Seconds 260] [-SkseLog <path>]
param([string]$Out, [int]$Seconds = 260, [string]$SkseLog = '')
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class Prof {
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenThread(uint a, bool b, uint id);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint a, bool b, uint id);
  [DllImport("kernel32.dll")] static extern uint SuspendThread(IntPtr h);
  [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr h);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetThreadContext(IntPtr h, IntPtr c);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr n, out IntPtr r);
  static IntPtr proc;
  public static bool Open(uint pid) { proc = OpenProcess(0x0410, false, pid); return proc != IntPtr.Zero; }
  // x64 CONTEXT: 1232 bytes, 16-byte aligned; ContextFlags at 0x30, Rsp at 0x98, Rip at 0xF8.
  public static long[] Sample(uint tid, int bytes) {
    IntPtr h = OpenThread(0x0002 | 0x0008 | 0x0040, false, tid);
    if (h == IntPtr.Zero) return null;
    IntPtr raw = Marshal.AllocHGlobal(1232 + 16);
    IntPtr ctx = new IntPtr((raw.ToInt64() + 15) & ~15L);
    for (int i = 0; i < 1232; i += 8) Marshal.WriteInt64(ctx, i, 0);
    Marshal.WriteInt32(ctx, 0x30, 0x00100003);
    byte[] buf = new byte[bytes]; IntPtr got = IntPtr.Zero; long rip = 0, rsp = 0; bool ok = false;
    if (SuspendThread(h) != 0xFFFFFFFF) {
      try { ok = GetThreadContext(h, ctx); if (ok) { rip = Marshal.ReadInt64(ctx, 0xF8); rsp = Marshal.ReadInt64(ctx, 0x98); ReadProcessMemory(proc, new IntPtr(rsp), buf, new IntPtr(bytes), out got); } }
      finally { ResumeThread(h); }
    }
    CloseHandle(h); Marshal.FreeHGlobal(raw);
    if (!ok) return null;
    int n = (int)(got.ToInt64() / 8); long[] r = new long[n + 1]; r[0] = rip;
    for (int i = 0; i < n; i++) r[i + 1] = BitConverter.ToInt64(buf, i * 8);
    return r;
  }
  static long[] starts = new long[0], ends = new long[0]; static string[] names = new string[0];
  public static void Modules(long[] s, long[] e, string[] n) { Array.Sort((long[])s.Clone(), n); Array.Sort((long[])s.Clone(), e); Array.Sort(s); starts = s; ends = e; names = n; }
  public static string Where(long a) { int i = Array.BinarySearch(starts, a); if (i < 0) i = ~i - 1; return i >= 0 && a < ends[i] ? names[i] + "+" + (a - starts[i]).ToString("X") : null; }
}
'@
$begin = Get-Date
$p = $null
while (-not $p) { $p = Get-Process SkyrimSE -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $begin.AddSeconds(-5) } | Select-Object -First 1; Start-Sleep -Milliseconds 100 }
[void][Prof]::Open([uint32]$p.Id)
$inv = [Globalization.CultureInfo]::InvariantCulture
$stacks = New-Object IO.StreamWriter((Join-Path $Out 'profile-stacks.csv')); $io = New-Object IO.StreamWriter((Join-Path $Out 'profile-io.csv'))
$stacks.WriteLine('t;tid;leaf;callers'); $io.WriteLine('t;cpu;readMB;readOps;otherOps;wsMB;skse')
$lastCpu = @{}; $busy = @(); $lastMods = -100; $lastSec = -100
function T { ((Get-Date) - $p.StartTime).TotalSeconds }
while ((T) -lt $Seconds) {
  $t = T
  try {
    if ($t - $lastMods -gt 8) {
      $p.Refresh(); $s = New-Object Collections.Generic.List[long]; $e = New-Object Collections.Generic.List[long]; $n = New-Object Collections.Generic.List[string]
      foreach ($m in $p.Modules) { $b = $m.BaseAddress.ToInt64(); $s.Add($b); $e.Add($b + $m.ModuleMemorySize); $n.Add($m.ModuleName) }
      [Prof]::Modules($s.ToArray(), $e.ToArray(), $n.ToArray()); $lastMods = $t
    }
    if ($t - $lastSec -ge 4) {
      $p.Refresh(); $use = @{}
      foreach ($th in $p.Threads) { try { $c = $th.TotalProcessorTime.TotalMilliseconds; if ($lastCpu.ContainsKey($th.Id)) { $use[$th.Id] = $c - $lastCpu[$th.Id] }; $lastCpu[$th.Id] = $c } catch {} }
      $busy = @($use.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 2 | Where-Object { $_.Value -gt 100 } | ForEach-Object { $_.Key })
      $w = Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)"
      $last = ''
      if ($SkseLog) { try { $fs = [IO.File]::Open($SkseLog, 'Open', 'Read', 'ReadWrite'); $k = [Math]::Min(300, $fs.Length); [void]$fs.Seek(-$k, 'End'); $bf = New-Object byte[] $k; [void]$fs.Read($bf, 0, $k); $fs.Close(); $last = ([Text.Encoding]::ASCII.GetString($bf).Trim() -split "`n")[-1].Trim() } catch {} }
      $io.WriteLine(($t.ToString('F1', $inv), (($w.KernelModeTime + $w.UserModeTime) / 1e7).ToString('F1', $inv), ($w.ReadTransferCount / 1MB).ToString('F0', $inv), $w.ReadOperationCount, $w.OtherOperationCount, ($w.WorkingSetSize / 1MB).ToString('F0', $inv), $last) -join ';'); $io.Flush(); $lastSec = $t
    }
    foreach ($id in $busy) {
      $a = [Prof]::Sample([uint32]$id, 6144); if (-not $a) { continue }
      $leaf = [Prof]::Where($a[0]); $c = New-Object Collections.Generic.List[string]
      for ($i = 1; $i -lt $a.Length -and $c.Count -lt 24; $i++) { $x = [Prof]::Where($a[$i]); if ($x) { $c.Add($x) } }
      $stacks.WriteLine("$($t.ToString('F2',$inv));$id;$leaf;$($c -join ',')")
    }
  } catch { if ($p.HasExited) { break } }
  Start-Sleep -Milliseconds 60
}
$stacks.Close(); $io.Close()
