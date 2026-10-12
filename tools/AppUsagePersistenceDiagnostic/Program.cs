using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using AppUsageIpc;
using AppUsageStorage;
using ApplicationIdentity = AppUsageStorage.ApplicationIdentity;

// Explicit development diagnostics only. Database reads use the 004.1 storage
// API; this tool has no SQL implementation and adds no public pipe operation.
string? output = null, dataRoot = null;
bool query = false, traffic = false;
try
{
    for (int index = 0; index < args.Length; index++)
        switch (args[index])
        {
            case "--query": query = true; break;
            case "--traffic": traffic = true; break;
            case "--output": output = Path.GetFullPath(args[++index]); break;
            case "--data-root": dataRoot = Path.GetFullPath(args[++index]); break;
            default: throw new ArgumentException("Unknown diagnostic option.");
        }
    if (query == traffic || output is null || query && dataRoot is null)
        throw new ArgumentException("Choose a bounded traffic run or an existing database query.");
    if (query)
    {
        string database = DailyAppUsageStore.GetDatabasePath(dataRoot!);
        if (!File.Exists(database)) throw new FileNotFoundException("Diagnostic reads never create a database.", database);
        var store = new DailyAppUsageStore(dataRoot!);
        var rows = store.GetUsageForRange(DateOnly.MinValue, DateOnly.MaxValue);
        await WriteAsync(output, new { DatabasePath = database, DatabaseBytes = new FileInfo(database).Length,
            SchemaVersion = DailyAppUsageStore.CurrentSchemaVersion, Rows = rows });
        return 0;
    }

    using var identity = WindowsIdentity.GetCurrent();
    bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    if (elevated) throw new InvalidOperationException("Controlled traffic must run from the normal-token wrapper.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(28));
    await using var client = new IpcClient(ServicePipeSecurity.DefaultPipeName,
        serviceName: ServicePipeSecurity.DefaultServiceName);
    var connected = await client.ConnectAsync(timeout.Token);
    if (connected.State != IpcState.Connected) throw new InvalidOperationException("Service diagnostic pipe was unavailable.");
    var capabilities = await client.GetCapabilitiesAsync(timeout.Token);
    if (capabilities.Value?.ServiceIdentity != "LocalSystem")
        throw new InvalidOperationException("The controlled service must be LocalSystem.");
    var elapsed = Stopwatch.StartNew();
    // Keep authenticated IPC requests within their existing three-second idle
    // deadline while the independently bounded network questions are pending.
    var exchangesTask = Task.WhenAll(
        SmallDnsExchangeAsync(AddressFamily.InterNetwork, timeout.Token),
        SmallDnsExchangeAsync(AddressFamily.InterNetworkV6, timeout.Token));
    CollectorSnapshot? snapshot = null;
    int snapshots = 0;
    while (elapsed.Elapsed < TimeSpan.FromSeconds(20))
    {
        var result = await client.GetSnapshotAsync(timeout.Token);
        if (result.State != IpcState.Connected || result.Value is null)
            throw new InvalidOperationException($"Service snapshot failed: {result.State}: {result.Detail}");
        snapshot = result.Value;
        snapshots++;
        await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
    }
    var exchanges = await exchangesTask;
    string path = Environment.ProcessPath!;
    var application = snapshot?.Applications.FirstOrDefault(value =>
        string.Equals(value.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
    await WriteAsync(output, new { ClientElevated = false, ClientPid = Environment.ProcessId,
        ExecutablePath = path, ApplicationIdentity = ApplicationIdentity.FromExecutablePath(path).Key,
        Seconds = elapsed.Elapsed.TotalSeconds, SnapshotCount = snapshots,
        Exchanges = exchanges, Application = application, LastSnapshot = snapshot });
    bool capturedBothFamilies = application is not null &&
        application.Counters.Ipv4RxBytes > 0 && application.Counters.Ipv4TxBytes > 0 &&
        application.Counters.Ipv6TxBytes > 0 &&
        application.Counters.NonLoopbackRxBytes > 0 && application.Counters.NonLoopbackTxBytes > 0;
    return capturedBothFamilies ? 0 : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Persistence diagnostic failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}

static async Task WriteAsync(string path, object value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value,
        new JsonSerializerOptions { WriteIndented = true }));
}

static async Task<object> SmallDnsExchangeAsync(AddressFamily family, CancellationToken token)
{
    // One standard example.com question: 29 transmitted payload bytes, at most
    // 512 received bytes, four seconds, no retry. Responses/endpoints are not
    // written to artifacts. No browser or VPN state is changed.
    byte[] request = [0x4e, 0x54, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
        0x03, (byte)'c', (byte)'o', (byte)'m', 0x00, 0x00, 0x01, 0x00, 0x01];
    var address = IPAddress.Parse(family == AddressFamily.InterNetwork ? "8.8.8.8" : "2001:4860:4860::8888");
    using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
    deadline.CancelAfter(TimeSpan.FromSeconds(4));
    int sent = 0, received = 0;
    string? failure = null;
    try
    {
        await socket.ConnectAsync(new IPEndPoint(address, 53), deadline.Token);
        sent = await socket.SendAsync(request, SocketFlags.None, deadline.Token);
        byte[] response = new byte[512];
        received = await socket.ReceiveAsync(response, SocketFlags.None, deadline.Token);
        if (received < 12 || response[0] != request[0] || response[1] != request[1])
            throw new InvalidDataException("Controlled DNS response did not match its question.");
    }
    catch (Exception exception) when (exception is SocketException or OperationCanceledException or InvalidDataException)
    {
        failure = exception.GetType().Name;
    }
    return new { Family = family == AddressFamily.InterNetwork ? "IPv4" : "IPv6",
        SentBytes = sent, ReceivedBytes = received, Success = received >= 12 && failure is null, Failure = failure };
}
