using System.Text.Json.Nodes;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Remote.DgLab.V4;

namespace YokoWave.Tests.Remote;

public sealed class DgLabV4ProtocolTests
{
    [Fact]
    public void PulseVersionsDecodeToNormalizedSamplesAndRejectInvalidFrames()
    {
        var first = DgLabV4Pulse.Decode(RemoteProtocol.Element(new[] { "21010A", "000000" }), 2);
        Assert.Equal(100, first[0][0].FrequencyHz);
        Assert.Equal(1, first[0][0].PulseWidth);
        Assert.Equal(0, first[1][0].PulseWidth);
        var second = DgLabV4Pulse.Decode(RemoteProtocol.Element(new[] { new[] { 10, 100, 200, 240, 0, 25, 50, 100 } }), 3);
        Assert.Equal(new[] { 100, 10, 1000d / 600, 1 }, second[0].Select(sample => sample.FrequencyHz));
        Assert.Equal(new[] { 0, .25, .5, 1 }, second[0].Select(sample => sample.PulseWidth));
        var frame = new OutputFrame(25, 10, 20, ReferenceSample: new(1, 20, .5));
        Assert.Equal("3232323232323232", DgLabV4Pulse.Encode(frame, 0));
        Assert.Throws<DgLabV4Exception>(() => DgLabV4Pulse.Decode(RemoteProtocol.Element(new[] { "0A" }), 3));
    }

    [Fact]
    public void SchedulerQueuesReplacesClearsAndCompletesTasksWithoutPerformingIo()
    {
        var scheduler = new DgLabV4Scheduler();
        DgLabV4Operation Level(int type, int value, double duration = 0, bool immediate = false) => new("slot", type, 0, RemoteProtocol.Element(value), Duration: duration, Immediate: immediate);
        scheduler.Submit("add", Level(3, 20));
        scheduler.Submit("temporary", Level(4, 30, 500));
        scheduler.Submit("queued", Level(4, 10, 500));
        Assert.Equal(25, scheduler.Evaluate(TimeSpan.Zero).ChannelA);
        Assert.Equal("completed", Assert.Single(scheduler.DrainCompleted()).Reason);
        Assert.Equal(15, scheduler.Evaluate(TimeSpan.FromMilliseconds(500)).ChannelA);
        Assert.Equal("temporary", Assert.Single(scheduler.DrainCompleted()).Id);
        scheduler.Submit("replacement", Level(4, 50, 500, true));
        Assert.Equal("replaced", Assert.Single(scheduler.DrainCompleted()).Reason);
        Assert.Equal(35, scheduler.Evaluate(TimeSpan.FromMilliseconds(600)).ChannelA);
        scheduler.Clear(0);
        Assert.Equal("cleared", Assert.Single(scheduler.DrainCompleted()).Reason);
        scheduler.Submit("zero", Level(7, 0));
        Assert.Equal(0, scheduler.Evaluate(TimeSpan.FromMilliseconds(700)).ChannelA);
        Assert.Equal("completed", Assert.Single(scheduler.DrainCompleted()).Reason);
        scheduler.Submit("cancel", Level(4, 20));
        scheduler.Cancel();
        Assert.Equal("cancelled", Assert.Single(scheduler.DrainCompleted()).Reason);
        Assert.Equal(0, scheduler.Evaluate(TimeSpan.FromSeconds(1)).ChannelA);
    }

    [Fact]
    public void DeviceCatalogIsolatesAppsDeepMergesAndRestrictsTransportCapabilities()
    {
        var catalog = new DgLabV4DeviceCatalog(Guid.NewGuid());
        object Snapshot(string name) => new
        {
            ev = "devices.snapshot",
            devices = new[] { new DgLabV4Device("slot", name, "COYOTE_030", JsonNode.Parse("{\"power\":80}")!.AsObject(),
            JsonNode.Parse("{\"hasDevice\":true,\"channelA\":{\"isMuted\":false,\"intensityMax\":90}}")!.AsObject()) }
        };
        catalog.ApplyEvent("one", RemoteProtocol.Element(Snapshot("First")));
        catalog.ApplyEvent("two", RemoteProtocol.Element(Snapshot("Second")));
        var slots = catalog.Snapshot();
        Assert.Equal(2, slots.Count);
        Assert.NotEqual(slots[0].Id, slots[1].Id);
        catalog.ApplyEvent("one", RemoteProtocol.Element(new { ev = "slots.patch", slots = new[] { new { slotId = "slot", slotState = new { channelA = new { isMuted = true } } } } }));
        var first = catalog.Snapshot().Single(slot => slot.AppId == "one");
        Assert.Equal(0, first.Limit(0));
        Assert.Equal(90, first.Device.SlotState!["channelA"]!["intensityMax"]!.GetValue<int>());
        Assert.True(first.Device.SlotState!["channelA"]!["isMuted"]!.GetValue<bool>());
        Assert.True(first.Descriptor.Supports(DeviceCapability.StatusQuery));
        Assert.False(first.Descriptor.Supports(DeviceCapability.Balance));
        Assert.False(new NativeProgramSettings(NativeProgramKind.CoyoteV2Pulse).Accepts(first.Descriptor));
        catalog.RemoveApp("one");
        Assert.Equal("Second", Assert.Single(catalog.Snapshot()).Device.Name);
    }

