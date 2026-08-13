// PSProcLassoGUI — Real-time system monitor with full GUI
// C# / .NET Framework 4.x WinForms, single self-contained executable.
// Shows every process ranked by CPU / RAM / GPU / VRAM live, with per-process
// priority, affinity, CPU/GPU/RAM limits, kill, launch, watchdog, ProBalance
// and persistent rules that are shared with the PowerShell TUI (rules.json).
// Written for clarity + robustness: every background operation is guarded so
// the app never crashes (errors surface on the status bar).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PSPL
{
    // ---------------------------------------------------------------------
    //  Native process control (suspend/resume throttle + working-set trim)
    // ---------------------------------------------------------------------
    internal static class Native
    {
        [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
        [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessInformation(IntPtr process, int informationClass,
            ref PROCESS_POWER_THROTTLING_STATE throttlingState, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetConsoleMode(IntPtr h, out uint m);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleMode(IntPtr h, uint m);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            public uint ControlFlags;
            public uint CpuRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        public const int JobObjectExtendedLimitInformation = 9;
        public const int JobObjectCpuRateControlInformation = 15;
        public const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
        public const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;
        public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x100;

        public static long GetTotalPhysicalRam()
        {
            try
            {
                var ms = new MEMORYSTATUSEX();
                ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                return GlobalMemoryStatusEx(ref ms) ? (long)ms.ullTotalPhys : 0;
            }
            catch { return 0; }
        }

        public static bool GetPhysicalMemory(out long total, out long available)
        {
            total = 0; available = 0;
            try
            {
                var ms = new MEMORYSTATUSEX();
                ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (!GlobalMemoryStatusEx(ref ms)) return false;
                total = (long)ms.ullTotalPhys;
                available = (long)ms.ullAvailPhys;
                return true;
            }
            catch { return false; }
        }

        public const uint PROCESS_SUSPEND_RESUME = 0x0800;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_SET_INFORMATION = 0x0200;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const int ProcessPowerThrottling = 4;
        public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        public static IntPtr OpenSuspendHandle(int pid)
        {
            return OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_INFORMATION, false, pid);
        }

        public static bool DisableExecutionSpeedThrottling(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_SET_INFORMATION |
                                        PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0
                };
                return SetProcessInformation(handle, ProcessPowerThrottling, ref state,
                    (uint)Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
            }
            finally { CloseHandle(handle); }
        }
    }

    // ---------------------------------------------------------------------
    //  Models
    // ---------------------------------------------------------------------
    internal class ProcRow
    {
        public int Id;
        public long StartTicks;
        public string Name;
        public string ExecutablePath;
        public string GroupKey;
        public double Cpu;
        public long Mem;
        public long Priv;
        public double Gpu;
        public bool GpuValid;
        public long Vram;
        public Dictionary<string, double> GpuEngines;
        public List<ProcRow> Members;
        public string Priority = "n/a";
        public string Affinity = "?";
        public int Threads;
        public bool HasLimit;
        public string Controls = "";
        public bool Watchdog;
        public bool Pb;
        public bool HasVisibleWindow;
        public int SessionId;
    }

    internal class Snapshot
    {
        public List<ProcRow> Rows = new List<ProcRow>();
        public double TotalCpu;
        public long RamUsed;
        public long RamTotal;
        public double AvailMB;
        public double GpuPct;
        public bool GpuValid;
        public long VramUsed;
        public long VramTotal;
        public long Standby;
        public int ProcessCount;
    }

    // Rules share the exact format of the PowerShell TUI's rules.json
    internal class Rule
    {
        public string priority { get; set; }
        public int[] affinity { get; set; }
        public int cpuLimit { get; set; }
        public int gpuLimit { get; set; }
        public int ramLimit { get; set; }
        public bool watchdog { get; set; }
        public string wdPath { get; set; }
        public string wdArgs { get; set; }
        public int restarts { get; set; }
        public bool enabled { get; set; }
        public bool optimizerManaged { get; set; }
        public string optimizerOriginalPriority { get; set; }
        public string optimizerReason { get; set; }
        public string optimizerUpdatedUtc { get; set; }
        public bool performanceManaged { get; set; }
        public string performanceOriginalPriority { get; set; }
        public int[] performanceOriginalAffinity { get; set; }
        public int performanceOriginalCpuLimit { get; set; }
        public int performanceOriginalGpuLimit { get; set; }
        public int performanceOriginalRamLimit { get; set; }
        public bool performanceHadRule { get; set; }
        public string performanceReason { get; set; }
        public string performanceUpdatedUtc { get; set; }

        public static Rule New()
        {
            return new Rule { priority = "", affinity = new int[0], cpuLimit = 0, gpuLimit = 0,
                              ramLimit = 0, watchdog = false, wdPath = "", wdArgs = "",
                              restarts = 0, enabled = true, optimizerManaged = false,
                              optimizerOriginalPriority = "", optimizerReason = "",
                              optimizerUpdatedUtc = "", performanceManaged = false,
                              performanceOriginalPriority = "",
                              performanceOriginalAffinity = new int[0],
                              performanceOriginalCpuLimit = 0,
                              performanceOriginalGpuLimit = 0,
                              performanceOriginalRamLimit = 0,
                              performanceHadRule = false,
                              performanceReason = "", performanceUpdatedUtc = "" };
        }
    }

    internal class OptimizationDecision
    {
        public int pid { get; set; }
        public long startTicks { get; set; }
        public string processName { get; set; }
        public string displayName { get; set; }
        public string executablePath { get; set; }
        public double cpuPercent { get; set; }
        public long ramBytes { get; set; }
        public double gpuPercent { get; set; }
        public bool gpuValid { get; set; }
        public long vramBytes { get; set; }
        public string currentPriority { get; set; }
        public string action { get; set; }
        public string targetPriority { get; set; }
        public string cpuAction { get; set; }
        public string ramAction { get; set; }
        public string gpuAction { get; set; }
        public string reason { get; set; }
        public bool persistent { get; set; }
        public bool applied { get; set; }
        public string error { get; set; }
    }

    internal class OptimizationReceipt
    {
        public string schema { get; set; }
        public string mode { get; set; }
        public string generatedUtc { get; set; }
        public string machine { get; set; }
        public double totalCpuPercent { get; set; }
        public long ramUsedBytes { get; set; }
        public long ramTotalBytes { get; set; }
        public int processCount { get; set; }
        public int changedProcesses { get; set; }
        public int persistentRules { get; set; }
        public int restoredLegacyPolicies { get; set; }
        public int failedChanges { get; set; }
        public List<OptimizationDecision> decisions { get; set; }
        public List<string> errors { get; set; }
    }

    internal class ResourceMeasurement
    {
        public int samples { get; set; }
        public int gpuSamples { get; set; }
        public long durationMs { get; set; }
        public double cpuPercent { get; set; }
        public long ramUsedBytes { get; set; }
        public long ramTotalBytes { get; set; }
        public double ramPercent { get; set; }
        public double gpuPercent { get; set; }
        public bool gpuValid { get; set; }
        public double cpuSpread { get; set; }
        public double ramSpreadPercent { get; set; }
        public double gpuSpread { get; set; }
        public List<ApplicationMeasurement> applications { get; set; }
    }

    internal class ApplicationMeasurement
    {
        public string key { get; set; }
        public string name { get; set; }
        public int samples { get; set; }
        public int gpuSamples { get; set; }
        public double cpuPercent { get; set; }
        public long ramBytes { get; set; }
        public double gpuPercent { get; set; }
        public bool gpuValid { get; set; }
        public long vramBytes { get; set; }
    }

    internal class OptimizationImpact
    {
        public double cpuChangePoints { get; set; }
        public double cpuImprovementPercent { get; set; }
        public long ramChangeBytes { get; set; }
        public double ramChangePoints { get; set; }
        public double ramImprovementPercent { get; set; }
        public bool gpuMeasured { get; set; }
        public double gpuChangePoints { get; set; }
        public double gpuImprovementPercent { get; set; }
        public string confidence { get; set; }
        public string interpretation { get; set; }
    }

    internal class ApplicationImpact
    {
        public string key { get; set; }
        public string name { get; set; }
        public string status { get; set; }
        public double cpuBeforePercent { get; set; }
        public double cpuAfterPercent { get; set; }
        public double cpuImprovementPercent { get; set; }
        public long ramBeforeBytes { get; set; }
        public long ramAfterBytes { get; set; }
        public double ramImprovementPercent { get; set; }
        public bool gpuMeasured { get; set; }
        public double gpuBeforePercent { get; set; }
        public double gpuAfterPercent { get; set; }
        public double gpuImprovementPercent { get; set; }
    }

    internal class OptimizationRunReceipt
    {
        public string schema { get; set; }
        public string generatedUtc { get; set; }
        public string machine { get; set; }
        public ResourceMeasurement before { get; set; }
        public ResourceMeasurement after { get; set; }
        public OptimizationImpact systemImpact { get; set; }
        public List<ApplicationImpact> applications { get; set; }
        public OptimizationReceipt actions { get; set; }
        public int restoredLegacyPolicies { get; set; }
        public bool startupEnabled { get; set; }
        public string persistenceScope { get; set; }
        public string tpuStatus { get; set; }
        public string receiptPath { get; set; }
        public List<string> errors { get; set; }
    }

    internal class OptimizationProgress
    {
        public int percent { get; set; }
        public string phase { get; set; }
        public string message { get; set; }
        public int current { get; set; }
        public int total { get; set; }
    }

    internal class AdaptiveTop20Decision
    {
        public int pid { get; set; }
        public long startTicks { get; set; }
        public string processName { get; set; }
        public string applicationName { get; set; }
        public string selectedBy { get; set; }
        public double cpuPercent { get; set; }
        public long ramBytes { get; set; }
        public double gpuPercent { get; set; }
        public string priorityBefore { get; set; }
        public string priorityAfter { get; set; }
        public bool protectedScheduling { get; set; }
        public bool priorityChanged { get; set; }
        public bool affinityExpanded { get; set; }
        public bool limitsRemoved { get; set; }
        public bool powerThrottlingDisabled { get; set; }
        public bool persistent { get; set; }
        public string action { get; set; }
        public string error { get; set; }
    }

    internal class AdaptiveTop20Receipt
    {
        public string schema { get; set; }
        public string mode { get; set; }
        public string generatedUtc { get; set; }
        public string machine { get; set; }
        public string[] topCpuApplications { get; set; }
        public string[] topRamApplications { get; set; }
        public string[] topGpuApplications { get; set; }
        public int targetedProcesses { get; set; }
        public int priorityChanges { get; set; }
        public int affinityExpansions { get; set; }
        public int limitRemovals { get; set; }
        public int powerThrottleDisables { get; set; }
        public int persistentRules { get; set; }
        public int protectedProcesses { get; set; }
        public int failures { get; set; }
        public List<AdaptiveTop20Decision> decisions { get; set; }
        public List<string> errors { get; set; }
    }

    internal class GpuLimitState { public double Pct; public bool Suspended; public long T0; public long StartTicks; }
    internal class CpuLimitState { public double Limit; public string Orig = "Normal"; public bool Limited; public bool Hard; public long StartTicks; }
    internal class RamLimitState { public long Mb; public bool Hard; public long StartTicks; }
    internal class PbState { public string Orig = "Normal"; public DateTime Until; }
    internal class ProcessHandleState { public IntPtr Handle; public long StartTicks; }
    internal class ResourceJobState
    {
        public IntPtr Handle;
        public int CpuPercent;
        public long RamMb;
        public long StartTicks;
    }

    // ---------------------------------------------------------------------
    //  Rules store (JSON, shared with the TUI)
    // ---------------------------------------------------------------------
    internal static class RulesStore
    {
        private const string MutexName = "Local\\PSProcLasso.RulesStore.v1";

        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".psproclasso", "rules.json"); }
        }

        public static ConcurrentDictionary<string, Rule> Load()
        {
            Mutex mutex = null;
            bool held = false;
            try
            {
                mutex = new Mutex(false, MutexName);
                try { held = mutex.WaitOne(10000); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return new ConcurrentDictionary<string, Rule>();
                return LoadFromPath(FilePath);
            }
            catch { return new ConcurrentDictionary<string, Rule>(); }
            finally
            {
                if (held) try { mutex.ReleaseMutex(); } catch { }
                if (mutex != null) mutex.Dispose();
            }
        }

        public static void Save(ConcurrentDictionary<string, Rule> rules)
        {
            string ignored;
            SaveExact(FilePath, rules, out ignored);
        }

        public static bool UpdateRule(string key, Action<Rule> mutation,
                                      out ConcurrentDictionary<string, Rule> latest,
                                      out string error)
        {
            return UpdateRuleAtPath(FilePath, key, mutation, out latest, out error);
        }

        public static bool MutateRules(Action<ConcurrentDictionary<string, Rule>> mutation,
                                       out ConcurrentDictionary<string, Rule> latest,
                                       out string error)
        {
            latest = new ConcurrentDictionary<string, Rule>();
            error = "";
            Mutex mutex = null;
            bool held = false;
            try
            {
                mutex = new Mutex(false, MutexName);
                try { held = mutex.WaitOne(10000); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) { error = "Timed out waiting for the rules file lock."; return false; }
                latest = LoadFromPath(FilePath);
                mutation(latest);
                return WriteToPath(FilePath, latest, out error);
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally
            {
                if (held) try { mutex.ReleaseMutex(); } catch { }
                if (mutex != null) mutex.Dispose();
            }
        }

        private static bool UpdateRuleAtPath(string path, string key, Action<Rule> mutation,
                                             out ConcurrentDictionary<string, Rule> latest,
                                             out string error)
        {
            latest = new ConcurrentDictionary<string, Rule>();
            error = "";
            Mutex mutex = null;
            bool held = false;
            try
            {
                mutex = new Mutex(false, MutexName);
                try { held = mutex.WaitOne(10000); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) { error = "Timed out waiting for the rules file lock."; return false; }
                latest = LoadFromPath(path);
                Rule rule;
                if (!latest.TryGetValue(key, out rule) || rule == null)
                    rule = Rule.New();
                mutation(rule);
                latest[key] = rule;
                return WriteToPath(path, latest, out error);
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally
            {
                if (held) try { mutex.ReleaseMutex(); } catch { }
                if (mutex != null) mutex.Dispose();
            }
        }

        private static bool SaveExact(string path, ConcurrentDictionary<string, Rule> rules, out string error)
        {
            error = "";
            Mutex mutex = null;
            bool held = false;
            try
            {
                mutex = new Mutex(false, MutexName);
                try { held = mutex.WaitOne(10000); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) { error = "Timed out waiting for the rules file lock."; return false; }
                return WriteToPath(path, rules, out error);
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally
            {
                if (held) try { mutex.ReleaseMutex(); } catch { }
                if (mutex != null) mutex.Dispose();
            }
        }

        private static ConcurrentDictionary<string, Rule> LoadFromPath(string path)
        {
            try
            {
                if (File.Exists(path)) return DeserializeRules(path);
            }
            catch { }
            try
            {
                string backup = path + ".bak";
                if (File.Exists(backup)) return DeserializeRules(backup);
            }
            catch { }
            return new ConcurrentDictionary<string, Rule>();
        }

        private static ConcurrentDictionary<string, Rule> DeserializeRules(string path)
        {
            var rules = new ConcurrentDictionary<string, Rule>();
            string json = File.ReadAllText(path);
            var dict = new JavaScriptSerializer().Deserialize<Dictionary<string, Rule>>(json);
            if (dict != null)
                foreach (var kv in dict) rules[kv.Key] = kv.Value ?? Rule.New();
            return rules;
        }

        private static bool WriteToPath(string path, ConcurrentDictionary<string, Rule> rules, out string error)
        {
            error = "";
            string temp = path + "." + Process.GetCurrentProcess().Id + "." +
                          Thread.CurrentThread.ManagedThreadId + ".tmp";
            try
            {
                var plain = new Dictionary<string, Rule>();
                foreach (var kv in rules) plain[kv.Key] = kv.Value;
                string json = new JavaScriptSerializer().Serialize(plain);
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try { File.Replace(temp, path, path + ".bak", true); }
                    catch
                    {
                        File.Copy(temp, path, true);
                        File.Delete(temp);
                    }
                }
                else File.Move(temp, path);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { try { File.Delete(temp); } catch { } }
        }

        internal static bool VerifyConcurrentMutationContract()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pspl-rules-contract-" +
                         Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "rules.json");
            Exception firstError = null;
            try
            {
                Directory.CreateDirectory(dir);
                var start = new ManualResetEvent(false);
                Thread a = new Thread(delegate()
                {
                    try
                    {
                        start.WaitOne();
                        for (int i = 1; i <= 40; i++)
                        {
                            ConcurrentDictionary<string, Rule> latest;
                            string error;
                            if (!UpdateRuleAtPath(path, "Alpha", r => r.cpuLimit = i, out latest, out error))
                                throw new InvalidOperationException(error);
                        }
                    }
                    catch (Exception ex) { firstError = ex; }
                });
                Thread b = new Thread(delegate()
                {
                    try
                    {
                        start.WaitOne();
                        for (int i = 1; i <= 40; i++)
                        {
                            ConcurrentDictionary<string, Rule> latest;
                            string error;
                            if (!UpdateRuleAtPath(path, "Beta", r => r.ramLimit = i, out latest, out error))
                                throw new InvalidOperationException(error);
                        }
                    }
                    catch (Exception ex) { firstError = ex; }
                });
                a.Start(); b.Start(); start.Set();
                if (!a.Join(15000) || !b.Join(15000) || firstError != null) return false;
                var final = LoadFromPath(path);
                Rule alpha, beta;
                return final.TryGetValue("Alpha", out alpha) && alpha.cpuLimit == 40 &&
                       final.TryGetValue("Beta", out beta) && beta.ramLimit == 40;
            }
            catch { return false; }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        internal static bool VerifyBackupRecoveryContract()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pspl-rules-backup-contract-" +
                         Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "rules.json");
            try
            {
                Directory.CreateDirectory(dir);
                var rules = new ConcurrentDictionary<string, Rule>();
                var expected = Rule.New();
                expected.cpuLimit = 37;
                rules["RecoveryProbe"] = expected;
                string error;
                if (!SaveExact(path, rules, out error)) return false;
                File.Copy(path, path + ".bak", true);
                File.WriteAllText(path, "{broken-json", new UTF8Encoding(false));
                Rule recovered;
                return LoadFromPath(path).TryGetValue("RecoveryProbe", out recovered) &&
                       recovered.cpuLimit == 37;
            }
            catch { return false; }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    internal static class StartupManager
    {
        public const string TaskName = "PSProcLasso Background Enforcer";

        public static bool IsEnabled()
        {
            string xml;
            if (RunTaskCommand("/Query /TN \"" + TaskName + "\" /XML", 8000, out xml) != 0)
                return false;
            return TaskXmlIsEnabled(xml);
        }

        public static bool Enable(out string error)
        {
            error = "";
            string xmlPath = Path.Combine(Path.GetTempPath(), "psproclasso-startup-" +
                             Process.GetCurrentProcess().Id + ".xml");
            try
            {
                string exe = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
                string user = WindowsIdentity.GetCurrent().Name;
                string xml = BuildTaskXml(exe, user);
                File.WriteAllText(xmlPath, xml, Encoding.Unicode);
                int exit = RunTaskCommand("/Create /TN \"" + TaskName + "\" /XML \"" + xmlPath + "\" /F", 15000);
                if (exit != 0 || !IsEnabled())
                {
                    error = "Task Scheduler rejected the startup task (exit " + exit + ").";
                    return false;
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { try { File.Delete(xmlPath); } catch { } }
        }

        private static string BuildTaskXml(string exe, string user)
        {
            return
                    "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                    "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                    "  <RegistrationInfo><Description>Silently enforces PSProcLasso process rules after every sign-in.</Description></RegistrationInfo>\r\n" +
                    "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId></LogonTrigger></Triggers>\r\n" +
                    "  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
                    "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><RestartOnFailure><Interval>PT1M</Interval><Count>255</Count></RestartOnFailure><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>true</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>\r\n" +
                    "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) +
                    "</Command><Arguments>--background</Arguments><WorkingDirectory>" +
                    SecurityElement.Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\r\n" +
                    "</Task>\r\n";
        }

        internal static bool VerifyLeastPrivilegeContract()
        {
            string xml = BuildTaskXml(@"C:\Apps\PSProcLassoGUI.exe", @"DOMAIN\User");
            return xml.Contains("<RunLevel>LeastPrivilege</RunLevel>") &&
                   !xml.Contains("<RunLevel>HighestAvailable</RunLevel>");
        }

        private static bool TaskXmlIsEnabled(string xml)
        {
            if (String.IsNullOrWhiteSpace(xml)) return false;
            try
            {
                var document = new System.Xml.XmlDocument();
                document.LoadXml(xml);
                var namespaces = new System.Xml.XmlNamespaceManager(document.NameTable);
                namespaces.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
                var enabled = document.SelectSingleNode("/t:Task/t:Settings/t:Enabled", namespaces);
                bool value;
                return enabled == null ||
                       !Boolean.TryParse(enabled.InnerText, out value) || value;
            }
            catch { return false; }
        }

        internal static bool VerifyDisabledStateContract()
        {
            const string prefix =
                "<?xml version=\"1.0\"?><Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers><Settings>";
            const string suffix = "</Settings></Task>";
            return TaskXmlIsEnabled(prefix + "<Enabled>true</Enabled>" + suffix) &&
                   !TaskXmlIsEnabled(prefix + "<Enabled>false</Enabled>" + suffix) &&
                   TaskXmlIsEnabled(prefix + suffix) &&
                   !TaskXmlIsEnabled("not xml");
        }

        public static bool Disable(out string error)
        {
            error = "";
            try
            {
                if (RunTaskCommand("/Query /TN \"" + TaskName + "\" /FO LIST", 8000) != 0)
                    return true;
                if (!IsEnabled()) return true;
                int exit = RunTaskCommand("/Change /TN \"" + TaskName + "\" /Disable", 10000);
                if (exit != 0 || IsEnabled())
                {
                    error = "Could not disable the startup task (exit " + exit + ").";
                    return false;
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static int RunTaskCommand(string arguments, int timeoutMs)
        {
            string ignored;
            return RunTaskCommand(arguments, timeoutMs, out ignored);
        }

        private static int RunTaskCommand(string arguments, int timeoutMs, out string output)
        {
            output = "";
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return -1;
                }
                try
                {
                    output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();
                    if (!String.IsNullOrWhiteSpace(error)) output += "\r\n" + error;
                }
                catch { }
                return p.ExitCode;
            }
        }
    }

    // ---------------------------------------------------------------------
    //  Sampler — background thread, ~200ms cadence (GPU ~1s)
    // ---------------------------------------------------------------------
    internal class Sampler
    {
        public volatile Snapshot Snap = new Snapshot();
        public volatile bool Running = true;
        public volatile bool GpuOn = true;
        public volatile bool ProBalance = true;
        public volatile bool EnforcementEnabled = true;
        // Legacy top-20 boosting is opt-in through its explicit CLI command. Running it
        // continuously could promote a background worker that the safe optimizer had
        // just deprioritized, so the normal GUI uses one coherent policy instead.
        public volatile bool AdaptiveTop20Enabled = false;

        public ConcurrentDictionary<string, Rule> Rules;
        public ConcurrentDictionary<int, GpuLimitState> GpuLimits = new ConcurrentDictionary<int, GpuLimitState>();
        public ConcurrentDictionary<int, CpuLimitState> CpuLimits = new ConcurrentDictionary<int, CpuLimitState>();
        public ConcurrentDictionary<int, RamLimitState> RamLimits = new ConcurrentDictionary<int, RamLimitState>();
        public ConcurrentDictionary<int, PbState> PbState = new ConcurrentDictionary<int, PbState>();
        public ConcurrentDictionary<int, ProcessHandleState> Handles = new ConcurrentDictionary<int, ProcessHandleState>();
        public ConcurrentDictionary<int, ResourceJobState> ResourceJobs = new ConcurrentDictionary<int, ResourceJobState>();
        public ConcurrentDictionary<int, Process> GpuGuards = new ConcurrentDictionary<int, Process>();
        public ConcurrentDictionary<int, long> AdaptiveTop20Pids =
            new ConcurrentDictionary<int, long>();

        private readonly object _errLock = new object();
        private readonly List<string> _errors = new List<string>();
        public IList<string> Errors { get { lock (_errLock) return _errors.ToList(); } }
        private void AddError(string msg)
        {
            lock (_errLock) { _errors.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + msg); if (_errors.Count > 6) _errors.RemoveAt(0); }
        }

        private sealed class CpuBaseline
        {
            public TimeSpan Total;
            public long StartTicks;
            public long SampleTicks;
            public string Name;
        }

        private sealed class GpuSample
        {
            public readonly Dictionary<int, double> Usage;
            public readonly Dictionary<int, long> Memory;
            public readonly Dictionary<int, Dictionary<string, double>> Engines;
            public readonly double Total;
            public readonly long VramUsed;
            public readonly long VramTotal;

            public GpuSample(Dictionary<int, double> usage, Dictionary<int, long> memory,
                             Dictionary<int, Dictionary<string, double>> engines,
                             double total, long vramUsed, long vramTotal)
            {
                Usage = usage;
                Memory = memory;
                Engines = engines;
                Total = total;
                VramUsed = vramUsed;
                VramTotal = vramTotal;
            }
        }

        private Dictionary<int, CpuBaseline> _prevCpu = new Dictionary<int, CpuBaseline>();
        private readonly ConcurrentDictionary<int, string> _pathByPid =
            new ConcurrentDictionary<int, string>();
        private readonly ConcurrentDictionary<int, long> _pathStartByPid =
            new ConcurrentDictionary<int, long>();
        private volatile GpuSample _gpuSample =
            new GpuSample(new Dictionary<int, double>(), new Dictionary<int, long>(),
                          new Dictionary<int, Dictionary<string, double>>(), 0, 0, 0);
        private long _lastSampleTicks;
        private readonly Dictionary<int, long> _ruleAppliedPids = new Dictionary<int, long>();
        private long _lastGpuStart;
        private DateTime _lastGpuInit = DateTime.MinValue;
        private volatile int _gpuWarmupUntil;
        private volatile int _fastIntervalMs = 150;      // warm-up cadence (first 2s: table fills fast)
        private volatile int _steadyIntervalMs = 500;    // live CPU/RAM/process ranking cadence
        private volatile int _gpuIntervalMs = 1000;      // Windows GPU counters are reliable at ~1 Hz
        private volatile int _adaptiveIntervalMs = 2000;
        private int _lastAdaptiveTop20Ms;
        private int _lastAdaptivePersistenceMs;
        private string _adaptiveRuleFingerprint = "";
        private readonly Dictionary<string, int> _adaptiveApplicationLastSeen =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _adaptiveApplicationMetrics =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly int _warmupMs = 2000;           // how long the fast warm-up cadence runs
        public long LastFastMs;
        public long LastGpuPassMs;
        public long LastGpuCycleMs;  // wall time between successive GPU pass starts
        public long FastDataTick;    // incremented whenever a CPU/RAM/process sample lands
        public long GpuDataTick;     // incremented every time a GPU pass lands
        public volatile bool GpuReady;  // true once the first GPU pass has landed
        private volatile int _gpuPublishedMs;

        public bool GpuFresh
        {
            get
            {
                int maxAge = Math.Max(2500, _gpuIntervalMs * 3);
                return GpuOn && GpuReady &&
                       IsTickFresh(Environment.TickCount, _gpuPublishedMs, maxAge);
            }
        }

        public int GpuDataAgeMs
        {
            get
            {
                if (!GpuReady || _gpuPublishedMs == 0) return Int32.MaxValue;
                int age = unchecked(Environment.TickCount - _gpuPublishedMs);
                return age >= 0 ? age : Int32.MaxValue;
            }
        }

        private PerformanceCounter _pcCpu, _pcAvail, _pcStbyN, _pcStbyC;
        private readonly List<CounterRef> _gpuPcs = new List<CounterRef>();
        private readonly List<CounterRef> _gpuMemPcs = new List<CounterRef>();
        private readonly List<CounterRef> _gpuAdapPcs = new List<CounterRef>();

        private class CounterRef
        {
            public string Name;
            public PerformanceCounter Pc;
            public Func<float> Reader;

            public float NextValue()
            {
                return Reader != null ? Reader() : Pc.NextValue();
            }
        }

        private Thread _thread;
        // heartbeat as int ms (Environment.TickCount); written by the sampler thread,
        // read by the UI for self-healing. Aligned 32-bit reads/writes are atomic.
        public volatile int HeartbeatMs = Environment.TickCount;
        private readonly object _startLock = new object();

        public void Start()
        {
            lock (_startLock)
            {
                if (_thread != null && _thread.IsAlive) return;
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Priority = ThreadPriority.AboveNormal;  // stay responsive even at 100% CPU
                _thread.Start();
                StartGpuThread();
                StartPathThread();
                if (EnforcementEnabled) StartEnforcementThread();
            }
        }

        public void SetInteractiveMode(bool interactive)
        {
            _fastIntervalMs = interactive ? 150 : 500;
            _steadyIntervalMs = interactive ? 500 : 1000;
            _gpuIntervalMs = interactive ? 1000 : 5000;
            _adaptiveIntervalMs = interactive ? 2000 : 5000;
        }

        public void Stop()
        {
            Running = false;
            try { if (_thread != null) _thread.Join(2000); } catch { }
            try { if (_gpuThread != null) _gpuThread.Join(2000); } catch { }
            try { if (_pathThread != null) _pathThread.Join(2000); } catch { }
            try { if (_enforcementThread != null) _enforcementThread.Join(2000); } catch { }
            // never leave a process suspended
            foreach (var kv in GpuLimits)
            {
                try { if (kv.Value.Suspended) ResumeProc(kv.Key, kv.Value.StartTicks); } catch { }
            }
            foreach (int pid in GpuGuards.Keys.ToList()) StopGpuGuard(pid);
            foreach (var kv in Handles) CloseProcHandle(kv.Key);
            foreach (int pid in ResourceJobs.Keys.ToList()) CloseResourceJob(pid);
        }

        // -----------------------------------------------------------------
        private void Loop()
        {
            // The whole loop is guarded so the sampler thread can never die: any
            // failure is logged and the next cycle continues. A heartbeat is stamped
            // every cycle so the UI can detect (and auto-restart) a wedged thread.
            // Startup order matters for "ready in <2s": the process table lands on the
            // very first sample (no counters needed) and CPU/RAM totals after the core
            // counters (milliseconds). GPU counters are created on a separate thread
            // (GpuLoop) so they never delay CPU/RAM data.
            var sw = Stopwatch.StartNew();
            try { SampleFast(); } catch (Exception ex) { AddError("sample: " + ex.Message); }
            try { InitCoreCounters(); } catch (Exception ex) { AddError("counters: " + ex.Message); }
            try { SampleFast(); } catch (Exception ex) { AddError("sample: " + ex.Message); }
            _lastSampleTicks = Stopwatch.GetTimestamp();

            while (Running)
            {
                try
                {
                    HeartbeatMs = Environment.TickCount;
                    long t0 = sw.ElapsedMilliseconds;
                    long tFast = t0;
                    try { SampleFast(); } catch (Exception ex) { AddError("sample: " + ex.Message); }
                    LastFastMs = sw.ElapsedMilliseconds - tFast;
                    if (EnforcementEnabled)
                        try { Steps(); } catch (Exception ex) { AddError("step: " + ex.Message); }
                    long took = sw.ElapsedMilliseconds - t0;
                    long interval = sw.ElapsedMilliseconds < _warmupMs ? _fastIntervalMs : _steadyIntervalMs;
                    Thread.Sleep(Math.Max(10, (int)(interval - took)));
                }
                catch (Exception ex) { AddError("loop: " + ex.Message); }
            }
        }

        // -----------------------------------------------------------------
        //  GPU sampling runs on its own thread so the slow GPU counter creation
        //  (~1s, hundreds of PerformanceCounter objects) and the ~550ms GPU pass
        //  can never delay the CPU/RAM cadence or the UI. Fully guarded: it cannot
        //  die, and if it does, EnsureAlive restarts it.
        private Thread _gpuThread;
        private volatile bool _gpuThreadRanOnce;
        private Thread _pathThread;
        private volatile bool _pathThreadRanOnce;
        private Thread _enforcementThread;
        private volatile bool _enforcementThreadRanOnce;

        private void StartGpuThread()
        {
            if (_gpuThread != null && _gpuThread.IsAlive) return;
            _gpuThread = new Thread(GpuLoop);
            _gpuThread.IsBackground = true;
            _gpuThread.Priority = ThreadPriority.AboveNormal;  // keep GPU data flowing even at 100% CPU
            _gpuThread.Start();
        }

        private void GpuLoop()
        {
            _gpuThreadRanOnce = true;
            long start = Environment.TickCount;
            while (Running)
            {
                try
                {
                    if (!GpuOn)
                    {
                        if (_gpuCountersInit) ResetGpuCounters();
                        if (GpuReady)
                        {
                            PublishGpuSample(new Dictionary<int, double>(),
                                             new Dictionary<int, long>(),
                                             new Dictionary<int, Dictionary<string, double>>(),
                                             0, 0, 0);
                            GpuReady = false;
                        }
                        Thread.Sleep(200);
                        continue;
                    }
                    if (!_gpuCountersInit) InitGpuCounters();
                    if (_gpuPcs.Count == 0 && _gpuMemPcs.Count == 0) { Thread.Sleep(1000); continue; }
                    int warmupDelay = GpuWarmupRemaining(Environment.TickCount, _gpuWarmupUntil);
                    if (warmupDelay > 0) { Thread.Sleep(warmupDelay); continue; }
                    long t0 = Environment.TickCount;
                    if (_lastGpuStart > 0) LastGpuCycleMs = t0 - _lastGpuStart;
                    _lastGpuStart = t0;
                    try
                    {
                        bool published = SampleGpu();
                        if (!published)
                        {
                            if (!GpuFresh) GpuReady = false;
                            Thread.Sleep(100);
                            continue;
                        }
                        GpuDataTick++;
                    }
                    catch (Exception ex) { AddError("gpu: " + ex.Message); }
                    LastGpuPassMs = Environment.TickCount - t0;
                    int took = (int)(Environment.TickCount - t0);
                    int cycle = (t0 - start) < _warmupMs ? 800 : _gpuIntervalMs;
                    Thread.Sleep(Math.Max(50, cycle - took));
                }
                catch (Exception ex) { AddError("gpu loop: " + ex.Message); Thread.Sleep(500); }
            }
        }

        private void StartPathThread()
        {
            if (_pathThread != null && _pathThread.IsAlive) return;
            _pathThread = new Thread(PathLoop);
            _pathThread.IsBackground = true;
            _pathThread.Priority = ThreadPriority.BelowNormal;
            _pathThread.Start();
        }

        private void PathLoop()
        {
            _pathThreadRanOnce = true;
            while (Running)
            {
                try
                {
                    var seen = new HashSet<int>();
                    Process[] processes;
                    try { processes = Process.GetProcesses(); }
                    catch { processes = new Process[0]; }
                    foreach (var process in processes)
                    {
                        try
                        {
                            int pid = process.Id;
                            seen.Add(pid);
                            long startTicks = 0;
                            try { startTicks = process.StartTime.ToUniversalTime().Ticks; } catch { }
                            long knownStart;
                            string knownPath;
                            if (_pathStartByPid.TryGetValue(pid, out knownStart) &&
                                _pathByPid.TryGetValue(pid, out knownPath) &&
                                (startTicks == 0 || knownStart == startTicks))
                                continue;
                            string path = "";
                            try { path = process.MainModule.FileName; } catch { }
                            _pathByPid[pid] = path ?? "";
                            _pathStartByPid[pid] = startTicks;
                        }
                        catch { }
                        finally { process.Dispose(); }
                    }
                    foreach (int pid in _pathByPid.Keys)
                    {
                        if (seen.Contains(pid)) continue;
                        string ignoredPath;
                        long ignoredStart;
                        _pathByPid.TryRemove(pid, out ignoredPath);
                        _pathStartByPid.TryRemove(pid, out ignoredStart);
                    }
                }
                catch (Exception ex) { AddError("path cache: " + ex.Message); }

                for (int i = 0; i < 150 && Running; i++) Thread.Sleep(100);
            }
        }

        private void StartEnforcementThread()
        {
            if (_enforcementThread != null && _enforcementThread.IsAlive) return;
            _enforcementThread = new Thread(EnforcementLoop);
            _enforcementThread.IsBackground = true;
            _enforcementThread.Priority = ThreadPriority.AboveNormal;
            _enforcementThread.Start();
        }

        private void EnforcementLoop()
        {
            _enforcementThreadRanOnce = true;
            while (Running)
            {
                try { StepGpuThrottle(); }
                catch (Exception ex) { AddError("GPU limiter: " + ex.Message); }
                Thread.Sleep(10);
            }
        }

        // Self-healing: called from the UI timer; if the sampler thread is dead or
        // wedged (no heartbeat for >2s) it is restarted so monitoring never stops.
        public void EnsureAlive()
        {
            // TickCount wraps every ~49 days; the subtraction stays correct because it
            // is an int difference < 2^31 in practice.
            // A healthy sampler checks in twice a second. Allow a generous margin for
            // temporary machine saturation, then self-heal if it genuinely wedges.
            bool stale = (Environment.TickCount - HeartbeatMs) > 5000;
            if (stale)
            {
                try { if (_thread != null && _thread.IsAlive) _thread.Abort(); } catch { }
                _thread = null;
                AddError("sampler thread restarted (heartbeat stale)");
                Start();
            }
            // GPU thread must be alive too; restart it independently if it died.
            // (_gpuThreadRanOnce avoids double-spawning while a fresh thread waits to run.)
            if (_gpuThread == null || (!_gpuThread.IsAlive && _gpuThreadRanOnce))
            {
                if (_gpuThread != null) { try { _gpuThread.Abort(); } catch { } _gpuThread = null; }
                _gpuThreadRanOnce = false;
                StartGpuThread();
            }
            if (_pathThread == null || (!_pathThread.IsAlive && _pathThreadRanOnce))
            {
                _pathThread = null;
                _pathThreadRanOnce = false;
                StartPathThread();
            }
            if (EnforcementEnabled &&
                (_enforcementThread == null || (!_enforcementThread.IsAlive && _enforcementThreadRanOnce)))
            {
                _enforcementThread = null;
                _enforcementThreadRanOnce = false;
                StartEnforcementThread();
            }
        }

        private void Steps()
        {
            ApplyRulesToNew();
            StepCpuLimits();
            StepRamLimits();
            StepProBalance();
            StepAdaptiveTop20();
            StepWatchdog();
        }

        // -----------------------------------------------------------------
        // Split so the process table + CPU/RAM totals can appear before the slow GPU
        // counter creation finishes: InitCoreCounters() is milliseconds, the GPU part
        // can take ~1s under load (hundreds of PerformanceCounter objects).
        private bool _coreCountersInit;
        private bool _gpuCountersInit;

        private void InitCounters()
        {
            InitCoreCounters();
            InitGpuCounters();
        }

        private void InitCoreCounters()
        {
            if (_coreCountersInit) return;
            _coreCountersInit = true;
            try { _pcCpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", true); _pcCpu.NextValue(); } catch { _pcCpu = null; }
            try { _pcAvail = new PerformanceCounter("Memory", "Available MBytes", "", true); _pcAvail.NextValue(); } catch { _pcAvail = null; }
            try
            {
                _pcStbyN = new PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes", "", true);
                _pcStbyC = new PerformanceCounter("Memory", "Standby Cache Core Bytes", "", true);
                _pcStbyN.NextValue(); _pcStbyC.NextValue();
            }
            catch { _pcStbyN = null; _pcStbyC = null; }
        }

        private void InitGpuCounters()
        {
            if (!GpuOn || _gpuCountersInit) return;
            try
            {
                SyncGpuCounters("GPU Engine", "Utilization Percentage", _gpuPcs);
                SyncGpuCounters("GPU Process Memory", "Dedicated Usage", _gpuMemPcs);
                SyncGpuCounters("GPU Adapter Memory", "Dedicated Usage", _gpuAdapPcs);
                _gpuCountersInit = _gpuPcs.Count > 0 || _gpuMemPcs.Count > 0 || _gpuAdapPcs.Count > 0;
                if (_gpuCountersInit)
                    _gpuWarmupUntil = unchecked(Environment.TickCount + 250);
            }
            catch { ResetGpuCounters(); }
        }

        private void ResetGpuCounters()
        {
            foreach (var list in new[] { _gpuPcs, _gpuMemPcs, _gpuAdapPcs })
            {
                foreach (var counter in list)
                    try { if (counter.Pc != null) counter.Pc.Dispose(); } catch { }
                list.Clear();
            }
            _gpuCountersInit = false;
            _gpuWarmupUntil = 0;
        }

        private void SampleFast()
        {
            var s = new Snapshot();
            GpuSample gpuSample = _gpuSample;
            bool gpuFresh = GpuFresh;
            long nowTicks = Stopwatch.GetTimestamp();
            double el = _lastSampleTicks == 0 ? 0 :
                (nowTicks - _lastSampleTicks) * 1000.0 / Stopwatch.Frequency;
            int cores = Math.Max(1, Environment.ProcessorCount);

            if (_pcCpu != null)
            {
                try { double v = _pcCpu.NextValue(); if (v >= 0) s.TotalCpu = Math.Round(Math.Min(100, v), 1); }
                catch { }
            }
            long physicalTotal, physicalAvailable;
            if (Native.GetPhysicalMemory(out physicalTotal, out physicalAvailable))
            {
                s.RamTotal = physicalTotal;
                s.RamUsed = Math.Max(0, physicalTotal - physicalAvailable);
                s.AvailMB = Math.Round(physicalAvailable / (1024.0 * 1024.0), 1);
            }
            else if (_pcAvail != null)
            {
                try { s.AvailMB = Math.Round(Math.Max(0, _pcAvail.NextValue()), 1); }
                catch { }
            }
            if (_pcStbyN != null)
            {
                try { s.Standby = (long)_pcStbyN.NextValue() + (long)_pcStbyC.NextValue(); } catch { }
            }

            var rows = new List<ProcRow>(500);
            var curIds = new HashSet<int>();
            Process[] procs = null;
            try { procs = Process.GetProcesses(); } catch (Exception ex) { AddError("procs: " + ex.Message); procs = new Process[0]; }

            foreach (var p in procs)
            {
                ProcRow r = new ProcRow();
                try
                {
                    r.Id = p.Id; curIds.Add(p.Id);
                    r.Name = p.ProcessName;
                    TimeSpan tp = TimeSpan.Zero;
                    bool cpuReadOk = false;
                    try { tp = p.TotalProcessorTime; cpuReadOk = true; } catch { }
                    long processSampleTicks = Stopwatch.GetTimestamp();
                    long startTicks = 0;
                    try { startTicks = p.StartTime.ToUniversalTime().Ticks; } catch { }
                    r.StartTicks = startTicks;
                    long cachedStart;
                    string cachedPath;
                    if (_pathByPid.TryGetValue(r.Id, out cachedPath) &&
                        _pathStartByPid.TryGetValue(r.Id, out cachedStart) &&
                        (startTicks == 0 || cachedStart == startTicks))
                    {
                        r.ExecutablePath = cachedPath;
                    }
                    else r.ExecutablePath = "";
                    r.GroupKey = ApplicationGroupKey(r.Name, r.ExecutablePath);
                    r.Cpu = UpdateCpuBaseline(_prevCpu, r.Id, r.Name, cpuReadOk, tp,
                                              startTicks, processSampleTicks, cores);
                    if (r.Cpu > 100.0) r.Cpu = 100.0;
                    r.Cpu = Math.Round(r.Cpu, 3);
                    try { r.Mem = p.WorkingSet64; } catch { }
                    try { r.Priv = p.PrivateMemorySize64; } catch { }
                    try { r.Priority = p.PriorityClass.ToString(); } catch { }
                    try { r.Affinity = FormatAffinity(p.ProcessorAffinity.ToInt64()); } catch { }
                    try { r.Threads = p.Threads.Count; } catch { }
                    try { r.HasVisibleWindow = p.MainWindowHandle != IntPtr.Zero; } catch { }
                    try { r.SessionId = p.SessionId; } catch { r.SessionId = -1; }
                    r.GpuValid = gpuFresh;
                    if (gpuFresh)
                    {
                        double gpu; if (gpuSample.Usage.TryGetValue(r.Id, out gpu)) r.Gpu = Math.Round(gpu, 3);
                        long vram; if (gpuSample.Memory.TryGetValue(r.Id, out vram)) r.Vram = vram;
                        Dictionary<string, double> engines;
                        if (gpuSample.Engines.TryGetValue(r.Id, out engines)) r.GpuEngines = engines;
                    }
                    r.HasLimit = GpuLimits.ContainsKey(r.Id) || CpuLimits.ContainsKey(r.Id) || RamLimits.ContainsKey(r.Id);
                    var controls = new List<string>();
                    CpuLimitState cpuLimit;
                    if (CpuLimits.TryGetValue(r.Id, out cpuLimit))
                        controls.Add("CPU " + cpuLimit.Limit.ToString("N0") + "% " + (cpuLimit.Hard ? "hard" : "priority fallback"));
                    GpuLimitState gpuLimit;
                    if (GpuLimits.TryGetValue(r.Id, out gpuLimit))
                        controls.Add("GPU duty " + gpuLimit.Pct.ToString("N0") + "%");
                    RamLimitState ramLimit;
                    if (RamLimits.TryGetValue(r.Id, out ramLimit))
                        controls.Add("RAM " + ramLimit.Mb + " MB " + (ramLimit.Hard ? "hard" : "trim fallback"));
                    long adaptiveIdentity;
                    if (AdaptiveTop20Pids.TryGetValue(r.Id, out adaptiveIdentity) &&
                        (adaptiveIdentity == 0 || adaptiveIdentity == r.StartTicks))
                        controls.Add("Top20 performance");
                    r.Controls = string.Join(", ", controls);
                    Rule rule;
                    if (Rules.TryGetValue(r.Name, out rule) && rule.watchdog && rule.enabled) r.Watchdog = true;
                    r.Pb = PbState.ContainsKey(r.Id);
                }
                catch { /* process exited mid-read */ }
                finally { p.Dispose(); }
                if (r.Id > 0 && !String.IsNullOrEmpty(r.Name)) rows.Add(r);
            }

            foreach (var k in _prevCpu.Keys.ToList()) if (!curIds.Contains(k)) _prevCpu.Remove(k);
            s.Rows = rows;
            s.ProcessCount = rows.Count;
            if (s.RamTotal <= 0)
            {
                s.RamTotal = Snap.RamTotal > 0 ? Snap.RamTotal : Native.GetTotalPhysicalRam();
                s.RamUsed = s.RamTotal > 0 ? Math.Max(0, s.RamTotal - (long)(s.AvailMB * 1024 * 1024)) : 0;
            }
            // One immutable GPU generation supplies both rows and totals. The fast
            // sampler can never observe the old clear-then-refill intermediate state.
            s.GpuValid = gpuFresh;
            if (gpuFresh)
            {
                s.GpuPct = gpuSample.Total;
                s.VramUsed = gpuSample.VramUsed;
                s.VramTotal = gpuSample.VramTotal;
            }
            _lastSampleTicks = nowTicks;
            Snap = s;
            Interlocked.Increment(ref FastDataTick);
        }

        private static double ComputeProcessCpuPercent(TimeSpan previousTotal, long previousStartTicks,
                                                       TimeSpan currentTotal, long currentStartTicks,
                                                       double elapsedMs, int cores)
        {
            if (elapsedMs <= 0 || cores <= 0) return 0;
            if (previousStartTicks != 0 && currentStartTicks != 0 &&
                previousStartTicks != currentStartTicks) return 0;
            double deltaMs = (currentTotal - previousTotal).TotalMilliseconds;
            if (deltaMs <= 0) return 0;
            return Math.Min(100.0, deltaMs / elapsedMs / cores * 100.0);
        }

        private static double UpdateCpuBaseline(Dictionary<int, CpuBaseline> baselines, int pid,
                                                string name, bool readOk, TimeSpan total,
                                                long startTicks, long sampleTicks, int cores)
        {
            if (!readOk) return 0;
            double value = 0;
            CpuBaseline previous;
            if (baselines.TryGetValue(pid, out previous) &&
                String.Equals(previous.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                double elapsedMs = (sampleTicks - previous.SampleTicks) * 1000.0 / Stopwatch.Frequency;
                value = ComputeProcessCpuPercent(previous.Total, previous.StartTicks,
                                                 total, startTicks, elapsedMs, cores);
            }
            baselines[pid] = new CpuBaseline
            {
                Total = total,
                StartTicks = startTicks,
                SampleTicks = sampleTicks,
                Name = name
            };
            return value;
        }

        internal static string ApplicationGroupKey(string name, string executablePath)
        {
            return "family:" + ApplicationFamilyToken(name);
        }

        internal static string ApplicationFamilyToken(string name)
        {
            string token = (name ?? "").Trim().ToLowerInvariant();
            if (token == "wsl" || token == "wslhost" || token == "wslservice" ||
                token == "wslrelay" || token == "wslg" || token == "vmmemwsl")
                return "windows-subsystem-for-linux";
            if (token == "wscript" || token == "cscript") return "windows-script-host";
            return token;
        }

        internal static string ApplicationDisplayName(string name)
        {
            string token = ApplicationFamilyToken(name);
            if (token == "windows-subsystem-for-linux") return "Windows Subsystem for Linux";
            if (token == "windows-script-host") return "Windows Script Host";
            return name ?? "";
        }

        private void PublishGpuSample(Dictionary<int, double> usage, Dictionary<int, long> memory,
                                      Dictionary<int, Dictionary<string, double>> engines,
                                      double total, long vramUsed, long vramTotal)
        {
            _gpuSample = new GpuSample(usage, memory, engines, total, vramUsed, vramTotal);
            _gpuPublishedMs = Environment.TickCount;
            GpuReady = true;
        }

        private static bool IsTickFresh(int now, int published, int maxAge)
        {
            if (published == 0 || maxAge < 0) return false;
            int age = unchecked(now - published);
            return age >= 0 && age <= maxAge;
        }

        internal static bool VerifyGpuFreshnessContract()
        {
            int now = 100000;
            bool neverPublished = !IsTickFresh(now, 0, 2500);
            bool fresh = IsTickFresh(now, now - 1000, 2500);
            bool stale = !IsTickFresh(now, now - 2501, 2500);
            int wrappedNow = Int32.MinValue + 100;
            int wrappedPublished = Int32.MaxValue - 99;
            bool wrapSafe = IsTickFresh(wrappedNow, wrappedPublished, 2500);
            return neverPublished && fresh && stale && wrapSafe;
        }

        internal static bool VerifyPidSafeCpuDeltaContract()
        {
            double sameProcess = ComputeProcessCpuPercent(
                TimeSpan.FromMilliseconds(100), 1000,
                TimeSpan.FromMilliseconds(200), 1000, 1000, 1);
            double reusedPid = ComputeProcessCpuPercent(
                TimeSpan.FromMilliseconds(100), 1000,
                TimeSpan.FromMilliseconds(500), 2000, 1000, 1);
            return Math.Abs(sameProcess - 10.0) < 0.001 && reusedPid == 0;
        }

        internal static bool VerifyCpuReadFailureRecoveryContract()
        {
            var baselines = new Dictionary<int, CpuBaseline>();
            long second = Stopwatch.Frequency;
            UpdateCpuBaseline(baselines, 77, "probe", true,
                              TimeSpan.FromMilliseconds(100), 1234, second, 1);
            double failed = UpdateCpuBaseline(baselines, 77, "probe", false,
                                              TimeSpan.Zero, 1234, second * 2, 1);
            double recovered = UpdateCpuBaseline(baselines, 77, "probe", true,
                                                 TimeSpan.FromMilliseconds(200), 1234,
                                                 second * 3, 1);
            return failed == 0 && Math.Abs(recovered - 5.0) < 0.001;
        }

        internal static bool VerifyGpuCounterFailureIsolationContract()
        {
            var counters = new List<CounterRef>
            {
                new CounterRef { Name = "pid_10_luid_0_phys_0_eng_0_engtype_3D", Reader = () => 12f },
                new CounterRef { Name = "pid_20_luid_0_phys_0_eng_0_engtype_3D", Reader = () => { throw new InvalidOperationException("expected"); } },
                new CounterRef { Name = "pid_30_luid_0_phys_0_eng_0_engtype_3D", Reader = () => 34f }
            };
            var usage = new Dictionary<int, double>();
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var enginesByPid = new Dictionary<int, Dictionary<string, double>>();
            int read = ReadGpuEngineCounters(counters, usage, engines, enginesByPid);
            return read == 2 && counters.Count == 2 &&
                   usage.ContainsKey(10) && usage.ContainsKey(30) &&
                   !usage.ContainsKey(20) && Math.Abs(usage[30] - 34) < 0.001;
        }

        private static int GpuWarmupRemaining(int now, int until)
        {
            int remaining = unchecked(until - now);
            return remaining > 0 && remaining <= 1000 ? remaining : 0;
        }

        internal static bool VerifyGpuWarmupContract()
        {
            int now = Int32.MaxValue - 100;
            int until = unchecked(now + 250);
            return GpuWarmupRemaining(now, until) == 250 &&
                   GpuWarmupRemaining(unchecked(now + 249), until) == 1 &&
                   GpuWarmupRemaining(unchecked(now + 250), until) == 0;
        }

        internal static bool VerifyProcessIdentitySafetyContract()
        {
            var sampler = new Sampler();
            using (var current = Process.GetCurrentProcess())
            {
                long start = GetStartTicks(current);
                Process match;
                bool accepted = sampler.TryGetExpectedProc(current.Id, start, current.ProcessName, out match);
                if (match != null) match.Dispose();
                Process stale;
                bool refused = !sampler.TryGetExpectedProc(current.Id, start + 1,
                                                           current.ProcessName, out stale);
                if (stale != null) stale.Dispose();
                return start != 0 && accepted && refused;
            }
        }

        internal static bool VerifyNativeStatusSafetyContract()
        {
            return NtStatusSucceeded(0) && NtStatusSucceeded(1) &&
                   !NtStatusSucceeded(unchecked((int)0xC0000001));
        }

        internal static bool VerifyResourceReleaseSafetyContract()
        {
            var sampler = new Sampler();
            IntPtr handle = Native.CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) return false;
            try
            {
                var state = new ResourceJobState { Handle = handle };
                bool set = sampler.ConfigureCpuJob(state, 25) &&
                           sampler.ConfigureRamJob(state, 128);
                bool cleared = sampler.ClearResourceJobLimits(state);
                return set && cleared && state.CpuPercent == 0 && state.RamMb == 0;
            }
            finally { Native.CloseHandle(handle); }
        }

        internal static bool VerifyAtomicGpuPublicationContract()
        {
            var sampler = new Sampler();
            int failed = 0;
            bool done = false;
            var writer = new Thread(() =>
            {
                for (int generation = 1; generation <= 20000; generation++)
                {
                    sampler.PublishGpuSample(
                        new Dictionary<int, double> { { 1, generation } },
                        new Dictionary<int, long> { { 1, generation } },
                        new Dictionary<int, Dictionary<string, double>>
                        {
                            { 1, new Dictionary<string, double> { { "engine", generation } } }
                        },
                        generation, generation, generation);
                }
                done = true;
            });
            writer.IsBackground = true;
            writer.Start();
            while (!done)
            {
                GpuSample sample = sampler._gpuSample;
                if (sample.Usage.Count == 0 && sample.Memory.Count == 0) continue;
                double usage;
                long memory;
                if (!sample.Usage.TryGetValue(1, out usage) ||
                    !sample.Memory.TryGetValue(1, out memory) ||
                    usage != memory || usage != sample.Total ||
                    memory != sample.VramUsed || memory != sample.VramTotal)
                {
                    Interlocked.Exchange(ref failed, 1);
                    break;
                }
            }
            writer.Join();
            return failed == 0;
        }

        private bool SampleGpu()
        {
            if (!GpuOn) return false;
            if (!_gpuCountersInit) InitGpuCounters();
            if (_gpuPcs.Count == 0 && _gpuMemPcs.Count == 0) return false;

            // GPU performance-counter instances are created and removed as applications
            // begin/end GPU work. Reconcile every pass so a newly active app appears in
            // the next visible GPU update instead of waiting for an extra cycle.
            if ((DateTime.Now - _lastGpuInit).TotalMilliseconds >= 750)
            {
                _lastGpuInit = DateTime.Now;
                SyncGpuCounters("GPU Engine", "Utilization Percentage", _gpuPcs);
                SyncGpuCounters("GPU Process Memory", "Dedicated Usage", _gpuMemPcs);
                SyncGpuCounters("GPU Adapter Memory", "Dedicated Usage", _gpuAdapPcs);
            }

            var gpuMap = new Dictionary<int, double>();
            var gpuMem = new Dictionary<int, long>();
            var engineTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var enginesByPid = new Dictionary<int, Dictionary<string, double>>();
            long processDedicatedUsed = 0, adapterDedicatedUsed = 0;
            bool hadEngineCounters = _gpuPcs.Count > 0;
            bool hadMemoryCounters = _gpuMemPcs.Count > 0 || _gpuAdapPcs.Count > 0;
            int engineReads = ReadGpuEngineCounters(_gpuPcs, gpuMap, engineTotals, enginesByPid);
            int memoryReads = ReadGpuMemoryCounters(_gpuMemPcs, gpuMem, ref processDedicatedUsed);
            int adapterReads = ReadGpuAdapterCounters(_gpuAdapPcs, ref adapterDedicatedUsed);
            if ((hadEngineCounters && engineReads == 0) ||
                (hadMemoryCounters && memoryReads + adapterReads == 0))
            {
                _gpuCountersInit = false;
                return false;
            }

            double total = Math.Round(engineTotals.Count == 0 ? 0 :
                Math.Min(100, engineTotals.Values.Max()), 1);
            long vramUsed = adapterDedicatedUsed > 0 ? adapterDedicatedUsed : processDedicatedUsed;
            // Publish the complete generation in one reference write. Windows exposes
            // reliable live dedicated usage, but not a universal physical capacity.
            PublishGpuSample(gpuMap, gpuMem, enginesByPid, total, vramUsed, 0);
            return true;
        }

        private static int ReadGpuEngineCounters(List<CounterRef> counters,
                                                 Dictionary<int, double> usage,
                                                 Dictionary<string, double> engineTotals,
                                                 Dictionary<int, Dictionary<string, double>> enginesByPid)
        {
            int read = 0;
            for (int i = counters.Count - 1; i >= 0; i--)
            {
                CounterRef g = counters[i];
                try
                {
                    double v = Math.Max(0, g.NextValue());
                    read++;
                    int pid;
                    if (!TryParsePid(g.Name, out pid)) continue;
                    string engine = GpuEngineIdentity(g.Name);
                    double old;
                    if (!engineTotals.TryGetValue(engine, out old)) old = 0;
                    engineTotals[engine] = old + v;

                    Dictionary<string, double> processEngines;
                    if (!enginesByPid.TryGetValue(pid, out processEngines))
                    {
                        processEngines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        enginesByPid[pid] = processEngines;
                    }
                    if (!processEngines.TryGetValue(engine, out old)) old = 0;
                    processEngines[engine] = old + v;
                    usage[pid] = Math.Min(100, processEngines.Values.Max());
                }
                catch
                {
                    try { if (g.Pc != null) g.Pc.Dispose(); } catch { }
                    counters.RemoveAt(i);
                }
            }
            return read;
        }

        private static int ReadGpuMemoryCounters(List<CounterRef> counters,
                                                 Dictionary<int, long> memory,
                                                 ref long total)
        {
            int read = 0;
            for (int i = counters.Count - 1; i >= 0; i--)
            {
                CounterRef g = counters[i];
                try
                {
                    long value = Math.Max(0, (long)g.NextValue());
                    read++;
                    total += value;
                    int pid;
                    if (!TryParsePid(g.Name, out pid)) continue;
                    long old;
                    if (!memory.TryGetValue(pid, out old)) old = 0;
                    memory[pid] = old + value;
                }
                catch
                {
                    try { if (g.Pc != null) g.Pc.Dispose(); } catch { }
                    counters.RemoveAt(i);
                }
            }
            return read;
        }

        private static int ReadGpuAdapterCounters(List<CounterRef> counters, ref long total)
        {
            int read = 0;
            for (int i = counters.Count - 1; i >= 0; i--)
            {
                CounterRef g = counters[i];
                try { total += Math.Max(0, (long)g.NextValue()); read++; }
                catch
                {
                    try { if (g.Pc != null) g.Pc.Dispose(); } catch { }
                    counters.RemoveAt(i);
                }
            }
            return read;
        }

        private static string GpuEngineIdentity(string instance)
        {
            int i = instance.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
            string id = i >= 0 ? instance.Substring(i) : instance;
            int duplicate = id.LastIndexOf('#');
            if (duplicate > 0)
            {
                int n;
                if (int.TryParse(id.Substring(duplicate + 1), out n)) id = id.Substring(0, duplicate);
            }
            return id;
        }

        private static void SyncGpuCounters(string category, string counter, List<CounterRef> list)
        {
            try
            {
                var names = new HashSet<string>(
                    new PerformanceCounterCategory(category).GetInstanceNames(),
                    StringComparer.OrdinalIgnoreCase);
                var existing = new HashSet<string>(list.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (names.Contains(list[i].Name)) continue;
                    try { list[i].Pc.Dispose(); } catch { }
                    list.RemoveAt(i);
                }

                foreach (string name in names)
                {
                    if (existing.Contains(name)) continue;
                    try
                    {
                        var pc = new PerformanceCounter(category, counter, name, true);
                        pc.NextValue();
                        list.Add(new CounterRef { Name = name, Pc = pc });
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static bool TryParsePid(string instance, out int pid)
        {
            // GPU engine/memory instance names look like "pid_1234_luid_..." — note the
            // pid_ prefix has NO leading underscore ("_pid_" never matches, which used to
            // make every process show 0% GPU). "pid_" also matches inside "_pid_" formats.
            pid = 0;
            int i = instance.IndexOf("pid_", StringComparison.Ordinal);
            if (i < 0) return false;
            i += 4;
            int j = i;
            while (j < instance.Length && Char.IsDigit(instance[j])) j++;
            if (j == i) return false;
            return int.TryParse(instance.Substring(i, j - i), out pid);
        }

        private static string FormatAffinity(long mask)
        {
            if (mask == 0) return "none";
            var bits = new List<int>();
            for (int i = 0; i < 64 && (mask >> i) != 0; i++) if (((mask >> i) & 1) == 1) bits.Add(i);
            if (bits.Count == 0) return "none";
            if (bits.Count == Environment.ProcessorCount) return "all";
            var parts = new List<string>();
            int k = 0;
            while (k < bits.Count)
            {
                int start = bits[k], end = start;
                while (k + 1 < bits.Count && bits[k + 1] == end + 1) { k++; end = bits[k]; }
                parts.Add(end == start ? start.ToString() : start + "-" + end);
                k++;
            }
            return string.Join(",", parts);
        }

        // -----------------------------------------------------------------
        //  Rules: apply to new processes of a watched name
        // -----------------------------------------------------------------
        private void ApplyRulesToNew()
        {
            var currentPids = new HashSet<int>();
            foreach (var row in Snap.Rows)
            {
                currentPids.Add(row.Id);
                Rule rule;
                if (!Rules.TryGetValue(row.Name, out rule) ||
                    !RuleAppliesInCurrentMode(rule, AdaptiveTop20Enabled)) continue;
                Process p;
                if (!TryGetProc(row.Id, out p)) continue;
                try
                {
                    long identity;
                    try { identity = p.StartTime.ToUniversalTime().Ticks; }
                    catch { identity = row.Id; }
                    long appliedIdentity;
                    bool managedPriorityDrift =
                        ManagedPriorityNeedsReapply(rule, row.Priority);
                    if (_ruleAppliedPids.TryGetValue(row.Id, out appliedIdentity) &&
                        appliedIdentity == identity && !managedPriorityDrift) continue;
                    ApplyRuleToProcess(p, rule);
                    _ruleAppliedPids[row.Id] = identity;
                }
                finally { p.Dispose(); }
            }

            foreach (int pid in _ruleAppliedPids.Keys.ToList())
                if (!currentPids.Contains(pid)) _ruleAppliedPids.Remove(pid);
            foreach (int pid in ResourceJobs.Keys.ToList())
                if (!currentPids.Contains(pid)) CloseResourceJob(pid);
        }

        internal static bool RuleAppliesInCurrentMode(Rule rule,
                                                      bool adaptiveTop20Enabled)
        {
            return rule != null && rule.enabled &&
                   (!rule.performanceManaged || adaptiveTop20Enabled);
        }

        internal static bool ManagedPriorityNeedsReapply(Rule rule,
                                                         string currentPriority)
        {
            return rule != null &&
                   (rule.optimizerManaged || rule.performanceManaged) &&
                   !String.IsNullOrWhiteSpace(rule.priority) &&
                   !String.Equals(currentPriority ?? "", rule.priority,
                                  StringComparison.OrdinalIgnoreCase);
        }

        internal static bool VerifyLegacyBoostIsolationContract()
        {
            Rule ordinary = Rule.New();
            ordinary.priority = "BelowNormal";
            Rule legacyBoost = Rule.New();
            legacyBoost.priority = "AboveNormal";
            legacyBoost.performanceManaged = true;
            Rule disabled = Rule.New();
            disabled.enabled = false;
            return RuleAppliesInCurrentMode(ordinary, false) &&
                   !RuleAppliesInCurrentMode(legacyBoost, false) &&
                   RuleAppliesInCurrentMode(legacyBoost, true) &&
                   ManagedPriorityNeedsReapply(legacyBoost, "Normal") &&
                   !ManagedPriorityNeedsReapply(legacyBoost, "AboveNormal") &&
                   !RuleAppliesInCurrentMode(disabled, true) &&
                   !RuleAppliesInCurrentMode(null, true);
        }

        public void ApplyRuleToProcess(Process p, Rule rule)
        {
            try
            {
                if (!String.IsNullOrEmpty(rule.priority))
                {
                    ProcessPriorityClass ppc;
                    if (Enum.TryParse(rule.priority, true, out ppc)) p.PriorityClass = ppc;
                }
                if (rule.affinity != null && rule.affinity.Length > 0)
                {
                    long mask = 0;
                    foreach (int b in rule.affinity) mask |= 1L << b;
                    p.ProcessorAffinity = (IntPtr)mask;
                }
                if (rule.cpuLimit > 0) ApplyCpuLimit(p, rule.cpuLimit);
                if (rule.gpuLimit > 0 && p.Id != Process.GetCurrentProcess().Id)
                {
                    GpuLimits[p.Id] = new GpuLimitState
                    {
                        Pct = rule.gpuLimit,
                        T0 = Environment.TickCount,
                        StartTicks = GetStartTicks(p)
                    };
                    EnsureGpuGuard(p);
                }
                if (rule.ramLimit > 0) ApplyRamLimit(p, rule.ramLimit);
            }
            catch (Exception ex) { AddError("rule for " + p.ProcessName + ": " + ex.Message); }
        }

        private bool TryGetResourceJob(Process p, out ResourceJobState state, out string error)
        {
            error = "";
            long startTicks = GetStartTicks(p);
            if (ResourceJobs.TryGetValue(p.Id, out state) && state.Handle != IntPtr.Zero)
            {
                if (state.StartTicks == startTicks) return true;
                CloseResourceJob(p.Id);
            }

            IntPtr job = Native.CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                error = "CreateJobObject failed (" + Marshal.GetLastWin32Error() + ")";
                state = null;
                return false;
            }
            try
            {
                if (!Native.AssignProcessToJobObject(job, p.Handle))
                {
                    error = "Windows refused Job Object assignment (" + Marshal.GetLastWin32Error() + ")";
                    Native.CloseHandle(job);
                    state = null;
                    return false;
                }
                state = new ResourceJobState { Handle = job, StartTicks = startTicks };
                ResourceJobs[p.Id] = state;
                return true;
            }
            catch (Exception ex)
            {
                Native.CloseHandle(job);
                state = null;
                error = ex.Message;
                return false;
            }
        }

        private static bool SetJobInfo<T>(IntPtr job, int infoClass, T value) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, mem, false);
                return Native.SetInformationJobObject(job, infoClass, mem, (uint)size);
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        private bool ConfigureCpuJob(ResourceJobState state, int percent)
        {
            var info = new Native.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION();
            if (percent > 0)
            {
                info.ControlFlags = Native.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE |
                                    Native.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP;
                info.CpuRate = (uint)Math.Max(1, Math.Min(10000, percent * 100));
            }
            bool ok = SetJobInfo(state.Handle, Native.JobObjectCpuRateControlInformation, info);
            if (ok) state.CpuPercent = percent;
            return ok;
        }

        private bool ConfigureRamJob(ResourceJobState state, long mb)
        {
            var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            if (mb > 0)
            {
                info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
                info.ProcessMemoryLimit = new UIntPtr((ulong)mb * 1024UL * 1024UL);
            }
            bool ok = SetJobInfo(state.Handle, Native.JobObjectExtendedLimitInformation, info);
            if (ok) state.RamMb = mb;
            return ok;
        }

        private void ApplyCpuLimit(Process p, int percent)
        {
            ResourceJobState job;
            string error;
            bool hard = TryGetResourceJob(p, out job, out error) && ConfigureCpuJob(job, percent);
            CpuLimits[p.Id] = new CpuLimitState
            {
                Limit = percent,
                Orig = SafePriority(p),
                Hard = hard,
                StartTicks = GetStartTicks(p)
            };
            if (!hard && error.Length > 0)
                AddError("CPU hard cap unavailable for " + p.ProcessName + "; using priority fallback: " + error);
        }

        private void ApplyRamLimit(Process p, long mb)
        {
            ResourceJobState job;
            string error;
            bool hard = TryGetResourceJob(p, out job, out error) && ConfigureRamJob(job, mb);
            RamLimits[p.Id] = new RamLimitState { Mb = mb, Hard = hard, StartTicks = GetStartTicks(p) };
            if (!hard && error.Length > 0)
                AddError("RAM hard cap unavailable for " + p.ProcessName + "; using trim fallback: " + error);
        }

        private static string SafePriority(Process p)
        {
            try { return p.PriorityClass.ToString(); }
            catch { return "Normal"; }
        }

        private void CloseResourceJob(int pid)
        {
            ResourceJobState state;
            if (!ResourceJobs.TryRemove(pid, out state) || state == null) return;
            if (!ClearResourceJobLimits(state))
                AddError("Could not clear all Job Object limits for PID " + pid + " before release");
            try { if (state.Handle != IntPtr.Zero) Native.CloseHandle(state.Handle); } catch { }
        }

        private bool ClearResourceJobLimits(ResourceJobState state)
        {
            if (state == null || state.Handle == IntPtr.Zero) return true;
            bool cpuOk = state.CpuPercent <= 0 || ConfigureCpuJob(state, 0);
            bool ramOk = state.RamMb <= 0 || ConfigureRamJob(state, 0);
            return cpuOk && ramOk && state.CpuPercent == 0 && state.RamMb == 0;
        }

        private void ReleaseResourceJobIfUnused(int pid)
        {
            ResourceJobState state;
            if (!ResourceJobs.TryGetValue(pid, out state)) return;
            if (state.CpuPercent <= 0 && state.RamMb <= 0) CloseResourceJob(pid);
        }

        // -----------------------------------------------------------------
        //  Limits
        // -----------------------------------------------------------------
        private void StepCpuLimits()
        {
            foreach (var kv in CpuLimits)
            {
                int id = kv.Key; var st = kv.Value;
                ProcRow row = null;
                foreach (var r in Snap.Rows) if (r.Id == id) { row = r; break; }
                if (row == null || (st.StartTicks != 0 && row.StartTicks != st.StartTicks))
                {
                    CpuLimits.TryRemove(id, out st);
                    continue;
                }
                Process p;
                if (!TryGetExpectedProc(id, st.StartTicks, row.Name, out p))
                {
                    CpuLimits.TryRemove(id, out st);
                    continue;
                }
                try
                {
                    if (st.Hard) continue;
                    if (row.Cpu > st.Limit && !st.Limited)
                    {
                        p.PriorityClass = ProcessPriorityClass.BelowNormal;
                        st.Limited = true;
                    }
                    else if (row.Cpu < st.Limit * 0.7 && st.Limited)
                    {
                        ProcessPriorityClass ppc;
                        if (Enum.TryParse(st.Orig, true, out ppc)) p.PriorityClass = ppc;
                        st.Limited = false;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        private void StepGpuThrottle()
        {
            if (GpuLimits.Count == 0) return;
            long now = Environment.TickCount;
            foreach (var kv in GpuLimits)
            {
                int id = kv.Key; var st = kv.Value;
                if (id == Process.GetCurrentProcess().Id) { GpuLimits.TryRemove(id, out st); CloseProcHandle(id); continue; }
                Process p;
                if (!TryGetExpectedProc(id, st.StartTicks, null, out p))
                {
                    GpuLimits.TryRemove(id, out st);
                    CloseProcHandle(id);
                    StopGpuGuard(id);
                    continue;
                }
                long onMs = (long)(st.Pct * 10);          // percent of 1000ms
                long phase = (now - st.T0) % 1000;
                if (phase < 0) phase += 1000;
                if (phase >= onMs)
                {
                    if (!st.Suspended)
                    {
                        if (SuspendProc(id, st.StartTicks)) st.Suspended = true;
                        else { GpuLimits.TryRemove(id, out st); CloseProcHandle(id); StopGpuGuard(id); AddError("Cannot throttle PID " + id + " (protected process or access denied)"); }
                    }
                }
                else if (st.Suspended && ResumeProc(id, st.StartTicks)) st.Suspended = false;
                p.Dispose();
            }
        }

        private void StepRamLimits()
        {
            foreach (var kv in RamLimits)
            {
                int id = kv.Key; var st = kv.Value;
                ProcRow row = null;
                foreach (var r in Snap.Rows) if (r.Id == id) { row = r; break; }
                if (row == null || (st.StartTicks != 0 && row.StartTicks != st.StartTicks))
                {
                    RamLimits.TryRemove(id, out st);
                    continue;
                }
                if (st.Hard) continue;
                if (row.Mem > st.Mb * 1024 * 1024)
                {
                    Process p;
                    if (TryGetExpectedProc(id, st.StartTicks, row.Name, out p))
                    {
                        try { Native.EmptyWorkingSet(p.Handle); } catch { }
                        finally { p.Dispose(); }
                    }
                }
            }
        }

        public AdaptiveTop20Receipt ApplyAdaptiveTop20(bool apply, bool persist)
        {
            Snapshot snapshot = Snap ?? new Snapshot();
            var cpu = AdaptiveTop20Optimizer.TopApplications(snapshot.Rows, "cpu", 20);
            var ram = AdaptiveTop20Optimizer.TopApplications(snapshot.Rows, "ram", 20);
            var gpu = AdaptiveTop20Optimizer.TopApplications(snapshot.Rows, "gpu", 20);
            Func<ProcRow, string> applicationKey = row =>
                row == null ? "" : (String.IsNullOrEmpty(row.GroupKey)
                    ? Sampler.ApplicationGroupKey(row.Name, row.ExecutablePath)
                    : row.GroupKey);
            var selectedByKey =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var currentApplications = cpu.Concat(ram).Concat(gpu)
                .GroupBy(applicationKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
            foreach (var application in currentApplications)
                selectedByKey[applicationKey(application)] =
                    AdaptiveTop20Optimizer.SelectedBy(application, cpu, ram, gpu);

            List<ProcRow> selectedApplications = currentApplications;
            if (apply && AdaptiveTop20Enabled)
            {
                int now = Environment.TickCount;
                foreach (var application in currentApplications)
                {
                    string key = applicationKey(application);
                    _adaptiveApplicationLastSeen[key] = now;
                    string oldMetrics;
                    if (!_adaptiveApplicationMetrics.TryGetValue(key, out oldMetrics))
                        oldMetrics = "";
                    var metrics = new HashSet<string>(
                        oldMetrics.Split(new[] { '+' },
                            StringSplitOptions.RemoveEmptyEntries),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (string metric in selectedByKey[key].Split('+'))
                        if (!String.IsNullOrWhiteSpace(metric)) metrics.Add(metric);
                    _adaptiveApplicationMetrics[key] = String.Join("+",
                        new[] { "CPU", "RAM", "GPU" }.Where(metrics.Contains));
                }
                foreach (string key in _adaptiveApplicationLastSeen.Keys.ToList())
                {
                    if (AdaptiveTop20Optimizer.IsRollingSelectionActive(
                            _adaptiveApplicationLastSeen[key], now, 300000)) continue;
                    _adaptiveApplicationLastSeen.Remove(key);
                    _adaptiveApplicationMetrics.Remove(key);
                }
                selectedApplications = MainForm.BuildApplicationRows(snapshot.Rows)
                    .Where(r => _adaptiveApplicationLastSeen.ContainsKey(
                        applicationKey(r))).ToList();
                foreach (var application in selectedApplications)
                {
                    string key = applicationKey(application);
                    selectedByKey[key] = _adaptiveApplicationMetrics[key] + "+RECENT";
                }
            }

            var targets = selectedApplications.SelectMany(MainForm.MemberRows)
                .GroupBy(r => r.Id + ":" + r.StartTicks,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
            var receipt = new AdaptiveTop20Receipt
            {
                schema = "psproclasso.top20-performance.v1",
                mode = apply ? "apply" : "plan",
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                machine = Environment.MachineName,
                topCpuApplications = cpu.Select(r => r.Name).ToArray(),
                topRamApplications = ram.Select(r => r.Name).ToArray(),
                topGpuApplications = gpu.Select(r => r.Name).ToArray(),
                targetedProcesses = targets.Count,
                decisions = new List<AdaptiveTop20Decision>(),
                errors = new List<string>()
            };

            foreach (var row in targets)
            {
                string selectedBy;
                if (!selectedByKey.TryGetValue(applicationKey(row), out selectedBy))
                    selectedBy = AdaptiveTop20Optimizer.SelectedBy(row, cpu, ram, gpu);
                var decision = new AdaptiveTop20Decision
                {
                    pid = row.Id,
                    startTicks = row.StartTicks,
                    processName = row.Name,
                    applicationName = Sampler.ApplicationDisplayName(row.Name),
                    selectedBy = selectedBy,
                    cpuPercent = row.Cpu,
                    ramBytes = row.Mem,
                    gpuPercent = row.Gpu,
                    priorityBefore = row.Priority ?? "",
                    priorityAfter = row.Priority ?? "",
                    protectedScheduling = AdaptiveTop20Optimizer.IsProtectedScheduling(row),
                    persistent = persist,
                    action = apply ? "accelerate" : "plan_accelerate",
                    error = ""
                };
                if (decision.protectedScheduling) receipt.protectedProcesses++;
                receipt.decisions.Add(decision);
                if (!apply) continue;

                Process process;
                if (!TryGetExpectedProc(row.Id, row.StartTicks, row.Name, out process))
                {
                    decision.action = "exited_or_changed";
                    decision.error = "process identity changed before acceleration";
                    continue;
                }

                try
                {
                    CpuLimitState cpuLimit;
                    RamLimitState ramLimit;
                    GpuLimitState gpuLimit;
                    bool removed = CpuLimits.TryRemove(row.Id, out cpuLimit);
                    removed = RamLimits.TryRemove(row.Id, out ramLimit) || removed;
                    if (GpuLimits.TryRemove(row.Id, out gpuLimit))
                    {
                        if (gpuLimit != null && gpuLimit.Suspended)
                            ResumeProc(row.Id, row.StartTicks);
                        StopGpuGuard(row.Id);
                        removed = true;
                    }
                    ResourceJobState job;
                    if (ResourceJobs.TryGetValue(row.Id, out job))
                    {
                        CloseResourceJob(row.Id);
                        removed = true;
                    }
                    decision.limitsRemoved = removed;
                    if (removed) receipt.limitRemovals++;

                    ProcessPriorityClass? target =
                        AdaptiveTop20Optimizer.TargetPriority(row);
                    if (target.HasValue)
                    {
                        ProcessPriorityClass before = process.PriorityClass;
                        if (before != target.Value)
                        {
                            process.PriorityClass = target.Value;
                            decision.priorityChanged = true;
                            receipt.priorityChanges++;
                        }
                        decision.priorityAfter = process.PriorityClass.ToString();
                    }

                    if (!decision.protectedScheduling)
                    {
                        long fullMask = Environment.ProcessorCount >= 63
                            ? -1L : (1L << Environment.ProcessorCount) - 1L;
                        long currentMask = process.ProcessorAffinity.ToInt64();
                        if (currentMask != fullMask)
                        {
                            process.ProcessorAffinity = (IntPtr)fullMask;
                            decision.affinityExpanded = true;
                            receipt.affinityExpansions++;
                        }
                    }

                    decision.powerThrottlingDisabled =
                        Native.DisableExecutionSpeedThrottling(row.Id);
                    if (decision.powerThrottlingDisabled)
                        receipt.powerThrottleDisables++;
                    AdaptiveTop20Pids[row.Id] = row.StartTicks;
                    decision.action = decision.protectedScheduling
                        ? "constraints_removed_os_scheduling_preserved"
                        : "accelerated";
                }
                catch (Exception ex)
                {
                    decision.error = ex.Message;
                    decision.action = "partial";
                    receipt.failures++;
                    receipt.errors.Add(row.Name + " PID " + row.Id + ": " + ex.Message);
                }
                finally { process.Dispose(); }
            }

            int persistenceNow = Environment.TickCount;
            bool persistenceDue = AdaptiveTop20Optimizer.IsPersistenceDue(
                _lastAdaptivePersistenceMs, persistenceNow, 30000);
            if (apply && persist && persistenceDue)
            {
                var persistentTargets = targets
                    .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()).OrderBy(r => r.Name).ToList();
                var selectedNames = new HashSet<string>(
                    persistentTargets.Select(r => r.Name),
                    StringComparer.OrdinalIgnoreCase);
                var restoredLiveRules =
                    new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
                if (Rules != null)
                {
                    foreach (var kv in Rules)
                    {
                        Rule existing = kv.Value;
                        if (existing == null || !existing.performanceManaged ||
                            selectedNames.Contains(kv.Key)) continue;
                        Rule restored = Rule.New();
                        restored.priority = existing.performanceOriginalPriority ?? "";
                        restored.affinity = existing.performanceOriginalAffinity == null
                            ? new int[0] : existing.performanceOriginalAffinity.ToArray();
                        restored.cpuLimit = existing.performanceOriginalCpuLimit;
                        restored.gpuLimit = existing.performanceOriginalGpuLimit;
                        restored.ramLimit = existing.performanceOriginalRamLimit;
                        restored.enabled = true;
                        restoredLiveRules[kv.Key] = restored;
                    }
                }
                string fingerprint = String.Join("|", persistentTargets.Select(r =>
                    r.Name + ":" + (selectedByKey.ContainsKey(applicationKey(r))
                        ? selectedByKey[applicationKey(r)]
                        : AdaptiveTop20Optimizer.SelectedBy(r, cpu, ram, gpu))));
                if (!String.Equals(_adaptiveRuleFingerprint, fingerprint,
                                   StringComparison.Ordinal) ||
                    restoredLiveRules.Count > 0)
                {
                    ConcurrentDictionary<string, Rule> latest;
                    string error;
                    int changed = 0;
                    bool saved = RulesStore.MutateRules(dict =>
                    {
                        foreach (var kv in dict.ToList())
                        {
                            Rule rule = kv.Value;
                            if (rule == null || !rule.performanceManaged ||
                                selectedNames.Contains(kv.Key)) continue;
                            if (AdaptiveTop20Optimizer.RestorePerformanceRule(rule))
                            {
                                if (AdaptiveTop20Optimizer.IsEmptyRule(rule))
                                {
                                    Rule ignored;
                                    dict.TryRemove(kv.Key, out ignored);
                                }
                                else dict[kv.Key] = rule;
                                changed++;
                            }
                        }
                        foreach (var kv in dict.ToList())
                        {
                            if (selectedNames.Contains(kv.Key) ||
                                !AdaptiveTop20Optimizer.IsEmptyRule(kv.Value)) continue;
                            Rule ignored;
                            if (dict.TryRemove(kv.Key, out ignored)) changed++;
                        }
                        foreach (var row in persistentTargets)
                        {
                            Rule rule;
                            bool hadExistingRule = dict.TryGetValue(row.Name, out rule) &&
                                                   rule != null;
                            if (!hadExistingRule)
                                rule = Rule.New();
                            string selectedBy = selectedByKey.ContainsKey(applicationKey(row))
                                ? selectedByKey[applicationKey(row)]
                                : AdaptiveTop20Optimizer.SelectedBy(row, cpu, ram, gpu);
                            if (AdaptiveTop20Optimizer.MergePerformanceRule(
                                    rule, row, selectedBy, hadExistingRule))
                            {
                                dict[row.Name] = rule;
                                changed++;
                            }
                        }
                    }, out latest, out error);
                    if (saved)
                    {
                        Rules = latest;
                        receipt.persistentRules = changed;
                        _adaptiveRuleFingerprint = fingerprint;
                        foreach (var row in snapshot.Rows)
                        {
                            Rule restored;
                            if (!restoredLiveRules.TryGetValue(row.Name, out restored))
                                continue;
                            RestoreLivePerformanceRule(row, restored);
                        }
                    }
                    else
                    {
                        receipt.failures++;
                        receipt.errors.Add("performance rules: " + error);
                    }
                }
                _lastAdaptivePersistenceMs = persistenceNow;
            }
            return receipt;
        }

        private void RestoreLivePerformanceRule(ProcRow row, Rule restored)
        {
            if (row == null || restored == null) return;
            long ignoredIdentity;
            AdaptiveTop20Pids.TryRemove(row.Id, out ignoredIdentity);
            Process process;
            if (!TryGetExpectedProc(row.Id, row.StartTicks, row.Name, out process))
                return;
            try
            {
                if (!AdaptiveTop20Optimizer.IsProtectedScheduling(row))
                {
                    ProcessPriorityClass target;
                    string original = restored.priority ?? "";
                    if (!Enum.TryParse(original, true, out target))
                        target = ProcessPriorityClass.Normal;
                    process.PriorityClass = target;

                    long mask = 0;
                    if (restored.affinity != null)
                        foreach (int bit in restored.affinity)
                            if (bit >= 0 && bit < 63) mask |= 1L << bit;
                    if (mask == 0)
                        mask = Environment.ProcessorCount >= 63
                            ? -1L : (1L << Environment.ProcessorCount) - 1L;
                    process.ProcessorAffinity = (IntPtr)mask;
                }
                if (restored.cpuLimit > 0) ApplyCpuLimit(process, restored.cpuLimit);
                if (restored.ramLimit > 0) ApplyRamLimit(process, restored.ramLimit);
                if (restored.gpuLimit > 0 && row.Id != Process.GetCurrentProcess().Id)
                {
                    GpuLimits[row.Id] = new GpuLimitState
                    {
                        Pct = restored.gpuLimit,
                        T0 = Environment.TickCount,
                        StartTicks = row.StartTicks
                    };
                    EnsureGpuGuard(process);
                }
            }
            catch (Exception ex)
            {
                AddError("Top20 restore for " + row.Name + ": " + ex.Message);
            }
            finally { process.Dispose(); }
        }

        public int RestoreLegacyPerformancePolicies(out List<string> errors)
        {
            errors = new List<string>();
            AdaptiveTop20Enabled = false;
            var restoredByName =
                new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
            ConcurrentDictionary<string, Rule> latest;
            string error;
            int restoredCount = 0;
            bool saved = RulesStore.MutateRules(dict =>
            {
                foreach (var kv in dict.ToList())
                {
                    Rule rule = kv.Value;
                    if (rule == null || !rule.performanceManaged) continue;
                    Rule restored = Rule.New();
                    restored.priority = rule.performanceOriginalPriority ?? "";
                    restored.affinity = rule.performanceOriginalAffinity == null
                        ? new int[0] : rule.performanceOriginalAffinity.ToArray();
                    restored.cpuLimit = rule.performanceOriginalCpuLimit;
                    restored.gpuLimit = rule.performanceOriginalGpuLimit;
                    restored.ramLimit = rule.performanceOriginalRamLimit;
                    restored.enabled = true;
                    restoredByName[kv.Key] = restored;
                    if (!AdaptiveTop20Optimizer.RestorePerformanceRule(rule)) continue;
                    restoredCount++;
                    if (AdaptiveTop20Optimizer.IsEmptyRule(rule))
                    {
                        Rule ignored;
                        dict.TryRemove(kv.Key, out ignored);
                    }
                    else dict[kv.Key] = rule;
                }
            }, out latest, out error);
            if (!saved)
            {
                errors.Add("Could not restore older adaptive policies: " + error);
                return 0;
            }

            Rules = latest;
            foreach (var row in (Snap == null ? new List<ProcRow>() : Snap.Rows))
            {
                Rule restored;
                if (!restoredByName.TryGetValue(row.Name, out restored)) continue;
                try { RestoreLivePerformanceRule(row, restored); }
                catch (Exception ex)
                {
                    errors.Add(row.Name + " PID " + row.Id + ": " + ex.Message);
                }
            }
            AdaptiveTop20Pids.Clear();
            _adaptiveApplicationLastSeen.Clear();
            _adaptiveApplicationMetrics.Clear();
            _adaptiveRuleFingerprint = "";
            return restoredCount;
        }

        private void StepAdaptiveTop20()
        {
            if (!AdaptiveTop20Enabled) return;
            int now = Environment.TickCount;
            int elapsed = unchecked(now - _lastAdaptiveTop20Ms);
            if (_lastAdaptiveTop20Ms != 0 && elapsed >= 0 &&
                elapsed < _adaptiveIntervalMs) return;
            _lastAdaptiveTop20Ms = now;
            AdaptiveTop20Receipt receipt = ApplyAdaptiveTop20(true, true);
            if (receipt.failures > 0 && receipt.errors.Count > 0)
                AddError("Top20: " + receipt.errors[0]);
        }

        private void StepProBalance()
        {
            DateTime now = DateTime.Now;
            // Restoration is independent of current load and of the toggle state.
            foreach (var kv in PbState.ToList())
            {
                if (now <= kv.Value.Until && ProBalance) continue;
                PbState st2;
                PbState.TryRemove(kv.Key, out st2);
                Process p2;
                if (st2 != null && TryGetProc(kv.Key, out p2))
                {
                    try
                    {
                        ProcessPriorityClass ppc2;
                        if (Enum.TryParse(st2.Orig, true, out ppc2)) p2.PriorityClass = ppc2;
                    }
                    catch { }
                    finally { p2.Dispose(); }
                }
            }

            if (!ProBalance || Snap.TotalCpu < 80) return;
            foreach (var r in Snap.Rows)
            {
                if (r.Cpu < 10) continue;
                if (GpuLimits.ContainsKey(r.Id) || CpuLimits.ContainsKey(r.Id)) continue;
                if (PbState.ContainsKey(r.Id)) continue;
                Process p;
                if (!TryGetProc(r.Id, out p)) continue;
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero) continue;
                    string original = p.PriorityClass.ToString();
                    p.PriorityClass = ProcessPriorityClass.BelowNormal;
                    PbState[r.Id] = new PbState { Orig = original, Until = now.AddSeconds(30) };
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        private readonly Dictionary<string, DateTime> _lastRestart = new Dictionary<string, DateTime>();

        private void StepWatchdog()
        {
            var nowNames = new HashSet<string>();
            foreach (var r in Snap.Rows) nowNames.Add(r.Name);
            DateTime now = DateTime.Now;
            foreach (var kv in Rules)
            {
                var rule = kv.Value;
                if (!rule.watchdog || !rule.enabled) continue;
                if (nowNames.Contains(kv.Key)) continue;
                if (String.IsNullOrEmpty(rule.wdPath) || !File.Exists(rule.wdPath)) continue;
                DateTime last;
                if (_lastRestart.TryGetValue(kv.Key, out last) && (now - last).TotalSeconds < 30) continue;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = rule.wdPath;
                    if (!String.IsNullOrEmpty(rule.wdArgs)) psi.Arguments = rule.wdArgs;
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                    _lastRestart[kv.Key] = now;
                    PersistRuleMutation(kv.Key, r => r.restarts++);
                }
                catch (Exception ex) { AddError("watchdog restart failed for " + kv.Key + ": " + ex.Message); }
            }
        }

        // -----------------------------------------------------------------
        //  Process helpers
        // -----------------------------------------------------------------
        private static bool TryGetProc(int id, out Process p)
        {
            p = null;
            try { p = Process.GetProcessById(id); return true; } catch { return false; }
        }

        private static long GetStartTicks(Process p)
        {
            try { return p.StartTime.ToUniversalTime().Ticks; } catch { return 0; }
        }

        private bool TryGetExpectedProc(int id, long expectedStartTicks, string expectedName, out Process p)
        {
            p = null;
            if (!TryGetProc(id, out p)) return false;
            try
            {
                if (expectedStartTicks != 0 && GetStartTicks(p) != expectedStartTicks)
                    throw new InvalidOperationException("process identity changed");
                if (!String.IsNullOrEmpty(expectedName) &&
                    !String.Equals(p.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("process name changed");
                return true;
            }
            catch (Exception ex)
            {
                try { p.Dispose(); } catch { }
                p = null;
                AddError("PID " + id + " action refused: " + ex.Message);
                return false;
            }
        }

        private IntPtr GetProcHandle(int id, long expectedStartTicks)
        {
            ProcessHandleState state;
            if (Handles.TryGetValue(id, out state) && state != null && state.Handle != IntPtr.Zero)
            {
                if (expectedStartTicks == 0 || state.StartTicks == expectedStartTicks) return state.Handle;
                CloseProcHandle(id);
            }
            IntPtr h = Native.OpenSuspendHandle(id);
            if (h != IntPtr.Zero)
                Handles[id] = new ProcessHandleState { Handle = h, StartTicks = expectedStartTicks };
            return h;
        }

        private void CloseProcHandle(int id)
        {
            ProcessHandleState state;
            if (Handles.TryRemove(id, out state) && state != null && state.Handle != IntPtr.Zero)
            {
                try { Native.CloseHandle(state.Handle); } catch { }
            }
        }

        private static bool NtStatusSucceeded(int status)
        {
            return status >= 0;
        }

        private bool SuspendProc(int id, long expectedStartTicks)
        {
            IntPtr h = GetProcHandle(id, expectedStartTicks);
            if (h == IntPtr.Zero) return false;
            try { return NtStatusSucceeded(Native.NtSuspendProcess(h)); } catch { return false; }
        }

        private bool ResumeProc(int id, long expectedStartTicks)
        {
            IntPtr h = GetProcHandle(id, expectedStartTicks);
            if (h == IntPtr.Zero) return false;
            try { return NtStatusSucceeded(Native.NtResumeProcess(h)); } catch { return false; }
        }

        private void EnsureGpuGuard(Process target)
        {
            Process existing;
            if (GpuGuards.TryGetValue(target.Id, out existing))
            {
                try { if (!existing.HasExited) return; } catch { }
                StopGpuGuard(target.Id);
            }
            try
            {
                long targetStart = target.StartTime.ToUniversalTime().Ticks;
                using (var me = Process.GetCurrentProcess())
                {
                    long parentStart = me.StartTime.ToUniversalTime().Ticks;
                    var guard = Process.Start(new ProcessStartInfo
                    {
                        FileName = Environment.GetCommandLineArgs()[0],
                        Arguments = "--gpu-guard " + target.Id + " " + me.Id + " " +
                                    targetStart + " " + parentStart,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    if (guard != null) GpuGuards[target.Id] = guard;
                }
            }
            catch (Exception ex) { AddError("GPU recovery guard: " + ex.Message); }
        }

        private void StopGpuGuard(int pid)
        {
            Process guard;
            if (!GpuGuards.TryRemove(pid, out guard) || guard == null) return;
            try { if (!guard.HasExited) guard.Kill(); } catch { }
            try { guard.Dispose(); } catch { }
        }

        // -----------------------------------------------------------------
        //  Public actions (called from the UI thread)
        // -----------------------------------------------------------------
        private bool PersistRuleMutation(string processName, Action<Rule> mutation)
        {
            ConcurrentDictionary<string, Rule> latest;
            string error;
            if (!RulesStore.UpdateRule(processName, mutation, out latest, out error))
            {
                AddError("rules save for " + processName + ": " + error);
                return false;
            }
            Rules = latest;
            return true;
        }

        public void SetPriority(int pid, string name) { SetPriority(pid, 0, null, name); }
        public void SetPriority(ProcRow row, string name)
        {
            if (row == null) return;
            foreach (var member in MainForm.MemberRows(row))
                SetPriority(member.Id, member.StartTicks, member.Name, name);
        }

        private void SetPriority(int pid, long expectedStartTicks, string expectedName, string name)
        {
            Process p = null;
            try
            {
                if (TryGetExpectedProc(pid, expectedStartTicks, expectedName, out p))
                {
                    p.PriorityClass = (ProcessPriorityClass)Enum.Parse(typeof(ProcessPriorityClass), name, true);
                    PersistRuleMutation(p.ProcessName, r => r.priority = name);
                    InvalidateRuleApplications(p.ProcessName);
                }
            }
            catch (Exception ex) { AddError("priority: " + ex.Message); }
            finally { if (p != null) p.Dispose(); }
        }

        public void SetAffinity(int pid, string spec) { SetAffinity(pid, 0, null, spec); }
        public void SetAffinity(ProcRow row, string spec)
        {
            if (row == null) return;
            foreach (var member in MainForm.MemberRows(row))
                SetAffinity(member.Id, member.StartTicks, member.Name, spec);
        }

        private void SetAffinity(int pid, long expectedStartTicks, string expectedName, string spec)
        {
            Process p = null;
            try
            {
                long mask = ParseCores(spec);
                if (mask <= 0) return;
                if (TryGetExpectedProc(pid, expectedStartTicks, expectedName, out p))
                {
                    p.ProcessorAffinity = (IntPtr)mask;
                    int[] affinity = MaskBits(mask);
                    PersistRuleMutation(p.ProcessName, r => r.affinity = affinity);
                    InvalidateRuleApplications(p.ProcessName);
                }
            }
            catch (Exception ex) { AddError("affinity: " + ex.Message); }
            finally { if (p != null) p.Dispose(); }
        }

        private static long ParseCores(string spec)
        {
            long mask = 0;
            foreach (string part in spec.Split(','))
            {
                string t = part.Trim();
                if (t.Length == 0) continue;
                if (t.Contains("-"))
                {
                    var rng = t.Split('-');
                    int a = int.Parse(rng[0].Trim()), b = int.Parse(rng[1].Trim());
                    if (a < 0 || b < a || b >= Math.Min(64, Environment.ProcessorCount))
                        throw new ArgumentOutOfRangeException("spec", "Core range is outside this processor.");
                    for (int i = a; i <= b; i++) mask |= 1L << i;
                }
                else
                {
                    int core = int.Parse(t);
                    if (core < 0 || core >= Math.Min(64, Environment.ProcessorCount))
                        throw new ArgumentOutOfRangeException("spec", "Core is outside this processor.");
                    mask |= 1L << core;
                }
            }
            return mask;
        }

        private static int[] MaskBits(long mask)
        {
            var bits = new List<int>();
            for (int i = 0; i < 64; i++) if (((mask >> i) & 1) == 1) bits.Add(i);
            return bits.ToArray();
        }

        private void InvalidateRuleApplications(string processName)
        {
            if (String.IsNullOrEmpty(processName)) return;
            foreach (var row in Snap.Rows)
                if (String.Equals(row.Name, processName, StringComparison.OrdinalIgnoreCase))
                    _ruleAppliedPids.Remove(row.Id);
        }

        public void SetCpuLimit(int pid, double pct) { SetCpuLimit(pid, 0, null, pct); }
        public void SetCpuLimit(ProcRow row, double pct)
        {
            if (row == null) return;
            foreach (var member in MainForm.MemberRows(row))
                SetCpuLimit(member.Id, member.StartTicks, member.Name, pct);
        }

        private void SetCpuLimit(int pid, long expectedStartTicks, string expectedName, double pct)
        {
            Process p = null;
            try
            {
                if (!TryGetExpectedProc(pid, expectedStartTicks, expectedName, out p)) return;
                int limit = (int)Math.Round(Math.Max(0, Math.Min(100, pct)));
                if (limit <= 0)
                {
                    CpuLimitState st; CpuLimits.TryRemove(pid, out st);
                    ResourceJobState job;
                    if (ResourceJobs.TryGetValue(pid, out job))
                    {
                        ConfigureCpuJob(job, 0);
                        ReleaseResourceJobIfUnused(pid);
                    }
                }
                else ApplyCpuLimit(p, limit);
                PersistRuleMutation(p.ProcessName, r => r.cpuLimit = limit);
                InvalidateRuleApplications(p.ProcessName);
            }
            catch (Exception ex) { AddError("CPU limit: " + ex.Message); }
            finally { if (p != null) p.Dispose(); }
        }

        public void SetGpuLimit(int pid, double pct) { SetGpuLimit(pid, 0, null, pct); }
        public void SetGpuLimit(ProcRow row, double pct)
        {
            if (row == null) return;
            foreach (var member in MainForm.MemberRows(row))
                SetGpuLimit(member.Id, member.StartTicks, member.Name, pct);
        }

        private void SetGpuLimit(int pid, long expectedStartTicks, string expectedName, double pct)
        {
            Process p = null;
            try
            {
                if (!TryGetExpectedProc(pid, expectedStartTicks, expectedName, out p)) return;
                int limit = (int)Math.Round(Math.Max(0, Math.Min(99, pct)));
                if (limit <= 0)
                {
                    GpuLimitState st;
                    if (GpuLimits.TryRemove(pid, out st) && st.Suspended) ResumeProc(pid, st.StartTicks);
                    CloseProcHandle(pid);
                    StopGpuGuard(pid);
                }
                else if (pid != Process.GetCurrentProcess().Id)
                {
                    GpuLimits[pid] = new GpuLimitState
                    {
                        Pct = limit,
                        T0 = Environment.TickCount,
                        StartTicks = GetStartTicks(p)
                    };
                    EnsureGpuGuard(p);
                }
                PersistRuleMutation(p.ProcessName, r => r.gpuLimit = limit);
                InvalidateRuleApplications(p.ProcessName);
            }
            catch (Exception ex) { AddError("GPU duty limit: " + ex.Message); }
            finally { if (p != null) p.Dispose(); }
        }

        public void SetRamLimit(int pid, long mb) { SetRamLimit(pid, 0, null, mb); }
        public void SetRamLimit(ProcRow row, long mb)
        {
            if (row == null) return;
            foreach (var member in MainForm.MemberRows(row))
                SetRamLimit(member.Id, member.StartTicks, member.Name, mb);
        }

        private void SetRamLimit(int pid, long expectedStartTicks, string expectedName, long mb)
        {
            Process p = null;
            try
            {
                if (!TryGetExpectedProc(pid, expectedStartTicks, expectedName, out p)) return;
                long limit = Math.Max(0, Math.Min(Int32.MaxValue, mb));
                if (limit <= 0)
                {
                    RamLimitState st; RamLimits.TryRemove(pid, out st);
                    ResourceJobState job;
                    if (ResourceJobs.TryGetValue(pid, out job))
                    {
                        ConfigureRamJob(job, 0);
                        ReleaseResourceJobIfUnused(pid);
                    }
                }
                else ApplyRamLimit(p, limit);
                PersistRuleMutation(p.ProcessName, r => r.ramLimit = (int)limit);
                InvalidateRuleApplications(p.ProcessName);
            }
            catch (Exception ex) { AddError("RAM limit: " + ex.Message); }
            finally { if (p != null) p.Dispose(); }
        }

        public void RemoveLimits(int pid) { RemoveLimits(pid, 0, null); }
        public void RemoveLimits(ProcRow row)
        {
            if (row == null) return;
            foreach (var member in MainForm.MemberRows(row))
                RemoveLimits(member.Id, member.StartTicks, member.Name);
        }

        private void RemoveLimits(int pid, long expectedStartTicks, string expectedName)
        {
            string processName = null;
            Process current;
            bool found = TryGetExpectedProc(pid, expectedStartTicks, expectedName, out current);
            if (!found && (expectedStartTicks != 0 || !String.IsNullOrEmpty(expectedName))) return;
            if (found)
            {
                try { processName = current.ProcessName; }
                catch { }
                finally { current.Dispose(); }
            }
            CpuLimitState cs; CpuLimits.TryRemove(pid, out cs);
            RamLimitState rs; RamLimits.TryRemove(pid, out rs);
            GpuLimitState gs;
            if (GpuLimits.TryRemove(pid, out gs) && gs.Suspended) ResumeProc(pid, gs.StartTicks);
            CloseProcHandle(pid);
            StopGpuGuard(pid);
            CloseResourceJob(pid);
            if (!String.IsNullOrEmpty(processName))
            {
                PersistRuleMutation(processName, r =>
                {
                    r.cpuLimit = 0;
                    r.gpuLimit = 0;
                    r.ramLimit = 0;
                });
                InvalidateRuleApplications(processName);
            }
            PbState st;
            if (PbState.TryRemove(pid, out st))
            {
                Process p;
                if (TryGetProc(pid, out p))
                {
                    try
                    {
                        ProcessPriorityClass ppc;
                        if (Enum.TryParse(st.Orig, true, out ppc)) p.PriorityClass = ppc;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
        }

        public void Kill(int pid) { Kill(pid, 0, null); }
        public void Kill(ProcRow row)
        {
            if (row != null) Kill(row.Id, row.StartTicks, row.Name);
        }

        private void Kill(int pid, long expectedStartTicks, string expectedName)
        {
            try
            {
                Process p;
                if (TryGetExpectedProc(pid, expectedStartTicks, expectedName, out p))
                {
                    try { p.Kill(); }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception ex) { AddError("kill: " + ex.Message); }
        }

        public void ToggleWatchdog(ProcRow row, string path)
        {
            if (row == null) return;
            var members = MainForm.MemberRows(row).ToList();
            bool anyEnabled = members.Any(member =>
            {
                Rule current;
                return Rules.TryGetValue(member.Name, out current) && current.watchdog;
            });
            bool enable = !anyEnabled;

            foreach (var group in members.GroupBy(member => member.Name,
                         StringComparer.OrdinalIgnoreCase))
            {
                var member = group.First();
                string memberPath = member.ExecutablePath;
                if (String.IsNullOrEmpty(memberPath) && member.Id == row.Id)
                    memberPath = path;
                if (enable && String.IsNullOrEmpty(memberPath))
                {
                    try
                    {
                        using (var process = Process.GetProcessById(member.Id))
                            memberPath = process.MainModule.FileName;
                    }
                    catch { }
                }
                if (enable && String.IsNullOrEmpty(memberPath))
                {
                    AddError("watchdog path unavailable for " + member.Name);
                    continue;
                }
                string savedPath = memberPath;
                PersistRuleMutation(member.Name, rule =>
                {
                    rule.watchdog = enable;
                    rule.wdPath = enable ? savedPath : "";
                    rule.wdArgs = "";
                });
            }
        }

        public bool ToggleWatchdog(string processName)
        {
            Rule current;
            if (!Rules.TryGetValue(processName, out current)) return false;
            bool enable = !current.watchdog;
            if (enable && String.IsNullOrEmpty(current.wdPath)) return false;
            return PersistRuleMutation(processName, rule =>
            {
                rule.watchdog = enable;
                if (!enable) { rule.wdPath = ""; rule.wdArgs = ""; }
            });
        }
    }

    // ---------------------------------------------------------------------
    //  Dark theme helpers
    // ---------------------------------------------------------------------
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(17, 19, 24);
        public static readonly Color Panel = Color.FromArgb(24, 28, 36);
        public static readonly Color Panel2 = Color.FromArgb(31, 37, 48);
        public static readonly Color Header = Color.FromArgb(30, 36, 48);
        public static readonly Color Track = Color.FromArgb(38, 45, 60);
        public static readonly Color Line = Color.FromArgb(44, 52, 68);
        public static readonly Color AltRow = Color.FromArgb(22, 26, 33);
        public static readonly Color SelRow = Color.FromArgb(31, 78, 121);
        public static readonly Color SelTop = Color.FromArgb(40, 92, 140);
        public static readonly Color Text = Color.FromArgb(216, 222, 233);
        public static readonly Color Dim = Color.FromArgb(122, 134, 150);
        public static readonly Color Accent = Color.FromArgb(79, 193, 255);
        public static readonly Color Green = Color.FromArgb(123, 201, 111);
        public static readonly Color Yellow = Color.FromArgb(229, 192, 123);
        public static readonly Color Red = Color.FromArgb(224, 108, 117);
        public static readonly Color Purple = Color.FromArgb(198, 120, 221);
        public static readonly Color MemColor = Color.FromArgb(176, 142, 246);   // RAM bars
        public static readonly Color VramColor = Color.FromArgb(94, 200, 220);   // VRAM bars

        public static Color PctColor(double p)
        {
            if (p >= 80) return Red;
            if (p >= 50) return Yellow;
            return Green;
        }
    }

    internal static class AdaptiveTop20Optimizer
    {
        private static readonly HashSet<string> ProtectedSchedulingNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "idle", "system", "registry", "memory compression", "secure system",
                "smss", "csrss", "wininit", "winlogon", "services", "lsass",
                "svchost", "fontdrvhost", "dwm", "audiodg", "wmiprvse",
                "conhost", "openconsole", "docker desktop",
                "processgovernor", "processlasso"
            };

        private static string Key(string name)
        {
            return (name ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        }

        internal static List<ProcRow> TopApplications(IEnumerable<ProcRow> rows,
                                                       string metric, int count)
        {
            var applications = MainForm.BuildApplicationRows(rows);
            IOrderedEnumerable<ProcRow> ordered;
            if (String.Equals(metric, "ram", StringComparison.OrdinalIgnoreCase))
                ordered = applications.OrderByDescending(r => r.Mem)
                    .ThenByDescending(r => r.Cpu).ThenBy(r => r.Name);
            else if (String.Equals(metric, "gpu", StringComparison.OrdinalIgnoreCase))
                ordered = applications.OrderByDescending(r => r.Gpu)
                    .ThenByDescending(r => r.Vram).ThenByDescending(r => r.Cpu)
                    .ThenBy(r => r.Name);
            else
                ordered = applications.OrderByDescending(r => r.Cpu)
                    .ThenByDescending(r => r.Mem).ThenBy(r => r.Name);
            return ordered.Take(Math.Max(1, count)).ToList();
        }

        internal static List<ProcRow> SelectTargets(IEnumerable<ProcRow> rows, int count)
        {
            var selectedApplications = TopApplications(rows, "cpu", count)
                .Concat(TopApplications(rows, "ram", count))
                .Concat(TopApplications(rows, "gpu", count))
                .GroupBy(r => r.GroupKey ?? r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());

            var result = new List<ProcRow>();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var application in selectedApplications)
            {
                foreach (var member in MainForm.MemberRows(application))
                {
                    string identity = member.Id + ":" + member.StartTicks;
                    if (identities.Add(identity)) result.Add(member);
                }
            }
            return result;
        }

        internal static bool IsProtectedScheduling(ProcRow row)
        {
            if (row == null) return true;
            return row.Id <= 4 || row.SessionId == 0 ||
                   ProtectedSchedulingNames.Contains(Key(row.Name));
        }

        internal static ProcessPriorityClass? TargetPriority(ProcRow row)
        {
            if (IsProtectedScheduling(row)) return null;
            ProcessPriorityClass current;
            if (!Enum.TryParse(row == null ? "" : row.Priority, true, out current))
                return null;
            if (current == ProcessPriorityClass.Idle ||
                current == ProcessPriorityClass.BelowNormal ||
                current == ProcessPriorityClass.Normal)
                return ProcessPriorityClass.AboveNormal;
            return null;
        }

        internal static string SelectedBy(ProcRow row, IList<ProcRow> cpu,
                                          IList<ProcRow> ram, IList<ProcRow> gpu)
        {
            string key = row == null ? "" :
                (String.IsNullOrEmpty(row.GroupKey)
                    ? Sampler.ApplicationGroupKey(row.Name, row.ExecutablePath)
                    : row.GroupKey);
            var metrics = new List<string>();
            if (cpu.Any(r => String.Equals(r.GroupKey, key, StringComparison.OrdinalIgnoreCase)))
                metrics.Add("CPU");
            if (ram.Any(r => String.Equals(r.GroupKey, key, StringComparison.OrdinalIgnoreCase)))
                metrics.Add("RAM");
            if (gpu.Any(r => String.Equals(r.GroupKey, key, StringComparison.OrdinalIgnoreCase)))
                metrics.Add("GPU");
            return String.Join("+", metrics);
        }

        private static int[] ParseAffinity(string text)
        {
            if (String.IsNullOrWhiteSpace(text) ||
                String.Equals(text, "all", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(text, "mixed", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
                return new int[0];
            var bits = new SortedSet<int>();
            foreach (string part in text.Split(','))
            {
                string token = part.Trim();
                int dash = token.IndexOf('-');
                int start, end;
                if (dash > 0 &&
                    Int32.TryParse(token.Substring(0, dash), out start) &&
                    Int32.TryParse(token.Substring(dash + 1), out end))
                {
                    for (int bit = Math.Max(0, start);
                         bit <= Math.Min(63, end); bit++) bits.Add(bit);
                }
                else if (Int32.TryParse(token, out start) && start >= 0 && start < 64)
                    bits.Add(start);
            }
            return bits.ToArray();
        }

        internal static bool MergePerformanceRule(Rule rule, ProcRow row,
                                                  string selectedBy, bool hadExistingRule)
        {
            if (rule == null || row == null) return false;
            string targetPriority = "";
            if (!IsProtectedScheduling(row))
            {
                ProcessPriorityClass current;
                if (Enum.TryParse(row.Priority ?? "", true, out current) &&
                    (current == ProcessPriorityClass.High ||
                     current == ProcessPriorityClass.RealTime))
                    targetPriority = current.ToString();
                else
                    targetPriority = ProcessPriorityClass.AboveNormal.ToString();
            }
            else if (!String.IsNullOrWhiteSpace(rule.priority))
                targetPriority = rule.priority;

            bool unchanged = rule.performanceManaged &&
                String.Equals(rule.priority ?? "", targetPriority,
                              StringComparison.OrdinalIgnoreCase) &&
                (rule.affinity == null || rule.affinity.Length == 0) &&
                rule.cpuLimit == 0 && rule.gpuLimit == 0 && rule.ramLimit == 0 &&
                String.Equals(rule.performanceReason ?? "", selectedBy ?? "",
                              StringComparison.Ordinal);
            if (unchanged) return false;

            if (!rule.performanceManaged)
            {
                rule.performanceHadRule = hadExistingRule;
                rule.performanceOriginalPriority = rule.priority ?? "";
                rule.performanceOriginalAffinity =
                    rule.affinity == null ? new int[0] : rule.affinity.ToArray();
                rule.performanceOriginalCpuLimit = rule.cpuLimit;
                rule.performanceOriginalGpuLimit = rule.gpuLimit;
                rule.performanceOriginalRamLimit = rule.ramLimit;
            }
            rule.priority = targetPriority;
            rule.affinity = new int[0];
            rule.cpuLimit = 0;
            rule.gpuLimit = 0;
            rule.ramLimit = 0;
            rule.enabled = true;
            rule.performanceManaged = true;
            rule.performanceReason = selectedBy ?? "";
            rule.performanceUpdatedUtc =
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            return true;
        }

        internal static bool RestorePerformanceRule(Rule rule)
        {
            if (rule == null || !rule.performanceManaged) return false;
            string originalPriority = rule.performanceOriginalPriority ?? "";
            if (String.Equals(originalPriority, "mixed", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(originalPriority, "n/a", StringComparison.OrdinalIgnoreCase))
                originalPriority = "";
            rule.priority = originalPriority;
            rule.affinity = rule.performanceOriginalAffinity == null
                ? new int[0] : rule.performanceOriginalAffinity.ToArray();
            rule.cpuLimit = rule.performanceOriginalCpuLimit;
            rule.gpuLimit = rule.performanceOriginalGpuLimit;
            rule.ramLimit = rule.performanceOriginalRamLimit;
            rule.performanceManaged = false;
            rule.performanceOriginalPriority = "";
            rule.performanceOriginalAffinity = new int[0];
            rule.performanceOriginalCpuLimit = 0;
            rule.performanceOriginalGpuLimit = 0;
            rule.performanceOriginalRamLimit = 0;
            rule.performanceHadRule = false;
            rule.performanceReason = "";
            rule.performanceUpdatedUtc = "";
            return true;
        }

        internal static bool IsEmptyRule(Rule rule)
        {
            if (rule == null) return true;
            return String.IsNullOrWhiteSpace(rule.priority) &&
                   (rule.affinity == null || rule.affinity.Length == 0) &&
                   rule.cpuLimit == 0 && rule.gpuLimit == 0 && rule.ramLimit == 0 &&
                   !rule.watchdog && String.IsNullOrWhiteSpace(rule.wdPath) &&
                   String.IsNullOrWhiteSpace(rule.wdArgs) && rule.restarts == 0 &&
                   !rule.optimizerManaged && !rule.performanceManaged;
        }

        internal static bool IsPersistenceDue(int lastTick, int nowTick, int intervalMs)
        {
            if (lastTick == 0) return true;
            int elapsed = unchecked(nowTick - lastTick);
            return elapsed >= Math.Max(1000, intervalMs);
        }

        internal static bool IsRollingSelectionActive(int lastSeenTick, int nowTick,
                                                      int windowMs)
        {
            int elapsed = unchecked(nowTick - lastSeenTick);
            return elapsed >= 0 && elapsed <= Math.Max(1000, windowMs);
        }

        internal static bool IsBenignApplyRace(string action)
        {
            return String.Equals(action, "exited_or_changed",
                                 StringComparison.Ordinal);
        }
    }

    internal static class SafeOptimizer
    {
        private static readonly HashSet<string> ProtectedNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "idle", "system", "registry", "memory compression", "secure system",
                "smss", "csrss", "wininit", "winlogon", "services", "lsass",
                "svchost", "fontdrvhost", "dwm", "explorer", "sihost", "taskhost",
                "taskhostw", "audiodg", "spoolsv", "conhost", "openconsole",
                "windowsterminal", "ctfmon", "searchhost", "searchindexer",
                "startmenuexperiencehost", "shellexperiencehost", "runtimebroker",
                "securityhealthservice", "msmpeng", "nissrv", "wudfhost", "wmiprvse",
                "processlasso", "processgovernor", "psproclassogui",
                "phoneexperiencehost", "yourphoneappproxy", "freebuff",
                "moonlightbackgroundgamepad"
            };

        private static readonly HashSet<string> PersistentBackgroundNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "clamd", "vmmemwsl", "asslatestgamebackup"
            };

        private static readonly HashSet<string> PressureWorkerNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "csc", "vbc", "msbuild", "dotnet", "cargo", "rustc", "cl",
                "link", "ninja", "cmake", "clang", "clang-cl", "javac",
                "gradle", "aapt2", "7z", "tar", "ffmpeg", "clamscan"
            };

        private static string Key(string name)
        {
            return (name ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        }

        private static bool IsAiSessionProcess(string name)
        {
            string n = Key(name);
            return n.StartsWith("codex", StringComparison.Ordinal) ||
                   n.StartsWith("chatgpt", StringComparison.Ordinal) ||
                   n.StartsWith("node_repl", StringComparison.Ordinal) ||
                   n.StartsWith("mcp", StringComparison.Ordinal) ||
                   n.StartsWith("claude", StringComparison.Ordinal) ||
                   n.StartsWith("gemini", StringComparison.Ordinal) ||
                   n.StartsWith("bun-baseline", StringComparison.Ordinal);
        }

        private static OptimizationDecision Decision(ProcRow row, string action,
                                                     string target, string reason,
                                                     bool persistent)
        {
            return new OptimizationDecision
            {
                pid = row == null ? 0 : row.Id,
                startTicks = row == null ? 0 : row.StartTicks,
                processName = row == null ? "" : row.Name,
                displayName = row == null ? "" : Sampler.ApplicationDisplayName(row.Name),
                executablePath = row == null ? "" : (row.ExecutablePath ?? ""),
                cpuPercent = row == null ? 0 : row.Cpu,
                ramBytes = row == null ? 0 : row.Mem,
                gpuPercent = row == null ? 0 : row.Gpu,
                gpuValid = row != null && row.GpuValid,
                vramBytes = row == null ? 0 : row.Vram,
                currentPriority = row == null ? "" : (row.Priority ?? ""),
                action = action,
                targetPriority = target,
                cpuAction = String.IsNullOrWhiteSpace(target)
                    ? "observe and preserve" : "set " + target + " priority",
                ramAction = "measure only; Windows manages working sets",
                gpuAction = row != null && row.GpuValid
                    ? "measure only; no unsafe duty-cycle throttle"
                    : "GPU telemetry unavailable; no value fabricated",
                reason = reason,
                persistent = persistent,
                applied = false,
                error = ""
            };
        }

        internal static OptimizationDecision Decide(
            ProcRow row, double totalCpu, int foregroundPid,
            ConcurrentDictionary<string, Rule> rules, int optimizerPid)
        {
            if (row == null || row.Id <= 0 || String.IsNullOrWhiteSpace(row.Name))
                return Decision(row, "preserve", "", "invalid or exited process", false);

            string name = Key(row.Name);
            bool persistentCandidate = PersistentBackgroundNames.Contains(name);
            Rule existing = null;
            if (rules != null) rules.TryGetValue(row.Name, out existing);

            if (row.Id == optimizerPid)
                return Decision(row, "preserve", "", "the optimizer never changes itself", false);
            if (row.Id <= 4 || row.SessionId == 0 || ProtectedNames.Contains(name))
                return Decision(row, "preserve", "", "critical Windows or user-experience process", false);
            if (IsAiSessionProcess(name))
                return Decision(row, "preserve", "", "active AI-session infrastructure", false);
            if (row.Id == foregroundPid || row.HasVisibleWindow)
                return Decision(row, "preserve", "", "foreground or visible application", false);
            if (existing != null && !String.IsNullOrWhiteSpace(existing.priority) &&
                !existing.optimizerManaged)
                return Decision(row, "preserve", "", "existing user or external priority rule", false);

            string priority = row.Priority ?? "";
            if (priority.Equals("BelowNormal", StringComparison.OrdinalIgnoreCase) ||
                priority.Equals("Idle", StringComparison.OrdinalIgnoreCase))
            {
                if (persistentCandidate &&
                    (existing == null || existing.optimizerManaged ||
                     String.IsNullOrWhiteSpace(existing.priority)))
                    return Decision(row, "persist_existing", "BelowNormal",
                                    "background service is already safely deprioritized", true);
                return Decision(row, "preserve", "", "already optimized below normal", false);
            }
            if (!priority.Equals("Normal", StringComparison.OrdinalIgnoreCase))
                return Decision(row, "preserve", "", "nondefault priority is already managed", false);

            if (persistentCandidate)
                return Decision(row, "lower_and_persist", "BelowNormal",
                                "recognized noninteractive background service", true);

            if (totalCpu >= 80 && row.Cpu >= 2.0 && PressureWorkerNames.Contains(name))
                return Decision(row, "lower_now", "BelowNormal",
                                "compute worker is competing with an interactive high-load session", false);

            return Decision(row, "preserve", "", "normal priority is appropriate", false);
        }

        private static bool IdentityStillMatches(Process process, OptimizationDecision decision)
        {
            if (process == null || decision == null) return false;
            try
            {
                if (!String.Equals(process.ProcessName, decision.processName,
                                   StringComparison.OrdinalIgnoreCase)) return false;
                if (decision.startTicks != 0 &&
                    process.StartTime.ToUniversalTime().Ticks != decision.startTicks) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool ApplyPriority(OptimizationDecision decision, string target)
        {
            Process process = null;
            try
            {
                process = Process.GetProcessById(decision.pid);
                if (!IdentityStillMatches(process, decision))
                {
                    decision.error = "PID identity changed before apply";
                    return false;
                }
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    decision.error = "process became interactive before apply";
                    return false;
                }
                ProcessPriorityClass targetClass;
                if (!Enum.TryParse(target, true, out targetClass))
                {
                    decision.error = "invalid target priority";
                    return false;
                }
                process.PriorityClass = targetClass;
                process.Refresh();
                if (process.PriorityClass != targetClass)
                {
                    decision.error = "Windows did not retain the requested priority";
                    return false;
                }
                decision.applied = true;
                return true;
            }
            catch (Exception ex)
            {
                decision.error = ex.Message;
                return false;
            }
            finally { if (process != null) process.Dispose(); }
        }

        internal static bool MergeManagedRule(Rule rule, string target, string reason)
        {
            if (rule == null) return false;
            if (!String.IsNullOrWhiteSpace(rule.priority) && !rule.optimizerManaged)
                return false;
            if (rule.optimizerManaged &&
                String.Equals(rule.priority ?? "", target ?? "",
                              StringComparison.OrdinalIgnoreCase) &&
                String.Equals(rule.optimizerReason ?? "", reason ?? "",
                              StringComparison.Ordinal))
                return false;
            if (!rule.optimizerManaged)
                rule.optimizerOriginalPriority = rule.priority ?? "";
            rule.priority = target;
            rule.optimizerManaged = true;
            rule.optimizerReason = reason ?? "";
            rule.optimizerUpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            return true;
        }

        internal static bool RestoreManagedRule(Rule rule)
        {
            if (rule == null || !rule.optimizerManaged) return false;
            rule.priority = rule.optimizerOriginalPriority ?? "";
            rule.optimizerManaged = false;
            rule.optimizerOriginalPriority = "";
            rule.optimizerReason = "";
            rule.optimizerUpdatedUtc = "";
            return true;
        }

        private static int ForegroundPid()
        {
            try
            {
                uint pid;
                IntPtr window = Native.GetForegroundWindow();
                if (window != IntPtr.Zero &&
                    Native.GetWindowThreadProcessId(window, out pid) != 0)
                    return unchecked((int)pid);
            }
            catch { }
            return 0;
        }

        private static void Report(Action<OptimizationProgress> progress, int percent,
                                   string phase, string message, int current, int total)
        {
            if (progress == null) return;
            try
            {
                progress(new OptimizationProgress
                {
                    percent = Math.Max(0, Math.Min(100, percent)),
                    phase = phase ?? "",
                    message = message ?? "",
                    current = Math.Max(0, current),
                    total = Math.Max(0, total)
                });
            }
            catch { }
        }

        internal static OptimizationReceipt Execute(
            Snapshot snapshot, string mode, Action<OptimizationProgress> progress = null)
        {
            if (snapshot == null) snapshot = new Snapshot();
            var rules = RulesStore.Load();
            int foregroundPid = ForegroundPid();
            int selfPid = Process.GetCurrentProcess().Id;
            var receipt = new OptimizationReceipt
            {
                schema = "psproclasso.optimization.v1",
                mode = mode,
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                machine = Environment.MachineName,
                totalCpuPercent = snapshot.TotalCpu,
                ramUsedBytes = snapshot.RamUsed,
                ramTotalBytes = snapshot.RamTotal,
                processCount = snapshot.Rows == null ? 0 : snapshot.Rows.Count,
                decisions = new List<OptimizationDecision>(),
                errors = new List<string>()
            };
            int rowCount = snapshot.Rows == null ? 0 : snapshot.Rows.Count;
            Report(progress, 0, "Reviewing processes",
                   "Building a safe decision for every observed process.", 0, rowCount);

            if (String.Equals(mode, "restore", StringComparison.OrdinalIgnoreCase))
            {
                int reviewed = 0;
                foreach (var row in snapshot.Rows ?? new List<ProcRow>())
                {
                    Rule rule;
                    if (rules.TryGetValue(row.Name, out rule) && rule.optimizerManaged)
                        receipt.decisions.Add(Decision(row, "restore_managed",
                            String.IsNullOrWhiteSpace(rule.optimizerOriginalPriority)
                                ? "Normal" : rule.optimizerOriginalPriority,
                            "restore optimizer-managed priority", false));
                    else
                        receipt.decisions.Add(Decision(row, "preserve", "",
                            "no optimizer-managed priority", false));
                    reviewed++;
                    Report(progress, rowCount == 0 ? 50 : reviewed * 50 / rowCount,
                           "Reviewing restore plan", row.Name + " PID " + row.Id,
                           reviewed, rowCount);
                }

                var restoreDecisions =
                    receipt.decisions.Where(x => x.action == "restore_managed").ToList();
                int restoredIndex = 0;
                foreach (var decision in restoreDecisions)
                {
                    if (ApplyPriority(decision, decision.targetPriority)) receipt.changedProcesses++;
                    else { receipt.failedChanges++; receipt.errors.Add(decision.processName + ": " + decision.error); }
                    restoredIndex++;
                    Report(progress, 50 + (restoreDecisions.Count == 0 ? 40 :
                           restoredIndex * 40 / restoreDecisions.Count),
                           "Restoring priorities", decision.displayName + " PID " + decision.pid,
                           restoredIndex, restoreDecisions.Count);
                }

                ConcurrentDictionary<string, Rule> restored;
                string restoreError;
                if (!RulesStore.MutateRules(dict =>
                    {
                        foreach (var kv in dict) RestoreManagedRule(kv.Value);
                    }, out restored, out restoreError))
                {
                    receipt.failedChanges++;
                    receipt.errors.Add("rules restore: " + restoreError);
                }
                Report(progress, 100, "Restore complete",
                       "Optimizer-owned priorities were restored.", rowCount, rowCount);
                return receipt;
            }

            int classified = 0;
            foreach (var row in snapshot.Rows ?? new List<ProcRow>())
            {
                receipt.decisions.Add(Decide(row, snapshot.TotalCpu, foregroundPid, rules, selfPid));
                classified++;
                Report(progress, rowCount == 0 ? 45 : classified * 45 / rowCount,
                       "Reviewing processes", row.Name + " PID " + row.Id,
                       classified, rowCount);
            }

            if (!String.Equals(mode, "apply", StringComparison.OrdinalIgnoreCase))
            {
                Report(progress, 100, "Plan complete",
                       "Every observed process has an explicit decision.", rowCount, rowCount);
                return receipt;
            }

            var changes = receipt.decisions.Where(x =>
                x.action == "lower_now" || x.action == "lower_and_persist").ToList();
            int changeIndex = 0;
            foreach (var decision in receipt.decisions)
            {
                if (decision.action != "lower_now" &&
                    decision.action != "lower_and_persist") continue;
                if (ApplyPriority(decision, decision.targetPriority)) receipt.changedProcesses++;
                else
                {
                    receipt.failedChanges++;
                    receipt.errors.Add(decision.processName + " PID " + decision.pid +
                                       ": " + decision.error);
                }
                changeIndex++;
                Report(progress, 45 + (changes.Count == 0 ? 25 :
                       changeIndex * 25 / changes.Count),
                       "Applying reversible CPU policy",
                       decision.displayName + " PID " + decision.pid,
                       changeIndex, changes.Count);
            }

            var persistent = receipt.decisions.Where(x =>
                x.persistent && (x.action == "persist_existing" ||
                                 (x.action == "lower_and_persist" && x.applied)))
                .Where(x =>
                {
                    Rule existing;
                    if (!rules.TryGetValue(x.processName, out existing) || existing == null)
                        return true;
                    return !(existing.optimizerManaged &&
                             String.Equals(existing.priority ?? "", "BelowNormal",
                                           StringComparison.OrdinalIgnoreCase) &&
                             String.Equals(existing.optimizerReason ?? "", x.reason ?? "",
                                           StringComparison.Ordinal));
                })
                .GroupBy(x => x.processName, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).ToList();
            if (persistent.Count > 0)
            {
                Report(progress, 75, "Saving policy",
                       "Writing reversible rules atomically.", 0, persistent.Count);
                ConcurrentDictionary<string, Rule> latest;
                string error;
                bool saved = RulesStore.MutateRules(dict =>
                {
                    foreach (var decision in persistent)
                    {
                        Rule rule;
                        if (!dict.TryGetValue(decision.processName, out rule) || rule == null)
                            rule = Rule.New();
                        if (MergeManagedRule(rule, "BelowNormal", decision.reason))
                        {
                            dict[decision.processName] = rule;
                            receipt.persistentRules++;
                        }
                    }
                }, out latest, out error);
                if (!saved)
                {
                    receipt.failedChanges++;
                    receipt.errors.Add("rules persistence: " + error);
                }
            }
            Report(progress, 100, "Policy applied",
                   "Safe process review and persistence finished.", rowCount, rowCount);
            return receipt;
        }

        internal static bool VerifyPolicyContract()
        {
            var rules = new ConcurrentDictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
            var protectedRow = new ProcRow { Id = 20, Name = "svchost", Priority = "Normal", SessionId = 0 };
            var visibleRow = new ProcRow { Id = 21, Name = "SOPFFO", Priority = "Normal",
                                           SessionId = 1, HasVisibleWindow = true, Cpu = 40 };
            var durableRow = new ProcRow { Id = 22, Name = "clamd", Priority = "Normal",
                                           SessionId = 1, Cpu = 1 };
            var workerRow = new ProcRow { Id = 23, Name = "msbuild", Priority = "Normal",
                                          SessionId = 1, Cpu = 12 };
            var aiRow = new ProcRow { Id = 24, Name = "node_repl", Priority = "Normal",
                                      SessionId = 1, Cpu = 30 };

            Rule userRule = Rule.New();
            userRule.priority = "High";
            rules["msbuild"] = userRule;
            bool existingRuleProtected =
                Decide(workerRow, 95, 0, rules, -1).action == "preserve";
            rules.TryRemove("msbuild", out userRule);

            Rule merge = Rule.New();
            merge.ramLimit = 4096;
            bool merged = MergeManagedRule(merge, "BelowNormal", "test") &&
                          merge.priority == "BelowNormal" && merge.optimizerManaged &&
                          merge.ramLimit == 4096;
            string firstUpdatedUtc = merge.optimizerUpdatedUtc;
            bool idempotentMerge = !MergeManagedRule(merge, "BelowNormal", "test") &&
                                   merge.optimizerUpdatedUtc == firstUpdatedUtc;
            bool restored = RestoreManagedRule(merge) && merge.priority == "" &&
                            !merge.optimizerManaged && merge.ramLimit == 4096;
            Rule external = Rule.New();
            external.priority = "AboveNormal";
            bool externalUntouched = !MergeManagedRule(external, "BelowNormal", "test") &&
                                     external.priority == "AboveNormal";

            return Decide(protectedRow, 95, 0, rules, -1).action == "preserve" &&
                   Decide(visibleRow, 95, 21, rules, -1).action == "preserve" &&
                   Decide(durableRow, 30, 0, rules, -1).action == "lower_and_persist" &&
                   Decide(workerRow, 95, 0, rules, -1).action == "lower_now" &&
                   Decide(workerRow, 40, 0, rules, -1).action == "preserve" &&
                   Decide(aiRow, 95, 0, rules, -1).action == "preserve" &&
                   existingRuleProtected && merged && idempotentMerge &&
                   restored && externalUntouched;
        }

        internal static bool VerifyAdaptiveTop20Contract()
        {
            var rows = new List<ProcRow>();
            for (int i = 1; i <= 25; i++)
            {
                rows.Add(new ProcRow
                {
                    Id = 100 + i,
                    StartTicks = i * 1000,
                    Name = "app" + i,
                    Cpu = i,
                    Mem = (26 - i) * 1024L,
                    Gpu = i == 13 ? 99 : 0,
                    Priority = i == 1 ? "BelowNormal" : "Normal",
                    SessionId = 1
                });
            }

            var selected = AdaptiveTop20Optimizer.SelectTargets(rows, 20);
            bool allMetricsCovered =
                selected.Any(r => r.Name == "app25") &&
                selected.Any(r => r.Name == "app1") &&
                selected.Any(r => r.Name == "app13");
            bool unique = selected.Select(r => r.Id).Distinct().Count() == selected.Count;
            bool safeBoost =
                AdaptiveTop20Optimizer.TargetPriority(rows[0]) == ProcessPriorityClass.AboveNormal &&
                AdaptiveTop20Optimizer.TargetPriority(rows[24]) == ProcessPriorityClass.AboveNormal;
            bool criticalProtected =
                AdaptiveTop20Optimizer.TargetPriority(new ProcRow
                {
                    Id = 4, Name = "System", Priority = "Normal", SessionId = 0
                }) == null;
            bool consoleProtected =
                AdaptiveTop20Optimizer.TargetPriority(new ProcRow
                {
                    Id = 502, Name = "OpenConsole", Priority = "Normal",
                    SessionId = 1
                }) == null;
            bool dockerSchedulingProtected =
                AdaptiveTop20Optimizer.TargetPriority(new ProcRow
                {
                    Id = 504, Name = "Docker Desktop", Priority = "Normal",
                    SessionId = 1
                }) == null;
            bool persistenceCadence =
                AdaptiveTop20Optimizer.IsPersistenceDue(0, 1000, 30000) &&
                !AdaptiveTop20Optimizer.IsPersistenceDue(1000, 20000, 30000) &&
                AdaptiveTop20Optimizer.IsPersistenceDue(1000, 31000, 30000);
            bool rollingRetention =
                AdaptiveTop20Optimizer.IsRollingSelectionActive(1000, 200000, 300000) &&
                !AdaptiveTop20Optimizer.IsRollingSelectionActive(1000, 301001, 300000);
            bool transientExitIsBenign =
                AdaptiveTop20Optimizer.IsBenignApplyRace("exited_or_changed") &&
                !AdaptiveTop20Optimizer.IsBenignApplyRace("partial");
            Rule alreadyFastRule = Rule.New();
            alreadyFastRule.cpuLimit = 25;
            bool durableFastStart = AdaptiveTop20Optimizer.MergePerformanceRule(
                    alreadyFastRule,
                    new ProcRow
                    {
                        Id = 500, Name = "fastapp", Priority = "AboveNormal",
                        SessionId = 1
                    }, "CPU", true) &&
                alreadyFastRule.priority == "AboveNormal" &&
                alreadyFastRule.cpuLimit == 0 &&
                alreadyFastRule.performanceOriginalCpuLimit == 25 &&
                alreadyFastRule.performanceManaged;
            Rule staleRule = Rule.New();
            staleRule.priority = "BelowNormal";
            staleRule.affinity = new[] { 0, 1 };
            staleRule.ramLimit = 2048;
            bool stalePrepared = AdaptiveTop20Optimizer.MergePerformanceRule(
                staleRule,
                new ProcRow
                {
                    Id = 501, Name = "staleapp", Priority = "BelowNormal",
                    Affinity = "0-1", SessionId = 1
                }, "RAM", true);
            bool staleRestored = AdaptiveTop20Optimizer.RestorePerformanceRule(staleRule) &&
                staleRule.priority == "BelowNormal" &&
                staleRule.affinity.SequenceEqual(new[] { 0, 1 }) &&
                staleRule.ramLimit == 2048 &&
                !staleRule.performanceManaged;
            Rule freshRule = Rule.New();
            bool freshPrepared = AdaptiveTop20Optimizer.MergePerformanceRule(
                freshRule,
                new ProcRow
                {
                    Id = 503, Name = "freshapp", Priority = "Normal",
                    Affinity = "all", SessionId = 1
                }, "CPU", false);
            bool freshRestored = AdaptiveTop20Optimizer.RestorePerformanceRule(freshRule) &&
                AdaptiveTop20Optimizer.IsEmptyRule(freshRule);
            return allMetricsCovered && unique && safeBoost && criticalProtected &&
                   consoleProtected && persistenceCadence && rollingRetention &&
                   dockerSchedulingProtected && transientExitIsBenign &&
                   durableFastStart && stalePrepared && staleRestored &&
                   freshPrepared && freshRestored;
        }
    }

    internal static class OptimizationWorkflow
    {
        private sealed class ApplicationAccumulator
        {
            public string Key;
            public string Name;
            public int Samples;
            public int GpuSamples;
            public double CpuTotal;
            public double RamTotal;
            public double GpuTotal;
            public double VramTotal;
        }

        internal const string PersistenceScope =
            "Saved rules are re-applied to matching processes whenever PSProcLasso is " +
            "manually running. Windows startup remains unchanged.";
        internal const string TpuStatus =
            "Windows exposes no general per-process TPU utilization counter; PSProcLasso " +
            "does not fabricate a TPU percentage.";

        internal static string DefaultReceiptPath
        {
            get
            {
                return Path.Combine(Path.GetDirectoryName(RulesStore.FilePath),
                                    "last-optimization.json");
            }
        }

        private static void Report(Action<OptimizationProgress> progress, int percent,
                                   string phase, string message, int current, int total)
        {
            if (progress == null) return;
            try
            {
                progress(new OptimizationProgress
                {
                    percent = Math.Max(0, Math.Min(100, percent)),
                    phase = phase ?? "",
                    message = message ?? "",
                    current = Math.Max(0, current),
                    total = Math.Max(0, total)
                });
            }
            catch { }
        }

        internal static int StageProgress(int start, int span, int current, int total)
        {
            if (total <= 0) return Math.Max(0, Math.Min(100, start));
            double fraction = Math.Max(0, Math.Min(1, current / (double)total));
            return Math.Max(0, Math.Min(100,
                start + (int)Math.Round(span * fraction)));
        }

        private static double Average(IEnumerable<double> values)
        {
            double[] data = values == null ? new double[0] : values.ToArray();
            return data.Length == 0 ? 0 : data.Average();
        }

        private static double Spread(IEnumerable<double> values)
        {
            double[] data = values == null ? new double[0] : values.ToArray();
            if (data.Length < 2) return 0;
            double average = data.Average();
            return Math.Sqrt(data.Sum(x => (x - average) * (x - average)) / data.Length);
        }

        internal static ResourceMeasurement Summarize(
            IEnumerable<Snapshot> source, long durationMs)
        {
            var snapshots = (source ?? Enumerable.Empty<Snapshot>())
                .Where(x => x != null).ToList();
            var cpu = snapshots.Select(x => Math.Max(0, Math.Min(100, x.TotalCpu))).ToArray();
            var ramBytes = snapshots.Select(x => Math.Max(0, x.RamUsed)).ToArray();
            var ramPercent = snapshots.Select(x => x.RamTotal <= 0 ? 0 :
                Math.Max(0, Math.Min(100, x.RamUsed * 100.0 / x.RamTotal))).ToArray();
            var gpu = snapshots.Where(x => x.GpuValid)
                .Select(x => Math.Max(0, Math.Min(100, x.GpuPct))).ToArray();
            var apps =
                new Dictionary<string, ApplicationAccumulator>(StringComparer.OrdinalIgnoreCase);

            foreach (var snapshot in snapshots)
            {
                foreach (var row in MainForm.BuildApplicationRows(snapshot.Rows))
                {
                    string key = String.IsNullOrWhiteSpace(row.GroupKey)
                        ? row.Name : row.GroupKey;
                    ApplicationAccumulator accumulator;
                    if (!apps.TryGetValue(key, out accumulator))
                    {
                        accumulator = new ApplicationAccumulator
                        {
                            Key = key,
                            Name = row.Name
                        };
                        apps[key] = accumulator;
                    }
                    accumulator.Samples++;
                    accumulator.CpuTotal += Math.Max(0, row.Cpu);
                    accumulator.RamTotal += Math.Max(0, row.Mem);
                    accumulator.VramTotal += Math.Max(0, row.Vram);
                    if (row.GpuValid)
                    {
                        accumulator.GpuSamples++;
                        accumulator.GpuTotal += Math.Max(0, row.Gpu);
                    }
                }
            }

            long totalRam = snapshots.Select(x => x.RamTotal)
                .Where(x => x > 0).DefaultIfEmpty(0).Max();
            return new ResourceMeasurement
            {
                samples = snapshots.Count,
                gpuSamples = gpu.Length,
                durationMs = Math.Max(0, durationMs),
                cpuPercent = Math.Round(Average(cpu), 2),
                ramUsedBytes = ramBytes.Length == 0 ? 0 :
                    (long)Math.Round(ramBytes.Average(x => (double)x)),
                ramTotalBytes = totalRam,
                ramPercent = Math.Round(Average(ramPercent), 2),
                gpuPercent = Math.Round(Average(gpu), 2),
                gpuValid = gpu.Length > 0,
                cpuSpread = Math.Round(Spread(cpu), 2),
                ramSpreadPercent = Math.Round(Spread(ramPercent), 2),
                gpuSpread = Math.Round(Spread(gpu), 2),
                applications = apps.Values.OrderByDescending(x => x.CpuTotal)
                    .ThenBy(x => x.Name).Select(x => new ApplicationMeasurement
                    {
                        key = x.Key,
                        name = x.Name,
                        samples = x.Samples,
                        gpuSamples = x.GpuSamples,
                        cpuPercent = Math.Round(x.Samples == 0 ? 0 :
                            x.CpuTotal / x.Samples, 2),
                        ramBytes = (long)Math.Round(x.Samples == 0 ? 0 :
                            x.RamTotal / x.Samples),
                        gpuPercent = Math.Round(x.GpuSamples == 0 ? 0 :
                            x.GpuTotal / x.GpuSamples, 2),
                        gpuValid = x.GpuSamples > 0,
                        vramBytes = (long)Math.Round(x.Samples == 0 ? 0 :
                            x.VramTotal / x.Samples)
                    }).ToList()
            };
        }

        private static double RelativeImprovement(double before, double after)
        {
            if (before <= 0) return after <= 0 ? 0 : -100;
            return Math.Round((before - after) * 100.0 / before, 2);
        }

        private static string Confidence(ResourceMeasurement before,
                                         ResourceMeasurement after)
        {
            if (before == null || after == null ||
                before.samples < 5 || after.samples < 5) return "low";
            bool stableCpu = before.cpuSpread <= 7 && after.cpuSpread <= 7;
            bool stableRam = before.ramSpreadPercent <= 1 &&
                             after.ramSpreadPercent <= 1;
            bool stableGpu = !before.gpuValid || !after.gpuValid ||
                             (before.gpuSpread <= 12 && after.gpuSpread <= 12);
            return stableCpu && stableRam && stableGpu &&
                   before.samples >= 8 && after.samples >= 8 ? "high" : "medium";
        }

        internal static OptimizationImpact Compare(ResourceMeasurement before,
                                                   ResourceMeasurement after)
        {
            before = before ?? new ResourceMeasurement();
            after = after ?? new ResourceMeasurement();
            bool gpuMeasured = before.gpuValid && after.gpuValid;
            return new OptimizationImpact
            {
                cpuChangePoints = Math.Round(before.cpuPercent - after.cpuPercent, 2),
                cpuImprovementPercent =
                    RelativeImprovement(before.cpuPercent, after.cpuPercent),
                ramChangeBytes = before.ramUsedBytes - after.ramUsedBytes,
                ramChangePoints = Math.Round(before.ramPercent - after.ramPercent, 2),
                ramImprovementPercent =
                    RelativeImprovement(before.ramUsedBytes, after.ramUsedBytes),
                gpuMeasured = gpuMeasured,
                gpuChangePoints = gpuMeasured
                    ? Math.Round(before.gpuPercent - after.gpuPercent, 2) : 0,
                gpuImprovementPercent = gpuMeasured
                    ? RelativeImprovement(before.gpuPercent, after.gpuPercent) : 0,
                confidence = Confidence(before, after),
                interpretation =
                    "Positive values mean lower observed load. Negative values mean load " +
                    "increased. Workload changes can affect the comparison, so no causal " +
                    "improvement is claimed without stable measurements."
            };
        }

        internal static List<ApplicationImpact> CompareApplications(
            ResourceMeasurement before, ResourceMeasurement after)
        {
            var result = new List<ApplicationImpact>();
            var beforeMap = (before == null || before.applications == null
                    ? Enumerable.Empty<ApplicationMeasurement>() : before.applications)
                .ToDictionary(x => x.key ?? x.name ?? "", StringComparer.OrdinalIgnoreCase);
            var afterMap = (after == null || after.applications == null
                    ? Enumerable.Empty<ApplicationMeasurement>() : after.applications)
                .ToDictionary(x => x.key ?? x.name ?? "", StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(beforeMap.Keys, StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(afterMap.Keys);
            foreach (string key in keys)
            {
                ApplicationMeasurement oldValue;
                ApplicationMeasurement newValue;
                bool hadBefore = beforeMap.TryGetValue(key, out oldValue);
                bool hadAfter = afterMap.TryGetValue(key, out newValue);
                oldValue = oldValue ?? new ApplicationMeasurement();
                newValue = newValue ?? new ApplicationMeasurement();
                bool gpuMeasured = hadBefore && hadAfter &&
                                   oldValue.gpuValid && newValue.gpuValid;
                result.Add(new ApplicationImpact
                {
                    key = key,
                    name = hadAfter ? newValue.name : oldValue.name,
                    status = hadBefore && hadAfter ? "comparable" :
                             hadBefore ? "exited during measurement" :
                             "started during measurement",
                    cpuBeforePercent = oldValue.cpuPercent,
                    cpuAfterPercent = newValue.cpuPercent,
                    cpuImprovementPercent = hadBefore && hadAfter
                        ? RelativeImprovement(oldValue.cpuPercent, newValue.cpuPercent) : 0,
                    ramBeforeBytes = oldValue.ramBytes,
                    ramAfterBytes = newValue.ramBytes,
                    ramImprovementPercent = hadBefore && hadAfter
                        ? RelativeImprovement(oldValue.ramBytes, newValue.ramBytes) : 0,
                    gpuMeasured = gpuMeasured,
                    gpuBeforePercent = oldValue.gpuPercent,
                    gpuAfterPercent = newValue.gpuPercent,
                    gpuImprovementPercent = gpuMeasured
                        ? RelativeImprovement(oldValue.gpuPercent, newValue.gpuPercent) : 0
                });
            }
            return result.OrderByDescending(x =>
                    Math.Abs(x.cpuBeforePercent - x.cpuAfterPercent))
                .ThenBy(x => x.name).ToList();
        }

        private static ResourceMeasurement Capture(
            Sampler sampler, int durationMs, int progressStart, int progressSpan,
            string phase, Action<OptimizationProgress> progress)
        {
            var snapshots = new List<Snapshot>();
            var watch = Stopwatch.StartNew();
            long lastTick = -1;
            while (watch.ElapsedMilliseconds < durationMs)
            {
                long tick = sampler.FastDataTick;
                if (tick != lastTick)
                {
                    Snapshot snapshot = sampler.Snap;
                    if (snapshot != null && snapshot.ProcessCount > 0 &&
                        snapshot.RamTotal > 0)
                    {
                        snapshots.Add(snapshot);
                        lastTick = tick;
                    }
                }
                int elapsed = (int)Math.Min(durationMs, watch.ElapsedMilliseconds);
                Report(progress, StageProgress(progressStart, progressSpan,
                       elapsed, durationMs), phase,
                       "Collecting stable CPU, RAM, and GPU samples.",
                       snapshots.Count, Math.Max(1, durationMs / 500));
                Thread.Sleep(75);
            }
            Snapshot final = sampler.Snap;
            if (final != null && final.ProcessCount > 0 &&
                (snapshots.Count == 0 || sampler.FastDataTick != lastTick))
                snapshots.Add(final);
            return Summarize(snapshots, watch.ElapsedMilliseconds);
        }

        internal static OptimizationRunReceipt Run(
            Sampler sampler, Action<OptimizationProgress> progress)
        {
            if (sampler == null) throw new ArgumentNullException("sampler");
            var receipt = new OptimizationRunReceipt
            {
                schema = "psproclasso.optimization-run.v1",
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                machine = Environment.MachineName,
                applications = new List<ApplicationImpact>(),
                persistenceScope = PersistenceScope,
                tpuStatus = TpuStatus,
                receiptPath = DefaultReceiptPath,
                errors = new List<string>()
            };

            bool restoreGpuOff = !sampler.GpuOn;
            sampler.GpuOn = true;
            try
            {
                sampler.AdaptiveTop20Enabled = false;
                Report(progress, 0, "Preparing",
                       "Waiting for fresh CPU, RAM, and GPU telemetry.", 0, 1);
                var ready = Stopwatch.StartNew();
                while (ready.ElapsedMilliseconds < 12000)
                {
                    bool coreReady = sampler.FastDataTick >= 3 &&
                                     sampler.Snap.ProcessCount > 0 &&
                                     sampler.Snap.RamTotal > 0;
                    bool gpuReady = sampler.GpuFresh && sampler.GpuDataTick >= 2;
                    if (coreReady && gpuReady) break;
                    Report(progress, StageProgress(0, 2,
                           (int)ready.ElapsedMilliseconds, 12000),
                           "Preparing",
                           gpuReady ? "CPU and RAM telemetry is warming up." :
                           "GPU telemetry is initializing; no zero is being assumed.",
                           (int)Math.Min(ready.ElapsedMilliseconds, 12000), 12000);
                    Thread.Sleep(100);
                }
                if (sampler.Snap.ProcessCount <= 0 || sampler.Snap.RamTotal <= 0)
                    throw new TimeoutException("CPU and RAM telemetry did not become ready.");

                receipt.before = Capture(sampler, 6000, 2, 30,
                                         "Measuring baseline", progress);
                Report(progress, 33, "Reconciling policy",
                       "Removing the older conflicting app-owned boost policy.", 0, 1);
                List<string> restoreErrors;
                receipt.restoredLegacyPolicies =
                    sampler.RestoreLegacyPerformancePolicies(out restoreErrors);
                receipt.errors.AddRange(restoreErrors);

                receipt.actions = SafeOptimizer.Execute(sampler.Snap, "apply", p =>
                    Report(progress, StageProgress(35, 30, p.percent, 100),
                           p.phase, p.message, p.current, p.total));
                if (receipt.actions.errors != null)
                    receipt.errors.AddRange(receipt.actions.errors);
                sampler.Rules = RulesStore.Load();

                Report(progress, 67, "Settling",
                       "Allowing scheduling changes to stabilize.", 0, 1);
                Thread.Sleep(1500);
                receipt.after = Capture(sampler, 6000, 70, 26,
                                        "Measuring result", progress);
                receipt.systemImpact = Compare(receipt.before, receipt.after);
                receipt.applications = CompareApplications(receipt.before, receipt.after);
                receipt.startupEnabled = StartupManager.IsEnabled();

                Report(progress, 98, "Saving receipt",
                       "Writing the complete before/after evidence.", 0, 1);
                string writeError;
                if (!AiAutomation.WriteJson(receipt.receiptPath, receipt, out writeError))
                    receipt.errors.Add("receipt: " + writeError);
                Report(progress, 100, "Finished",
                       "Measured optimization is complete.", 1, 1);
                return receipt;
            }
            finally
            {
                if (restoreGpuOff) sampler.GpuOn = false;
            }
        }

        internal static string FormatSignedChange(double improvement)
        {
            if (Math.Abs(improvement) < 0.005) return "unchanged";
            return Math.Abs(improvement).ToString("N1", CultureInfo.CurrentCulture) +
                   "% " + (improvement > 0 ? "lower" : "higher");
        }

        internal static bool VerifyContract()
        {
            const long gb = 1024L * 1024 * 1024;
            var beforeSnapshots = new List<Snapshot>();
            var afterSnapshots = new List<Snapshot>();
            for (int i = 0; i < 10; i++)
            {
                beforeSnapshots.Add(new Snapshot
                {
                    TotalCpu = 50,
                    RamUsed = 50 * gb,
                    RamTotal = 100 * gb,
                    GpuPct = 40,
                    GpuValid = true,
                    ProcessCount = 1,
                    Rows = new List<ProcRow>
                    {
                        new ProcRow
                        {
                            Id = 10, StartTicks = 100, Name = "testapp",
                            GroupKey = "testapp", Cpu = 20, Mem = 20 * gb,
                            Gpu = 30, GpuValid = true, Priority = "Normal",
                            SessionId = 1
                        }
                    }
                });
                afterSnapshots.Add(new Snapshot
                {
                    TotalCpu = 40,
                    RamUsed = 45 * gb,
                    RamTotal = 100 * gb,
                    GpuPct = 20,
                    GpuValid = true,
                    ProcessCount = 1,
                    Rows = new List<ProcRow>
                    {
                        new ProcRow
                        {
                            Id = 10, StartTicks = 100, Name = "testapp",
                            GroupKey = "testapp", Cpu = 10, Mem = 18 * gb,
                            Gpu = 15, GpuValid = true, Priority = "Normal",
                            SessionId = 1
                        }
                    }
                });
            }

            ResourceMeasurement before = Summarize(beforeSnapshots, 6000);
            ResourceMeasurement after = Summarize(afterSnapshots, 6000);
            OptimizationImpact impact = Compare(before, after);
            ApplicationImpact app = CompareApplications(before, after).Single();
            var unavailableBefore = Summarize(new[]
            {
                new Snapshot
                {
                    TotalCpu = 50, RamUsed = 50 * gb, RamTotal = 100 * gb,
                    GpuValid = false, ProcessCount = 1
                }
            }, 500);
            OptimizationImpact unavailable = Compare(unavailableBefore, after);
            OptimizationImpact worse = Compare(before, new ResourceMeasurement
            {
                samples = 10, cpuPercent = 60, ramUsedBytes = 50 * gb,
                ramTotalBytes = 100 * gb, ramPercent = 50, gpuValid = true,
                gpuSamples = 10, gpuPercent = 40
            });
            Rule combined = Rule.New();
            combined.optimizerManaged = true;
            combined.optimizerOriginalPriority = "";
            combined.priority = "AboveNormal";
            combined.performanceManaged = true;
            combined.performanceOriginalPriority = "BelowNormal";
            bool legacyConflictRestored =
                AdaptiveTop20Optimizer.RestorePerformanceRule(combined) &&
                combined.optimizerManaged && !combined.performanceManaged &&
                combined.priority == "BelowNormal";
            int[] progressValues = Enumerable.Range(0, 11)
                .Select(i => StageProgress(35, 30, i, 10)).ToArray();
            bool monotonic = progressValues.First() == 35 &&
                             progressValues.Last() == 65 &&
                             progressValues.Zip(progressValues.Skip(1),
                                 (a, b) => b >= a).All(x => x);

            return Math.Abs(impact.cpuImprovementPercent - 20) < 0.01 &&
                   Math.Abs(impact.ramImprovementPercent - 10) < 0.01 &&
                   Math.Abs(impact.gpuImprovementPercent - 50) < 0.01 &&
                   impact.gpuMeasured && impact.confidence == "high" &&
                   Math.Abs(app.cpuImprovementPercent - 50) < 0.01 &&
                   Math.Abs(app.ramImprovementPercent - 10) < 0.01 &&
                   Math.Abs(app.gpuImprovementPercent - 50) < 0.01 &&
                   !unavailable.gpuMeasured &&
                   Math.Abs(unavailable.gpuImprovementPercent) < 0.01 &&
                   Math.Abs(worse.cpuImprovementPercent + 20) < 0.01 &&
                   legacyConflictRestored && monotonic &&
                   PersistenceScope.IndexOf("manually running",
                       StringComparison.OrdinalIgnoreCase) >= 0 &&
                   TpuStatus.IndexOf("does not fabricate",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class AiAutomation
    {
        internal static string DefaultPath(string mode)
        {
            string suffix = mode == "snapshot" ? "snapshot" :
                            mode == "plan" ? "optimize-plan" :
                            mode == "apply" ? "optimize-apply" :
                            mode == "top20-plan" ? "top20-plan" :
                            mode == "top20-apply" ? "top20-apply" : "optimize-restore";
            return Path.Combine(Path.GetTempPath(), "psproclasso-" + suffix + ".json");
        }

        private static bool WaitForSnapshot(Sampler sampler, bool needGpu)
        {
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < (needGpu ? 10000 : 5000))
            {
                Thread.Sleep(100);
                Snapshot snapshot = sampler.Snap;
                if (sampler.FastDataTick >= 3 && snapshot.ProcessCount > 0 &&
                    snapshot.RamTotal > 0 &&
                    (!needGpu || (sampler.GpuFresh && sampler.GpuDataTick >= 2)))
                    return true;
            }
            return false;
        }

        private static object SnapshotDocument(Snapshot snapshot)
        {
            var applications = new List<Dictionary<string, object>>();
            foreach (var row in MainForm.BuildApplicationRows(snapshot.Rows)
                         .OrderByDescending(r => r.Cpu).ThenBy(r => r.Name))
            {
                applications.Add(new Dictionary<string, object>
                {
                    { "name", row.Name },
                    { "processCount", MainForm.MemberCount(row) },
                    { "pids", MainForm.MemberRows(row).Select(r => r.Id).ToArray() },
                    { "cpuPercent", row.Cpu },
                    { "ramBytes", row.Mem },
                    { "gpuPercent", row.Gpu },
                    { "vramBytes", row.Vram },
                    { "priority", row.Priority },
                    { "affinity", row.Affinity },
                    { "executablePath", row.ExecutablePath ?? "" },
                    { "managed", row.Controls ?? "" }
                });
            }
            return new Dictionary<string, object>
            {
                { "schema", "psproclasso.snapshot.v1" },
                { "generatedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "machine", Environment.MachineName },
                { "system", new Dictionary<string, object>
                    {
                        { "cpuPercent", snapshot.TotalCpu },
                        { "ramUsedBytes", snapshot.RamUsed },
                        { "ramTotalBytes", snapshot.RamTotal },
                        { "gpuPercent", snapshot.GpuPct },
                        { "vramUsedBytes", snapshot.VramUsed },
                        { "vramTotalBytes", snapshot.VramTotal },
                        { "processCount", snapshot.ProcessCount },
                        { "applicationCount", applications.Count }
                    }
                },
                { "applications", applications }
            };
        }

        internal static bool WriteJson(string path, object document, out string error)
        {
            error = "";
            string temp = "";
            try
            {
                path = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                temp = path + "." + Process.GetCurrentProcess().Id + ".tmp";
                string json = new JavaScriptSerializer
                {
                    MaxJsonLength = Int32.MaxValue,
                    RecursionLimit = 100
                }.Serialize(document);
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null, true);
                else File.Move(temp, path);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { if (!String.IsNullOrEmpty(temp)) try { File.Delete(temp); } catch { } }
        }

        internal static bool Run(string mode, string outputPath)
        {
            Sampler sampler = null;
            try
            {
                bool needGpu = mode == "snapshot" || mode == "plan" || mode == "apply";
                sampler = new Sampler
                {
                    Rules = RulesStore.Load(),
                    ProBalance = false,
                    EnforcementEnabled = false,
                    GpuOn = needGpu
                };
                sampler.Start();
                if (!WaitForSnapshot(sampler, needGpu))
                    throw new TimeoutException("Live process sampling did not become ready.");
                Snapshot snapshot = sampler.Snap;
                object document;
                if (mode == "snapshot")
                {
                    document = SnapshotDocument(snapshot);
                }
                else
                {
                    int restoredLegacy = 0;
                    var restoreErrors = new List<string>();
                    if (mode == "apply")
                    {
                        long beforeTick = sampler.FastDataTick;
                        restoredLegacy =
                            sampler.RestoreLegacyPerformancePolicies(out restoreErrors);
                        var refresh = Stopwatch.StartNew();
                        while (refresh.ElapsedMilliseconds < 2000 &&
                               sampler.FastDataTick == beforeTick) Thread.Sleep(50);
                        snapshot = sampler.Snap;
                    }
                    OptimizationReceipt optimization =
                        SafeOptimizer.Execute(snapshot, mode);
                    optimization.restoredLegacyPolicies = restoredLegacy;
                    if (restoreErrors.Count > 0)
                    {
                        optimization.errors.AddRange(restoreErrors);
                        optimization.failedChanges += restoreErrors.Count;
                    }
                    document = optimization;
                }
                string error;
                return WriteJson(String.IsNullOrWhiteSpace(outputPath)
                    ? DefaultPath(mode) : outputPath, document, out error);
            }
            catch (Exception ex)
            {
                string error;
                WriteJson(String.IsNullOrWhiteSpace(outputPath)
                    ? DefaultPath(mode) : outputPath,
                    new Dictionary<string, object>
                    {
                        { "schema", "psproclasso.error.v1" },
                        { "mode", mode },
                        { "error", ex.Message }
                    }, out error);
                return false;
            }
            finally { if (sampler != null) sampler.Stop(); }
        }

        internal static bool RunAdaptiveTop20(bool apply, string outputPath)
        {
            Sampler sampler = null;
            string mode = apply ? "top20-apply" : "top20-plan";
            try
            {
                sampler = new Sampler
                {
                    Rules = RulesStore.Load(),
                    ProBalance = false,
                    EnforcementEnabled = false,
                    AdaptiveTop20Enabled = false,
                    GpuOn = true
                };
                sampler.Start();
                if (!WaitForSnapshot(sampler, true))
                    throw new TimeoutException("Live CPU, RAM, and GPU sampling did not become ready.");
                AdaptiveTop20Receipt receipt =
                    sampler.ApplyAdaptiveTop20(apply, apply);
                string error;
                return WriteJson(String.IsNullOrWhiteSpace(outputPath)
                    ? DefaultPath(mode) : outputPath, receipt, out error) &&
                    receipt.failures == 0;
            }
            catch (Exception ex)
            {
                string error;
                WriteJson(String.IsNullOrWhiteSpace(outputPath)
                    ? DefaultPath(mode) : outputPath,
                    new Dictionary<string, object>
                    {
                        { "schema", "psproclasso.error.v1" },
                        { "mode", mode },
                        { "error", ex.Message }
                    }, out error);
                return false;
            }
            finally { if (sampler != null) sampler.Stop(); }
        }

        internal static bool VerifyJsonContract()
        {
            try
            {
                var receipt = new OptimizationReceipt
                {
                    schema = "psproclasso.optimization.v1",
                    mode = "plan",
                    generatedUtc = "2026-08-12T00:00:00.0000000Z",
                    machine = "contract",
                    processCount = 1,
                    decisions = new List<OptimizationDecision>
                    {
                        new OptimizationDecision
                        {
                            pid = 42, processName = "worker", action = "preserve",
                            reason = "normal priority is appropriate"
                        }
                    },
                    errors = new List<string>()
                };
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(receipt);
                OptimizationReceipt parsed =
                    serializer.Deserialize<OptimizationReceipt>(json);
                return parsed != null &&
                       String.Equals(parsed.schema, "psproclasso.optimization.v1",
                                     StringComparison.Ordinal) &&
                       String.Equals(parsed.mode, "plan", StringComparison.Ordinal) &&
                       parsed.decisions != null && parsed.decisions.Count == 1 &&
                       parsed.decisions[0].pid == 42 &&
                       parsed.decisions[0].action == "preserve";
            }
            catch { return false; }
        }
    }

    internal class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { }
        private class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Theme.SelRow; } }
            public override Color MenuItemBorder { get { return Theme.SelRow; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.SelRow; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.SelRow; } }
            public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
            public override Color MenuBorder { get { return Theme.Header; } }
            public override Color MenuItemPressedGradientBegin { get { return Theme.SelRow; } }
            public override Color MenuItemPressedGradientEnd { get { return Theme.SelRow; } }
            public override Color SeparatorDark { get { return Theme.Header; } }
            public override Color SeparatorLight { get { return Theme.Header; } }
        }
    }

    // ---------------------------------------------------------------------
    //  Meter control: label + colored bar + value
    // ---------------------------------------------------------------------
    internal class Meter : Control
    {
        public string Caption { get; set; }
        public double Percent { get; set; }
        public string Value { get; set; }

        public Meter()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 34;
            Font = new Font("Segoe UI", 8.5f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            double p = Math.Max(0, Math.Min(100, Percent));
            Color col = Theme.PctColor(p);

            // colored status dot + bold caption (left), value (right)
            using (var dot = new SolidBrush(col)) g.FillEllipse(dot, 3, 5, 7, 7);
            using (var cap = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (var capB = new SolidBrush(Theme.Text))
                g.DrawString(Caption.ToUpperInvariant(), cap, capB, 14, 1);
            string val = Value ?? "";
            if (val.Length > 0)
            {
                using (var vB = new SolidBrush(Theme.Dim))
                {
                    var sz = g.MeasureString(val, Font);
                    g.DrawString(val, Font, vB, ClientSize.Width - sz.Width - 4, 1);
                }
            }

            // rounded track with hairline rim
            var barRect = new Rectangle(2, 20, Math.Max(10, ClientSize.Width - 4), 10);
            using (var track = new SolidBrush(Theme.Track)) g.FillRoundRect(track, barRect, 5);
            using (var rim = new Pen(Theme.Line)) g.DrawRoundRect(rim, barRect, 5);

            int w = (int)(barRect.Width * p / 100.0);
            if (w > 3)
            {
                var fillRect = new Rectangle(barRect.X + 1, barRect.Y + 1, Math.Max(1, w - 1), barRect.Height - 2);
                // soft glow behind the fill
                using (var glow = new SolidBrush(Color.FromArgb(42, col.R, col.G, col.B)))
                    g.FillRoundRect(glow, new Rectangle(fillRect.X - 1, fillRect.Y - 1, Math.Min(barRect.Width, fillRect.Width + 2), fillRect.Height + 2), 5);
                // vertical gradient fill (lighter top, saturated base)
                using (var fill = new LinearGradientBrush(fillRect, Color.FromArgb(255, Math.Min(255, col.R + 50), Math.Min(255, col.G + 50), Math.Min(255, col.B + 50)), col, 90f))
                    g.FillRoundRect(fill, fillRect, 4);
                // bright leading edge
                if (fillRect.Width > 5)
                    using (var edge = new SolidBrush(Color.FromArgb(235, 250, 255)))
                        g.FillRectangle(edge, fillRect.Right - 2, fillRect.Y + 1, 2, fillRect.Height - 2);
            }
        }
    }

    internal static class GfxExt
    {
        public static void FillRoundRect(this Graphics g, Brush b, Rectangle r, int radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var path = new GraphicsPath())
            {
                int d = radius * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        public static void DrawRoundRect(this Graphics g, Pen p, Rectangle r, int radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var path = new GraphicsPath())
            {
                int d = radius * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.DrawPath(p, path);
            }
        }
    }

    // ---------------------------------------------------------------------
    //  Gradient panel: subtle vertical gradient + optional bottom hairline
    // ---------------------------------------------------------------------
    internal class GradientPanel : Panel
    {
        public Color ColorTop = Color.FromArgb(40, 46, 62);
        public Color ColorBottom = Theme.Panel;
        public bool DrawBottomLine;

        public GradientPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new LinearGradientBrush(ClientRectangle, ColorTop, ColorBottom, 90f))
                e.Graphics.FillRectangle(b, ClientRectangle);
            if (DrawBottomLine)
            {
                using (var p = new Pen(Theme.Line))
                    e.Graphics.DrawLine(p, 0, ClientSize.Height - 1, ClientSize.Width, ClientSize.Height - 1);
            }
        }
    }

    // ---------------------------------------------------------------------
    //  Small dark message/confirm box
    // ---------------------------------------------------------------------
    internal class DarkBox : Form
    {
        public DarkBox(string title, string text, bool confirm)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            ClientSize = new Size(420, confirm ? 128 : 110);
            Font = new Font("Segoe UI", 9f);

            var lbl = new Label { Text = text, Left = 16, Top = 16, Width = 388, AutoSize = false, Height = 60, ForeColor = Theme.Text, BackColor = Theme.Bg };
            Controls.Add(lbl);

            if (confirm)
            {
                var yes = MakeButton("Yes", 300);
                var no = MakeButton("No", 352);
                yes.Click += (s, e) => { DialogResult = DialogResult.Yes; Close(); };
                no.Click += (s, e) => { DialogResult = DialogResult.No; Close(); };
                AcceptButton = no; CancelButton = no;
            }
            else
            {
                var ok = MakeButton("OK", 352);
                ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
                AcceptButton = ok; CancelButton = ok;
            }
        }

        private Button MakeButton(string txt, int x)
        {
            var b = new Button { Text = txt, Left = x, Top = ClientSize.Height - 44, Width = 52, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Text };
            b.FlatAppearance.BorderColor = Theme.Header;
            Controls.Add(b);
            return b;
        }
    }

    internal class OptimizationProgressForm : Form
    {
        private readonly Label _phase;
        private readonly Label _message;
        private readonly Label _count;
        private readonly ProgressBar _progress;
        private readonly TextBox _details;
        private readonly Button _close;
        private int _lastPercent;
        private string _lastPhase = "";
        private bool _finished;

        public OptimizationProgressForm()
        {
            Text = "PSProcLasso - Measured Optimization";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            ClientSize = new Size(620, 390);
            Font = new Font("Segoe UI", 9f);
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);

            _phase = new Label
            {
                Left = 18, Top = 18, Width = 584, Height = 28,
                Text = "Preparing", Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = Theme.Accent, BackColor = Theme.Bg
            };
            _message = new Label
            {
                Left = 18, Top = 52, Width = 584, Height = 42,
                Text = "Waiting for live telemetry.", ForeColor = Theme.Text,
                BackColor = Theme.Bg
            };
            _progress = new ProgressBar
            {
                Left = 18, Top = 100, Width = 584, Height = 18,
                Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous
            };
            _count = new Label
            {
                Left = 18, Top = 124, Width = 584, Height = 22,
                Text = "0%", TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Theme.Dim, BackColor = Theme.Bg
            };
            _details = new TextBox
            {
                Left = 18, Top = 154, Width = 584, Height = 184,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Panel,
                ForeColor = Theme.Text, Font = new Font("Consolas", 9f)
            };
            _close = new Button
            {
                Text = "CLOSE", Left = 512, Top = 348, Width = 90, Height = 28,
                Enabled = false, FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Panel, ForeColor = Theme.Text
            };
            _close.FlatAppearance.BorderColor = Theme.Header;
            _close.Click += (s, e) => Close();
            Controls.Add(_phase);
            Controls.Add(_message);
            Controls.Add(_progress);
            Controls.Add(_count);
            Controls.Add(_details);
            Controls.Add(_close);
            FormClosing += (s, e) =>
            {
                if (_finished) return;
                e.Cancel = true;
            };
        }

        public void UpdateProgress(OptimizationProgress update)
        {
            if (update == null || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<OptimizationProgress>(UpdateProgress), update); }
                catch { }
                return;
            }
            int percent = Math.Max(_lastPercent, Math.Max(0, Math.Min(100, update.percent)));
            _lastPercent = percent;
            if (_progress.Value != percent) _progress.Value = percent;
            _phase.Text = String.IsNullOrWhiteSpace(update.phase) ? "Working" : update.phase;
            _message.Text = update.message ?? "";
            _count.Text = percent + "%" +
                (update.total > 0 ? "   " + update.current + " / " + update.total : "");
            if (!String.Equals(_lastPhase, _phase.Text, StringComparison.Ordinal))
            {
                _lastPhase = _phase.Text;
                _details.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " +
                                    _phase.Text + "\r\n");
            }
        }

        private static string MetricLine(string name, double before, double after,
                                         double improvement)
        {
            return name.PadRight(4) + "  " + before.ToString("N1") + "% -> " +
                   after.ToString("N1") + "%   (" +
                   OptimizationWorkflow.FormatSignedChange(improvement) + ")";
        }

        public void Complete(OptimizationRunReceipt receipt)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<OptimizationRunReceipt>(Complete), receipt); }
                catch { }
                return;
            }
            _finished = true;
            _lastPercent = 100;
            _progress.Value = 100;
            _phase.Text = receipt != null && receipt.errors != null &&
                          receipt.errors.Count > 0
                ? "Finished with warnings" : "Optimization measured";
            _message.Text =
                "The result below is the observed load change, not an invented estimate.";
            var text = new StringBuilder();
            if (receipt == null || receipt.before == null || receipt.after == null ||
                receipt.systemImpact == null)
            {
                text.AppendLine("No measurement receipt was produced.");
            }
            else
            {
                text.AppendLine(MetricLine("CPU", receipt.before.cpuPercent,
                    receipt.after.cpuPercent, receipt.systemImpact.cpuImprovementPercent));
                text.AppendLine(MetricLine("RAM", receipt.before.ramPercent,
                    receipt.after.ramPercent, receipt.systemImpact.ramImprovementPercent));
                if (receipt.systemImpact.gpuMeasured)
                    text.AppendLine(MetricLine("GPU", receipt.before.gpuPercent,
                        receipt.after.gpuPercent,
                        receipt.systemImpact.gpuImprovementPercent));
                else
                    text.AppendLine("GPU   unavailable in one measurement window; no 0% result fabricated");
                text.AppendLine();
                text.AppendLine("Confidence: " + receipt.systemImpact.confidence);
                text.AppendLine("Processes reviewed: " +
                    (receipt.actions == null ? 0 : receipt.actions.processCount));
                text.AppendLine("Priority changes: " +
                    (receipt.actions == null ? 0 : receipt.actions.changedProcesses));
                text.AppendLine("Persistent rules written: " +
                    (receipt.actions == null ? 0 : receipt.actions.persistentRules));
                text.AppendLine("Older conflicting policies restored: " +
                    receipt.restoredLegacyPolicies);
                text.AppendLine("Windows startup: " +
                    (receipt.startupEnabled ? "enabled" : "off"));
                text.AppendLine();
                text.AppendLine(receipt.persistenceScope);
                text.AppendLine(receipt.tpuStatus);
                text.AppendLine("Receipt: " + receipt.receiptPath);
                if (receipt.errors != null && receipt.errors.Count > 0)
                {
                    text.AppendLine();
                    text.AppendLine("Warnings:");
                    foreach (string error in receipt.errors.Take(8))
                        text.AppendLine("  - " + error);
                }
            }
            _details.Text = text.ToString();
            _details.SelectionStart = 0;
            _details.SelectionLength = 0;
            _count.Text = "100%   complete";
            _close.Enabled = true;
            _close.Focus();
        }

        public void Fail(Exception error)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<Exception>(Fail), error); }
                catch { }
                return;
            }
            _finished = true;
            _phase.Text = "Optimization could not finish";
            _message.Text = "No success percentage is shown because the measurement failed.";
            _details.Text = error == null ? "Unknown error." : error.ToString();
            _count.Text = _lastPercent + "%   stopped safely";
            _close.Enabled = true;
            _close.Focus();
        }

        private static bool RenderForContract(OptimizationProgressForm form,
                                              string outputPath)
        {
            try
            {
                bool wasVisible = form.Visible;
                if (!wasVisible)
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-32000, -32000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    Application.DoEvents();
                }
                form.PerformLayout();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    int nonBackground = 0;
                    int accentPixels = 0;
                    for (int y = 0; y < bitmap.Height; y += 2)
                    {
                        for (int x = 0; x < bitmap.Width; x += 2)
                        {
                            Color color = bitmap.GetPixel(x, y);
                            if (Math.Abs(color.R - Theme.Bg.R) +
                                Math.Abs(color.G - Theme.Bg.G) +
                                Math.Abs(color.B - Theme.Bg.B) > 18)
                                nonBackground++;
                            if (color.B > color.R + 20 &&
                                color.G > color.R + 20) accentPixels++;
                        }
                    }
                    bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
                    if (!wasVisible) form.Hide();
                    return nonBackground > 1200 && accentPixels > 20;
                }
            }
            catch { return false; }
        }

        internal static bool VerifyContract()
        {
            using (var form = new OptimizationProgressForm())
            {
                form.UpdateProgress(new OptimizationProgress
                {
                    percent = 25, phase = "Baseline", message = "sample", current = 2, total = 10
                });
                form.UpdateProgress(new OptimizationProgress
                {
                    percent = 10, phase = "Older update", message = "late", current = 1, total = 10
                });
                bool monotonic = form._progress.Value == 25;
                form.Complete(new OptimizationRunReceipt
                {
                    before = new ResourceMeasurement
                    {
                        cpuPercent = 50, ramPercent = 50, gpuPercent = 40, gpuValid = true
                    },
                    after = new ResourceMeasurement
                    {
                        cpuPercent = 40, ramPercent = 45, gpuPercent = 20, gpuValid = true
                    },
                    systemImpact = new OptimizationImpact
                    {
                        cpuImprovementPercent = 20, ramImprovementPercent = 10,
                        gpuMeasured = true, gpuImprovementPercent = 50, confidence = "high"
                    },
                    actions = new OptimizationReceipt { processCount = 10 },
                    persistenceScope = OptimizationWorkflow.PersistenceScope,
                    tpuStatus = OptimizationWorkflow.TpuStatus,
                    receiptPath = @"C:\Temp\receipt.json",
                    errors = new List<string>()
                });
                bool rendered = RenderForContract(form,
                    Path.Combine(Path.GetTempPath(), "pspl-optimizer-progress.png"));
                return monotonic && form._progress.Value == 100 &&
                       form._close.Enabled &&
                       rendered &&
                       form._details.Text.Contains("CPU") &&
                       form._details.Text.Contains("50.0% -> 40.0%") &&
                       form._details.Text.Contains("Windows startup: off") &&
                       form._details.Text.Contains("does not fabricate");
            }
        }
    }

    // ---------------------------------------------------------------------
    //  Details form
    // ---------------------------------------------------------------------
    internal class DetailsForm : Form
    {
        public DetailsForm(ProcRow row)
        {
            int processCount = MainForm.MemberCount(row);
            Text = "Details — " + row.Name +
                   (processCount > 1 ? "  (" + processCount + " processes)" : "  (PID " + row.Id + ")");
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = new Font("Consolas", 9f);
            ClientSize = new Size(720, 320);
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = true; MinimizeBox = false;

            var tb = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill, BackColor = Theme.Bg, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f)
            };
            Controls.Add(tb);

            var sb = new StringBuilder();
            sb.AppendLine("=== " + row.Name +
                          (processCount > 1 ? "  (" + processCount + " processes)" : "  (PID " + row.Id + ")") +
                          " ===========================");
            sb.AppendLine("Path       : " + (!String.IsNullOrEmpty(row.ExecutablePath)
                ? row.ExecutablePath
                : Safe(() => Process.GetProcessById(row.Id).MainModule.FileName)));
            sb.AppendLine("PIDs       : " + String.Join(", ", MainForm.MemberRows(row)
                .Select(r => r.Id.ToString(CultureInfo.InvariantCulture)).ToArray()));
            sb.AppendLine("CPU        : " + MainForm.FormatUsagePercent(row.Cpu) + "    Working set: " + FmtBytes(row.Mem) + "    Private: " + FmtBytes(row.Priv));
            sb.AppendLine("GPU        : " +
                          (row.GpuValid ? MainForm.FormatUsagePercent(row.Gpu) : "n/a") +
                          "    VRAM: " + FmtBytes(row.Vram));
            sb.AppendLine("Priority   : " + row.Priority + "    Affinity cores: " + row.Affinity);
            sb.AppendLine("Controls   : " + (String.IsNullOrEmpty(row.Controls) ? "none" : row.Controls));
            sb.AppendLine("Threads    : " + row.Threads);
            if (processCount == 1)
                sb.AppendLine("CommandLine: " + GetCmdLine(row.Id));
            tb.Text = sb.ToString();
        }

        private static string Safe(Func<string> f) { try { return f(); } catch { return "n/a"; } }

        private static string GetCmdLine(int pid)
        {
            try
            {
                using (var mo = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                {
                    foreach (ManagementObject m in mo.Get())
                    {
                        var v = m["CommandLine"];
                        if (v != null) return v.ToString();
                    }
                }
            }
            catch { }
            return "n/a";
        }

        public static string FmtBytes(long b)
        {
            if (b >= 1024L * 1024 * 1024 * 1024) return (b / (1024.0 * 1024 * 1024 * 1024)).ToString("N1") + " TB";
            if (b >= 1024L * 1024 * 1024) return (b / (1024.0 * 1024 * 1024)).ToString("N1") + " GB";
            if (b >= 1024L * 1024) return (b / (1024.0 * 1024)).ToString("N0") + " MB";
            if (b >= 1024) return (b / 1024.0).ToString("N0") + " KB";
            return b + " B";
        }
    }

    internal class SmoothListView : ListView
    {
        private const int LvmSetExtendedListViewStyle = 0x1036;
        private const int LvsExDoubleBuffer = 0x00010000;

        public SmoothListView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SendMessage(Handle, LvmSetExtendedListViewStyle,
                (IntPtr)LvsExDoubleBuffer, (IntPtr)LvsExDoubleBuffer);
        }
    }

    // ---------------------------------------------------------------------
    //  Main form
    // ---------------------------------------------------------------------
    internal class MainForm : Form
    {
        private readonly Sampler _sampler;
        private readonly System.Windows.Forms.Timer _timer;
        private ListView _list;
        private Font _listBoldFont;
        private Meter _mCpu, _mRam, _mGpu;
        private Label _lblInfo, _lblStatus, _lblSel;
        private CheckBox _chkBalance, _chkGpu, _chkGroupApps;
        private Button _btnCpu, _btnRam, _btnGpu, _btnCopy, _btnSelectAll;
        private Button _btnOptimize, _btnClearSearch;
        private TextBox _txtSearch;
        private string[] _searchTokens = new string[0];
        private int _filteredApplicationCount;
        private int _totalApplicationCount;
        private bool _optimizationRunning;
        private string _lastOptimizationSummary = "";

        private enum SortKey { Cpu, Ram, Gpu, Name, Pid, Vram, Priority }
        private SortKey _sort = SortKey.Cpu;
        private bool _sortAsc;
        private bool _groupApplications;
        private const int VisibleRefreshIntervalMs = 1000;
        private bool _scrollToTopOnNextRefresh = true;
        private readonly Dictionary<string, ListViewItem> _byGroup =
            new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);
        private bool _dragSelecting;
        private int _dragAnchorIndex = -1;
        private int _dragCurrentIndex = -1;
        private Point _dragPointer;
        private HashSet<string> _dragBaseSelection =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Forms.Timer _dragScrollTimer;

        private NotifyIcon _tray;
        private ContextMenuStrip _trayMenu;
        private bool _trayMinimized;
        private bool _reallyExit;
        private readonly bool _startHidden;
        private readonly EventWaitHandle _showEvent;

        public MainForm(bool enableEnforcement = true, bool startHidden = false,
                        EventWaitHandle showEvent = null, bool showTrayIcon = true)
        {
            _startHidden = startHidden;
            _showEvent = showEvent;
            _sampler = new Sampler();
            _sampler.SetInteractiveMode(!startHidden);
            _sampler.Rules = RulesStore.Load();
            _sampler.ProBalance = enableEnforcement;
            _sampler.EnforcementEnabled = enableEnforcement;
            _sampler.Start();   // begin sampling immediately so data is ready while the UI builds
            Text = "PSProcLasso — Real-Time System Monitor";
            try { Icon = TrayIcon(); } catch { }   // custom window icon (same art as the tray)
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1080, 640);
            MinimumSize = new Size(860, 480);
            KeyPreview = true;
            if (_startHidden)
            {
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
                Opacity = 0;
            }

            BuildUi();
            BuildTray(showTrayIcon);
            // The sampler still collects CPU/RAM twice per second, while the visible
            // table commits one coherent latest frame per second to avoid visual churn.
            _timer = new System.Windows.Forms.Timer { Interval = VisibleRefreshIntervalMs };
            _timer.Tick += (s, e) =>
            {
                _sampler.EnsureAlive();   // self-healing: never let sampling stop
                if (_showEvent != null && _showEvent.WaitOne(0)) ShowWindow();
                // Background enforcement still samples and applies rules continuously,
                // but rebuilding hundreds of hidden rows twice a second only wastes CPU.
                if (Visible) RefreshAll();
            };
            _timer.Start();
            Shown += (s, e) =>
            {
                RefreshAll();
                if (_startHidden)
                {
                    _trayMinimized = true;
                    Hide();
                }
            };
            FormClosed += (s, e) =>
            {
                _timer.Stop();
                if (_dragScrollTimer != null) _dragScrollTimer.Stop();
                _sampler.Stop();
                if (_listBoldFont != null) { _listBoldFont.Dispose(); _listBoldFont = null; }
            };
        }

        // -----------------------------------------------------------------
        //  System tray: minimize-to-tray + right-click quick actions
        // -----------------------------------------------------------------
        private void BuildTray(bool visible)
        {
            _tray = new NotifyIcon { Text = "PSProcLasso — enforcing rules…", Visible = visible, Icon = TrayIcon() };
            _trayMenu = new ContextMenuStrip { Renderer = new DarkRenderer(), BackColor = Theme.Panel, ForeColor = Theme.Text };
            _trayMenu.ForeColor = Theme.Text;
            _trayMenu.Font = new Font("Segoe UI", 9f);
            _trayMenu.Opening += (s, e) => RebuildTrayMenu();   // fresh checkmarks every open
            RebuildTrayMenu();
            _tray.ContextMenuStrip = _trayMenu;
            _tray.DoubleClick += (s, e) => ShowWindow();

            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized) HideToTray();
            };
            // X keeps the app enforcing in the tray; use the tray menu's Exit to quit.
            FormClosing += (s, e) =>
            {
                if (!_reallyExit)
                {
                    e.Cancel = true;
                    HideToTray();
                }
            };
        }

        private static Icon TrayIcon()
        {
            // The same icon is embedded into the executable for Explorer/taskbar
            // pinning, then reused here so every Windows surface stays consistent.
            try
            {
                using (var embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    if (embedded != null) return (Icon)embedded.Clone();
            }
            catch { }

            // Last-resort fallback for unusual hosts that cannot read PE resources.
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (var bg = new SolidBrush(Color.FromArgb(30, 36, 48)))
                    g.FillRectangle(bg, 0, 0, 16, 16);
                using (var bar = new SolidBrush(Color.FromArgb(123, 201, 111)))
                {
                    g.FillRectangle(bar, 3, 9, 3, 4);
                    g.FillRectangle(bar, 7, 6, 3, 7);
                    g.FillRectangle(bar, 11, 3, 3, 10);
                }
            }
            IntPtr h = bmp.GetHicon();
            using (var ic = Icon.FromHandle(h))
            {
                Icon clone = (Icon)ic.Clone();
                Native.DestroyIcon(h);
                bmp.Dispose();
                return clone;
            }
        }

        private void HideToTray()
        {
            if (_trayMinimized) return;
            _trayMinimized = true;
            _sampler.SetInteractiveMode(false);
            Hide();
            _tray.ShowBalloonTip(2500, "PSProcLasso", "Still running — limits, watchdog and ProBalance are enforcing.\nRight-click the tray icon for quick actions.", ToolTipIcon.Info);
        }

        private void ShowWindow()
        {
            _trayMinimized = false;
            _sampler.SetInteractiveMode(true);
            Opacity = 1;
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void RebuildTrayMenu()
        {
            if (_trayMenu == null) return;
            _trayMenu.Items.Clear();

            _trayMenu.Items.Add("Show window", null, (s, e) => ShowWindow());
            _trayMenu.Items.Add("Hide window", null, (s, e) => HideToTray());
            _trayMenu.Items.Add(new ToolStripSeparator());

            var sort = new ToolStripMenuItem("Sort by"); sort.ForeColor = Theme.Text;
            sort.DropDownItems.Add("CPU (1)", null, (s, e) => SelectSortDescending(SortKey.Cpu));
            sort.DropDownItems.Add("RAM (2)", null, (s, e) => SelectSortDescending(SortKey.Ram));
            sort.DropDownItems.Add("GPU (3)", null, (s, e) => SelectSortDescending(SortKey.Gpu));
            sort.DropDownItems.Add("VRAM", null, (s, e) => SelectSortDescending(SortKey.Vram));
            sort.DropDownItems.Add("Name", null, (s, e) => SelectSortDescending(SortKey.Name));
            sort.DropDownItems.Add("PID", null, (s, e) => SelectSortDescending(SortKey.Pid));
            sort.DropDown.BackColor = Theme.Panel; sort.DropDown.ForeColor = Theme.Text;
            _trayMenu.Items.Add(sort);

            var presets = new ToolStripMenuItem("Limit presets"); presets.ForeColor = Theme.Text;
            presets.DropDownItems.Add("Cap top CPU process at 50%", null, (s, e) => ApplyPreset(SortKey.Cpu, 0));
            presets.DropDownItems.Add("Cap top GPU process at 50%", null, (s, e) => ApplyPreset(SortKey.Gpu, 1));
            presets.DropDownItems.Add("Trim top RAM process working set", null, (s, e) => ApplyPreset(SortKey.Ram, 2));
            presets.DropDownItems.Add("Remove limits from top CPU process", null, (s, e) => ApplyPreset(SortKey.Cpu, 3));
            presets.DropDown.BackColor = Theme.Panel; presets.DropDown.ForeColor = Theme.Text;
            _trayMenu.Items.Add(presets);

            var wd = new ToolStripMenuItem("Watchdog"); wd.ForeColor = Theme.Text;
            bool anyW = false;
            foreach (var kv in _sampler.Rules)
            {
                if (!kv.Value.watchdog) continue;
                anyW = true;
                var name = kv.Key;
                var it = new ToolStripMenuItem(name + "  —  ON (click to turn OFF)") { Checked = true };
                it.ForeColor = Theme.Text;
                it.Click += (s, e) => ToggleWatchdogRule(name);
                wd.DropDownItems.Add(it);
            }
            if (!anyW)
            {
                var none = new ToolStripMenuItem("(no watchdog rules)") { Enabled = false };
                wd.DropDownItems.Add(none);
            }
            wd.DropDown.BackColor = Theme.Panel; wd.DropDown.ForeColor = Theme.Text;
            _trayMenu.Items.Add(wd);

            var pb = new ToolStripMenuItem("ProBalance") { Checked = _sampler.ProBalance, CheckOnClick = true };
            pb.ForeColor = Theme.Text;
            pb.CheckedChanged += (s, e) => _sampler.ProBalance = pb.Checked;
            _trayMenu.Items.Add(pb);

            var gpu = new ToolStripMenuItem("GPU sampling") { Checked = _sampler.GpuOn, CheckOnClick = true };
            gpu.ForeColor = Theme.Text;
            gpu.CheckedChanged += (s, e) => _sampler.GpuOn = gpu.Checked;
            _trayMenu.Items.Add(gpu);

            var startup = new ToolStripMenuItem("Start silently with Windows")
            {
                Checked = StartupManager.IsEnabled()
            };
            startup.ForeColor = Theme.Text;
            startup.Click += (s, e) =>
            {
                string error;
                bool wanted = !StartupManager.IsEnabled();
                bool ok = wanted ? StartupManager.Enable(out error) : StartupManager.Disable(out error);
                startup.Checked = StartupManager.IsEnabled();
                if (!ok) new DarkBox("Windows startup", error, false).ShowDialog(this);
                else _tray.ShowBalloonTip(1800, "PSProcLasso",
                    startup.Checked
                        ? "Silent rule enforcement will start automatically after every Windows sign-in."
                        : "Automatic Windows startup is off.",
                    ToolTipIcon.Info);
            };
            _trayMenu.Items.Add(startup);

            _trayMenu.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem("Exit"); exit.ForeColor = Theme.Red;
            exit.Click += (s, e) =>
            {
                _reallyExit = true;
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                Application.Exit();
            };
            _trayMenu.Items.Add(exit);
        }

        private void ApplyPreset(SortKey key, int kind)
        {
            var rows = BuildVisibleRows(_sampler.Snap.Rows);
            if (rows == null || rows.Count == 0) return;
            ProcRow top = null;
            foreach (var r in rows)
            {
                if (top == null) { top = r; continue; }
                bool better = false;
                switch (key)
                {
                    case SortKey.Cpu: better = r.Cpu > top.Cpu; break;
                    case SortKey.Ram: better = r.Mem > top.Mem; break;
                    case SortKey.Gpu: better = r.Gpu > top.Gpu; break;
                }
                if (better) top = r;
            }
            if (top == null || top.Id <= 0) return;
            switch (kind)
            {
                case 0: _sampler.SetCpuLimit(top, 50); break;
                case 1: _sampler.SetGpuLimit(top, 50); break;
                case 2:
                    long mb = Math.Max(64, (long)(top.Mem / (1024.0 * 1024) * 0.75));
                    _sampler.SetRamLimit(top, mb);
                    break;
                case 3: _sampler.RemoveLimits(top); break;
            }
            _tray.ShowBalloonTip(1800, "PSProcLasso", "Limit applied to " + top.Name +
                                 (MemberCount(top) > 1
                                     ? " (" + MemberCount(top) + " related processes)."
                                     : " (PID " + top.Id + ")."), ToolTipIcon.Info);
        }

        private void ToggleWatchdogRule(string name)
        {
            if (!_sampler.ToggleWatchdog(name)) return;
            Rule rule;
            if (!_sampler.Rules.TryGetValue(name, out rule)) return;
            _tray.ShowBalloonTip(1800, "PSProcLasso", "Watchdog " +
                                 (rule.watchdog ? "ON" : "OFF") + " for " + name + ".",
                                 ToolTipIcon.Info);
        }

        // --- headless test hooks (used by --uicheck) ---
        internal Sampler SamplerForTest { get { return _sampler; } }

        internal void RefreshAllForTest() { RefreshAll(); }
        internal void RefreshListOnce() { RefreshList(); }
        internal void SortForTest(int key) { _sort = (SortKey)key; _sortAsc = false; }
        internal string SortForTestGet { get { return _sort.ToString(); } }
        internal bool SortAscendingForTest { get { return _sortAsc; } }
        internal bool GroupApplicationsForTest { get { return _groupApplications; } }
        internal string StatusTextForTest { get { return _lblStatus == null ? "" : _lblStatus.Text; } }
        internal bool VisibleRowsAreSinglePidForTest
        {
            get
            {
                if (_list == null || _list.Items.Count == 0) return false;
                foreach (ListViewItem item in _list.Items)
                {
                    var row = item.Tag as ProcRow;
                    if (row == null || row.Id <= 0 || row.Members == null ||
                        row.Members.Count != 1 || row.Members[0].Id != row.Id ||
                        String.IsNullOrEmpty(row.GroupKey) ||
                        !row.GroupKey.StartsWith("process:", StringComparison.Ordinal))
                        return false;
                }
                return true;
            }
        }
        internal void SetSearchForTest(string query)
        {
            _txtSearch.Text = query ?? "";
            RefreshList();
        }
        internal int VisibleRowCountForTest { get { return _list.Items.Count; } }
        internal string[] VisibleNamesForTest
        {
            get
            {
                return _list.Items.Cast<ListViewItem>()
                    .Where(x => x.Tag is ProcRow)
                    .Select(x => ((ProcRow)x.Tag).Name).ToArray();
            }
        }
        internal string SearchTextForTest { get { return _txtSearch.Text; } }
        internal string FirstRowSearchQueryForTest
        {
            get
            {
                if (_list.Items.Count == 0 || !(_list.Items[0].Tag is ProcRow)) return "";
                var row = (ProcRow)_list.Items[0].Tag;
                var member = MemberRows(row).FirstOrDefault();
                return member == null ? row.Name : row.Name + " " +
                    member.Id.ToString(CultureInfo.InvariantCulture);
            }
        }
        internal string CurrentProcessSearchQueryForTest
        {
            get
            {
                using (var current = Process.GetCurrentProcess())
                    return current.ProcessName + " " +
                        current.Id.ToString(CultureInfo.InvariantCulture);
            }
        }
        internal bool VisibleTopMatchesSortForTest()
        {
            if (_list.Items.Count == 0 || !(_list.Items[0].Tag is ProcRow)) return false;
            var expected = new List<ProcRow>();
            foreach (ListViewItem item in _list.Items)
                if (item.Tag is ProcRow) expected.Add((ProcRow)item.Tag);
            expected.Sort(CompareRows);
            return expected.Count > 0 && ((ProcRow)_list.Items[0].Tag).Id == expected[0].Id;
        }
        internal void ClickSortButton(int i)
        {
            ApplySortButton(i == 0 ? SortKey.Cpu : i == 1 ? SortKey.Ram : SortKey.Gpu);
        }

        internal bool SelectDragRangeForTest(int anchor, int current)
        {
            _dragBaseSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return ApplyDragRangeSelection(anchor, current);
        }

        internal string[] TrayMenuTextsForTest()
        {
            RebuildTrayMenu();
            var list = new List<string>();
            foreach (ToolStripItem it in _trayMenu.Items)
            {
                if (it is ToolStripSeparator) continue;
                var mi = it as ToolStripMenuItem;
                list.Add(mi == null ? it.Text : mi.Text + " (" + mi.DropDownItems.Count + ")");
            }
            return list.ToArray();
        }

        internal bool StartupMenuCheckedForTest()
        {
            RebuildTrayMenu();
            foreach (ToolStripItem item in _trayMenu.Items)
            {
                var menu = item as ToolStripMenuItem;
                if (menu != null && menu.Text == "Start silently with Windows")
                    return menu.Checked;
            }
            return true;
        }

        internal void ShutdownForTest()
        {
            _reallyExit = true;
            _timer.Stop();
            _sampler.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        }

        // -----------------------------------------------------------------
        private void BuildUi()
        {
            // --- top meters ---
            var top = new GradientPanel { Height = 114, DrawBottomLine = true };
            _mCpu = new Meter { Caption = "CPU", Left = 12, Top = 6, Width = 330 };
            _mRam = new Meter { Caption = "RAM", Left = 356, Top = 6, Width = 330 };
            _mGpu = new Meter { Caption = "GPU", Left = 700, Top = 6, Width = 330 };
            _mCpu.AccessibleName = "Total CPU usage";
            _mRam.AccessibleName = "Total RAM usage";
            _mGpu.AccessibleName = "Total GPU usage";
            top.Controls.Add(_mCpu); top.Controls.Add(_mRam); top.Controls.Add(_mGpu);

            _lblInfo = new Label { AutoSize = true, Left = 12, Top = 48, ForeColor = Theme.Dim, BackColor = Color.Transparent, Font = new Font("Segoe UI", 8.5f) };
            top.Controls.Add(_lblInfo);

            _chkBalance = new CheckBox { Text = "ProBalance", Left = 700, Top = 46, Width = 110, ForeColor = Theme.Text, BackColor = Color.Transparent };
            _chkBalance.Checked = _sampler.ProBalance;
            _chkBalance.CheckedChanged += (s, e) => _sampler.ProBalance = _chkBalance.Checked;
            _chkGpu = new CheckBox { Text = "GPU sampling", Left = 820, Top = 46, Width = 130, ForeColor = Theme.Text, BackColor = Color.Transparent };
            _chkGpu.Checked = _sampler.GpuOn;
            _chkGpu.CheckedChanged += (s, e) => _sampler.GpuOn = _chkGpu.Checked;
            var btnHelp = new Button { Text = "?", Left = 1030, Top = 42, Width = 30, Height = 26, FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Accent };
            btnHelp.FlatAppearance.BorderColor = Theme.Header;
            btnHelp.Click += (s, e) => ShowHelp();
            top.Controls.Add(_chkBalance); top.Controls.Add(_chkGpu); top.Controls.Add(btnHelp);

            // --- one-click sort buttons: highest usage first ---
            var sortLbl = new Label { Text = "Sort:", Left = 12, Top = 92, Width = 40, AutoSize = false, Height = 20, ForeColor = Theme.Dim, BackColor = Color.Transparent, Font = new Font("Segoe UI", 8.5f) };
            top.Controls.Add(sortLbl);
            _btnCpu = MakeSortButton("CPU", 52, SortKey.Cpu);
            _btnRam = MakeSortButton("RAM", 120, SortKey.Ram);
            _btnGpu = MakeSortButton("GPU", 188, SortKey.Gpu);
            top.Controls.Add(_btnCpu); top.Controls.Add(_btnRam); top.Controls.Add(_btnGpu);
            _chkGroupApps = new CheckBox
            {
                Text = "Group apps", Left = 266, Top = 90, Width = 100, Height = 20,
                ForeColor = Theme.Text, BackColor = Color.Transparent,
                Checked = false, AccessibleName = "Group related processes by application"
            };
            _chkGroupApps.CheckedChanged += (s, e) =>
            {
                _groupApplications = _chkGroupApps.Checked;
                _byGroup.Clear();
                _dispOrder.Clear();
                _list.BeginUpdate();
                try { _list.Items.Clear(); }
                finally { _list.EndUpdate(); }
                _scrollToTopOnNextRefresh = true;
                RefreshList();
            };
            top.Controls.Add(_chkGroupApps);

            _btnCopy = MakeCommandButton("COPY", 374, 82);
            _btnCopy.Click += (s, e) => CopySelectedRows();
            _btnSelectAll = MakeCommandButton("SELECT ALL", 464, 104);
            _btnSelectAll.Click += (s, e) => SelectAllRows();
            _btnOptimize = MakeCommandButton("OPTIMIZE", 576, 90);
            _btnOptimize.Click += (s, e) => RunSafeOptimizationFromUi();

            var searchLabel = new Label
            {
                Text = "SEARCH", Left = 678, Top = 92, Width = 52, Height = 20,
                ForeColor = Theme.Dim, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold)
            };
            _txtSearch = new TextBox
            {
                Left = 734, Top = 88, Width = 258, Height = 24,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Panel, ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 9f),
                AccessibleName = "Search processes"
            };
            _txtSearch.TextChanged += (s, e) =>
            {
                SetSearchQuery(_txtSearch.Text);
                RefreshList();
            };
            _txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    ClearSearch();
                    _list.Focus();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Down)
                {
                    FocusFirstVisibleRow();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            _btnClearSearch = MakeCommandButton("X", 998, 28);
            _btnClearSearch.AccessibleName = "Clear search";
            _btnClearSearch.Click += (s, e) => ClearSearch();
            var tips = new ToolTip();
            tips.SetToolTip(_txtSearch, "Filter by application, PID, executable path, priority or controls");
            tips.SetToolTip(_btnClearSearch, "Clear search");
            tips.SetToolTip(_btnOptimize,
                "Measure baseline, apply safe reversible changes, then report CPU/RAM/GPU impact");
            tips.SetToolTip(_chkGroupApps,
                "Off shows one independently measured PID per row; on combines related processes");

            top.Controls.Add(_btnCopy); top.Controls.Add(_btnSelectAll);
            top.Controls.Add(_btnOptimize); top.Controls.Add(searchLabel);
            top.Controls.Add(_txtSearch); top.Controls.Add(_btnClearSearch);
            Controls.Add(top);

            // --- list ---
            _list = new SmoothListView
            {
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                OwnerDraw = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Bg,
                ForeColor = Theme.Text,
                Font = new Font("Consolas", 9.5f),
                MultiSelect = true,
                ShowItemToolTips = true
            };
            _list.Columns.Add("PID / COUNT", 88, HorizontalAlignment.Right);
            _list.Columns.Add("NAME", 210, HorizontalAlignment.Left);
            _list.Columns.Add("CPU%", 62, HorizontalAlignment.Right);
            _list.Columns.Add("RAM %", 132, HorizontalAlignment.Right);
            _list.Columns.Add("GPU%", 62, HorizontalAlignment.Right);
            _list.Columns.Add("VRAM", 100, HorizontalAlignment.Right);
            _list.Columns.Add("PRIORITY", 112, HorizontalAlignment.Left);
            _list.Columns.Add("AFFINITY", 96, HorizontalAlignment.Left);
            _listBoldFont = new Font(_list.Font, FontStyle.Bold);

            _list.DrawColumnHeader += List_DrawColumnHeader;
            _list.DrawSubItem += List_DrawSubItem;
            _list.ColumnClick += (s, e) =>
            {
                var map = new[] { SortKey.Pid, SortKey.Name, SortKey.Cpu, SortKey.Ram, SortKey.Gpu, SortKey.Vram, SortKey.Priority, SortKey.Name };
                if (e.Column >= 0 && e.Column < map.Length)
                {
                    if (_sort == map[e.Column]) _sortAsc = !_sortAsc;
                    else { _sort = map[e.Column]; _sortAsc = false; }
                    RefreshList();
                }
            };
            _list.DoubleClick += (s, e) => ShowDetailsForSelection();
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete)
                {
                    TryKill();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Enter) ShowDetailsForSelection();
                else if (e.Control && e.KeyCode == Keys.C)
                {
                    CopySelectedRows();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.Control && e.KeyCode == Keys.A)
                {
                    SelectAllRows();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.Control && e.KeyCode == Keys.F)
                {
                    FocusSearch();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            _list.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right) ShowRowMenu(e);
            };
            _list.MouseDown += List_MouseDownForRange;
            _list.MouseMove += List_MouseMoveForRange;
            _list.MouseUp += List_MouseUpForRange;
            _list.MouseWheel += (s, e) =>
            {
                if (!_dragSelecting) return;
                _dragPointer = e.Location;
                BeginInvoke(new Action(() =>
                {
                    if (_dragSelecting) ContinueDragSelectionAtPointer();
                }));
            };
            _list.MouseCaptureChanged += (s, e) =>
            {
                if (!_list.Capture) EndDragRangeSelection();
            };
            _list.ItemSelectionChanged += (s, e) =>
            {
                if (!_dragSelecting) UpdateSelectedInfo();
            };
            _list.Resize += (s, e) => ResizeProcessColumns();
            Controls.Add(_list);

            // --- status bar ---
            var status = new GradientPanel { Height = 26, ColorTop = Theme.Panel2, ColorBottom = Theme.Header };
            _lblStatus = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Dim, Font = new Font("Consolas", 8.5f), Padding = new Padding(8, 0, 0, 0), BackColor = Theme.Header };
            status.Controls.Add(_lblStatus);
            Controls.Add(status);

            _lblSel = new Label { AutoSize = false, Left = 12, Top = 118, Height = 22, ForeColor = Theme.Dim, Font = new Font("Consolas", 8.5f), BackColor = Theme.Bg };
            Controls.Add(_lblSel);
            _lblSel.BringToFront();

            // Explicit bounds avoid WinForms docking/z-order ambiguity and make every
            // band visible at all supported window sizes.
            Action layout = () =>
            {
                int statusH = 26;
                int selectionH = 26;
                top.SetBounds(0, 0, ClientSize.Width, 114);
                _lblSel.SetBounds(12, top.Bottom, Math.Max(0, ClientSize.Width - 24), selectionH);
                _list.SetBounds(0, top.Bottom + selectionH, ClientSize.Width,
                    Math.Max(80, ClientSize.Height - top.Height - selectionH - statusH));
                status.SetBounds(0, ClientSize.Height - statusH, ClientSize.Width, statusH);
                ResizeProcessColumns();
            };
            ClientSizeChanged += (s, e) => layout();
            top.Resize += (s, e) =>
            {
                int gap = 14;
                int meterW = Math.Max(210, (top.ClientSize.Width - 4 * gap) / 3);
                _mCpu.SetBounds(gap, 6, meterW, _mCpu.Height);
                _mRam.SetBounds(gap * 2 + meterW, 6, meterW, _mRam.Height);
                _mGpu.SetBounds(gap * 3 + meterW * 2, 6, meterW, _mGpu.Height);
                btnHelp.Left = Math.Max(8, top.ClientSize.Width - btnHelp.Width - 12);
                _chkGpu.Left = Math.Max(500, btnHelp.Left - _chkGpu.Width - 10);
                _chkBalance.Left = Math.Max(380, _chkGpu.Left - _chkBalance.Width - 10);
                int searchRight = btnHelp.Left - 10;
                _btnClearSearch.Left = Math.Max(792, searchRight - _btnClearSearch.Width);
                _txtSearch.Width = Math.Max(112, _btnClearSearch.Left - _txtSearch.Left - 6);
            };
            layout();

            // keyboard shortcuts
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.D1) SelectSortDescending(SortKey.Cpu);
                else if (e.KeyCode == Keys.D2) SelectSortDescending(SortKey.Ram);
                else if (e.KeyCode == Keys.D3) SelectSortDescending(SortKey.Gpu);
                else if (e.KeyCode == Keys.F5) RefreshAll();
                else if (e.Control && e.KeyCode == Keys.F)
                {
                    FocusSearch();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Escape && _searchTokens.Length > 0)
                {
                    ClearSearch();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
        }

        private void ResizeProcessColumns()
        {
            if (_list == null || _list.Columns.Count < 8) return;
            int fixedWidth = 88 + 62 + 132 + 62 + 100 + 112 + 96;
            _list.Columns[1].Width = Math.Max(180, _list.ClientSize.Width - fixedWidth - 24);
        }

        // -----------------------------------------------------------------
        private Button MakeSortButton(string text, int left, SortKey key)
        {
            var b = new Button
            {
                Text = text, Left = left, Top = 88, Width = 62, Height = 24,
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Dim,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Theme.Header;
            b.Click += (s, e) => ApplySortButton(key);
            return b;
        }

        private Button MakeCommandButton(string text, int left, int width)
        {
            var b = new Button
            {
                Text = text, Left = left, Top = 88, Width = width, Height = 24,
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Theme.Header;
            return b;
        }

        private void SetSearchQuery(string query)
        {
            _searchTokens = (query ?? "")
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            _scrollToTopOnNextRefresh = true;
        }

        private void FocusSearch()
        {
            if (_txtSearch == null) return;
            _txtSearch.Focus();
            _txtSearch.SelectAll();
        }

        private void ClearSearch()
        {
            if (_txtSearch == null) return;
            if (_txtSearch.Text.Length == 0)
            {
                SetSearchQuery("");
                RefreshList();
                return;
            }
            _txtSearch.Clear();
        }

        private void FocusFirstVisibleRow()
        {
            if (_list == null || _list.Items.Count == 0) return;
            _list.Focus();
            if (_list.SelectedItems.Count == 0)
                _list.Items[0].Selected = true;
            _list.Items[0].Focused = true;
            _list.EnsureVisible(0);
        }

        private static bool MatchesSearch(ProcRow row, IEnumerable<string> tokens)
        {
            if (row == null) return false;
            var wanted = tokens == null ? new string[0] :
                tokens.Where(x => !String.IsNullOrWhiteSpace(x)).ToArray();
            if (wanted.Length == 0) return true;

            var text = new StringBuilder();
            text.Append(row.Name).Append(' ')
                .Append(row.ExecutablePath).Append(' ')
                .Append(row.GroupKey).Append(' ')
                .Append(row.Priority).Append(' ')
                .Append(row.Affinity).Append(' ')
                .Append(row.Controls).Append(' ');
            foreach (var member in MemberRows(row))
            {
                text.Append(member.Name).Append(' ')
                    .Append(member.Id.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(member.ExecutablePath).Append(' ')
                    .Append(member.Priority).Append(' ')
                    .Append(member.Controls).Append(' ');
            }
            string haystack = text.ToString();
            foreach (string token in wanted)
                if (haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            return true;
        }

        private static List<ProcRow> FilterApplicationRows(
            IEnumerable<ProcRow> rows, IEnumerable<string> tokens)
        {
            return (rows ?? new List<ProcRow>())
                .Where(r => MatchesSearch(r, tokens)).ToList();
        }

        internal static bool VerifySearchFilterContract()
        {
            var chrome = new ProcRow
            {
                Id = 100,
                Name = "chrome",
                ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                GroupKey = "chrome",
                Priority = "Normal",
                Members = new List<ProcRow>
                {
                    new ProcRow { Id = 100, Name = "chrome", ExecutablePath =
                        @"C:\Program Files\Google\Chrome\Application\chrome.exe" },
                    new ProcRow { Id = 101, Name = "chrome", ExecutablePath =
                        @"C:\Program Files\Google\Chrome\Application\chrome.exe" }
                }
            };
            var wsl = new ProcRow
            {
                Id = 200, Name = "Windows Subsystem for Linux",
                ExecutablePath = @"C:\Windows\System32\wsl.exe",
                GroupKey = "family:wsl", Priority = "BelowNormal",
                Members = new List<ProcRow>
                {
                    new ProcRow { Id = 200, Name = "wsl", ExecutablePath =
                        @"C:\Windows\System32\wsl.exe" },
                    new ProcRow { Id = 201, Name = "vmmemWSL" }
                }
            };
            var rows = new[] { chrome, wsl };
            return FilterApplicationRows(rows, new[] { "chrome" }).Count == 1 &&
                   FilterApplicationRows(rows, new[] { "101" }).Single() == chrome &&
                   FilterApplicationRows(rows, new[] { "google", "100" }).Single() == chrome &&
                   FilterApplicationRows(rows, new[] { "vmmem" }).Single() == wsl &&
                   FilterApplicationRows(rows, new[] { "below" }).Single() == wsl &&
                   FilterApplicationRows(rows, new[] { "missing" }).Count == 0 &&
                   FilterApplicationRows(rows, new string[0]).Count == 2;
        }

        private void RunSafeOptimizationFromUi()
        {
            if (_optimizationRunning) return;
            string message =
                "Measure a stable baseline, review every observed process, remove the older " +
                "conflicting app-owned boost policy, and apply only reversible safe changes?\n\n" +
                "No process will be closed. RAM will not be force-trimmed, GPU will not be " +
                "suspended, and critical, visible, AI, and externally managed processes stay protected.";
            if (new DarkBox("Measured safe optimization", message, true).ShowDialog(this) !=
                DialogResult.Yes) return;

            _optimizationRunning = true;
            _btnOptimize.Enabled = false;
            _btnOptimize.Text = "WORKING...";
            OptimizationRunReceipt result = null;
            Exception failure = null;
            using (var progress = new OptimizationProgressForm())
            {
                progress.Shown += (s, e) =>
                {
                    var worker = new Thread(delegate()
                    {
                        try
                        {
                            result = OptimizationWorkflow.Run(
                                _sampler, progress.UpdateProgress);
                            progress.Complete(result);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                            progress.Fail(ex);
                        }
                    });
                    worker.IsBackground = true;
                    worker.Priority = ThreadPriority.AboveNormal;
                    worker.Start();
                };
                progress.ShowDialog(this);
            }

            _optimizationRunning = false;
            _btnOptimize.Enabled = true;
            _btnOptimize.Text = "OPTIMIZE";
            _sampler.Rules = RulesStore.Load();
            if (result != null && result.systemImpact != null)
            {
                string gpu = result.systemImpact.gpuMeasured
                    ? ", GPU " + OptimizationWorkflow.FormatSignedChange(
                        result.systemImpact.gpuImprovementPercent)
                    : ", GPU not comparable";
                _lastOptimizationSummary =
                    "Measured: CPU " + OptimizationWorkflow.FormatSignedChange(
                        result.systemImpact.cpuImprovementPercent) +
                    ", RAM " + OptimizationWorkflow.FormatSignedChange(
                        result.systemImpact.ramImprovementPercent) + gpu +
                    " (" + result.systemImpact.confidence + " confidence)";
                _tray.ShowBalloonTip(3500, "PSProcLasso",
                    _lastOptimizationSummary,
                    result.errors == null || result.errors.Count == 0
                        ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            else if (failure != null)
            {
                _lastOptimizationSummary =
                    "Optimization stopped safely: " + failure.Message;
            }
            RefreshAll();
        }

        private void ApplySortButton(SortKey key)
        {
            // Resource buttons are promises, not direction toggles: choosing CPU, RAM,
            // or GPU must always put the highest current consumers first. Column headers
            // remain available when an explicit ascending view is wanted.
            SelectSortDescending(key);
        }

        private void SelectSortDescending(SortKey key)
        {
            _sort = key;
            _sortAsc = false;
            _scrollToTopOnNextRefresh = true;
            RefreshList();
            UpdateSortButtons();
        }

        // Highlights the button of the currently active sort metric (kept in sync with
        // column clicks, the 1/2/3 keys, and the tray sort menu via RefreshAll).
        private void UpdateSortButtons()
        {
            if (_btnCpu == null) return;
            _btnCpu.ForeColor = _sort == SortKey.Cpu ? Theme.Accent : Theme.Dim;
            _btnCpu.BackColor = _sort == SortKey.Cpu ? Theme.Panel2 : Theme.Panel;
            _btnCpu.FlatAppearance.BorderColor = _sort == SortKey.Cpu ? Theme.Accent : Theme.Header;
            _btnRam.ForeColor = _sort == SortKey.Ram ? Theme.Accent : Theme.Dim;
            _btnRam.BackColor = _sort == SortKey.Ram ? Theme.Panel2 : Theme.Panel;
            _btnRam.FlatAppearance.BorderColor = _sort == SortKey.Ram ? Theme.Accent : Theme.Header;
            _btnGpu.ForeColor = _sort == SortKey.Gpu ? Theme.Accent : Theme.Dim;
            _btnGpu.BackColor = _sort == SortKey.Gpu ? Theme.Panel2 : Theme.Panel;
            _btnGpu.FlatAppearance.BorderColor = _sort == SortKey.Gpu ? Theme.Accent : Theme.Header;
        }

        // -----------------------------------------------------------------
        private void RefreshAll()
        {
            try
            {
                var s = _sampler.Snap;
                double memPct = s.RamTotal > 0 ? s.RamUsed * 100.0 / s.RamTotal : 0;
                UpdateMeter(_mCpu, s.TotalCpu, s.TotalCpu.ToString("N1") + "%");
                UpdateMeter(_mRam, memPct, memPct.ToString("N0") + "%  " +
                            DetailsForm.FmtBytes(s.RamUsed) + " / " + DetailsForm.FmtBytes(s.RamTotal));
                bool gpuFresh = s.GpuValid && _sampler.GpuFresh;
                string gpuState = !_sampler.GpuOn ? "off" :
                                  gpuFresh ? s.GpuPct.ToString("N1") + "%" :
                                  _sampler.GpuDataTick > 0 ? "reconnecting..." : "initializing...";
                UpdateMeter(_mGpu, gpuFresh ? s.GpuPct : 0, gpuState);

                int applicationCount = BuildApplicationRows(s.Rows).Count;
                _lblInfo.Text = "Available " + DetailsForm.FmtBytes((long)(s.AvailMB * 1024 * 1024)) +
                                "   ·   Standby " + DetailsForm.FmtBytes(s.Standby) +
                                "   ·   " + (gpuFresh
                                    ? "VRAM " + DetailsForm.FmtBytes(s.VramUsed) + (s.VramTotal > 0 ? " / " + DetailsForm.FmtBytes(s.VramTotal) : "")
                                    : !_sampler.GpuOn ? "GPU sampling off" :
                                      _sampler.GpuDataTick > 0 ? "GPU reconnecting..." : "GPU initializing...") +
                                "   ·   " + s.ProcessCount + " processes in " + applicationCount +
                                " apps   ·   " + DateTime.Now.ToString("HH:mm:ss");

                RefreshList();
                UpdateSortButtons();

                var errs = _sampler.Errors;
                string errTxt = errs.Count > 0 ? "   [!] " + string.Join("  |  ", errs) : "";
                string viewNoun = _groupApplications ? "apps" : "processes";
                string filterTxt = _searchTokens.Length == 0 ? "" :
                    "   Search: " + _filteredApplicationCount + "/" +
                    _totalApplicationCount + " " + viewNoun;
                _lblStatus.Text = "Sort: " + _sort +
                    (_sortAsc ? "  LOWEST FIRST ▲" : "  HIGHEST FIRST ▼") +
                    "   View: " + (_groupApplications ? "APPLICATIONS" : "PROCESSES") +
                    (gpuFresh ? "   GPU age " + _sampler.GpuDataAgeMs + " ms" : "") +
                    filterTxt +
                    (String.IsNullOrWhiteSpace(_lastOptimizationSummary)
                        ? "" : "   " + _lastOptimizationSummary) +
                    errTxt;
            }
            catch { }
        }

        private static void UpdateMeter(Meter meter, double percent, string value)
        {
            if (meter.Percent == percent && String.Equals(meter.Value, value, StringComparison.Ordinal))
                return;
            meter.Percent = percent;
            meter.Value = value;
            meter.AccessibleDescription = value;
            meter.Invalidate();
        }

        internal static List<ProcRow> BuildApplicationRows(IEnumerable<ProcRow> source)
        {
            var result = new List<ProcRow>();
            if (source == null) return result;

            var groups = source.Where(r => r != null && r.Id > 0 && !String.IsNullOrEmpty(r.Name))
                .GroupBy(r => String.IsNullOrEmpty(r.GroupKey)
                    ? Sampler.ApplicationGroupKey(r.Name, r.ExecutablePath)
                    : r.GroupKey, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var members = group.OrderBy(r => r.Id).ToList();
                if (members.Count == 0) continue;
                var first = members[0];
                var row = new ProcRow
                {
                    Id = first.Id,
                    StartTicks = first.StartTicks,
                    Name = Sampler.ApplicationDisplayName(first.Name),
                    ExecutablePath = members.Select(r => r.ExecutablePath)
                        .FirstOrDefault(x => !String.IsNullOrWhiteSpace(x)) ?? "",
                    GroupKey = group.Key,
                    Members = members,
                    Cpu = Math.Min(100, members.Sum(r => Math.Max(0, r.Cpu))),
                    Mem = members.Sum(r => Math.Max(0, r.Mem)),
                    Priv = members.Sum(r => Math.Max(0, r.Priv)),
                    Vram = members.Sum(r => Math.Max(0, r.Vram)),
                    Threads = members.Sum(r => Math.Max(0, r.Threads)),
                    Priority = CommonMemberText(members, r => r.Priority, "mixed"),
                    Affinity = CommonMemberText(members, r => r.Affinity, "mixed"),
                    GpuValid = members.Any(r => r.GpuValid),
                    HasLimit = members.Any(r => r.HasLimit),
                    Watchdog = members.Any(r => r.Watchdog),
                    Pb = members.Any(r => r.Pb),
                    HasVisibleWindow = members.Any(r => r.HasVisibleWindow),
                    SessionId = first.SessionId
                };

                var controls = members.Select(r => r.Controls)
                    .Where(x => !String.IsNullOrEmpty(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                row.Controls = String.Join("; ", controls);

                var groupedEngines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var member in members)
                {
                    if (member.GpuEngines == null) continue;
                    foreach (var engine in member.GpuEngines)
                    {
                        double old;
                        if (!groupedEngines.TryGetValue(engine.Key, out old)) old = 0;
                        groupedEngines[engine.Key] = old + Math.Max(0, engine.Value);
                    }
                }
                row.GpuEngines = groupedEngines;
                row.Gpu = groupedEngines.Count > 0
                    ? Math.Min(100, groupedEngines.Values.Max())
                    : members.Max(r => Math.Max(0, r.Gpu));
                result.Add(row);
            }
            return result;
        }

        internal static List<ProcRow> BuildProcessRows(IEnumerable<ProcRow> source)
        {
            var result = new List<ProcRow>();
            foreach (var original in source ?? Enumerable.Empty<ProcRow>())
            {
                if (original == null || original.Id <= 0 ||
                    String.IsNullOrWhiteSpace(original.Name)) continue;
                var row = new ProcRow
                {
                    Id = original.Id,
                    StartTicks = original.StartTicks,
                    Name = original.Name,
                    ExecutablePath = original.ExecutablePath,
                    GroupKey = "process:" + original.Id.ToString(CultureInfo.InvariantCulture) +
                               ":" + original.StartTicks.ToString(CultureInfo.InvariantCulture),
                    Cpu = original.Cpu,
                    Mem = original.Mem,
                    Priv = original.Priv,
                    Gpu = original.Gpu,
                    GpuValid = original.GpuValid,
                    Vram = original.Vram,
                    GpuEngines = original.GpuEngines,
                    Priority = original.Priority,
                    Affinity = original.Affinity,
                    Threads = original.Threads,
                    HasLimit = original.HasLimit,
                    Controls = original.Controls,
                    Watchdog = original.Watchdog,
                    Pb = original.Pb,
                    HasVisibleWindow = original.HasVisibleWindow,
                    SessionId = original.SessionId,
                    Members = new List<ProcRow> { original }
                };
                result.Add(row);
            }
            return result;
        }

        private List<ProcRow> BuildVisibleRows(IEnumerable<ProcRow> source)
        {
            return _groupApplications ? BuildApplicationRows(source) : BuildProcessRows(source);
        }

        internal static bool VerifyProcessViewContract()
        {
            var source = new[]
            {
                new ProcRow { Id = 101, StartTicks = 1001, Name = "same", Cpu = 3, Mem = 10 },
                new ProcRow { Id = 102, StartTicks = 1002, Name = "same", Cpu = 7, Mem = 20 }
            };
            var rows = BuildProcessRows(source);
            return rows.Count == 2 && rows.Select(r => r.Id).SequenceEqual(new[] { 101, 102 }) &&
                   rows.Select(r => r.GroupKey).Distinct(StringComparer.Ordinal).Count() == 2 &&
                   rows.All(r => MemberCount(r) == 1) &&
                   Math.Abs(rows[1].Cpu - 7) < 0.001 && rows[1].Mem == 20;
        }

        private static string CommonMemberText(List<ProcRow> members,
                                               Func<ProcRow, string> selector,
                                               string mixed)
        {
            string first = selector(members[0]) ?? "";
            for (int i = 1; i < members.Count; i++)
                if (!String.Equals(first, selector(members[i]) ?? "",
                                   StringComparison.OrdinalIgnoreCase)) return mixed;
            return first;
        }

        internal static int MemberCount(ProcRow row)
        {
            return row != null && row.Members != null && row.Members.Count > 0
                ? row.Members.Count : row == null ? 0 : 1;
        }

        internal static IEnumerable<ProcRow> MemberRows(ProcRow row)
        {
            if (row == null) return Enumerable.Empty<ProcRow>();
            return row.Members != null && row.Members.Count > 0
                ? (IEnumerable<ProcRow>)row.Members : new[] { row };
        }

        internal static bool VerifyApplicationGroupingContract()
        {
            var rows = new List<ProcRow>
            {
                new ProcRow
                {
                    Id = 10, StartTicks = 100, Name = "example",
                    ExecutablePath = @"C:\Apps\Example\example.exe",
                    Cpu = 10, Mem = 100, Priv = 80, Vram = 40, Threads = 2,
                    Priority = "Normal", Affinity = "all",
                    Gpu = 20, GpuEngines = new Dictionary<string, double>
                    {
                        { "engine-0", 20 }, { "engine-1", 5 }
                    }
                },
                new ProcRow
                {
                    Id = 11, StartTicks = 110, Name = "example",
                    ExecutablePath = @"C:\Apps\Example\example.exe",
                    Cpu = 15, Mem = 200, Priv = 160, Vram = 60, Threads = 3,
                    Priority = "Normal", Affinity = "all",
                    Gpu = 40, GpuEngines = new Dictionary<string, double>
                    {
                        { "engine-0", 30 }, { "engine-1", 40 }
                    }
                },
                new ProcRow
                {
                    Id = 12, StartTicks = 120, Name = "example",
                    ExecutablePath = @"D:\Portable\example.exe",
                    Cpu = 1, Mem = 10, Priv = 8, Vram = 2, Threads = 1,
                    Priority = "Normal", Affinity = "all"
                },
                new ProcRow { Id = 20, Name = "fallback", Cpu = 2, Mem = 20 },
                new ProcRow { Id = 21, Name = "fallback", Cpu = 3, Mem = 30 },
                new ProcRow { Id = 30, Name = "wsl", Cpu = 4, Mem = 40 },
                new ProcRow { Id = 31, Name = "wslhost", Cpu = 5, Mem = 50 },
                new ProcRow { Id = 32, Name = "vmmemWSL", Cpu = 6, Mem = 60 }
            };

            var grouped = BuildApplicationRows(rows);
            var example = grouped.FirstOrDefault(r =>
                String.Equals(r.GroupKey,
                    Sampler.ApplicationGroupKey("example", @"C:\Apps\Example\example.exe"),
                    StringComparison.OrdinalIgnoreCase));
            var fallback = grouped.FirstOrDefault(r =>
                String.Equals(r.GroupKey, Sampler.ApplicationGroupKey("fallback", ""),
                    StringComparison.OrdinalIgnoreCase));
            var wsl = grouped.FirstOrDefault(r =>
                String.Equals(r.GroupKey, Sampler.ApplicationGroupKey("wsl", ""),
                    StringComparison.OrdinalIgnoreCase));
            return grouped.Count == 3 && example != null && fallback != null && wsl != null &&
                   MemberCount(example) == 3 && example.Members.Select(r => r.Id)
                       .SequenceEqual(new[] { 10, 11, 12 }) &&
                   Math.Abs(example.Cpu - 26) < 0.001 && example.Mem == 310 &&
                   example.Priv == 248 && example.Vram == 102 && example.Threads == 6 &&
                   Math.Abs(example.Gpu - 50) < 0.001 &&
                   MemberCount(fallback) == 2 && fallback.Mem == 50 &&
                   MemberCount(wsl) == 3 &&
                   wsl.Name == "Windows Subsystem for Linux" && wsl.Mem == 150;
        }

        private int CompareRows(ProcRow a, ProcRow b)
        {
            int c = 0;
            switch (_sort)
            {
                case SortKey.Cpu: c = a.Cpu.CompareTo(b.Cpu); break;
                case SortKey.Ram: c = a.Mem.CompareTo(b.Mem); break;
                case SortKey.Gpu:
                    if (a.GpuValid != b.GpuValid)
                        c = a.GpuValid.CompareTo(b.GpuValid);
                    else c = a.Gpu.CompareTo(b.Gpu);
                    break;
                case SortKey.Name: c = String.CompareOrdinal(a.Name, b.Name); break;
                case SortKey.Pid: c = a.Id.CompareTo(b.Id); break;
                case SortKey.Vram: c = a.Vram.CompareTo(b.Vram); break;
                case SortKey.Priority: c = String.CompareOrdinal(a.Priority, b.Priority); break;
            }
            if (c == 0) c = String.CompareOrdinal(a.Name, b.Name);
            if (c == 0) c = String.CompareOrdinal(RowIdentity(a), RowIdentity(b));
            return _sortAsc ? c : -c;
        }

        private List<string> _dispOrder = new List<string>();   // application keys in displayed order
        private long _maxMem, _maxVram;                   // scale for in-cell MEMORY/VRAM bars

        private void RefreshList()
        {
            try
            {
                var snap = _sampler.Snap;
                if (snap.Rows == null || snap.Rows.Count == 0)
                {
                    _totalApplicationCount = 0;
                    _filteredApplicationCount = 0;
                    if (_list.Items.Count > 0)
                    {
                        _list.BeginUpdate();
                        try
                        {
                            _list.Items.Clear();
                            _byGroup.Clear();
                            _dispOrder.Clear();
                        }
                        finally { _list.EndUpdate(); }
                    }
                    UpdateSelectedInfo();
                    return;
                }
                var selectedIdentities = new HashSet<string>(StringComparer.Ordinal);
                foreach (ListViewItem selected in _list.SelectedItems)
                    if (selected.Tag is ProcRow) selectedIdentities.Add(RowIdentity((ProcRow)selected.Tag));

                var allRows = BuildVisibleRows(snap.Rows);
                _totalApplicationCount = allRows.Count;
                var rows = FilterApplicationRows(allRows, _searchTokens);
                _filteredApplicationCount = rows.Count;
                rows.Sort(CompareRows);

                _maxMem = 0; _maxVram = 0;
                foreach (var r in rows)
                {
                    if (r.Mem > _maxMem) _maxMem = r.Mem;
                    if (r.Vram > _maxVram) _maxVram = r.Vram;
                }

                var newOrder = new List<string>(rows.Count);
                foreach (var r in rows) newOrder.Add(RowIdentity(r));

                // Re-sort whenever the current order differs from the true sorted order —
                // no deadband, so the highest users are ALWAYS on top. The live refresh and
                // stable tie-break keep equal values predictable, so the list stays
                // calm without ever going stale.
                bool reorder = _dispOrder.Count != newOrder.Count;
                if (!reorder)
                {
                    for (int i = 0; i < newOrder.Count; i++)
                    {
                        if (newOrder[i] != _dispOrder[i]) { reorder = true; break; }
                    }
                }

                if (reorder)
                {
                    string topGroup = null;
                    if (_list.TopItem != null && _list.TopItem.Tag is ProcRow)
                        topGroup = RowIdentity((ProcRow)_list.TopItem.Tag);

                    _list.BeginUpdate();
                    try
                    {
                        var wanted = new HashSet<string>(newOrder, StringComparer.OrdinalIgnoreCase);
                        foreach (string groupKey in _byGroup.Keys.ToList())
                        {
                            if (wanted.Contains(groupKey)) continue;
                            ListViewItem stale;
                            if (_byGroup.TryGetValue(groupKey, out stale)) _list.Items.Remove(stale);
                            _byGroup.Remove(groupKey);
                        }

                        for (int i = 0; i < rows.Count; i++)
                        {
                            var r = rows[i];
                            string groupKey = RowIdentity(r);
                            ListViewItem it;
                            if (!_byGroup.TryGetValue(groupKey, out it))
                            {
                                it = CreateProcessItem();
                                _byGroup[groupKey] = it;
                                _list.Items.Add(it);
                            }
                            UpdateProcessItem(it, r, snap.RamTotal);
                            it.Selected = selectedIdentities.Contains(groupKey);
                            if (it.Index != i)
                            {
                                _list.Items.Remove(it);
                                _list.Items.Insert(i, it);
                            }
                        }
                    }
                    finally { _list.EndUpdate(); }

                    if (_scrollToTopOnNextRefresh && _list.Items.Count > 0)
                        _list.EnsureVisible(0);
                    else if (!String.IsNullOrEmpty(topGroup))
                    {
                        ListViewItem top;
                        if (_byGroup.TryGetValue(topGroup, out top) && top.Index >= 0)
                            _list.EnsureVisible(top.Index);
                    }
                }
                else
                {
                    // Change only cells whose displayed value actually changed, but
                    // commit the entire sample as one buffered frame. This prevents
                    // row-by-row shimmer while keeping every value current.
                    bool anyChanged = false;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        ListViewItem it;
                        if (_byGroup.TryGetValue(RowIdentity(rows[i]), out it) &&
                            ProcessItemNeedsUpdate(it, rows[i], snap.RamTotal))
                        { anyChanged = true; break; }
                    }
                    if (anyChanged)
                    {
                        _list.BeginUpdate();
                        try
                        {
                            for (int i = 0; i < rows.Count; i++)
                            {
                                ListViewItem it;
                                if (_byGroup.TryGetValue(RowIdentity(rows[i]), out it))
                                    UpdateProcessItem(it, rows[i], snap.RamTotal);
                            }
                        }
                        finally { _list.EndUpdate(); }
                    }
                }
                _dispOrder = newOrder;
                _scrollToTopOnNextRefresh = false;
                UpdateSelectedInfo();
            }
            catch { }
        }

        private static ListViewItem CreateProcessItem()
        {
            var it = new ListViewItem("");
            for (int i = 1; i < 8; i++) it.SubItems.Add("");
            return it;
        }

        private static string RowIdentity(ProcRow row)
        {
            if (!String.IsNullOrEmpty(row.GroupKey)) return row.GroupKey;
            return Sampler.ApplicationGroupKey(row.Name, row.ExecutablePath);
        }

        private static string ProcessCountCell(ProcRow row)
        {
            int count = MemberCount(row);
            return count > 1 ? count.ToString(CultureInfo.InvariantCulture) + " procs"
                             : row.Id.ToString(CultureInfo.InvariantCulture);
        }

        private static string MemberPidText(ProcRow row)
        {
            return String.Join(", ", MemberRows(row)
                .Select(r => r.Id.ToString(CultureInfo.InvariantCulture)).ToArray());
        }

        private static bool ProcessItemNeedsUpdate(ListViewItem it, ProcRow r, long ramTotal)
        {
            var old = it.Tag as ProcRow;
            return old == null || old.StartTicks != r.StartTicks ||
                    !String.Equals(it.SubItems[0].Text, ProcessCountCell(r), StringComparison.Ordinal) ||
                   !String.Equals(it.SubItems[1].Text, r.Name, StringComparison.Ordinal) ||
                   !String.Equals(it.SubItems[2].Text, FormatUsagePercent(r.Cpu), StringComparison.Ordinal) ||
                   !String.Equals(it.SubItems[3].Text, FormatRamCell(r.Mem, ramTotal), StringComparison.Ordinal) ||
                   !String.Equals(it.SubItems[4].Text, FormatGpuCell(r), StringComparison.Ordinal) ||
                   !String.Equals(it.SubItems[5].Text, DetailsForm.FmtBytes(r.Vram), StringComparison.Ordinal) ||
                   !String.Equals(it.SubItems[6].Text, r.Priority, StringComparison.Ordinal) ||
                   !String.Equals(it.SubItems[7].Text, r.Affinity, StringComparison.Ordinal);
        }

        private static void UpdateProcessItem(ListViewItem it, ProcRow r, long ramTotal)
        {
            it.Tag = r;
            SetCell(it, 0, ProcessCountCell(r));
            SetCell(it, 1, r.Name);
            SetCell(it, 2, FormatUsagePercent(r.Cpu));
            SetCell(it, 3, FormatRamCell(r.Mem, ramTotal));
            SetCell(it, 4, FormatGpuCell(r));
            SetCell(it, 5, DetailsForm.FmtBytes(r.Vram));
            SetCell(it, 6, r.Priority);
            SetCell(it, 7, r.Affinity);
            it.ToolTipText = MemberCount(r) > 1
                ? r.Name + " — " + MemberCount(r) + " related processes; PIDs " + MemberPidText(r)
                : r.Name + " — PID " + r.Id;
        }

        private static void SetCell(ListViewItem item, int index, string value)
        {
            if (!String.Equals(item.SubItems[index].Text, value, StringComparison.Ordinal))
                item.SubItems[index].Text = value;
        }

        private void UpdateSelectedInfo()
        {
            try
            {
                if (_list.SelectedItems.Count > 1)
                {
                    double cpu = 0, gpu = 0;
                    long ram = 0, vram = 0;
                    int processCount = 0;
                    foreach (ListViewItem item in _list.SelectedItems)
                    {
                        var sr = item.Tag as ProcRow;
                        if (sr == null) continue;
                        processCount += MemberCount(sr);
                        cpu += sr.Cpu;
                        gpu = Math.Max(gpu, sr.Gpu);
                        ram += sr.Mem;
                        vram += sr.Vram;
                    }
                    _lblSel.Text = _list.SelectedItems.Count + " apps selected (" +
                                   processCount + " processes)   CPU " +
                                   FormatUsagePercent(cpu) + "   RAM " + DetailsForm.FmtBytes(ram) +
                                   "   GPU top " + (_sampler.Snap.GpuValid
                                       ? FormatUsagePercent(gpu) : "--") + "   VRAM " +
                                   DetailsForm.FmtBytes(vram) + "     Ctrl+C copies an AI-ready table";
                }
                else if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is ProcRow)
                {
                    var r = (ProcRow)_list.SelectedItems[0].Tag;
                    string marks = "";
                    if (r.HasLimit) marks += "* ";
                    if (r.Watchdog) marks += "W ";
                    if (r.Pb) marks += "P ";
                    string identity = MemberCount(r) > 1
                        ? MemberCount(r) + " processes"
                        : "PID " + r.Id;
                    _lblSel.Text = "▸ " + r.Name + "  " + identity + "   CPU " + FormatUsagePercent(r.Cpu) + "   RAM " +
                                   FormatRamCell(r.Mem, _sampler.Snap.RamTotal) + "   GPU " + FormatGpuCell(r) + "   VRAM " +
                                   DetailsForm.FmtBytes(r.Vram) + (marks.Length > 0 ? "   [" + marks.Trim() + "]" : "") +
                                   "     (double-click for details, right-click for actions)";
                }
                else _lblSel.Text = "";
            }
            catch { }
        }

        // -----------------------------------------------------------------
        private ProcRow SelectedRow()
        {
            if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is ProcRow)
                return (ProcRow)_list.SelectedItems[0].Tag;
            return null;
        }

        private List<ProcRow> SelectedRows()
        {
            var rows = new List<ProcRow>();
            foreach (ListViewItem item in _list.Items)
                if (item.Selected && item.Tag is ProcRow) rows.Add((ProcRow)item.Tag);
            return rows;
        }

        private void List_MouseDownForRange(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var item = _list.GetItemAt(e.X, e.Y);
            if (item == null) return;

            _dragSelecting = true;
            _dragAnchorIndex = item.Index;
            _dragCurrentIndex = item.Index;
            _dragPointer = e.Location;
            _dragBaseSelection = (ModifierKeys & Keys.Control) == Keys.Control
                ? new HashSet<string>(_list.SelectedItems.Cast<ListViewItem>()
                    .Where(x => x.Tag is ProcRow)
                    .Select(x => RowIdentity((ProcRow)x.Tag)),
                    StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ApplyDragRangeSelection(_dragAnchorIndex, _dragCurrentIndex);
            _list.Capture = true;

            if (_dragScrollTimer == null)
            {
                _dragScrollTimer = new System.Windows.Forms.Timer { Interval = 60 };
                _dragScrollTimer.Tick += (s, args) => AutoScrollDragSelection();
            }
            _dragScrollTimer.Start();
        }

        private void List_MouseMoveForRange(object sender, MouseEventArgs e)
        {
            if (!_dragSelecting || (e.Button & MouseButtons.Left) == 0) return;
            _dragPointer = e.Location;
            ContinueDragSelectionAtPointer();
        }

        private void ContinueDragSelectionAtPointer()
        {
            if (!_dragSelecting) return;
            var item = _list.GetItemAt(
                Math.Max(1, Math.Min(_list.ClientSize.Width - 2, _dragPointer.X)),
                Math.Max(1, Math.Min(_list.ClientSize.Height - 2, _dragPointer.Y)));
            if (item != null && item.Index != _dragCurrentIndex)
            {
                _dragCurrentIndex = item.Index;
                ApplyDragRangeSelection(_dragAnchorIndex, _dragCurrentIndex);
            }
            AutoScrollDragSelection();
        }

        private void List_MouseUpForRange(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !_dragSelecting) return;
            _dragPointer = e.Location;
            AutoScrollDragSelection();
            EndDragRangeSelection();
        }

        private void AutoScrollDragSelection()
        {
            if (!_dragSelecting || _list.Items.Count == 0) return;
            int top = _list.TopItem == null ? 0 : _list.TopItem.Index;
            int bottom = LastVisibleItemIndex();
            int target = DragAutoScrollTarget(_dragPointer.Y, _list.ClientSize.Height,
                                              top, bottom, _list.Items.Count,
                                              _dragCurrentIndex);
            if (target == _dragCurrentIndex) return;
            _dragCurrentIndex = target;
            _list.EnsureVisible(target);
            ApplyDragRangeSelection(_dragAnchorIndex, target);
        }

        private static int DragAutoScrollTarget(int pointerY, int clientHeight,
                                                int topIndex, int bottomIndex,
                                                int itemCount, int currentIndex)
        {
            if (itemCount <= 0) return -1;
            const int edge = 30;
            if (pointerY <= edge) return Math.Max(0, topIndex - 1);
            if (pointerY >= clientHeight - edge)
                return Math.Min(itemCount - 1, bottomIndex + 1);
            return Math.Max(0, Math.Min(itemCount - 1, currentIndex));
        }

        internal static bool VerifyDragAutoScrollContract()
        {
            return DragAutoScrollTarget(0, 400, 10, 25, 100, 17) == 9 &&
                   DragAutoScrollTarget(399, 400, 10, 25, 100, 17) == 26 &&
                   DragAutoScrollTarget(200, 400, 10, 25, 100, 17) == 17 &&
                   DragAutoScrollTarget(0, 400, 0, 25, 100, 17) == 0 &&
                   DragAutoScrollTarget(399, 400, 80, 99, 100, 90) == 99;
        }

        private int LastVisibleItemIndex()
        {
            for (int y = _list.ClientSize.Height - 2; y >= 0; y -= 4)
            {
                var item = _list.GetItemAt(Math.Max(2, _list.ClientSize.Width / 2), y);
                if (item != null) return item.Index;
            }
            return _list.TopItem == null ? 0 : _list.TopItem.Index;
        }

        private bool ApplyDragRangeSelection(int anchor, int current)
        {
            if (_list == null || anchor < 0 || current < 0 ||
                anchor >= _list.Items.Count || current >= _list.Items.Count) return false;
            int first = Math.Min(anchor, current);
            int last = Math.Max(anchor, current);
            _list.BeginUpdate();
            try
            {
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    var item = _list.Items[i];
                    string identity = item.Tag is ProcRow
                        ? RowIdentity((ProcRow)item.Tag) : "";
                    item.Selected = (i >= first && i <= last) ||
                                    _dragBaseSelection.Contains(identity);
                }
            }
            finally { _list.EndUpdate(); }
            _list.EnsureVisible(current);
            UpdateSelectedInfo();
            return true;
        }

        private void EndDragRangeSelection()
        {
            if (!_dragSelecting) return;
            _dragSelecting = false;
            _dragAnchorIndex = -1;
            _dragCurrentIndex = -1;
            if (_dragScrollTimer != null) _dragScrollTimer.Stop();
            if (_list != null && _list.Capture) _list.Capture = false;
            UpdateSelectedInfo();
        }

        private void SelectAllRows()
        {
            if (_list == null) return;
            _list.BeginUpdate();
            foreach (ListViewItem item in _list.Items) item.Selected = true;
            _list.EndUpdate();
            UpdateSelectedInfo();
        }

        private void CopySelectedRows()
        {
            try
            {
                var rows = SelectedRows();
                if (rows.Count == 0) return;
                if (!TrySetClipboardText(BuildClipboardText(rows, _sampler.Snap)))
                {
                    _lblStatus.Text = "Copy failed because another app is holding the clipboard. Try again.";
                    return;
                }
                int processCount = rows.Sum(r => MemberCount(r));
                _lblStatus.Text = "Copied " + rows.Count +
                                  " app" + (rows.Count == 1 ? "" : "s") + " (" +
                                  processCount + " processes)" +
                                  " with CPU, RAM, GPU, priority and affinity.";
            }
            catch (Exception ex) { _lblStatus.Text = "Copy failed: " + ex.Message; }
        }

        internal static bool TrySetClipboardText(string text)
        {
            if (String.IsNullOrEmpty(text)) return false;
            try
            {
                Clipboard.SetDataObject(text, true, 10, 100);
                return true;
            }
            catch { return false; }
        }

        internal static string ReadClipboardTextWithRetry()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
                catch
                {
                    Application.DoEvents();
                    Thread.Sleep(100);
                }
            }
            return "";
        }

        internal static string BuildClipboardText(IEnumerable<ProcRow> source, Snapshot snap)
        {
            var rows = source == null ? new List<ProcRow>() : source.ToList();
            var sb = new StringBuilder();
            double ramPct = snap != null && snap.RamTotal > 0
                ? snap.RamUsed * 100.0 / snap.RamTotal : 0;
            sb.AppendLine("PSProcLasso process snapshot\t" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            if (snap != null)
                sb.AppendLine("SYSTEM\tCPU " + snap.TotalCpu.ToString("N1", CultureInfo.InvariantCulture) +
                              "%\tRAM " + ramPct.ToString("N1", CultureInfo.InvariantCulture) +
                              "% (" + DetailsForm.FmtBytes(snap.RamUsed) + " / " +
                              DetailsForm.FmtBytes(snap.RamTotal) + ")\tGPU " +
                              snap.GpuPct.ToString("N1", CultureInfo.InvariantCulture) + "%");
            sb.AppendLine("NAME\tPROCESS COUNT\tPIDS\tCPU%\tRAM%\tRAM WORKING SET\tPRIVATE COMMIT\tGPU%\tVRAM\tPRIORITY\tAFFINITY\tMANAGED");
            foreach (var r in rows)
            {
                double rowRamPct = snap != null && snap.RamTotal > 0
                    ? r.Mem * 100.0 / snap.RamTotal : 0;
                string managed = (String.IsNullOrEmpty(r.Controls) ? "" : r.Controls + " ") +
                                 (r.Watchdog ? "watchdog " : "") +
                                 (r.Pb ? "ProBalance" : "");
                sb.AppendLine(r.Name + "\t" + MemberCount(r) + "\t" +
                              MemberPidText(r) + "\t" +
                              r.Cpu.ToString("0.###", CultureInfo.InvariantCulture) + "\t" +
                              rowRamPct.ToString("N1", CultureInfo.InvariantCulture) + "\t" +
                              DetailsForm.FmtBytes(r.Mem) + "\t" +
                              DetailsForm.FmtBytes(r.Priv) + "\t" +
                              (r.GpuValid
                                  ? r.Gpu.ToString("0.###", CultureInfo.InvariantCulture)
                                  : "n/a") + "\t" +
                              DetailsForm.FmtBytes(r.Vram) + "\t" +
                              r.Priority + "\t" + r.Affinity + "\t" + managed.Trim());
            }
            return sb.ToString();
        }

        private void ShowDetailsForSelection()
        {
            var r = SelectedRow();
            if (r == null) return;
            new DetailsForm(r).ShowDialog(this);
        }

        private void TryKill()
        {
            var r = SelectedRow();
            if (r == null) return;
            int count = MemberCount(r);
            string target = count > 1
                ? r.Name + " and all " + count + " related processes?"
                : r.Name + "  (PID " + r.Id + ")?";
            if (new DarkBox("Kill process", "Kill " + target, true).ShowDialog(this) == DialogResult.Yes)
                foreach (var member in MemberRows(r)) _sampler.Kill(member);
        }

        // -----------------------------------------------------------------
        private void ShowRowMenu(MouseEventArgs e)
        {
            var item = _list.GetItemAt(e.X, e.Y);
            if (item == null || !(item.Tag is ProcRow)) return;
            if (!item.Selected)
            {
                _list.SelectedItems.Clear();
                item.Selected = true;
            }
            var row = (ProcRow)item.Tag;

            var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), BackColor = Theme.Panel, ForeColor = Theme.Text };
            menu.ForeColor = Theme.Text;
            menu.Font = new Font("Segoe UI", 9f);

            var copy = new ToolStripMenuItem("Copy selected rows  (Ctrl+C)");
            copy.ForeColor = Theme.Accent;
            copy.Click += (s, ev) => CopySelectedRows();
            menu.Items.Add(copy);
            var selectAll = new ToolStripMenuItem("Select all rows  (Ctrl+A)");
            selectAll.ForeColor = Theme.Text;
            selectAll.Click += (s, ev) => SelectAllRows();
            menu.Items.Add(selectAll);
            menu.Items.Add(new ToolStripSeparator());

            var pri = new ToolStripMenuItem("Priority");
            pri.ForeColor = Theme.Text;
            string[] priNames = { "Idle", "BelowNormal", "Normal", "AboveNormal", "High", "Realtime" };
            foreach (string pn in priNames)
                pri.DropDownItems.Add(pn, null, (s, ev) => _sampler.SetPriority(row, pn));
            pri.DropDown.BackColor = Theme.Panel;
            pri.DropDown.ForeColor = Theme.Text;
            menu.Items.Add(pri);

            var aff = new ToolStripMenuItem("Set CPU affinity…");
            aff.ForeColor = Theme.Text;
            aff.Click += (s, ev) =>
            {
                var box = new DarkInput("Set CPU affinity", "Cores for " + row.Name + "  (e.g. 0-3,5, Enter = all):", row.Affinity == "all" ? "" : row.Affinity);
                if (box.ShowDialog(this) == DialogResult.OK)
                {
                    string spec = box.Value.Trim();
                    if (spec.Length == 0) spec = string.Join(",", Enumerable.Range(0, Environment.ProcessorCount));
                    _sampler.SetAffinity(row, spec);
                }
            };
            menu.Items.Add(aff);

            var cpuLim = new ToolStripMenuItem("CPU limit…");
            cpuLim.ForeColor = Theme.Text;
            cpuLim.Click += (s, ev) =>
            {
                var box = new DarkInput("CPU limit", "CPU cap % for " + row.Name + "  (0 = remove):", "");
                if (box.ShowDialog(this) == DialogResult.OK)
                {
                    double v;
                    if (double.TryParse(box.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                        _sampler.SetCpuLimit(row, Math.Max(0, Math.Min(100, v)));
                }
            };
            menu.Items.Add(cpuLim);

            var gpuLim = new ToolStripMenuItem("GPU limit…");
            gpuLim.ForeColor = Theme.Text;
            gpuLim.Click += (s, ev) =>
            {
                var box = new DarkInput("GPU limit", "GPU cap % for " + row.Name + "  (0 = remove, throttles by suspend/resume):", "");
                if (box.ShowDialog(this) == DialogResult.OK)
                {
                    double v;
                    if (double.TryParse(box.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                        _sampler.SetGpuLimit(row, Math.Max(0, Math.Min(100, v)));
                }
            };
            menu.Items.Add(gpuLim);

            var ramLim = new ToolStripMenuItem("RAM limit (MB)…");
            ramLim.ForeColor = Theme.Text;
            ramLim.Click += (s, ev) =>
            {
                var box = new DarkInput("RAM limit", "RAM cap MB for " + row.Name + "  (0 = remove, trims working set):", "");
                if (box.ShowDialog(this) == DialogResult.OK)
                {
                    long v;
                    if (long.TryParse(box.Value, out v)) _sampler.SetRamLimit(row, Math.Max(0, v));
                }
            };
            menu.Items.Add(ramLim);

            var remove = new ToolStripMenuItem("Remove all limits");
            remove.ForeColor = Theme.Text;
            remove.Click += (s, ev) => _sampler.RemoveLimits(row);
            menu.Items.Add(remove);

            menu.Items.Add(new ToolStripSeparator());

            var wd = new ToolStripMenuItem(row.Watchdog ? "Watchdog: ON (click to turn OFF)" : "Watchdog: OFF (auto-restart)");
            wd.ForeColor = Theme.Text;
            wd.Click += (s, ev) =>
            {
                string path = row.ExecutablePath;
                if (String.IsNullOrEmpty(path))
                    try { using (var p = Process.GetProcessById(row.Id)) path = p.MainModule.FileName; } catch { }
                if (String.IsNullOrEmpty(path))
                {
                    new DarkBox("Watchdog", "Cannot find the executable path of " + row.Name + ".", false).ShowDialog(this);
                    return;
                }
                _sampler.ToggleWatchdog(row, path);
            };
            menu.Items.Add(wd);

            var kill = new ToolStripMenuItem("Kill");
            kill.ForeColor = Theme.Red;
            kill.Click += (s, ev) => TryKill();
            menu.Items.Add(kill);

            var details = new ToolStripMenuItem("Details");
            details.ForeColor = Theme.Text;
            details.Click += (s, ev) => ShowDetailsForSelection();
            menu.Items.Add(details);

            menu.Show(_list, e.Location);
        }

        private void ShowHelp()
        {
            var txt = "PSProcLasso — Real-Time System Monitor\n\n" +
                      "  Click any column header  → rank by CPU / RAM / GPU / VRAM / name / PID\n" +
                      "                          (click again to flip direction)\n" +
                      "  Click a row / ↑ ↓        → select     Enter or double-click → details\n" +
                      "  Press + drag over rows  → select a blue range; edge/wheel auto-scrolls\n" +
                      "  Ctrl+click / Shift+click → add any number of application rows\n" +
                      "  Ctrl+C / COPY            → copy an AI-ready table with all selected metrics\n" +
                      "  Ctrl+A / SELECT ALL      → select every visible process\n" +
                      "  Ctrl+F / SEARCH          → filter by app, PID, path, priority or controls\n" +
                      "  Esc                      → clear the active search instantly\n" +
                      "  OPTIMIZE                 → measure baseline, review every PID, apply only safe\n" +
                      "                             reversible changes, then show measured CPU/RAM/GPU results\n" +
                      "  Right-click a row        → priority, affinity, CPU/GPU/RAM limits,\n" +
                      "                             watchdog, kill (applies to every member process)\n" +
                      "  Keys: 1 = CPU  2 = RAM  3 = GPU   F5 = refresh now   Del = kill\n" +
                      "  Minimizing (or closing) sends the app to the system tray — limits,\n" +
                      "  watchdog and ProBalance keep enforcing in the background. Right-click\n" +
                      "  the tray icon for quick actions (sort, limit presets, watchdog,\n" +
                      "  ProBalance/GPU toggles); Exit in the tray menu quits for real.\n" +
                      "\n" +
                "Live meters: CPU / RAM update twice a second; GPU updates about once a second.\n" +
                      "Rules are saved to %USERPROFILE%\\.psproclasso\\rules.json and shared\n" +
                      "with the PSProcLasso PowerShell TUI. Optimization evidence is saved to\n" +
                      "%USERPROFILE%\\.psproclasso\\last-optimization.json.\n" +
                      "AI automation: --ai-snapshot, --optimize-plan, --optimize-apply and\n" +
                      "--optimize-restore write stable JSON receipts to a requested path.\n" +
                      "\n" +
                      "CPU and RAM use Windows Job Object hard caps when the target permits\n" +
                      "assignment, with clearly reported fallbacks otherwise. GPU duty limiting\n" +
                      "uses rapid suspend/resume and therefore limits the whole app while active.\n" +
                      "Start silently with Windows is available from the tray menu; it uses a\n" +
                      "hidden least-privilege logon task, so no terminal window appears and\n" +
                      "user-writable rules never become an elevation path.\n" +
                      "Protected/system processes may still be refused by Windows; every refusal\n" +
                      "is caught and shown on the status bar instead of being silently ignored.";
            new DarkBox("PSProcLasso — Help", txt, false).ShowDialog(this);
        }

        // -----------------------------------------------------------------
        //  Owner-draw
        // -----------------------------------------------------------------
        private void List_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new LinearGradientBrush(e.Bounds, Theme.Panel2, Theme.Header, 90f))
                e.Graphics.FillRectangle(b, e.Bounds);
            // vertical hairline separators between columns
            if (e.ColumnIndex > 0)
                using (var sep = new Pen(Theme.Line))
                    e.Graphics.DrawLine(sep, e.Bounds.X, e.Bounds.Y + 4, e.Bounds.X, e.Bounds.Bottom - 4);
            // hairline under the header row
            using (var bot = new Pen(Theme.Line))
                e.Graphics.DrawLine(bot, e.Bounds.X, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            string txt = _list.Columns[e.ColumnIndex].Text;
            bool sortCol = (e.ColumnIndex == 0 && _sort == SortKey.Pid) ||
                           (e.ColumnIndex == 1 && _sort == SortKey.Name) ||
                           (e.ColumnIndex == 2 && _sort == SortKey.Cpu) ||
                           (e.ColumnIndex == 3 && _sort == SortKey.Ram) ||
                           (e.ColumnIndex == 4 && _sort == SortKey.Gpu) ||
                           (e.ColumnIndex == 5 && _sort == SortKey.Vram) ||
                           (e.ColumnIndex == 6 && _sort == SortKey.Priority);
            using (var f = new Font("Segoe UI", 8.5f, sortCol ? FontStyle.Bold : FontStyle.Regular))
            using (var b2 = new SolidBrush(sortCol ? Theme.Accent : Theme.Dim))
            using (var fmt = new StringFormat { LineAlignment = StringAlignment.Center })
            {
                if (e.ColumnIndex > 0 && _list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right)
                    fmt.Alignment = StringAlignment.Far;
                Rectangle r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y,
                                            Math.Max(1, e.Bounds.Width - 12), e.Bounds.Height);
                if (sortCol) r.Width = Math.Max(1, r.Width - 3);
                e.Graphics.DrawString(txt + (sortCol ? (_sortAsc ? " ▲" : " ▼") : ""), f, b2, r, fmt);
            }
        }

        private void List_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            var row = e.Item.Tag as ProcRow;
            bool selected = e.Item.Selected;
            int col = e.ColumnIndex;

            Color bg = selected ? Theme.SelRow : ((e.ItemIndex % 2 == 0) ? Theme.Bg : Theme.AltRow);
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);

            // Task-Manager-style mini bar behind the value in the usage columns
            if (row != null && !selected)
            {
                if (col == 2) DrawCellBar(e.Graphics, e.Bounds, Math.Max(0, Math.Min(100, row.Cpu)) / 100.0, Theme.PctColor(row.Cpu));
                else if (col == 4 && row.GpuValid)
                    DrawCellBar(e.Graphics, e.Bounds,
                                Math.Max(0, Math.Min(100, row.Gpu)) / 100.0,
                                Theme.PctColor(row.Gpu));
                else if (col == 3 && _maxMem > 0) DrawCellBar(e.Graphics, e.Bounds, (double)row.Mem / _maxMem, Theme.MemColor);
                else if (col == 5 && _maxVram > 0) DrawCellBar(e.Graphics, e.Bounds, (double)row.Vram / _maxVram, Theme.VramColor);
            }

            // accent caret marks the selected row
            if (selected && col == 1)
                using (var acc = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(acc, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);

            string text = e.Item.SubItems[col].Text;
            Color fg = Theme.Text;
            bool bold = false;

            if (row != null)
            {
                if (col == 0) { fg = Theme.Dim; text = text.PadLeft(5); }
                else if (col == 2) { fg = Theme.PctColor(row.Cpu); if (row.Cpu >= 80) bold = true; text = text.PadLeft(5); }
                else if (col == 3) { if (row.Mem >= 1024L * 1024 * 1024) fg = Theme.Yellow; text = text.PadLeft(8); }
                else if (col == 4)
                {
                    fg = row.GpuValid ? Theme.PctColor(row.Gpu) : Theme.Dim;
                    if (row.GpuValid && row.Gpu >= 80) bold = true;
                    text = text.PadLeft(5);
                }
                else if (col == 5) { if (row.Vram >= 1024L * 1024 * 1024) fg = Theme.Yellow; text = text.PadLeft(8); }
                else if (col == 1)
                {
                    fg = selected ? Color.White : Theme.Text;
                    if (row.HasLimit) text = "* " + text;
                    if (row.Watchdog) text = "W " + text;
                    if (row.Pb) text = "P " + text;
                }
                else if (col == 6) { fg = selected ? Color.White : Theme.Dim; }
                else if (col == 7) { fg = selected ? Color.White : Theme.Dim; }
            }

            if (selected) fg = Color.White;
            using (var b2 = new SolidBrush(fg))
            using (var fmt = new StringFormat { LineAlignment = StringAlignment.Center })
            {
                if (_list.Columns[col].TextAlign == HorizontalAlignment.Right) fmt.Alignment = StringAlignment.Far;
                e.Graphics.DrawString(text, bold && _listBoldFont != null ? _listBoldFont : _list.Font,
                                      b2, new RectangleF(e.Bounds.X + 3, e.Bounds.Y,
                                      e.Bounds.Width - 6, e.Bounds.Height), fmt);
            }
        }

        // RAM cell shows the app's working set as a % of total RAM, with bytes alongside
        private static string FormatRamCell(long mem, long ramTotal)
        {
            string pct = ramTotal > 0 ? FormatUsagePercent(mem * 100.0 / ramTotal) : "--";
            return pct + "  " + DetailsForm.FmtBytes(mem);
        }

        private static string FormatGpuCell(ProcRow row)
        {
            return row != null && row.GpuValid ? FormatUsagePercent(row.Gpu) : "--";
        }

        internal static bool VerifyUnavailableGpuRenderingContract()
        {
            return FormatGpuCell(new ProcRow { Gpu = 0, GpuValid = false }) == "--" &&
                   FormatGpuCell(new ProcRow { Gpu = 0, GpuValid = true }) == "0%" &&
                   FormatGpuCell(new ProcRow { Gpu = 1.25, GpuValid = true }) == "1.3%";
        }

        internal static bool VerifyCalmRefreshCadenceContract()
        {
            return VisibleRefreshIntervalMs >= 900 && VisibleRefreshIntervalMs <= 1100;
        }

        internal static string FormatUsagePercent(double value)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value <= 0) return "0%";
            if (value < 0.01) return "<0.01%";
            if (value < 1) return value.ToString("N2") + "%";
            return value.ToString("N1") + "%";
        }

        private static void DrawCellBar(Graphics g, Rectangle b, double frac, Color col)
        {
            if (frac <= 0.01) return;
            int w = Math.Max(3, (int)((b.Width - 8) * Math.Min(1, frac)));
            var r = new Rectangle(b.X + 3, b.Y + 4, w, b.Height - 8);
            if (r.Height <= 0) return;
            using (var br = new SolidBrush(Color.FromArgb(46, col.R, col.G, col.B)))
                g.FillRectangle(br, r);
        }
    }

    // ---------------------------------------------------------------------
    //  Small dark text-input dialog
    // ---------------------------------------------------------------------
    internal class DarkInput : Form
    {
        public string Value { get { return _tb.Text; } }

        private readonly TextBox _tb;

        public DarkInput(string title, string prompt, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            ClientSize = new Size(440, 112);
            Font = new Font("Segoe UI", 9f);

            var lbl = new Label { Text = prompt, Left = 16, Top = 14, Width = 408, AutoSize = false, Height = 34, ForeColor = Theme.Text, BackColor = Theme.Bg };
            Controls.Add(lbl);

            _tb = new TextBox { Left = 16, Top = 48, Width = 408, BackColor = Theme.Panel, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Text = initial };
            Controls.Add(_tb);

            var ok = new Button { Text = "OK", Left = 292, Top = 76, Width = 62, Height = 26, FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Text, DialogResult = DialogResult.OK };
            ok.FlatAppearance.BorderColor = Theme.Header;
            var cancel = new Button { Text = "Cancel", Left = 362, Top = 76, Width = 62, Height = 26, FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Text, DialogResult = DialogResult.Cancel };
            cancel.FlatAppearance.BorderColor = Theme.Header;
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    // ---------------------------------------------------------------------
    //  Entry point
    // ---------------------------------------------------------------------
    internal static class Program
    {
        private const string InstanceMutexName = "Local\\PSProcLassoGUI.SingleInstance";
        private const string ShowEventName = "Local\\PSProcLassoGUI.ShowWindow";
        private const string InstanceScopeEnvironment = "PSPROCLASSO_INSTANCE_SCOPE";
        private const string BackgroundBudgetMutexName = "Local\\PSProcLasso.BackgroundRecoveryBudget.v1";
        private const int BackgroundRestartLimit = 5;
        private static readonly TimeSpan BackgroundRestartWindow = TimeSpan.FromMinutes(5);

        private static ProcessPriorityClass DesiredMonitorPriority(
            ProcessPriorityClass inherited)
        {
            if (inherited == ProcessPriorityClass.RealTime)
                return ProcessPriorityClass.High;
            if (inherited == ProcessPriorityClass.High)
                return ProcessPriorityClass.High;
            return ProcessPriorityClass.AboveNormal;
        }

        private static void ApplyMonitorPriority()
        {
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    ProcessPriorityClass wanted =
                        DesiredMonitorPriority(current.PriorityClass);
                    if (current.PriorityClass != wanted)
                        current.PriorityClass = wanted;
                }
            }
            catch { }
        }

        internal static bool VerifyMonitorPriorityContract()
        {
            return DesiredMonitorPriority(ProcessPriorityClass.Idle) ==
                       ProcessPriorityClass.AboveNormal &&
                   DesiredMonitorPriority(ProcessPriorityClass.BelowNormal) ==
                       ProcessPriorityClass.AboveNormal &&
                   DesiredMonitorPriority(ProcessPriorityClass.Normal) ==
                       ProcessPriorityClass.AboveNormal &&
                   DesiredMonitorPriority(ProcessPriorityClass.AboveNormal) ==
                       ProcessPriorityClass.AboveNormal &&
                   DesiredMonitorPriority(ProcessPriorityClass.High) ==
                       ProcessPriorityClass.High &&
                   DesiredMonitorPriority(ProcessPriorityClass.RealTime) ==
                       ProcessPriorityClass.High;
        }

        private static string ScopedKernelObjectName(string baseName, string scope)
        {
            if (String.IsNullOrWhiteSpace(scope)) return baseName;
            var safe = new StringBuilder();
            foreach (char c in scope.Trim())
            {
                if (Char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                    safe.Append(c);
                else safe.Append('_');
                if (safe.Length >= 64) break;
            }
            return safe.Length == 0 ? baseName : baseName + "." + safe;
        }

        internal static bool VerifyInstanceScopeContract()
        {
            string production = ScopedKernelObjectName(InstanceMutexName, "");
            string first = ScopedKernelObjectName(InstanceMutexName, "report:one");
            string second = ScopedKernelObjectName(InstanceMutexName, "report/two");
            string show = ScopedKernelObjectName(ShowEventName, "report:one");
            return production == InstanceMutexName &&
                   first == InstanceMutexName + ".report_one" &&
                   second == InstanceMutexName + ".report_two" &&
                   first != second &&
                   show == ShowEventName + ".report_one";
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "--background-guard")
            {
                RunBackgroundGuard(args);
                return;
            }
            if (args.Length >= 5 && args[0] == "--gpu-guard")
            {
                Environment.ExitCode = RunGpuGuard(args) ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--probe")
            {
                Thread.Sleep(30000);
                return;
            }
            if (args.Length > 0 && args[0] == "--probe-busy")
            {
                long x = 1;
                while (true) x = unchecked(x * 1664525 + 1013904223);
            }
            if (args.Length > 0 && args[0] == "--install-startup")
            {
                string error;
                Environment.ExitCode = StartupManager.Enable(out error) ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--remove-startup")
            {
                string error;
                Environment.ExitCode = StartupManager.Disable(out error) ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--enforcementcheck")
            {
                RunEnforcementCheck();
                Environment.ExitCode = CheckPassed("pspl-gui-enforcement.txt") ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--backgroundguardcheck")
            {
                RunBackgroundGuardCheck();
                Environment.ExitCode = CheckPassed("pspl-gui-background-guard.txt") ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--selftest")
            {
                RunSelfTest();
                Environment.ExitCode = CheckPassed("pspl-gui-selftest.txt") ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--accuracycheck")
            {
                RunAccuracyCheck();
                Environment.ExitCode = CheckPassed("pspl-gui-accuracy.txt") ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--uicheck")
            {
                RunUiCheck();
                Environment.ExitCode = CheckPassed("pspl-gui-uicheck.txt") ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--timing")
            {
                RunTiming();
                Environment.ExitCode = CheckPassed("pspl-gui-timing.txt") ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--startup")
            {
                RunStartup();
                Environment.ExitCode = CheckPassed("pspl-gui-startup.txt") ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--cadence")
            {
                RunCadence();
                Environment.ExitCode = CheckPassed("pspl-gui-cadence.txt") ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--report")
            {
                // optional: --report [output-path] — runs every check and writes one
                // consolidated report (defaults to %TEMP%\pspl-gui-report.txt)
                Environment.ExitCode = RunReport(args.Length > 1 ? args[1] : null) ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--ai-snapshot")
            {
                Environment.ExitCode = AiAutomation.Run("snapshot",
                    args.Length > 1 ? args[1] : null) ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--optimize-plan")
            {
                Environment.ExitCode = AiAutomation.Run("plan",
                    args.Length > 1 ? args[1] : null) ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--optimize-apply")
            {
                Environment.ExitCode = AiAutomation.Run("apply",
                    args.Length > 1 ? args[1] : null) ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--optimize-restore")
            {
                Environment.ExitCode = AiAutomation.Run("restore",
                    args.Length > 1 ? args[1] : null) ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--top20-plan")
            {
                Environment.ExitCode = AiAutomation.RunAdaptiveTop20(false,
                    args.Length > 1 ? args[1] : null) ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--top20-apply")
            {
                Environment.ExitCode = AiAutomation.RunAdaptiveTop20(true,
                    args.Length > 1 ? args[1] : null) ? 0 : 1;
                return;
            }
            if (args.Length > 0 && args[0] == "--tops")
            {
                RunTops();
                return;
            }
            ApplyMonitorPriority();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => { /* never crash on an exception */ };
            // The UI thread must build the window fast even when the machine is at
            // 100% CPU, so it gets a priority boost for the whole session.
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal; } catch { }
            bool background = args.Length > 0 && args[0] == "--background";
            bool created;
            string instanceScope =
                Environment.GetEnvironmentVariable(InstanceScopeEnvironment);
            using (var mutex = new Mutex(true,
                       ScopedKernelObjectName(InstanceMutexName, instanceScope), out created))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                       ScopedKernelObjectName(ShowEventName, instanceScope)))
            {
                if (!created)
                {
                    if (!background) showEvent.Set();
                    return;
                }
                int selfPid = Process.GetCurrentProcess().Id;
                long selfStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
                string cleanMarker = BackgroundGuardMarker(selfPid, selfStart);
                if (background) StartBackgroundGuard(selfPid, selfStart, cleanMarker);
                try
                {
                    Application.Run(new MainForm(true, background, showEvent));
                }
                finally
                {
                    if (background)
                    {
                        try { File.WriteAllText(cleanMarker, "clean"); } catch { }
                    }
                }
            }
        }

        private static string BackgroundGuardMarker(int pid, long startTicks)
        {
            return Path.Combine(Path.GetTempPath(), "psproclasso-background-clean-" +
                                pid + "-" + startTicks + ".flag");
        }

        private static string BackgroundRecoveryBudgetPath()
        {
            return Path.Combine(Path.GetTempPath(), "psproclasso-background-recovery-budget.txt");
        }

        private static void StartBackgroundGuard(int pid, long startTicks, string cleanMarker)
        {
            try
            {
                File.Delete(cleanMarker);
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.GetCommandLineArgs()[0],
                    Arguments = "--background-guard " + pid + " " + startTicks,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }

        private static void RunBackgroundGuard(string[] args)
        {
            int parentPid;
            long parentStart;
            if (!Int32.TryParse(args[1], out parentPid) ||
                !Int64.TryParse(args[2], out parentStart)) return;

            string marker = BackgroundGuardMarker(parentPid, parentStart);
            while (ProcessIdentityMatches(parentPid, parentStart)) Thread.Sleep(100);
            if (File.Exists(marker))
            {
                try { File.Delete(marker); } catch { }
                ClearBackgroundRecoveryBudget(BackgroundRecoveryBudgetPath());
                return;
            }

            int delayMs;
            if (!TryReserveBackgroundRestart(BackgroundRecoveryBudgetPath(), DateTime.UtcNow, out delayMs))
                return;
            Thread.Sleep(delayMs);
            for (int attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Environment.GetCommandLineArgs()[0],
                        Arguments = "--background",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    return;
                }
                catch { Thread.Sleep(1000); }
            }
        }

        private static bool TryReserveBackgroundRestart(string path, DateTime utcNow, out int delayMs)
        {
            delayMs = 0;
            Mutex mutex = null;
            bool held = false;
            try
            {
                mutex = new Mutex(false, BackgroundBudgetMutexName);
                try { held = mutex.WaitOne(10000); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return false;

                var recent = new List<long>();
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        long ticks;
                        if (!Int64.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
                            continue;
                        TimeSpan age = utcNow - new DateTime(ticks, DateTimeKind.Utc);
                        if (age >= TimeSpan.Zero && age <= BackgroundRestartWindow)
                            recent.Add(ticks);
                    }
                }
                if (recent.Count >= BackgroundRestartLimit) return false;
                recent.Add(utcNow.Ticks);
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(path, recent.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(),
                                   new UTF8Encoding(false));
                delayMs = Math.Min(8000, 500 * (1 << Math.Min(4, recent.Count - 1)));
                return true;
            }
            catch { return false; }
            finally
            {
                if (held) try { mutex.ReleaseMutex(); } catch { }
                if (mutex != null) mutex.Dispose();
            }
        }

        private static void ClearBackgroundRecoveryBudget(string path)
        {
            Mutex mutex = null;
            bool held = false;
            try
            {
                mutex = new Mutex(false, BackgroundBudgetMutexName);
                try { held = mutex.WaitOne(10000); }
                catch (AbandonedMutexException) { held = true; }
                if (held) File.Delete(path);
            }
            catch { }
            finally
            {
                if (held) try { mutex.ReleaseMutex(); } catch { }
                if (mutex != null) mutex.Dispose();
            }
        }

        private static bool VerifyBackgroundRecoveryBudgetContract()
        {
            string path = Path.Combine(Path.GetTempPath(), "pspl-background-budget-contract-" +
                          Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                DateTime now = new DateTime(638000000000000000L, DateTimeKind.Utc);
                int delay = 0;
                for (int i = 0; i < BackgroundRestartLimit; i++)
                {
                    if (!TryReserveBackgroundRestart(path, now.AddMilliseconds(i), out delay))
                        return false;
                    if (delay < 500 || delay > 8000) return false;
                }
                if (TryReserveBackgroundRestart(path, now.AddSeconds(1), out delay)) return false;
                if (!TryReserveBackgroundRestart(path, now.Add(BackgroundRestartWindow).AddSeconds(1), out delay))
                    return false;
                ClearBackgroundRecoveryBudget(path);
                return !File.Exists(path);
            }
            catch { return false; }
            finally { try { File.Delete(path); } catch { } }
        }

        private static bool RunGpuGuard(string[] args)
        {
            int targetPid, parentPid;
            long targetStart, parentStart;
            if (!Int32.TryParse(args[1], out targetPid) ||
                !Int32.TryParse(args[2], out parentPid) ||
                !Int64.TryParse(args[3], out targetStart) ||
                !Int64.TryParse(args[4], out parentStart)) return false;
            while (true)
            {
                if (!ProcessIdentityMatches(targetPid, targetStart)) return true;
                if (!ProcessIdentityMatches(parentPid, parentStart)) break;
                Thread.Sleep(50);
            }
            IntPtr handle = Native.OpenSuspendHandle(targetPid);
            if (handle == IntPtr.Zero) return false;
            try { return Native.NtResumeProcess(handle) >= 0; }
            catch { return false; }
            finally { Native.CloseHandle(handle); }
        }

        private static bool ProcessIdentityMatches(int pid, long startTicks)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return p.StartTime.ToUniversalTime().Ticks == startTicks;
            }
            catch { return false; }
        }

        private static void RunEnforcementCheck()
        {
            var log = new StringBuilder();
            string probePath = Path.Combine(Path.GetTempPath(), "PSPLResourceProbe.exe");
            string rulesPath = RulesStore.FilePath;
            string rulesBackupPath = rulesPath + ".bak";
            bool hadRulesFile = File.Exists(rulesPath);
            bool hadRulesBackup = File.Exists(rulesBackupPath);
            byte[] originalRulesBytes = hadRulesFile ? File.ReadAllBytes(rulesPath) : null;
            byte[] originalRulesBackupBytes = hadRulesBackup ? File.ReadAllBytes(rulesBackupPath) : null;
            Process probe = null;
            Sampler sam = null;
            try
            {
                File.Copy(Environment.GetCommandLineArgs()[0], probePath, true);
                probe = Process.Start(new ProcessStartInfo
                {
                    FileName = probePath,
                    Arguments = "--probe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                Thread.Sleep(500);
                sam = new Sampler { Rules = RulesStore.Load(), ProBalance = false, GpuOn = false };
                sam.Start();
                Thread.Sleep(800);

                sam.SetCpuLimit(probe.Id, 35);
                sam.SetRamLimit(probe.Id, 256);
                CpuLimitState cpu;
                RamLimitState ram;
                bool cpuLive = sam.CpuLimits.TryGetValue(probe.Id, out cpu);
                bool ramLive = sam.RamLimits.TryGetValue(probe.Id, out ram);
                Rule saved;
                bool persisted = sam.Rules.TryGetValue("PSPLResourceProbe", out saved) &&
                                 saved.cpuLimit == 35 && saved.ramLimit == 256;
                log.AppendLine("CPU cap live=" + cpuLive + " hard=" + (cpuLive && cpu.Hard));
                log.AppendLine("RAM cap live=" + ramLive + " hard=" + (ramLive && ram.Hard));
                log.AppendLine("rule persisted=" + persisted);

                sam.SetGpuLimit(probe.Id, 50);
                bool sawGpuOn = false, sawGpuOff = false;
                var gpuWatch = Stopwatch.StartNew();
                while (gpuWatch.ElapsedMilliseconds < 1800)
                {
                    GpuLimitState gpuState;
                    if (sam.GpuLimits.TryGetValue(probe.Id, out gpuState))
                    {
                        if (gpuState.Suspended) sawGpuOff = true;
                        else sawGpuOn = true;
                    }
                    Thread.Sleep(10);
                }
                Process guardProcess;
                bool guardRunning = sam.GpuGuards.TryGetValue(probe.Id, out guardProcess) &&
                                    guardProcess != null && !guardProcess.HasExited;
                log.AppendLine("GPU duty cycle observed running=" + sawGpuOn + " suspended=" + sawGpuOff);
                log.AppendLine("GPU recovery companion running=" + guardRunning);

                sam.RemoveLimits(probe.Id);
                Rule cleared;
                bool removalPersisted = sam.Rules.TryGetValue("PSPLResourceProbe", out cleared) &&
                                        cleared.cpuLimit == 0 && cleared.gpuLimit == 0 &&
                                        cleared.ramLimit == 0;
                bool liveRemoved = !sam.CpuLimits.ContainsKey(probe.Id) &&
                                   !sam.RamLimits.ContainsKey(probe.Id) &&
                                   !sam.GpuLimits.ContainsKey(probe.Id);
                bool probeResponsive = false;
                try { probe.Refresh(); probeResponsive = !probe.HasExited && probe.Responding; }
                catch { probeResponsive = false; }
                bool guardRemoved = !sam.GpuGuards.ContainsKey(probe.Id);
                log.AppendLine("live removal=" + liveRemoved);
                log.AppendLine("saved removal=" + removalPersisted);
                log.AppendLine("probe resumed=" + probeResponsive);
                log.AppendLine("GPU recovery companion removed=" + guardRemoved);

                bool processPolicy = VerifyPersistentProcessPolicy(probePath, sam, probe, log);
                bool crashRecovery = VerifyGpuCrashRecovery(probePath, log);
                log.AppendLine(cpuLive && ramLive && persisted && sawGpuOn && sawGpuOff &&
                               guardRunning && liveRemoved && removalPersisted &&
                               probeResponsive && guardRemoved && processPolicy && crashRecovery
                    ? "RESULT: OK - live and persistent resource-control contract."
                    : "RESULT: FAIL - resource-control contract.");
            }
            catch (Exception ex)
            {
                log.AppendLine("FATAL: " + ex);
                log.AppendLine("RESULT: FAIL");
            }
            finally
            {
                if (sam != null) sam.Stop();
                if (probe != null)
                {
                    try { if (!probe.HasExited) probe.Kill(); } catch { }
                    probe.Dispose();
                }
                try { File.Delete(probePath); } catch { }
                RestoreFileSnapshot(rulesPath, hadRulesFile, originalRulesBytes);
                RestoreFileSnapshot(rulesBackupPath, hadRulesBackup, originalRulesBackupBytes);
            }
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pspl-gui-enforcement.txt"), log.ToString()); } catch { }
        }

        private static void RestoreFileSnapshot(string path, bool existed, byte[] bytes)
        {
            try
            {
                if (!existed) { File.Delete(path); return; }
                string temp = path + ".restore-" + Process.GetCurrentProcess().Id;
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path))
                {
                    try { File.Replace(temp, path, null, true); }
                    catch { File.Copy(temp, path, true); File.Delete(temp); }
                }
                else File.Move(temp, path);
            }
            catch { }
        }

        private static bool VerifyPersistentProcessPolicy(string probePath, Sampler sam, Process first, StringBuilder log)
        {
            Process second = null;
            try
            {
                first.Refresh();
                long availableMask = first.ProcessorAffinity.ToInt64();
                int core = 0;
                while (core < Math.Min(63, Environment.ProcessorCount) &&
                       ((availableMask >> core) & 1L) == 0) core++;
                long expectedMask = 1L << core;
                sam.SetPriority(first.Id, "BelowNormal");
                sam.SetAffinity(first.Id, core.ToString(CultureInfo.InvariantCulture));
                Thread.Sleep(600);
                first.Refresh();
                bool firstApplied = first.PriorityClass == ProcessPriorityClass.BelowNormal &&
                                    first.ProcessorAffinity.ToInt64() == expectedMask;

                second = Process.Start(new ProcessStartInfo
                {
                    FileName = probePath, Arguments = "--probe",
                    UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                var wait = Stopwatch.StartNew();
                bool secondApplied = false;
                while (wait.ElapsedMilliseconds < 3000)
                {
                    Thread.Sleep(100);
                    try
                    {
                        second.Refresh();
                        secondApplied = second.PriorityClass == ProcessPriorityClass.BelowNormal &&
                                        second.ProcessorAffinity.ToInt64() == expectedMask;
                        if (secondApplied) break;
                    }
                    catch { }
                }
                Rule diskRule;
                bool saved = RulesStore.Load().TryGetValue("PSPLResourceProbe", out diskRule) &&
                             diskRule.priority == "BelowNormal" &&
                             diskRule.affinity != null && diskRule.affinity.Length == 1 &&
                             diskRule.affinity[0] == core;
                bool ok = firstApplied && secondApplied && saved;
                log.AppendLine("priority/affinity: first=" + firstApplied +
                               " newInstance=" + secondApplied + " saved=" + saved);
                return ok;
            }
            catch (Exception ex)
            {
                log.AppendLine("priority/affinity persistence failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (second != null)
                {
                    try { if (!second.HasExited) second.Kill(); } catch { }
                    try { second.Dispose(); } catch { }
                }
            }
        }

        private static bool VerifyGpuCrashRecovery(string probePath, StringBuilder log)
        {
            Process target = null, fakeParent = null, guard = null;
            IntPtr targetHandle = IntPtr.Zero;
            try
            {
                target = Process.Start(new ProcessStartInfo
                {
                    FileName = probePath, Arguments = "--probe-busy",
                    UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                fakeParent = Process.Start(new ProcessStartInfo
                {
                    FileName = probePath, Arguments = "--probe",
                    UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                Thread.Sleep(500);
                guard = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.GetCommandLineArgs()[0],
                    Arguments = "--gpu-guard " + target.Id + " " + fakeParent.Id + " " +
                                target.StartTime.ToUniversalTime().Ticks + " " +
                                fakeParent.StartTime.ToUniversalTime().Ticks,
                    UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                targetHandle = Native.OpenSuspendHandle(target.Id);
                if (targetHandle == IntPtr.Zero || Native.NtSuspendProcess(targetHandle) != 0) return false;
                target.Refresh();
                TimeSpan before = target.TotalProcessorTime;
                Thread.Sleep(300);
                target.Refresh();
                TimeSpan whileSuspended = target.TotalProcessorTime;
                fakeParent.Kill();
                fakeParent.WaitForExit(2000);
                bool guardExited = guard.WaitForExit(12000);
                bool guardReportedSuccess = guardExited && guard.ExitCode == 0;
                TimeSpan afterRecovery = whileSuspended;
                var recoveryWait = Stopwatch.StartNew();
                while (recoveryWait.ElapsedMilliseconds < 12000)
                {
                    Thread.Sleep(100);
                    target.Refresh();
                    afterRecovery = target.TotalProcessorTime;
                    if ((afterRecovery - whileSuspended).TotalMilliseconds > 40) break;
                }
                double suspendedDelta = (whileSuspended - before).TotalMilliseconds;
                double recoveredDelta = (afterRecovery - whileSuspended).TotalMilliseconds;
                bool ok = suspendedDelta < 40 && guardReportedSuccess && recoveredDelta > 40;
                log.AppendLine("GPU crash recovery: suspended CPU delta=" +
                               suspendedDelta.ToString("N0") + " ms, resumed delta=" +
                               recoveredDelta.ToString("N0") + " ms, guardSuccess=" +
                               guardReportedSuccess + " recovered=" + ok);
                return ok;
            }
            catch (Exception ex)
            {
                log.AppendLine("GPU crash recovery failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (targetHandle != IntPtr.Zero) Native.CloseHandle(targetHandle);
                foreach (var p in new[] { guard, fakeParent, target })
                {
                    if (p == null) continue;
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    try { p.Dispose(); } catch { }
                }
            }
        }

        private sealed class OracleCpuPoint
        {
            public int Id;
            public long StartTicks;
            public string Name;
            public TimeSpan Total;
            public long SampleTicks;
        }

        private static Dictionary<int, OracleCpuPoint> CaptureOracleCpuPoints()
        {
            var result = new Dictionary<int, OracleCpuPoint>();
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return result; }
            foreach (var process in processes)
            {
                try
                {
                    result[process.Id] = new OracleCpuPoint
                    {
                        Id = process.Id,
                        StartTicks = process.StartTime.ToUniversalTime().Ticks,
                        Name = process.ProcessName,
                        Total = process.TotalProcessorTime,
                        SampleTicks = Stopwatch.GetTimestamp()
                    };
                }
                catch { }
                finally { process.Dispose(); }
            }
            return result;
        }

        private static List<ProcRow> CaptureOracleCpuRows(int intervalMs)
        {
            var first = CaptureOracleCpuPoints();
            Thread.Sleep(intervalMs);
            var second = CaptureOracleCpuPoints();
            int cores = Math.Max(1, Environment.ProcessorCount);
            var rows = new List<ProcRow>();
            foreach (var current in second.Values)
            {
                OracleCpuPoint previous;
                if (!first.TryGetValue(current.Id, out previous) ||
                    previous.StartTicks != current.StartTicks) continue;
                double elapsedMs = (current.SampleTicks - previous.SampleTicks) *
                                   1000.0 / Stopwatch.Frequency;
                double deltaMs = (current.Total - previous.Total).TotalMilliseconds;
                double cpu = elapsedMs > 0 && deltaMs > 0
                    ? Math.Min(100, deltaMs / elapsedMs / cores * 100.0) : 0;
                rows.Add(new ProcRow
                {
                    Id = current.Id,
                    StartTicks = current.StartTicks,
                    Name = current.Name,
                    Cpu = cpu
                });
            }
            return rows;
        }

        private static List<ProcRow> CaptureOracleRamRows()
        {
            var rows = new List<ProcRow>();
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return rows; }
            foreach (var process in processes)
            {
                try
                {
                    rows.Add(new ProcRow
                    {
                        Id = process.Id,
                        StartTicks = process.StartTime.ToUniversalTime().Ticks,
                        Name = process.ProcessName,
                        Mem = process.WorkingSet64
                    });
                }
                catch { }
                finally { process.Dispose(); }
            }
            return rows;
        }

        private static List<ProcRow> CaptureOracleGpuRows(int intervalMs)
        {
            var counters = new List<KeyValuePair<string, PerformanceCounter>>();
            try
            {
                string[] names = new PerformanceCounterCategory("GPU Engine")
                    .GetInstanceNames();
                foreach (string name in names)
                {
                    try
                    {
                        var counter = new PerformanceCounter(
                            "GPU Engine", "Utilization Percentage", name, true);
                        counter.NextValue();
                        counters.Add(new KeyValuePair<string, PerformanceCounter>(
                            name, counter));
                    }
                    catch { }
                }
                if (counters.Count == 0) return new List<ProcRow>();
                Thread.Sleep(intervalMs);

                var byPid = new Dictionary<int, Dictionary<string, double>>();
                foreach (var pair in counters)
                {
                    try
                    {
                        int pid;
                        if (!TryParseOracleGpuPid(pair.Key, out pid)) continue;
                        string engine = OracleGpuEngineIdentity(pair.Key);
                        double value = Math.Max(0, pair.Value.NextValue());
                        Dictionary<string, double> engines;
                        if (!byPid.TryGetValue(pid, out engines))
                        {
                            engines = new Dictionary<string, double>(
                                StringComparer.OrdinalIgnoreCase);
                            byPid[pid] = engines;
                        }
                        double old;
                        if (!engines.TryGetValue(engine, out old)) old = 0;
                        engines[engine] = old + value;
                    }
                    catch { }
                }

                var rows = new List<ProcRow>();
                foreach (var entry in byPid)
                {
                    Process process = null;
                    try
                    {
                        process = Process.GetProcessById(entry.Key);
                        rows.Add(new ProcRow
                        {
                            Id = process.Id,
                            StartTicks = process.StartTime.ToUniversalTime().Ticks,
                            Name = process.ProcessName,
                            Gpu = Math.Min(100, entry.Value.Values.DefaultIfEmpty(0).Max()),
                            GpuValid = true
                        });
                    }
                    catch { }
                    finally { if (process != null) process.Dispose(); }
                }
                return rows;
            }
            catch { return new List<ProcRow>(); }
            finally
            {
                foreach (var pair in counters)
                    try { pair.Value.Dispose(); } catch { }
            }
        }

        private static bool TryParseOracleGpuPid(string instance, out int pid)
        {
            pid = 0;
            int start = instance.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return false;
            start += 4;
            int end = start;
            while (end < instance.Length && Char.IsDigit(instance[end])) end++;
            return end > start &&
                   Int32.TryParse(instance.Substring(start, end - start), out pid);
        }

        private static string OracleGpuEngineIdentity(string instance)
        {
            int start = instance.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
            string identity = start >= 0 ? instance.Substring(start) : instance;
            int suffix = identity.LastIndexOf('#');
            int duplicate;
            if (suffix > 0 &&
                Int32.TryParse(identity.Substring(suffix + 1), out duplicate))
                identity = identity.Substring(0, suffix);
            return identity;
        }

        private static bool SameProcessIdentity(ProcRow left, ProcRow right)
        {
            return left != null && right != null && left.Id == right.Id &&
                   (left.StartTicks == 0 || right.StartTicks == 0 ||
                    left.StartTicks == right.StartTicks);
        }

        private static bool TopSetAgrees(List<ProcRow> oracle, List<ProcRow> sampled,
                                         Func<ProcRow, double> metric, int count)
        {
            var oracleTop = oracle.OrderByDescending(metric).Take(count).ToList();
            var sampledTop = sampled.OrderByDescending(metric).Take(count).ToList();
            if (oracleTop.Count == 0 || sampledTop.Count == 0 ||
                metric(oracleTop[0]) <= 0) return false;
            bool oracleLeaderVisible = sampledTop.Any(x =>
                SameProcessIdentity(oracleTop[0], x));
            bool sampledLeaderVisible = oracleTop.Any(x =>
                SameProcessIdentity(sampledTop[0], x));
            return oracleLeaderVisible && sampledLeaderVisible;
        }

        private static bool RamLeaderAgrees(List<ProcRow> oracle, List<ProcRow> sampled)
        {
            var oracleTop = oracle.OrderByDescending(r => r.Mem).FirstOrDefault();
            var sampledTop = sampled.OrderByDescending(r => r.Mem).FirstOrDefault();
            if (oracleTop == null || sampledTop == null) return false;
            if (SameProcessIdentity(oracleTop, sampledTop)) return true;
            var sampledLeaderInOracle = oracle.FirstOrDefault(r =>
                SameProcessIdentity(r, sampledTop));
            if (sampledLeaderInOracle == null) return false;
            long tolerance = Math.Max(96L * 1024 * 1024,
                                      (long)(oracleTop.Mem * 0.05));
            return oracleTop.Mem - sampledLeaderInOracle.Mem <= tolerance;
        }

        private static string TopSummary(IEnumerable<ProcRow> rows,
                                         Func<ProcRow, double> metric,
                                         string suffix)
        {
            return String.Join(", ", rows.OrderByDescending(metric).Take(5)
                .Select(r => r.Name + "(" + r.Id + ")=" +
                    metric(r).ToString("N2", CultureInfo.InvariantCulture) + suffix)
                .ToArray());
        }

        private static void RunAccuracyCheck()
        {
            var log = new StringBuilder();
            Sampler sampler = null;
            try
            {
                sampler = new Sampler
                {
                    Rules = RulesStore.Load(),
                    ProBalance = false,
                    EnforcementEnabled = false,
                    AdaptiveTop20Enabled = false,
                    GpuOn = true
                };
                sampler.Start();
                var readyWait = Stopwatch.StartNew();
                while (readyWait.ElapsedMilliseconds < 12000 &&
                       (sampler.FastDataTick < 3 || !sampler.GpuFresh))
                    Thread.Sleep(100);

                int ramPass = 0;
                const int ramRounds = 6;
                for (int round = 0; round < ramRounds; round++)
                {
                    var oracle = CaptureOracleRamRows();
                    var sampled = sampler.Snap.Rows;
                    bool pass = RamLeaderAgrees(oracle, sampled);
                    if (pass) ramPass++;
                    log.AppendLine("RAM round " + (round + 1) + ": " +
                        (pass ? "agree" : "DISAGREE") + " oracle=" +
                        TopSummary(oracle, r => r.Mem / (1024.0 * 1024.0), "MB") +
                        " sampled=" +
                        TopSummary(sampled, r => r.Mem / (1024.0 * 1024.0), "MB"));
                    Thread.Sleep(250);
                }

                int cpuPass = 0;
                const int cpuRounds = 7;
                for (int round = 0; round < cpuRounds; round++)
                {
                    var oracle = CaptureOracleCpuRows(650);
                    var sampled = sampler.Snap.Rows;
                    bool pass = TopSetAgrees(oracle, sampled, r => r.Cpu, 5);
                    if (pass) cpuPass++;
                    log.AppendLine("CPU round " + (round + 1) + ": " +
                        (pass ? "agree" : "DISAGREE") + " oracle=" +
                        TopSummary(oracle, r => r.Cpu, "%") + " sampled=" +
                        TopSummary(sampled, r => r.Cpu, "%"));
                }

                int gpuPass = 0;
                const int gpuRounds = 3;
                for (int round = 0; round < gpuRounds; round++)
                {
                    var oracle = CaptureOracleGpuRows(1100);
                    var sampled = sampler.Snap.Rows.Where(r => r.GpuValid).ToList();
                    bool pass = sampler.GpuFresh &&
                                TopSetAgrees(oracle, sampled, r => r.Gpu, 5);
                    if (pass) gpuPass++;
                    log.AppendLine("GPU round " + (round + 1) + ": " +
                        (pass ? "agree" : "DISAGREE") + " oracle=" +
                        TopSummary(oracle, r => r.Gpu, "%") + " sampled=" +
                        TopSummary(sampled, r => r.Gpu, "%") +
                        " age=" + sampler.GpuDataAgeMs + "ms");
                }

                bool ramOk = ramPass >= 5;
                bool cpuOk = cpuPass >= 5;
                bool gpuOk = gpuPass >= 2;
                log.AppendLine("RAM agreement=" + ramPass + "/" + ramRounds);
                log.AppendLine("CPU agreement=" + cpuPass + "/" + cpuRounds);
                log.AppendLine("GPU agreement=" + gpuPass + "/" + gpuRounds);
                log.AppendLine("sampler errors=" + sampler.Errors.Count);
                foreach (string error in sampler.Errors)
                    log.AppendLine("  - " + error);
                log.AppendLine(ramOk && cpuOk && gpuOk && sampler.Errors.Count == 0
                    ? "RESULT: OK - independent Windows readings confirm live CPU, RAM, and GPU leaders."
                    : "RESULT: FAIL - independent leader agreement was below the required threshold.");
            }
            catch (Exception ex)
            {
                log.AppendLine("FATAL: " + ex);
                log.AppendLine("RESULT: FAIL");
            }
            finally
            {
                if (sampler != null) sampler.Stop();
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(),
                                      "pspl-gui-accuracy.txt"), log.ToString());
                }
                catch { }
            }
        }

        private static void RunTops()
        {
            // Dumps the top-10 by CPU / RAM / GPU exactly as the sampler computes them,
            // so ranking problems can be diagnosed from real data.
            var log = new StringBuilder();
            try
            {
                var sam = new Sampler();
                sam.Rules = RulesStore.Load();
                sam.ProBalance = false;
                sam.EnforcementEnabled = false;
                sam.Start();
                Thread.Sleep(13000);   // let warm-up + first steady GPU pass land
                var s = sam.Snap;
                var rows = s.Rows;
                log.AppendLine("total processes: " + s.ProcessCount);
                int withGpu = 0;
                foreach (var r in rows) if (r.Gpu > 0) withGpu++;
                log.AppendLine("processes with GPU>0: " + withGpu + " of " + s.ProcessCount);
                log.AppendLine("");
                var byCpu = rows.OrderByDescending(r => r.Cpu).Take(10);
                log.AppendLine("TOP 10 BY CPU:");
                foreach (var r in byCpu) log.AppendLine("   " + r.Name.PadRight(24) + " cpu=" + r.Cpu.ToString("N1") + "%  gpu=" + r.Gpu.ToString("N1") + "%  mem=" + DetailsForm.FmtBytes(r.Mem));
                log.AppendLine("");
                var byMem = rows.OrderByDescending(r => r.Mem).Take(10);
                log.AppendLine("TOP 10 BY RAM:");
                foreach (var r in byMem) log.AppendLine("   " + r.Name.PadRight(24) + " mem=" + DetailsForm.FmtBytes(r.Mem) + "  cpu=" + r.Cpu.ToString("N1") + "%");
                log.AppendLine("");
                var byGpu = rows.OrderByDescending(r => r.Gpu).Take(10);
                log.AppendLine("TOP 10 BY GPU:");
                foreach (var r in byGpu) log.AppendLine("   " + r.Name.PadRight(24) + " gpu=" + r.Gpu.ToString("N1") + "%  cpu=" + r.Cpu.ToString("N1") + "%");
                log.AppendLine("");
                log.AppendLine("errors=" + sam.Errors.Count);
                foreach (var x in sam.Errors) log.AppendLine("   - " + x);
                sam.Stop();
                log.AppendLine("RESULT: OK");
            }
            catch (Exception ex) { log.AppendLine("FATAL: " + ex); }
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pspl-gui-tops.txt"), log.ToString()); } catch { }
            Console.Write("");
        }

        private static bool RunReport(string outPath)
        {
            // Runs the entire verification battery and consolidates every check's output
            // into a single report, then prints a verdict to the console.
            var log = new StringBuilder();
            log.AppendLine("==============================================================");
            log.AppendLine(" PSProcLassoGUI — full verification report");
            log.AppendLine(" " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  ·  " + Environment.MachineName + "  ·  " + Environment.OSVersion.VersionString);
            log.AppendLine("==============================================================");

            string[] checkFiles = {
                "pspl-gui-selftest.txt", "pspl-gui-accuracy.txt",
                "pspl-gui-cadence.txt", "pspl-gui-startup.txt",
                "pspl-gui-timing.txt", "pspl-gui-uicheck.txt",
                "pspl-gui-enforcement.txt", "pspl-gui-background-guard.txt"
            };
            foreach (string name in checkFiles)
                try { File.Delete(Path.Combine(Path.GetTempPath(), name)); } catch { }

            RunSelfTest();
            log.AppendLine("--- [1/8] sampling self-test ---");
            AppendCheckOutput(log, "pspl-gui-selftest.txt");

            RunAccuracyCheck();
            log.AppendLine("--- [2/8] independent CPU/RAM/GPU accuracy comparison ---");
            AppendCheckOutput(log, "pspl-gui-accuracy.txt");

            RunCadence();
            log.AppendLine("--- [3/8] live sampling cadence (CPU/RAM ~500ms, GPU ~1s) ---");
            AppendCheckOutput(log, "pspl-gui-cadence.txt");

            RunStartup();
            log.AppendLine("--- [4/8] startup readiness (<3s table, <5s warmed GPU under load) ---");
            AppendCheckOutput(log, "pspl-gui-startup.txt");

            RunTiming();
            log.AppendLine("--- [5/8] warm-up timing ---");
            AppendCheckOutput(log, "pspl-gui-timing.txt");

            RunUiCheck();
            log.AppendLine("--- [6/8] UI construction + paint ---");
            AppendCheckOutput(log, "pspl-gui-uicheck.txt");

            RunEnforcementCheck();
            log.AppendLine("--- [7/8] live + persistent CPU/RAM/GPU controls ---");
            AppendCheckOutput(log, "pspl-gui-enforcement.txt");

            RunBackgroundGuardCheck();
            log.AppendLine("--- [8/8] hidden background crash recovery ---");
            AppendCheckOutput(log, "pspl-gui-background-guard.txt");

            bool allOk = checkFiles.All(CheckPassed);
            log.AppendLine("");
            log.AppendLine(allOk ? "RESULT: OK - ALL REPORTS PASSED." : "RESULT: FAIL - one or more checks failed (see sections above).");

            string path = String.IsNullOrEmpty(outPath) ? Path.Combine(Path.GetTempPath(), "pspl-gui-report.txt") : outPath;
            try { File.WriteAllText(path, log.ToString()); }
            catch (Exception ex) { Console.WriteLine("report write failed: " + ex.Message); allOk = false; }
            Console.WriteLine("PSProcLassoGUI: " + (allOk ? "ALL REPORTS PASSED" : "REPORT FAILED"));
            Console.WriteLine("Full report: " + path);
            return allOk;
        }

        private static bool CheckPassed(string name)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), name);
                string result = File.ReadAllLines(path)
                    .LastOrDefault(line => line.StartsWith("RESULT:", StringComparison.Ordinal));
                return result != null && result.StartsWith("RESULT: OK", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static void RunBackgroundGuardCheck()
        {
            var log = new StringBuilder();
            string probePath = Path.Combine(Path.GetTempPath(), "PSPLBackgroundGuardProbe.exe");
            Process first = null;
            Process replacement = null;
            try
            {
                File.Copy(Environment.GetCommandLineArgs()[0], probePath, true);
                var probeStart = new ProcessStartInfo
                {
                    FileName = probePath,
                    Arguments = "--background",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                probeStart.EnvironmentVariables[InstanceScopeEnvironment] =
                    "background-guard-check-" + Process.GetCurrentProcess().Id + "-" +
                    Guid.NewGuid().ToString("N");
                first = Process.Start(probeStart);
                int firstPid = first.Id;
                int firstGuardPid = WaitForCommandProcess(probePath, "--background-guard " + firstPid + " ", 30000, -1);
                first.Refresh();
                bool firstHidden = first.MainWindowHandle == IntPtr.Zero;
                first.Kill();
                first.WaitForExit(3000);

                int replacementPid = WaitForCommandProcess(probePath, "--background", 30000, firstPid);
                if (replacementPid > 0) replacement = Process.GetProcessById(replacementPid);
                int replacementGuardPid = replacementPid > 0
                    ? WaitForCommandProcess(probePath, "--background-guard " + replacementPid + " ", 30000, -1)
                    : -1;
                bool replacementHidden = false;
                if (replacement != null)
                {
                    replacement.Refresh();
                    replacementHidden = replacement.MainWindowHandle == IntPtr.Zero;
                    string marker = BackgroundGuardMarker(replacement.Id,
                                    replacement.StartTime.ToUniversalTime().Ticks);
                    File.WriteAllText(marker, "clean");
                    replacement.Kill();
                    replacement.WaitForExit(3000);
                }

                bool cleanExitStayedDown = WaitForNoCommandProcesses(probePath, 8000);
                bool ok = firstGuardPid > 0 && firstHidden && replacementPid > 0 &&
                          replacementGuardPid > 0 && replacementHidden && cleanExitStayedDown;
                log.AppendLine("initial background PID=" + firstPid + " guard PID=" + firstGuardPid +
                               " hidden=" + firstHidden);
                log.AppendLine("replacement background PID=" + replacementPid + " guard PID=" +
                               replacementGuardPid + " hidden=" + replacementHidden);
                log.AppendLine("clean-exit marker suppressed restart=" + cleanExitStayedDown);
                log.AppendLine(ok
                    ? "RESULT: OK - hidden background companion recovered a crash and honored clean exit."
                    : "RESULT: FAIL - hidden background crash-recovery contract.");
            }
            catch (Exception ex)
            {
                log.AppendLine("FATAL: " + ex);
                log.AppendLine("RESULT: FAIL");
            }
            finally
            {
                foreach (int pid in FindCommandProcesses(probePath, null))
                {
                    try { Process.GetProcessById(pid).Kill(); } catch { }
                }
                try { File.Delete(probePath); } catch { }
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(),
                                      "pspl-gui-background-guard.txt"), log.ToString());
                }
                catch { }
            }
        }

        private static int WaitForCommandProcess(string exePath, string commandToken,
                                                 int timeoutMs, int excludedPid)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                foreach (int pid in FindCommandProcesses(exePath, commandToken))
                    if (pid != excludedPid) return pid;
                Thread.Sleep(100);
            }
            return -1;
        }

        private static bool WaitForNoCommandProcesses(string exePath, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (FindCommandProcesses(exePath, null).Count == 0) return true;
                Thread.Sleep(100);
            }
            return FindCommandProcesses(exePath, null).Count == 0;
        }

        private static List<int> FindCommandProcesses(string exePath, string commandToken)
        {
            var result = new List<int>();
            try
            {
                using (var search = new ManagementObjectSearcher(
                    "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process"))
                using (var found = search.Get())
                {
                    foreach (ManagementObject item in found)
                    {
                        string path = Convert.ToString(item["ExecutablePath"]);
                        string command = Convert.ToString(item["CommandLine"]);
                        if (!String.Equals(path, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                        if (commandToken != null)
                        {
                            if (commandToken == "--background")
                            {
                                if (command.IndexOf("--background", StringComparison.OrdinalIgnoreCase) < 0 ||
                                    command.IndexOf("--background-guard", StringComparison.OrdinalIgnoreCase) >= 0)
                                    continue;
                            }
                            else if (command.IndexOf(commandToken, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        }
                        result.Add(Convert.ToInt32(item["ProcessId"]));
                    }
                }
            }
            catch { }
            return result;
        }

        private static void AppendCheckOutput(StringBuilder log, string name)
        {
            try
            {
                log.AppendLine(File.ReadAllText(Path.Combine(Path.GetTempPath(), name)).TrimEnd('\r', '\n'));
            }
            catch (Exception ex) { log.AppendLine("(could not read check output: " + ex.Message + ")"); }
            log.AppendLine("");
        }

        private static void RunTiming()
        {
            // Measures real per-pass timings over ~8s: fast-cycle cost, GPU-pass cost,
            // and the wall-clock gap between successive GPU data points (the number that
            // matters for "does GPU update at least once a second?").
            var log = new StringBuilder();
            try
            {
                var sam = new Sampler();
                sam.Rules = RulesStore.Load();
                sam.ProBalance = false;   // don't demote real processes during a measurement run
                sam.EnforcementEnabled = false;
                sam.Start();
                var sw = Stopwatch.StartNew();
                var fast = new List<long>();
                var gpu = new List<long>();
                var gpuCycles = new List<long>();
                while (sw.ElapsedMilliseconds < 5200)
                {
                    Thread.Sleep(100);
                    fast.Add(sam.LastFastMs);
                    if (sam.LastGpuPassMs > 0) gpu.Add(sam.LastGpuPassMs);
                    if (sam.LastGpuCycleMs > 0) gpuCycles.Add(sam.LastGpuCycleMs);
                }
                sam.Stop();
                long fastAverage = fast.Count > 0 ? (long)fast.Average() : Int64.MaxValue;
                long fastMax = fast.Count > 0 ? fast.Max() : Int64.MaxValue;
                long gpuAverage = gpu.Count > 0 ? (long)gpu.Average() : Int64.MaxValue;
                long gpuMax = gpu.Count > 0 ? gpu.Max() : Int64.MaxValue;
                if (fast.Count > 0) log.AppendLine("warm-up fast cycle: avg " + fastAverage + " ms, max " + fastMax + " ms over " + fast.Count + " samples");
                if (gpu.Count > 0) log.AppendLine("gpu pass:   avg " + gpuAverage + " ms, max " + gpuMax + " ms over " + gpu.Count + " samples");
                if (gpuCycles.Count > 1) log.AppendLine("warm-up gpu cycle (pass-to-pass): avg " + (long)gpuCycles.Average() + " ms, max " + gpuCycles.Max() + " ms over " + (gpuCycles.Count - 1) + " cycles");
                else log.AppendLine("gpu: no cycles measured (counters unavailable)");
                log.AppendLine("steady state target: CPU/RAM 500 ms; GPU 1000 ms");
                log.AppendLine("errors=" + sam.Errors.Count);
                foreach (var x in sam.Errors) log.AppendLine("   - " + x);
                bool timingOk = sam.Errors.Count == 0 && fast.Count > 0 &&
                                fastAverage <= 400 && fastMax <= 900 &&
                                gpu.Count > 0 && gpuCycles.Count > 1 &&
                                gpuAverage <= 900 && gpuMax <= 1700;
                log.AppendLine(timingOk
                    ? "RESULT: OK - measured CPU/RAM and GPU timing stayed within bounds."
                    : "RESULT: FAIL - timing samples missing, errors logged, or bounds exceeded.");
            }
            catch (Exception ex) { log.AppendLine("FATAL: " + ex); log.AppendLine("RESULT: FAIL"); }
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pspl-gui-timing.txt"), log.ToString()); } catch { }
        }

        private static void RunCadence()
        {
            // Verifies data-delivery ticks rather than waiting for utilization values to
            // change, because an idle machine can legitimately report the same value.
            var log = new StringBuilder();
            try
            {
                var sam = new Sampler();
                sam.Rules = RulesStore.Load();
                sam.ProBalance = false;
                sam.EnforcementEnabled = false;
                sam.Start();
                var sw = Stopwatch.StartNew();
                long lastFast = -1, lastGpu = -1;
                var fastTimes = new List<long>();
                var gpuTimes = new List<long>();
                while (sw.ElapsedMilliseconds < 5500)
                {
                    Thread.Sleep(50);
                    long f = Interlocked.Read(ref sam.FastDataTick);
                    long g = Interlocked.Read(ref sam.GpuDataTick);
                    if (f != lastFast) { fastTimes.Add(sw.ElapsedMilliseconds); lastFast = f; }
                    if (g != lastGpu) { gpuTimes.Add(sw.ElapsedMilliseconds); lastGpu = g; }
                }
                sam.Stop();
                long fastGap = LastGapAfter(fastTimes, 2500);
                long gpuGap = LastGapAfter(gpuTimes, 2500);
                log.AppendLine("CPU/RAM sample ticks: " + fastTimes.Count + "; last steady gap=" + fastGap + " ms");
                log.AppendLine("GPU sample ticks: " + gpuTimes.Count + "; last steady gap=" + gpuGap + " ms");
                bool fastOk = fastGap >= 250 && fastGap <= 900;
                bool gpuUnavailable = sam.Errors.Count == 0 && sam.GpuDataTick == 0;
                bool gpuOk = gpuUnavailable || (gpuGap >= 500 && gpuGap <= 1700);
                log.AppendLine(fastOk && gpuOk
                    ? "RESULT: OK - live CPU/RAM and GPU cadence."
                    : "RESULT: FAIL - cadence outside target.");
            }
            catch (Exception ex) { log.AppendLine("FATAL: " + ex); }
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pspl-gui-cadence.txt"), log.ToString()); } catch { }
        }

        private static long LastGapAfter(List<long> times, long after)
        {
            var steady = times.Where(x => x >= after).ToList();
            return steady.Count >= 2 ? steady[steady.Count - 1] - steady[steady.Count - 2] : -1;
        }

        private static void RunStartup()
        {
            // Measures how fast the full app (real MainForm + sampler + UI timer) has
            // live data: process table, CPU/RAM totals, and first GPU data.
            var log = new StringBuilder();
            var sw = Stopwatch.StartNew();
            try
            {
                Application.EnableVisualStyles();
                var f = new MainForm(false, false, null, false);
                long formMs = sw.ElapsedMilliseconds;
                long firstTable = -1, firstTotals = -1, firstGpu = -1;
                while (sw.ElapsedMilliseconds < 6000)
                {
                    Thread.Sleep(50);
                    var s = f.SamplerForTest.Snap;
                    if (firstTable < 0 && s.ProcessCount > 0) firstTable = sw.ElapsedMilliseconds;
                    if (firstTotals < 0 && s.TotalCpu > 0) firstTotals = sw.ElapsedMilliseconds;
                    if (firstGpu < 0 && (s.GpuPct > 0 || f.SamplerForTest.GpuDataTick > 0)) firstGpu = sw.ElapsedMilliseconds;
                    if (firstTable >= 0 && firstTotals >= 0 && firstGpu >= 0) break;
                }
                log.AppendLine("form constructed:  " + formMs + " ms");
                log.AppendLine("first process table: " + firstTable + " ms");
                log.AppendLine("first CPU/RAM totals: " + firstTotals + " ms");
                log.AppendLine("first GPU data:       " + firstGpu + " ms");
                log.AppendLine("errors=" + f.SamplerForTest.Errors.Count);
                foreach (var x in f.SamplerForTest.Errors) log.AppendLine("   - " + x);
                f.ShutdownForTest();
                bool ready = firstTable >= 0 && firstTable < 3000 &&
                             firstTotals >= 0 && firstTotals < 3000 &&
                             firstGpu >= 0 && firstGpu < 5000 &&
                             f.SamplerForTest.Errors.Count == 0;
                log.AppendLine(ready
                    ? "RESULT: OK - table/totals ready in <3s and warmed GPU in <5s under load."
                    : "RESULT: SLOW - table=" + firstTable + " totals=" + firstTotals +
                      " GPU=" + firstGpu + " ms");
            }
            catch (Exception ex) { log.AppendLine("FATAL: " + ex); }
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pspl-gui-startup.txt"), log.ToString()); } catch { }
        }

        private static void RunUiCheck()
        {
            var log = new StringBuilder();
            try
            {
                bool applicationGrouping = MainForm.VerifyApplicationGroupingContract();
                log.AppendLine("application-family grouping=" + applicationGrouping);
                bool embeddedIcon = VerifyExecutableIconContract();
                log.AppendLine("embedded executable icon=" + embeddedIcon);
                Application.EnableVisualStyles();
                var f = new MainForm(false, false, null, false);
                f.ShowInTaskbar = false;
                f.Opacity = 0;
                f.Show();
                var showWait = Stopwatch.StartNew();
                while (showWait.ElapsedMilliseconds < 4000 && !f.SamplerForTest.GpuFresh)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }
                f.RefreshAllForTest();
                Application.DoEvents();
                string[] menu = f.TrayMenuTextsForTest();
                log.AppendLine("tray menu: " + string.Join("  |  ", menu));
                bool startupMenuChecked = f.StartupMenuCheckedForTest();
                bool startupMenuAccurate = startupMenuChecked == StartupManager.IsEnabled();
                log.AppendLine("startup menu checked=" + startupMenuChecked +
                               " matches scheduler=" + startupMenuAccurate);
                // Drive the list through both render paths (stable in-place updates and
                // real re-sorts) for 3s — the calm-order logic must never throw.
                var sw2 = Stopwatch.StartNew();
                int renders = 0, resort = 0;
                while (sw2.ElapsedMilliseconds < 3000)
                {
                    f.SortForTest((sw2.ElapsedMilliseconds % 700) < 350 ? 0 : 2);   // alternate CPU/GPU sort
                    f.RefreshListOnce();
                    Application.DoEvents();
                    renders++;
                    Thread.Sleep(50);
                }
                f.SortForTest(2); f.RefreshListOnce();   // force one more re-sort path
                resort++;
                log.AppendLine("list renders: " + renders + " (in-place + re-sort paths), no exceptions");
                // Click the three resource buttons, including repeat clicks, and verify
                // they always mean highest-first rather than silently toggling ascending.
                string clicks = "";
                f.ClickSortButton(0); f.ClickSortButton(0);
                clicks += "CPU->" + f.SortForTestGet + "/descending=" + (!f.SortAscendingForTest) + " ";
                f.ClickSortButton(1); f.ClickSortButton(1);
                clicks += "RAM->" + f.SortForTestGet + "/descending=" + (!f.SortAscendingForTest) + " ";
                f.ClickSortButton(2); f.ClickSortButton(2);
                clicks += "GPU->" + f.SortForTestGet + "/descending=" + (!f.SortAscendingForTest);
                log.AppendLine("sort buttons: " + clicks);
                f.RefreshListOnce();
                bool topVisible = f.VisibleTopMatchesSortForTest();
                log.AppendLine("GPU highest consumer visible at top: " + topVisible);
                bool sortButtonsOk = !f.SortAscendingForTest && f.SortForTestGet == "Gpu" && topVisible;

                int fullRowCount = f.VisibleRowCountForTest;
                bool liveSearch = false, searchUsesProcessNoun = false;
                int filteredRowCount = -1;
                if (fullRowCount > 0)
                {
                    string query = f.CurrentProcessSearchQueryForTest;
                    if (!String.IsNullOrWhiteSpace(query))
                    {
                        f.SetSearchForTest(query);
                        f.RefreshAllForTest();
                        Application.DoEvents();
                        filteredRowCount = f.VisibleRowCountForTest;
                        liveSearch = filteredRowCount == 1 &&
                                     f.SearchTextForTest == query;
                        searchUsesProcessNoun =
                            f.StatusTextForTest.Contains("Search: 1/") &&
                            f.StatusTextForTest.Contains(" processes") &&
                            !f.StatusTextForTest.Contains(" apps");
                        f.SetSearchForTest("");
                        f.RefreshAllForTest();
                        Application.DoEvents();
                        liveSearch = liveSearch && f.SearchTextForTest.Length == 0 &&
                                     f.VisibleRowCountForTest > filteredRowCount;
                    }
                }
                log.AppendLine("instant search: liveNamePidFilter=" + liveSearch +
                               " processNoun=" + searchUsesProcessNoun +
                               " fullRows=" + fullRowCount +
                               " filteredRows=" + filteredRowCount +
                               " restoredRows=" + f.VisibleRowCountForTest);
                bool defaultProcessView = !f.GroupApplicationsForTest &&
                                          f.VisibleRowsAreSinglePidForTest &&
                                          f.StatusTextForTest.Contains("View: PROCESSES") &&
                                          searchUsesProcessNoun;
                bool processViewContract = MainForm.VerifyProcessViewContract();
                bool unavailableGpuRendering =
                    MainForm.VerifyUnavailableGpuRenderingContract();
                bool calmRefreshCadence =
                    MainForm.VerifyCalmRefreshCadenceContract();
                log.AppendLine("default process view: enabled=" + defaultProcessView +
                               " onePidContract=" + processViewContract +
                               " unavailableGpuMarker=" + unavailableGpuRendering +
                               " calmOneSecondPaint=" + calmRefreshCadence);

                // Bulk-copy contract: users must be able to select several processes,
                // keep that selection through live refreshes, and copy a structured
                // table directly into an AI chat.
                var listField = typeof(MainForm).GetField("_list",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var processList = (ListView)listField.GetValue(f);
                bool multiSelect = processList.MultiSelect;
                int selectionBefore = 0, selectionAfter = 0;
                bool copiedTable = false, honestGpuAggregate = false;
                if (processList.Items.Count >= 2)
                {
                    processList.Items[0].Selected = true;
                    processList.Items[1].Selected = true;
                    selectionBefore = processList.SelectedItems.Count;
                    f.RefreshListOnce();
                    selectionAfter = processList.SelectedItems.Count;
                    var selectionLabelField = typeof(MainForm).GetField("_lblSel",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var selectionLabel = (Label)selectionLabelField.GetValue(f);
                    honestGpuAggregate = selectionLabel.Text.Contains("GPU top ") &&
                                         !selectionLabel.Text.Contains("GPU sum ");
                    try
                    {
                        var selectedRows = processList.SelectedItems.Cast<ListViewItem>()
                            .Select(x => x.Tag as ProcRow).Where(x => x != null).ToList();
                        string copied = MainForm.BuildClipboardText(selectedRows, f.SamplerForTest.Snap);
                        copiedTable = copied.Contains("NAME") && copied.Contains("CPU%") &&
                                      copied.Contains("RAM") && copied.Contains("GPU%");
                    }
                    catch { copiedTable = false; }
                }
                bool bulkCopyOk = multiSelect && selectionBefore >= 2 &&
                                  selectionAfter == selectionBefore && copiedTable &&
                                  honestGpuAggregate;
                log.AppendLine("bulk selection/copy: multi=" + multiSelect +
                                " selected=" + selectionBefore + "->" + selectionAfter +
                                " structuredClipboard=" + copiedTable +
                                " honestGpuAggregate=" + honestGpuAggregate);

                bool dragRangeSelection = processList.Items.Count >= 8 &&
                                          f.SelectDragRangeForTest(1, 6) &&
                                          processList.SelectedItems.Count == 6 &&
                                          processList.Items[1].Selected &&
                                          processList.Items[6].Selected;
                bool dragAutoScroll = MainForm.VerifyDragAutoScrollContract();
                log.AppendLine("click-hold drag range selection=" + dragRangeSelection +
                               " edgeAutoScroll=" + dragAutoScroll);
                if (dragRangeSelection)
                {
                    var selectionBitmap = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
                    f.DrawToBitmap(selectionBitmap,
                        new Rectangle(0, 0, f.ClientSize.Width, f.ClientSize.Height));
                    selectionBitmap.Save(Path.Combine(Path.GetTempPath(),
                        "pspl-gui-uicheck-selection.png"));
                    selectionBitmap.Dispose();
                }
                bulkCopyOk = bulkCopyOk && dragRangeSelection && dragAutoScroll;

                // A nonzero resource must never be rendered as a literal 0.0%, and
                // automatic live reorders must keep the highest row visible without
                // destroying/recreating every ListViewItem (the source of flashing).
                var formatRam = typeof(MainForm).GetMethod("FormatRamCell",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                string tinyRam = (string)formatRam.Invoke(null,
                    new object[] { 2L * 1024 * 1024, 96L * 1024 * 1024 * 1024 });
                bool nonzeroPrecision = !tinyRam.StartsWith("0.0%", StringComparison.Ordinal);
                bool bufferedList = processList is SmoothListView;

                bool topSurvivesLiveReorder = false, rowsReused = false;
                if (processList.Items.Count >= 4)
                {
                    processList.SelectedItems.Clear();
                    processList.Items[processList.Items.Count - 1].Selected = true;
                    f.ClickSortButton(1);
                    f.RefreshListOnce();
                    var oldFirst = processList.Items[0];
                    var orderField = typeof(MainForm).GetField("_dispOrder",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var forced = new List<string>();
                    foreach (ListViewItem it in processList.Items)
                        if (it.Tag is ProcRow) forced.Add(((ProcRow)it.Tag).GroupKey);
                    forced.Reverse();
                    orderField.SetValue(f, forced);
                    f.RefreshListOnce();
                    topSurvivesLiveReorder = f.VisibleTopMatchesSortForTest();
                    foreach (ListViewItem it in processList.Items)
                        if (Object.ReferenceEquals(it, oldFirst)) { rowsReused = true; break; }
                }
                bool calmAccurateList = nonzeroPrecision && bufferedList &&
                                        topSurvivesLiveReorder && rowsReused;
                log.AppendLine("accuracy/calmness: tinyRAM='" + tinyRam +
                               "' topAfterLiveReorder=" + topSurvivesLiveReorder +
                               " rowObjectsReused=" + rowsReused +
                               " doubleBuffered=" + bufferedList);

                bool allMetricTops = true;
                string[] metricNames = { "cpu", "ram", "gpu" };
                for (int metric = 0; metric < 3; metric++)
                {
                    f.ClickSortButton(metric);
                    f.RefreshListOnce();
                    Application.DoEvents();
                    bool metricTop = f.VisibleTopMatchesSortForTest();
                    allMetricTops = allMetricTops && metricTop;
                    string first = processList.Items.Count > 0
                        ? processList.Items[0].SubItems[1].Text + " " +
                          processList.Items[0].SubItems[metric == 0 ? 2 : metric == 1 ? 3 : 4].Text
                        : "(none)";
                    var metricBitmap = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
                    f.DrawToBitmap(metricBitmap, new Rectangle(0, 0, f.ClientSize.Width, f.ClientSize.Height));
                    metricBitmap.Save(Path.Combine(Path.GetTempPath(),
                        "pspl-gui-uicheck-" + metricNames[metric] + ".png"));
                    metricBitmap.Dispose();
                    log.AppendLine(metricNames[metric].ToUpperInvariant() +
                                   " visible leader: " + first + " correct=" + metricTop);
                }

                // Force a full offscreen render — exercises OnPaint of the meters, the
                // gradient panels, column headers, and every owner-drawn row.
                bool paintOk = false;
                try
                {
                    var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.ClientSize.Width, f.ClientSize.Height));
                    bmp.Save(Path.Combine(Path.GetTempPath(), "pspl-gui-uicheck.png"));
                    log.AppendLine("painted " + f.ClientSize.Width + "x" + f.ClientSize.Height + " offscreen (all controls + owner-draw), no exceptions");
                    paintOk = true;
                    bmp.Dispose();
                }
                catch (Exception ex) { log.AppendLine("PAINT FAILED: " + ex.Message); }
                f.ShutdownForTest();
                log.AppendLine(applicationGrouping && defaultProcessView &&
                               processViewContract && unavailableGpuRendering &&
                               calmRefreshCadence && embeddedIcon && startupMenuAccurate &&
                               liveSearch &&
                               sortButtonsOk && bulkCopyOk &&
                               calmAccurateList && allMetricTops && paintOk
                    ? "RESULT: OK - trustworthy one-PID ranking, optional grouping, calm repaint, and honest GPU availability."
                    : "RESULT: FAIL - process view, search, grouping, icon, selection, ranking, calmness, or GPU availability failed.");
            }
            catch (Exception ex)
            {
                log.AppendLine("FAILED: " + ex);
                log.AppendLine("RESULT: FAIL");
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "pspl-gui-uicheck.txt"), log.ToString());
        }

        private static bool VerifyExecutableIconContract()
        {
            try
            {
                using (var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                {
                    if (icon == null || icon.Width < 16 || icon.Height < 16) return false;
                    using (var bitmap = icon.ToBitmap())
                    {
                        int vividGreen = 0, opaque = 0;
                        for (int y = 0; y < bitmap.Height; y++)
                        {
                            for (int x = 0; x < bitmap.Width; x++)
                            {
                                Color color = bitmap.GetPixel(x, y);
                                if (color.A < 80) continue;
                                opaque++;
                                if (color.G > color.R + 25 && color.G > color.B + 5 &&
                                    color.G > 110) vividGreen++;
                            }
                        }
                        return opaque > 40 && vividGreen >= 5;
                    }
                }
            }
            catch { return false; }
        }

        private static void RunSelfTest()
        {
            var log = new StringBuilder();
            try
            {
                bool atomicGpuPublication = Sampler.VerifyAtomicGpuPublicationContract();
                bool pidSafeCpuDelta = Sampler.VerifyPidSafeCpuDeltaContract();
                bool cpuReadFailureRecovery = Sampler.VerifyCpuReadFailureRecoveryContract();
                bool gpuCounterFailureIsolation = Sampler.VerifyGpuCounterFailureIsolationContract();
                bool gpuWarmupSafety = Sampler.VerifyGpuWarmupContract();
                bool gpuFreshnessSafety = Sampler.VerifyGpuFreshnessContract();
                bool processIdentitySafety = Sampler.VerifyProcessIdentitySafetyContract();
                bool nativeStatusSafety = Sampler.VerifyNativeStatusSafetyContract();
                bool resourceReleaseSafety = Sampler.VerifyResourceReleaseSafetyContract();
                bool legacyBoostIsolationSafety =
                    Sampler.VerifyLegacyBoostIsolationContract();
                bool startupPrivilegeSafety = StartupManager.VerifyLeastPrivilegeContract();
                bool startupDisabledStateSafety = StartupManager.VerifyDisabledStateContract();
                bool concurrentRuleSafety = RulesStore.VerifyConcurrentMutationContract();
                bool backupRuleSafety = RulesStore.VerifyBackupRecoveryContract();
                bool recoveryBudgetSafety = VerifyBackgroundRecoveryBudgetContract();
                bool searchFilterSafety = MainForm.VerifySearchFilterContract();
                bool processViewSafety = MainForm.VerifyProcessViewContract();
                bool unavailableGpuRenderingSafety =
                    MainForm.VerifyUnavailableGpuRenderingContract();
                bool calmRefreshCadenceSafety =
                    MainForm.VerifyCalmRefreshCadenceContract();
                bool optimizerPolicySafety = SafeOptimizer.VerifyPolicyContract();
                bool optimizerWorkflowSafety = OptimizationWorkflow.VerifyContract();
                bool optimizerProgressUiSafety = OptimizationProgressForm.VerifyContract();
                bool adaptiveTop20Safety = SafeOptimizer.VerifyAdaptiveTop20Contract();
                bool aiAutomationSafety = AiAutomation.VerifyJsonContract();
                bool monitorPrioritySafety = VerifyMonitorPriorityContract();
                bool diagnosticInstanceScopeSafety = VerifyInstanceScopeContract();
                log.AppendLine("atomic GPU publication=" + atomicGpuPublication);
                log.AppendLine("PID-safe CPU delta=" + pidSafeCpuDelta);
                log.AppendLine("CPU read failure recovery=" + cpuReadFailureRecovery);
                log.AppendLine("GPU counter failure isolation=" + gpuCounterFailureIsolation);
                log.AppendLine("GPU counter warm-up safety=" + gpuWarmupSafety);
                log.AppendLine("GPU publication freshness=" + gpuFreshnessSafety);
                log.AppendLine("process identity safety=" + processIdentitySafety);
                log.AppendLine("native suspend/resume status safety=" + nativeStatusSafety);
                log.AppendLine("resource release safety=" + resourceReleaseSafety);
                log.AppendLine("legacy boost isolation=" + legacyBoostIsolationSafety);
                log.AppendLine("startup least-privilege safety=" + startupPrivilegeSafety);
                log.AppendLine("startup disabled-state safety=" + startupDisabledStateSafety);
                log.AppendLine("concurrent rule mutation safety=" + concurrentRuleSafety);
                log.AppendLine("rules backup recovery safety=" + backupRuleSafety);
                log.AppendLine("background recovery budget safety=" + recoveryBudgetSafety);
                log.AppendLine("instant search filter safety=" + searchFilterSafety);
                log.AppendLine("one-PID process view safety=" + processViewSafety);
                log.AppendLine("unavailable GPU rendering safety=" +
                               unavailableGpuRenderingSafety);
                log.AppendLine("calm visible refresh cadence=" +
                               calmRefreshCadenceSafety);
                log.AppendLine("safe optimizer policy=" + optimizerPolicySafety);
                log.AppendLine("measured optimizer workflow=" + optimizerWorkflowSafety);
                log.AppendLine("optimizer progress UI=" + optimizerProgressUiSafety);
                log.AppendLine("adaptive top-20 performance policy=" + adaptiveTop20Safety);
                log.AppendLine("AI automation JSON contract=" + aiAutomationSafety);
                log.AppendLine("monitor responsiveness priority=" + monitorPrioritySafety);
                log.AppendLine("diagnostic instance isolation=" + diagnosticInstanceScopeSafety);

                var sam = new Sampler();
                sam.Rules = RulesStore.Load();
                sam.ProBalance = false;   // diagnostics must observe, never alter, real processes
                sam.EnforcementEnabled = false;
                sam.Start();
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 15000)
                {
                    Thread.Sleep(250);
                    var s = sam.Snap;
                    bool cpuReady = sam.FastDataTick >= 3;
                    bool ramReady = s.RamTotal > 0 && s.AvailMB > 0 && s.RamUsed > 0;
                    bool gpuReady = sam.GpuFresh && sam.GpuDataTick >= 2;
                    bool gpuRowsReady = s.GpuPct <= 0 || s.Rows.Any(r => r.Gpu > 0);
                    if (!cpuReady || !ramReady || !gpuReady || !gpuRowsReady || s.ProcessCount == 0) continue;

                    log.AppendLine("cycle: cpu=" + s.TotalCpu.ToString("N1") + "% gpu=" + s.GpuPct.ToString("N1") +
                                   "% procs=" + s.ProcessCount + " ram=" + DetailsForm.FmtBytes(s.RamUsed) +
                                   " vram=" + DetailsForm.FmtBytes(s.VramUsed) + "/" + DetailsForm.FmtBytes(s.VramTotal) +
                                   " avail=" + DetailsForm.FmtBytes((long)(s.AvailMB * 1024 * 1024)) + " standby=" + DetailsForm.FmtBytes(s.Standby));
                    log.AppendLine("TOP 5 CPU:");
                    foreach (var r in s.Rows.OrderByDescending(r => r.Cpu).ThenBy(r => r.Name).Take(5))
                        log.AppendLine("   " + r.Name + "  cpu=" + r.Cpu.ToString("N1") + "%  mem=" + DetailsForm.FmtBytes(r.Mem));
                    log.AppendLine("TOP 5 RAM:");
                    foreach (var r in s.Rows.OrderByDescending(r => r.Mem).ThenBy(r => r.Name).Take(5))
                        log.AppendLine("   " + r.Name + "  mem=" + DetailsForm.FmtBytes(r.Mem) + "  cpu=" + r.Cpu.ToString("N1") + "%");
                    log.AppendLine("TOP 5 GPU:");
                    foreach (var r in s.Rows.OrderByDescending(r => r.Gpu).ThenBy(r => r.Name).Take(5))
                        log.AppendLine("   " + r.Name + "  gpu=" + r.Gpu.ToString("N1") + "%  cpu=" + r.Cpu.ToString("N1") + "%");
                    break;
                }
                var errs = sam.Errors;
                log.AppendLine("errors=" + errs.Count);
                if (errs.Count > 0) foreach (var x in errs) log.AppendLine("   - " + x);
                var final = sam.Snap;
                bool ready = sam.FastDataTick >= 3 && final.ProcessCount > 0 &&
                             final.RamTotal > 0 && final.AvailMB > 0 &&
                             sam.GpuFresh && sam.GpuDataTick >= 2 &&
                             (final.GpuPct <= 0 || final.Rows.Any(r => r.Gpu > 0));
                if (!ready)
                    log.AppendLine("readiness: fastTicks=" + sam.FastDataTick +
                                   " gpuTicks=" + sam.GpuDataTick +
                                   " gpuReady=" + sam.GpuFresh +
                                   " processes=" + final.ProcessCount +
                                   " ramTotal=" + final.RamTotal +
                                   " availableMB=" + final.AvailMB +
                                   " totalGpu=" + final.GpuPct.ToString("N3") +
                                   " gpuRows=" + final.Rows.Count(r => r.Gpu > 0));
                sam.Stop();
                log.AppendLine(errs.Count == 0 && ready && atomicGpuPublication && pidSafeCpuDelta &&
                                cpuReadFailureRecovery && gpuCounterFailureIsolation && gpuWarmupSafety &&
                                gpuFreshnessSafety
                                 && processIdentitySafety && nativeStatusSafety && resourceReleaseSafety
                                 && legacyBoostIsolationSafety
                                 && startupPrivilegeSafety && startupDisabledStateSafety &&
                                 concurrentRuleSafety && backupRuleSafety
                                && recoveryBudgetSafety && searchFilterSafety && processViewSafety &&
                                unavailableGpuRenderingSafety && calmRefreshCadenceSafety &&
                                optimizerPolicySafety && optimizerWorkflowSafety &&
                                optimizerProgressUiSafety &&
                                adaptiveTop20Safety &&
                                aiAutomationSafety &&
                                monitorPrioritySafety && diagnosticInstanceScopeSafety
                    ? "RESULT: OK - warmed CPU/RAM/GPU sampling with zero exceptions."
                    : "RESULT: FAIL - sampling readiness, atomic publication, PID identity, or errors failed.");
            }
            catch (Exception ex)
            {
                log.AppendLine("FATAL: " + ex);
            }
            string outFile = Path.Combine(Path.GetTempPath(), "pspl-gui-selftest.txt");
            try { File.WriteAllText(outFile, log.ToString()); } catch { }
            Console.Write(""); // ensure reference; selftest output is written to the temp file
        }
    }
}
