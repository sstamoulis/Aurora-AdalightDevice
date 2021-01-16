using Aurora;
using Aurora.Devices;
using Aurora.Settings;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Threading.Tasks;
using System.Linq;
using RJCP.IO.Ports;
using System.Threading;

public class AdalightDevice : IDevice
{
    const string DEVICE_NAME = "Adalight";
    const string SERIAL_PORT_VARIABLE = DEVICE_NAME + "_serial_port";
    const string BAUDRATE_VARIABLE = DEVICE_NAME + "_baudrate";
    const string LED_COUNT_VARIABLE = DEVICE_NAME + "_led_count";

    protected static readonly byte[] MAGIC = new byte[] { (byte)'A', (byte)'d', (byte)'a' };
    protected static readonly DeviceKeys[] ACTIVE_KEYS = new DeviceKeys[]
    {
            DeviceKeys.ESC,
            DeviceKeys.F1,
            DeviceKeys.F2,
            DeviceKeys.F3,
            DeviceKeys.F4,
            DeviceKeys.F5,
            DeviceKeys.F6,
            DeviceKeys.F7,
            DeviceKeys.F8,
            DeviceKeys.F9,
            DeviceKeys.F10,
            DeviceKeys.F11,
            DeviceKeys.F12,
    };

    protected SerialPortStream port;
    protected Dictionary<DeviceKeys, int[]> ledMapping;
    protected Task<bool> SendTask;
    protected byte[] header;
    protected bool isInitialized;
    protected bool crashed;
    protected Exception crashException;
    protected VariableRegistry variableRegistry;
    protected System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
    long lastUpdateTime = 0;

    protected void Crash(Exception ex, string stage)
    {
        Global.logger.Error($"Device {DEVICE_NAME} encountered an error during {stage}. Exception: {ex}");
        crashed = true;
        crashException = ex;
        isInitialized = false;
    }

    public string SerialPort => Global.Configuration.VarRegistry.GetVariable<string>(SERIAL_PORT_VARIABLE);
    public int Baudrate => Global.Configuration.VarRegistry.GetVariable<int>(BAUDRATE_VARIABLE);
    public int LedCount => Global.Configuration.VarRegistry.GetVariable<int>(LED_COUNT_VARIABLE);

    public string DeviceDetails
    {
        get
        {
            if (crashed) return $"Error: {crashException.Message}";
            else return port.IsOpen ? "Connected" : "Disconnected";
        }
    }

    public string DeviceName => DEVICE_NAME;

    public string DeviceUpdatePerformance => isInitialized ? $"{lastUpdateTime} ms" : "";

    public VariableRegistry RegisteredVariables
    {
        get
        {
            if (variableRegistry == null)
            {
                variableRegistry = new VariableRegistry();
                variableRegistry.Register(SERIAL_PORT_VARIABLE, "COM1", "Serial port");
                variableRegistry.Register(BAUDRATE_VARIABLE, 115200, "Baudrate");
                variableRegistry.Register(LED_COUNT_VARIABLE, 15, "LED count", Math.Pow(2, 16) - 1, 1);
            }
            return variableRegistry;
        }
    }

    public bool Initialize()
    {
        try
        {
            header = new byte[MAGIC.Length + 3];
            Array.Copy(MAGIC, header, MAGIC.Length);
            byte hi, lo, chk;
            hi = (byte)((LedCount - 1) >> 8);
            lo = (byte)((LedCount - 1) & 0xff);
            chk = (byte)(hi ^ lo ^ 0x55);
            header[MAGIC.Length] = hi;
            header[MAGIC.Length + 1] = lo;
            header[MAGIC.Length + 2] = chk;
            if (port != null)
            {
                lock (port)
                {
                    port.Dispose();
                }
            }
            port = new SerialPortStream();
            port.PortName = SerialPort;
            port.BaudRate = Baudrate;
            port.Open();
            AssignLeds();
            isInitialized = true;
            crashed = false;
            return true;
        }
        catch (Exception ex)
        {
            Crash(ex, "Initialization");
            return false;
        }
    }

    protected void AssignLeds()
    {
        ledMapping = new Dictionary<DeviceKeys, int[]>();
        int ledsPerKey = LedCount / ACTIVE_KEYS.Length;
        if (ledsPerKey == 0)
        {
            // Less than ACTIVE_KEYS leds available, assign from left to right
            for (int i = 0; i < LedCount; i++)
                ledMapping[ACTIVE_KEYS[i]] = new int[] { i };
        }
        else
        {
            int remainingLeds = LedCount % ACTIVE_KEYS.Length;
            int leftLeds = remainingLeds / 2;
            int rightLeds = remainingLeds - leftLeds;
            for (int i = 0, currentLed = 0; i < ACTIVE_KEYS.Length; i++)
            {
                int leds = ledsPerKey;
                if (i < leftLeds || i >= ACTIVE_KEYS.Length - rightLeds) leds++;
                int[] map = new int[leds];
                for (int j = 0; j < leds; j++)
                    map[j] = currentLed++;
                ledMapping[ACTIVE_KEYS[i]] = map;
            }
        }
    }

    public bool IsConnected()
    {
        return port.IsOpen;
    }

    public bool IsInitialized => isInitialized && !crashed;

    public bool IsKeyboardConnected()
    {
        return IsInitialized;
    }

    public bool IsPeripheralConnected()
    {
        return IsInitialized;
    }

    public bool Reconnect()
    {
        throw new NotImplementedException();
    }

    public void Reset()
    {
        if (isInitialized)
        {
            Shutdown();
            Initialize();
        }
    }

    public void Shutdown()
    {
        lock (port)
        {
            port.Close();
        }
        isInitialized = false;
    }

    public bool UpdateDevice(Dictionary<DeviceKeys, Color> keyColors, DoWorkEventArgs e, bool forced = false)
    {
        if (SendTask == null || SendTask.IsCompleted)
        {
            watch.Restart();
            try
            {
                byte[] colors = new byte[LedCount * 3];
                foreach (KeyValuePair<DeviceKeys, Color> key in keyColors)
                    if (ledMapping.ContainsKey(key.Key))
                        foreach (int id in ledMapping[key.Key])
                        {
                            colors[id * 3] = key.Value.R;
                            colors[id * 3 + 1] = key.Value.G;
                            colors[id * 3 + 2] = key.Value.B;
                        }

                SendTask = Task.Run(() => SendColorsToDevice(colors, forced));

                return true;
            }
            catch (Exception ex)
            {
                Crash(ex, "UpdateDevice");
                return false;
            }
        }
        else
        {
            return true;
        }
    }

    public bool UpdateDevice(DeviceColorComposition colorComposition, DoWorkEventArgs e, bool forced = false)
    {
        return UpdateDevice(colorComposition.keyColors, e, forced);
    }

    protected bool SendColorsToDevice(byte[] colors, bool forced)
    {
        try
        {
            byte[] packet = new byte[header.Length + colors.Length];
            Array.Copy(header, packet, header.Length);
            Array.Copy(colors, 0, packet, header.Length, colors.Length);
            byte[] ackBuffer = new byte[MAGIC.Length];
            lock (port)
            {
                port.Write(packet, 0, packet.Length);
                port.Flush();
                port.Read(ackBuffer, 0, ackBuffer.Length);
            }
            watch.Stop();
            Interlocked.Exchange(ref lastUpdateTime, watch.ElapsedMilliseconds);
            return ackBuffer.SequenceEqual(MAGIC);
        }
        catch (Exception ex)
        {
            Crash(ex, "SendColorsToDevice");
            return false;
        }
    }
}
