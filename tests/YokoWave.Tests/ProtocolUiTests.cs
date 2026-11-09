using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using YokoWave.App.ViewModels;
using YokoWave.App.Views.Components;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

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
        string language = L10n.Current.Language;
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
                    foreach (var field in editor.Parameters.Where(field => field.IsChoice))
                        Assert.NotEmpty(field.Choices);
                    string draft = editor.Json;
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
}
