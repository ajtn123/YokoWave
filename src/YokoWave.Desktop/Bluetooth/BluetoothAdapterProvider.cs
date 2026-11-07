using YokoWave.Core.Bluetooth;

namespace YokoWave.Desktop.Bluetooth;

internal static class BluetoothAdapterProvider
{
    public static IBluetoothAdapter Create()
    {
#if WINDOWS
        return new WindowsBluetoothAdapter();
#else
        return new UnsupportedBluetoothAdapter();
#endif
    }
}
