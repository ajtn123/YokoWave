using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YokoWave.App.ViewModels;
using YokoWave.App.Views.Components;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using Xunit;

namespace YokoWave.Tests;

public sealed class ProtocolUiTests
{
    [AvaloniaFact]
    public async Task EveryProtocolCommandHasEditableLocalizedParametersAndAValidTypedTemplate()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.Paw, BluetoothProtocol.Civet, BluetoothProtocol.YokonexV1, BluetoothProtocol.Opossum);
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var devices = await controller.ScanAsync(TestContext.Current.CancellationToken);
        var editor = new ProtocolFeaturesViewModel(controller, error => throw error);
        var window = new Window { Content = new ProtocolFeaturesView { DataContext = editor } };
        var language = L10n.Current.Language;
        try
        {
            window.Show();
            window.GetVisualDescendants().OfType<Expander>().First().IsExpanded = true;
            foreach (var device in devices)
            {
                await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
                editor.SelectDevice(device);
                foreach (var option in editor.Commands)
                {
                    editor.SelectedCommand = option;
                    option.Parse(editor.Json).Validate();
                    Assert.DoesNotContain(editor.Parameters, field => field.Label.StartsWith('['));
                    Assert.DoesNotContain(editor.Parameters, field => field.Label.Contains("ReadOnly", StringComparison.Ordinal));
                    foreach (var field in editor.Parameters.Where(field => field.IsChoice)) Assert.NotEmpty(field.Choices);
                    var draft = editor.Json;
                    L10n.Current.SetLanguage("en");
                    editor.RefreshLocalization();
                    L10n.Current.SetLanguage("zh-CN");
                    editor.RefreshLocalization();
                    Assert.Equal(draft, editor.Json);
                    Dispatcher.UIThread.RunJobs();
                }
            }
            var paw = devices.Single(device => device.Profile?.Protocol == BluetoothProtocol.Paw);
            editor.SelectDevice(paw);
            editor.SelectedCommand = editor.Commands.Single(option => option.Key == "PawRandom");
            editor.Parameters.First(field => field.Label == L10n.Get("ProtocolField.MinimumSeconds")).Number = 12;
            Assert.Equal(12, Assert.IsType<PawRandomReaction>(Assert.IsType<ConfigurePaw>(editor.SelectedCommand.Parse(editor.Json)).Trigger).MinimumSeconds);
            await editor.ExecuteAsync();
            Assert.Equal(12, adapter.Links[paw.Id].Writes[^1].Data[5]);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(window.GetVisualDescendants().OfType<NumericUpDown>());
            editor.SelectDevice(devices.Single(device => device.Profile?.Protocol == BluetoothProtocol.YokonexV1));
            editor.SelectedCommand = editor.Commands.Single(option => option.Key == "Query");
            Assert.Equal(7, Assert.Single(editor.Parameters).Choices.Count);
            editor.SelectDevice(devices.Single(device => device.Profile?.Protocol == BluetoothProtocol.Civet));
            Assert.Equal(2, Assert.Single(editor.Parameters).Choices.Count);
        }
        finally
        {
            window.Close();
            L10n.Current.SetLanguage(language);
        }
    }

    [AvaloniaFact]
    public async Task NativeModeEditorsPersistOnlyModeSettingsAndKeepInputAcrossLanguageChanges()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), store);
        await vm.InitializeAsync();
        await vm.AddModeAsync(ModeKind.NativeProgram);
        var mode = vm.SelectedMode;
        var device = vm.Devices.First(item => item.Device.Kind == DeviceKind.Vibration);
        await vm.ToggleConnectionAsync(device);
        mode.NativeEditor.SelectedKind = mode.NativeEditor.Kinds.Single(option => option.Value == (int)NativeProgramKind.MotorPreset);
        mode.AssignTarget(device.Id, true);
        mode.NativeEditor.PresetA = 3;
        mode.NativeEnabledA = true;
        Assert.Equal(100, mode.ChannelA);
        Assert.Equal(100, mode.OutputLimit);
        Assert.True(mode.CanStart);
        var view = new NativeProgramView { DataContext = mode };
        var window = new Window { Content = view };
        var language = L10n.Current.Language;
        try
        {
            window.Show();
            await mode.TogglePlaybackAsync();
            await vm.TickAsync();
            mode.NativeEditor.PresetA = 5;
            Assert.Equal(3, mode.NativeEditor.PresetA);
            Assert.True(mode.IsRunning);
            await mode.StopAsync();
            mode.NativeEditor.SelectedKind = mode.NativeEditor.Kinds.Single(option => option.Value == (int)NativeProgramKind.CoyoteV2Pulse);
            mode.NativeEditor.XA = 12;
            vm.SelectedLanguage = vm.Languages.Single(item => item.Code == (language == "en" ? "zh-CN" : "en"));
            Assert.Equal(12, mode.NativeEditor.XA);
            Assert.Equal(3, mode.NativeEditor.PresetA);
            await vm.SaveAsync();
            var config = (await store.LoadAsync()).Modes!.Single(item => item.Kind == ModeKind.NativeProgram);
            Assert.Equal(12, config.NativeProgram!.PulseA!.X);
            Assert.Equal(3, config.NativeProgram.PresetA);
            Assert.NotNull(config.Hardware);
            Assert.False(File.ReadAllText(System.IO.Path.Combine(folder.Path, "settings.json")).Contains("PresetA", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
            L10n.Current.SetLanguage(language);
        }
    }
}
