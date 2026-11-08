using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.App.ViewModels;

public sealed partial class PlaybackModeViewModel
{
    public NativeProgramEditorViewModel NativeEditor
    { get; }
    public HardwareOutputEditorViewModel HardwareEditor
    { get; }
    public bool IsNativeProgram => Kind == ModeKind.NativeProgram;
    public bool IsNativeMotor => IsNativeProgram && NativeEditor.IsMotor;
    public bool NativeEnabledA
    {
        get => ChannelA > 0;
        set => ChannelA = value ? 100 : 0;
    }
    public bool NativeEnabledB
    {
        get => ChannelB > 0;
        set => ChannelB = value ? 100 : 0;
    }
    public bool NativeEnabledC
    {
        get => ChannelC > 0;
        set => ChannelC = value ? 100 : 0;
    }
    public bool HasAnalogChannelC => HasChannelC && !IsNativeMotor;
    public bool HasAuxiliaryMotor => TargetDescriptors().Any(device => device.Profile?.Supports(DeviceCapability.AuxiliaryMotor) == true);
    public bool HasHardwareOptions => HasAuxiliaryMotor || HasDgBalances;
    private void RefreshNativeStrength(string property)
    {
        if (!IsNativeProgram) return;
        Raise(property);
        Raise(nameof(CanStart));
        PlaybackCommand.NotifyCanExecuteChanged();
    }
    private void OnNativeSettingsChanged()
    {
        if (IsNativeMotor && CanEditRouting) OutputLimit = 100;
        foreach (var name in new[] { nameof(IsNativeMotor), nameof(HasStrength), nameof(HasAnalogChannelC), nameof(CanStart) }) Raise(name);
        PlaybackCommand.NotifyCanExecuteChanged();
        _host.RefreshModeGroups();
    }
}
