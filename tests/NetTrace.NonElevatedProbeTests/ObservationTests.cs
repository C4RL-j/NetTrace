using System.Net;
using AppUsageEtwProbe;
using AppUsageNonElevatedProbe;

internal static class ObservationTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("An event header PID cannot replace a missing observed attribution field", Check.Sync(HeaderPidIsNotAttribution)),
        ("Incomplete occurrences of one event schema cannot combine into coherent byte attribution", Check.Sync(IncompleteOccurrences)),
    ];

    private static void HeaderPidIsNotAttribution()
    {
        var resolver = new FakeResolver();
        var observation = CreateObservation(resolver);
        observation.Add(Raw(new Dictionary<string, object?> { ["NumBytes"] = 64U }));
        Check.Equal(0, resolver.Calls, "header PID does not trigger fabricated process lookup");
        Check.Equal(0, observation.Processes.Count, "header PID is not accepted as a process identity");
        Check.Equal(0L, observation.AttributedTransferBytes, "bytes are not attributed from the header execution context");
        Check.Equal(64L, observation.TransferBytesWithoutPid, "unattributed observed bytes remain visible");
        var classification = Classify(observation);
        Check.True(!classification.HasAttribution && !classification.HasCoherentAccounting,
            "payload attribution is reported missing despite a positive header PID");
    }

    private static void IncompleteOccurrences()
    {
        var observation = CreateObservation(new FakeResolver());
        observation.Add(Raw(new Dictionary<string, object?> { ["Pid"] = 1234U }));
        observation.Add(Raw(new Dictionary<string, object?> { ["NumBytes"] = 64U }));
        var incomplete = Classify(observation);
        Check.True(incomplete.HasAttribution && incomplete.HasByteCount,
            "separately observed fields remain reportable as partial capabilities");
        Check.True(!incomplete.HasCoherentAccounting,
            "no actual occurrence carried both payload attribution and transferred bytes");
        Check.Equal(ProviderSuitability.PartiallySuitable, incomplete.Suitability,
            "complementary incomplete occurrences cannot make a fully suitable provider");
        Check.Equal(0L, observation.AttributedTransferBytes, "no attributed bytes are invented");
        Check.Equal(2L, observation.Events.Sum(item => item.EventCount), "both incomplete occurrences remain counted");

        observation.Add(Raw(new Dictionary<string, object?> { ["Pid"] = 1234U, ["NumBytes"] = 128U }));
        var complete = Classify(observation);
        Check.True(complete.HasCoherentAccounting, "a later complete occurrence establishes coherent accounting");
        Check.Equal(ProviderSuitability.FullySuitable, complete.Suitability,
            "complete observed accounting can pass supplied controlled verification");
        Check.Equal(128L, observation.AttributedTransferBytes, "only the complete occurrence contributes attributed bytes");
    }

    private static CandidateObservation CreateObservation(FakeResolver resolver)
    {
        var descriptor = new CandidateEventDescriptor(900, 2, "FixtureTransfer", "FixtureSend",
            [new PayloadField("Pid", "win:UInt32"), new PayloadField("NumBytes", "win:UInt32")],
            "Pid", "NumBytes", TrafficDirection.Send, true, false, null, null);
        var schema = new CandidateProviderSchema(new("Fixture provider", Guid.NewGuid()),
            true, 1, [descriptor], [], 1);
        return new(schema, resolver, new HashSet<IPAddress>());
    }

    private static CandidateRawEvent Raw(IReadOnlyDictionary<string, object?> payload) =>
        new(900, 2, 4321, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), payload);

    private static CapabilityClassification Classify(CandidateObservation observation) =>
        CapabilityClassifier.Classify(new(true, true, true, false, true,
            observation.Events.Sum(item => item.EventCount), 0, true, observation.Events));

    private sealed class FakeResolver : IProcessResolver
    {
        public int Calls { get; private set; }
        public ProcessIdentity Resolve(int pid, DateTime eventTimeUtc)
        {
            Calls++;
            return new(pid, "fixture.exe", @"C:\Fixtures\fixture.exe", DateTime.UnixEpoch, "Deterministic fixture");
        }
    }
}
