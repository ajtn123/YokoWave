namespace YokoWave.Core.Bluetooth;

/// <summary>Compatibility name for callers using the original Yokonex-only transport.</summary>
public sealed class ElectricalBluetoothTransport(IBluetoothAdapter adapter) : BluetoothDeviceTransport(adapter);
