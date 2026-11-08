using YokoWave.Core;

namespace YokoWave.App.Playback;

/// <summary>App-side input lifecycle. Core mode runtimes consume features without owning microphones or UI state.</summary>
internal class ModeInputController : IAsyncDisposable
{
    public virtual Task StartAsync() => Task.CompletedTask;
    public virtual Task UpdateAsync(SessionEngine engine) => Task.CompletedTask;
    public virtual void SetPaused(bool paused) { }
    public virtual void Cancel() { }
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static ModeInputController Create(PlaybackModeDefinition definition, IAudioInputFactory audioFactory,
        DeviceController devices, Guid owner, Func<AudioSettings> audio, Func<ReactiveSettings> reactive,
        Func<double> angle, Func<(double Frequency, double Width, bool Held)> touch)
    {
        if (definition.UsesAudio) return new AudioModeInputController(audioFactory, audio, devices.Clock);
        if (definition.Kind == ModeKind.Touch) return new TouchModeInputController(touch);
        if (definition.UsesReactiveSettings && devices.IsSimulation)
            return new SimulatedMotionInputController(devices, owner, () => definition.NeedsMotion(reactive()), angle);
        return new ModeInputController();
    }
}

internal sealed class TouchModeInputController(Func<(double Frequency, double Width, bool Held)> current) : ModeInputController
{
    public override Task UpdateAsync(SessionEngine engine)
    {
        var touch = current();
        return touch.Held ? engine.SetTouchAsync(touch.Frequency, touch.Width, true) : Task.CompletedTask;
    }
}

internal sealed class SimulatedMotionInputController(DeviceController devices, Guid owner, Func<bool> enabled, Func<double> angle) : ModeInputController
{
    public override Task UpdateAsync(SessionEngine engine) => enabled() ? devices.SetSimulatedMotionAsync(owner, angle()) : Task.CompletedTask;
}