    [Fact]
    public void SensorFreshnessChangesOnlyWithMeasurementsAndDescriptorsReplaceMembership()
    {
        var clock = new ManualInputClock();
        var catalog = new DgLabV4DeviceCatalog(Guid.NewGuid(), clock);
        catalog.ApplyEvent("app", RemoteProtocol.Element(new
        {
            ev = "devices.snapshot",
            devices = new[]
        {
            new DgLabV4Device("sensor", "Sensor", "BMTR_1", new() { ["pressure"] = 2 }),
            new DgLabV4Device("unknown", "Unknown", "FUTURE_DEVICE")
        }
        }));
        Assert.False(catalog.Snapshot().Single(slot => slot.Device.SlotId == "unknown").Descriptor.IsSupported);
        var captured = catalog.Snapshot().Single(slot => slot.Device.SlotId == "sensor").CapturedAt;
        clock.Advance(3);
        catalog.ApplyEvent("app", RemoteProtocol.Element(new { ev = "slots.patch", slots = new[] { new { slotId = "sensor", props = new { power = 90 } } } }));
        Assert.Equal(captured, catalog.Snapshot().Single(slot => slot.Device.SlotId == "sensor").CapturedAt);
        catalog.ApplyDescriptions("app", RemoteProtocol.Element(new { devices = new[] { new DgLabV4Device("sensor", "Renamed", "BMTR_1") } }), catalog.Revision("app"));
        var sensor = Assert.Single(catalog.Snapshot());
        Assert.Equal(2, sensor.Device.Props!["pressure"]!.GetValue<int>());
        Assert.Equal(captured, sensor.CapturedAt);
        catalog.ApplyEvent("app", RemoteProtocol.Element(new { ev = "slots.patch", slots = new[] { new { slotId = "sensor", props = new { updateTime = "next-sample" } } } }));
        Assert.Equal(clock.GetUtcNow(), Assert.Single(catalog.Snapshot()).CapturedAt);
    }

    [Fact]
    public void TaskPrioritiesAndChannelClearsRemainIndependent()
    {
        var scheduler = new DgLabV4Scheduler(2047);
        scheduler.Submit("base", new("s", 3, 0, RemoteProtocol.Element(1023.5)));
        scheduler.Submit("b", new("s", 4, 1, RemoteProtocol.Element(2047)));
        scheduler.Submit("low", new("s", 0, 0, RemoteProtocol.Element(new[] { "3232323228282828" }), Priority: 0));
        scheduler.Submit("high", new("s", 0, 0, RemoteProtocol.Element(new[] { "3232323264646464" }), Priority: 2));
        var frame = scheduler.Evaluate(TimeSpan.Zero);
        Assert.Equal(50, frame.ChannelA);
        Assert.Equal(100, frame.ChannelB);
        Assert.Equal(1, frame.ReferenceSample!.PulseWidth);
        scheduler.Clear(0);
        Assert.Equal(100, scheduler.Evaluate(TimeSpan.FromMilliseconds(100)).ChannelB);
        Assert.Equal(new[] { "completed", "cleared", "cleared" }, scheduler.DrainCompleted().Select(result => result.Reason));
        Assert.Throws<DgLabV4Exception>(() => new DgLabV4Scheduler(double.NaN));
        Assert.Throws<DgLabV4Exception>(() => scheduler.Submit("invalid", new("s", 7, 0, RemoteProtocol.Element(1))));
    }

