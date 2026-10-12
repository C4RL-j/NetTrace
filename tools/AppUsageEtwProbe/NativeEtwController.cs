using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AppUsageEtwProbe;

public sealed record NativeEtwSessionDescriptor(string Name, Guid SessionGuid, uint LogFileMode,
    bool HasLogFile, ulong SessionHandle, int EventsLost);

// TraceEvent 3.2.8 exposes no custom session GUID setter. This narrow controller
// creates the same real-time provider with a deterministic GUID; event consumption
// remains in the shared TDH/TraceEvent parser. It never enumerates or stops sessions.
public sealed class NativeEtwController : IDisposable
{
    public const uint RealTimeMode = 0x100;
    public const uint IndependentMode = 0x08000000;
    public const uint StopOnHybridShutdownMode = 0x00400000;
    public const uint PersistOnHybridShutdownMode = 0x00800000;
    public const uint ExpectedLogFileMode = RealTimeMode | IndependentMode | StopOnHybridShutdownMode;
    // Windows selected PERSIST automatically for the original Session-0 prototype.
    // Accept only that observed legacy mode and the new explicitly selected mode,
    // never arbitrary flags, file logging, buffering, or private/system loggers.
    public static bool IsExpectedRealtimeMode(uint mode) => mode == ExpectedLogFileMode
        || mode == (RealTimeMode | IndependentMode | PersistOnHybridShutdownMode);
    private readonly string name;
    private readonly Guid guid;
    private ulong handle;
    private bool owned;

    public NativeEtwController(string name, Guid guid)
    {
        this.name = name;
        this.guid = guid != Guid.Empty ? guid : throw new ArgumentException("An explicit project GUID is required.");
    }

    public void Start(ulong keywordMask)
    {
        using var properties = new PropertiesBuffer(name, guid, creating: true);
        uint error = StartTrace(out handle, name, properties.Pointer);
        Throw(error, "Create owned real-time ETW session");
        owned = true;
        try
        {
            var provider = ProviderSchema.ProviderGuid;
            Throw(EnableTraceEx2(handle, ref provider, 1, 5, keywordMask, 0, 5000, IntPtr.Zero),
                "Enable installed Kernel-Network provider");
        }
        catch { Stop(); throw; }
    }

    public int EventsLost => Query(name)?.EventsLost
        ?? throw new InvalidOperationException("Owned ETW session is no longer available.");

    public void DisableAndFlush()
    {
        if (!owned || !VerifyCurrentLifetime()) return;
        var provider = ProviderSchema.ProviderGuid;
        Throw(EnableTraceEx2(handle, ref provider, 0, 0, 0, 0, 5000, IntPtr.Zero), "Disable owned ETW provider");
        using var properties = new PropertiesBuffer(name, guid);
        Throw(ControlTrace(handle, null, properties.Pointer, 3), "Flush owned ETW session");
    }

    public void Stop()
    {
        if (!owned || !VerifyCurrentLifetime()) return;
        // A handle identifies the created lifetime. Passing it instead of the
        // name avoids deleting a later replacement if an external admin stopped it.
        using var properties = new PropertiesBuffer(name, guid);
        uint error = ControlTrace(handle, null, properties.Pointer, 1);
        if (error != 4201) Throw(error, "Stop owned ETW session");
        owned = false;
    }

    private bool VerifyCurrentLifetime()
    {
        var current = Query(name);
        if (current is null) { owned = false; return false; }
        if (current.Name != name || current.SessionGuid != guid || current.SessionHandle != handle
            || !IsExpectedRealtimeMode(current.LogFileMode) || current.HasLogFile)
            throw new InvalidOperationException("Owned ETW session identity changed; refusing provider/session mutation.");
        return true;
    }

    public static NativeEtwSessionDescriptor? Query(string name)
    {
        using var properties = new PropertiesBuffer(name, Guid.Empty);
        uint error = ControlTrace(0, name, properties.Pointer, 0);
        if (error == 4201) return null;
        Throw(error, "Query exact ETW session");
        var value = Marshal.PtrToStructure<EventTraceProperties>(properties.Pointer);
        string actualName = properties.ReadString(value.LoggerNameOffset);
        return new(actualName, value.Wnode.Guid, value.LogFileMode,
            properties.ReadString(value.LogFileNameOffset).Length > 0, value.Wnode.HistoricalContext,
            checked((int)value.EventsLost));
    }

    public static void StopVerified(NativeEtwSessionDescriptor observed)
    {
        var current = Query(observed.Name);
        if (current is null) return;
        if (current.SessionGuid != observed.SessionGuid || current.SessionHandle != observed.SessionHandle
            || current.LogFileMode != observed.LogFileMode || current.HasLogFile != observed.HasLogFile)
            throw new InvalidOperationException("ETW session identity changed during recovery; refusing cleanup.");
        using var properties = new PropertiesBuffer(observed.Name, observed.SessionGuid);
        uint error = ControlTrace(observed.SessionHandle, null, properties.Pointer, 1);
        if (error != 4201) Throw(error, "Recover verified stale ETW session");
    }

    public void Dispose() => Stop();
    private static void Throw(uint error, string operation)
    {
        if (error != 0) throw new Win32Exception(checked((int)error), operation + " failed.");
    }

    private sealed class PropertiesBuffer : IDisposable
    {
        private const int Characters = 1024;
        private readonly int headerSize = Marshal.SizeOf<EventTraceProperties>();
        public IntPtr Pointer { get; }
        private int TotalSize => headerSize + Characters * 4;
        public PropertiesBuffer(string name, Guid guid, bool creating = false)
        {
            if (string.IsNullOrEmpty(name) || name.Length >= Characters)
                throw new ArgumentException("Invalid exact ETW session name.", nameof(name));
            Pointer = Marshal.AllocHGlobal(TotalSize);
            Marshal.Copy(new byte[TotalSize], 0, Pointer, TotalSize);
            var value = new EventTraceProperties
            {
                Wnode = new() { BufferSize = (uint)TotalSize, Guid = guid, ClientContext = 1, Flags = 0x00020000 },
                BufferSize = 64, MinimumBuffers = 256, MaximumBuffers = 330,
                LogFileMode = ExpectedLogFileMode, FlushTimer = 1,
                LoggerNameOffset = (uint)headerSize,
                LogFileNameOffset = creating ? 0u : (uint)(headerSize + Characters * 2)
            };
            Marshal.StructureToPtr(value, Pointer, false);
            Marshal.Copy((name + '\0').ToCharArray(), 0, IntPtr.Add(Pointer, headerSize), name.Length + 1);
        }
        public string ReadString(uint offset)
        {
            if (offset == 0) return "";
            if (offset < headerSize || offset >= TotalSize || offset % 2 != 0)
                throw new InvalidOperationException("Unexpected ETW session property layout.");
            int remaining = (TotalSize - (int)offset) / 2;
            string text = Marshal.PtrToStringUni(IntPtr.Add(Pointer, (int)offset), remaining)!;
            int terminator = text.IndexOf('\0');
            if (terminator < 0) throw new InvalidOperationException("Unterminated ETW session property.");
            return text[..terminator];
        }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize, ProviderId;
        public ulong HistoricalContext, TimeStamp;
        public Guid Guid;
        public uint ClientContext, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
    private static extern uint StartTrace(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
    private static extern uint ControlTrace(ulong handle, string? name, IntPtr properties, uint control);
    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level,
        ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr parameters);
}
