using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using YokoWave.App.ViewModels;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using YokoWave.Tests.Support.Bluetooth;
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Ui;

public sealed class NodeSettingsTests
{
    [AvaloniaFact]
    public async Task NodeSettingsUpdateInPlaceSupportUndoAndSurviveReloadWithoutApplying()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(folder.Path));
        await vm.InitializeAsync();
        var editor = vm.Pipelines.Selected!;
        var automation = editor.Nodes.First(n => n.Type == BuiltInNodes.Automation);
        editor.SelectedNodes.Add(automation);
        var step = Field(automation, "Step");
        Field(automation, "Mode").Selected = Field(automation, "Mode").Choices.Single(c => c.Value == (int)StrengthMode.StepIncrease);
        step.Number = 7;
        Field(automation, "IntervalSeconds").Number = 3;
        Assert.Same(automation, editor.SelectedNode);
        Assert.Same(step, Field(automation, "Step"));
        Assert.Equal(new StrengthSettings(StrengthMode.StepIncrease, 3, Step: 7), Read<AutomationNode>(editor, automation).Settings);
        step.Number = 8;
        editor.UndoCommand.Execute(null);
        Assert.Equal(7, Read<AutomationNode>(editor, editor.Nodes.Single(n => n.Id == automation.Id)).Settings.Step);
        editor.RedoCommand.Execute(null);
        Assert.Equal(8, Read<AutomationNode>(editor, editor.Nodes.Single(n => n.Id == automation.Id)).Settings.Step);
        var touch = editor.AddNode(BuiltInNodes.Touch);
        Field(touch, "Playback").Selected = Field(touch, "Playback").Choices.Single(c => c.Value == (int)TouchPlayback.RecordedLoop);
        Assert.Equal(TouchPlayback.RecordedLoop, Read<TouchNode>(editor, touch).Playback);
        var cycle = editor.AddNode(BuiltInNodes.Cycle);
        Field(cycle, "MinimumWait").Number = 120;
        Assert.Contains(editor.Diagnostics, d => d.Node == cycle.Id && d.Key == "Pipeline.Error.Configuration");
        Field(cycle, "MaximumWait").Number = 150;
        Field(cycle, "ActivityReset").Enabled = true;
        Assert.DoesNotContain(editor.Diagnostics, d => d.Node == cycle.Id);
        Assert.True(Read<CycleNode>(editor, cycle).ActivityReset);
        var command = Assert.IsType<CommandNodeEditorViewModel>(editor.AddNode(BuiltInNodes.Command));
        command.SelectedCommand = command.Commands.Single(c => c.Key == "Indicator");
        var color = command.CommandParameters.Single(p => p.Label == L10n.Get("ProtocolField.Color"));
        color.Number = 4;
        command.OnStart = true;
        Assert.Equal(4, Assert.IsType<ConfigureIndicator>(Read<CommandNode>(editor, command).Command).Color);
        Assert.True(Read<CommandNode>(editor, command).OnStart);
        Assert.Same(color, Assert.Single(command.CommandParameters));
        string language = L10n.Current.Language;
        try
        {
            L10n.Current.SetLanguage(language == "en" ? "zh-CN" : "en");
            Dispatcher.UIThread.RunJobs();
            Assert.Same(color, Assert.Single(command.CommandParameters));
            Assert.Equal(4, color.Number);
        }
        finally
        {
            L10n.Current.SetLanguage(language);
        }

        await vm.Pipelines.SaveAsync();
        await using var restored = new MainViewModel(new DeviceController(new SimulationTransport()), new(folder.Path));
        await restored.InitializeAsync();
        var loaded = restored.Pipelines.Selected!;
        Assert.Equal(8, Read<AutomationNode>(loaded, loaded.Nodes.Single(n => n.Id == automation.Id)).Settings.Step);
        Assert.Equal(TouchPlayback.RecordedLoop, Read<TouchNode>(loaded, loaded.Nodes.Single(n => n.Id == touch.Id)).Playback);
        Assert.Equal(150, Read<CycleNode>(loaded, loaded.Nodes.Single(n => n.Id == cycle.Id)).MaximumWait);
        Assert.Equal(4, Assert.IsType<ConfigureIndicator>(Read<CommandNode>(loaded, loaded.Nodes.Single(n => n.Id == command.Id)).Command).Color);
    }

    [AvaloniaFact]
    public async Task HardwareUpdatesAreSerializedRecoverAfterSaveFailureAndDrainBeforeDisposal()
    {
        using var state = new UiTestState();
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3);
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<DeviceOutputSettings> saved = [];
        List<Exception> errors = [];
        bool failNextSave = false;
        await using var devices = new DevicesPaneViewModel(controller, () => new BluetoothDeviceTransport(adapter), async () =>
        {
            await barrier.Task;
            if (failNextSave)
            {
                failNextSave = false;
                throw new IOException("Test save failure");
            }

            saved.Add(Assert.Single(controller.OutputSettings));
        }, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => null, errors.Add, (_, _) => { });
        await devices.ScanAsync();
        var hardware = Assert.Single(devices.Devices).HardwareOptions!;
        hardware.LimitA = 35;
        hardware.FrequencyA = 200;
        hardware.WidthA = 90;
        devices.Refresh();
        Assert.Equal(35, hardware.LimitA);
        Assert.Equal(200, hardware.FrequencyA);
        Assert.Equal(90, hardware.WidthA);
        Assert.False(devices.FlushPreferencesAsync().IsCompleted);
        barrier.SetResult();
        await devices.FlushPreferencesAsync();
        Assert.Equal(3, saved.Count);
        Assert.Equal(35, saved[^1].Hardware!.ChannelALimit);
        Assert.Equal(200, saved[^1].Balances!.FrequencyA);
        Assert.Equal(90, saved[^1].Balances!.WidthA);
        Assert.Empty(errors);
        failNextSave = true;
        hardware.RelativeStrength = true;
        await devices.FlushPreferencesAsync();
        Assert.IsType<IOException>(Assert.Single(errors));
        hardware.WidthB = 80;
        await devices.FlushPreferencesAsync();
        Assert.True(saved[^1].Hardware!.RelativeStrength);
        Assert.Equal(80, saved[^1].Balances!.WidthB);
        barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        hardware.Gentle = true;
        var dispose = devices.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        barrier.SetResult();
        await dispose;
        Assert.True(saved[^1].Gentle);
        Assert.Equal(35, saved[^1].Hardware!.ChannelALimit);
    }

    private static ParameterFieldViewModel Field(NodeEditorViewModel node, string name) => node.Parameters.Single(p => p.Label == L10n.Get("Pipeline.Setting." + name));
    private static T Read<T>(PipelineEditorViewModel editor, NodeEditorViewModel node) => (T)editor.Registry.Get(node.Type).Read(node.ToDefinition().Configuration);
}