    [Fact]
    public void StaleQueriesCannotUndoNewerEventsAndInvalidMeasurementsDoNotBecomeFresh()
    {
        var clock = new ManualInputClock();
        var catalog = new DgLabV4DeviceCatalog(Guid.NewGuid(), clock);
        long initial = catalog.Revision("app");
        catalog.ApplyEvent("app", RemoteProtocol.Element(new
        {
            ev = "devices.snapshot",
            devices = new[]
        {
            new DgLabV4Device("s", "Sensor", "BMTR_1", new() { ["pressure"] = 3 }, new() { ["hasDevice"] = true })
        }
        }));
        Assert.False(catalog.ApplyDescriptions("app", RemoteProtocol.Element(new { devices = new DgLabV4Device[0] }), initial));
        Assert.Single(catalog.Snapshot());
        clock.Advance(4);
        catalog.ApplyEvent("app", RemoteProtocol.Element(new { ev = "slots.patch", slots = new[] { new { slotId = "s", props = new { pressure = (double?)null } } } }));
        var slot = Assert.Single(catalog.Snapshot());
        Assert.Equal(DateTimeOffset.MinValue, slot.CapturedAt);
        Assert.Null(slot.Status(clock.GetUtcNow()).Telemetry!.PressureKpa);
        long revision = catalog.Revision("app");
        Assert.Throws<DgLabV4Exception>(() => catalog.ApplyEvent("app", RemoteProtocol.Element(new { ev = "devices.snapshot", devices = new[] { new DgLabV4Device("duplicate", "a", "BMTR_1"), new DgLabV4Device("duplicate", "b", "BMTR_1") } })));
        Assert.Equal(revision, catalog.Revision("app"));
        Assert.Equal(slot.Id, Assert.Single(catalog.Snapshot()).Id);
        catalog.Clear();
        Assert.False(catalog.ApplyDescriptions("app", RemoteProtocol.Element(new { devices = new[] { slot.Device } }), revision));
        Assert.Empty(catalog.Snapshot());
    }

    [Fact]
    public void TaskWindowsRespectSubTickDurationsAndImmediateReplacementAtCapacity()
    {
        var scheduler = new DgLabV4Scheduler();
        scheduler.Submit("level", new("s", 4, 0, RemoteProtocol.Element(100), Duration: 50));
        scheduler.Submit("pulse", new("s", 0, 0, RemoteProtocol.Element(new[] { "3232323264646464" }), Duration: 50));
        var frame = scheduler.Evaluate(TimeSpan.Zero);
        Assert.Equal(new[] { 1d, 1, 0, 0 }, frame.WindowA!.Select(sample => sample.Level));
        Assert.Equal(new[] { 1d, 1, 0, 0 }, frame.WindowA!.Select(sample => sample.PulseWidth));
        Assert.Equal(0, scheduler.Evaluate(TimeSpan.FromMilliseconds(100)).ChannelA);
        Assert.Equal(2, scheduler.DrainCompleted().Count);
        for (int i = 0; i < 256; i++)
            scheduler.Submit(i.ToString(), new("s", 4, 0, RemoteProtocol.Element(0)));
        scheduler.Submit("replace", new("s", 4, 0, RemoteProtocol.Element(0), Immediate: true));
        Assert.Equal(1, scheduler.Count);
        Assert.Equal(256, scheduler.DrainCompleted().Count);
    }

    [Fact]
    public void SlotStatusMapsOnlyReportedValuesAndPreservesUnknownFields()
    {
        var slot = new DgLabV4Slot("id", "app", new("slot", "Opossum", "OVC_1", new()
        {
            ["power"] = 90,
            ["version"] = 2,
            ["channelAStatus"] = true,
            ["channelBStatus"] = false,
            ["mode"] = 2,
            ["future"] = new JsonObject { ["nested"] = "value" }
        }, new() { ["hasDevice"] = true }), DateTimeOffset.UtcNow);
        var status = slot.Status(DateTimeOffset.UtcNow);
        Assert.Equal(ElectrodeContact.Connected, status.ContactA);
        Assert.Equal(ElectrodeContact.NotConnected, status.ContactB);
        Assert.Equal((byte)2, status.HardwareVersion);
        Assert.Equal(2, status.Telemetry!.ModeA);
        Assert.Null(status.Telemetry.IntensityA);
        Assert.Equal("value", slot.Device.Props!["future"]!["nested"]!.GetValue<string>());
    }
}
