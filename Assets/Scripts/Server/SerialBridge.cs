using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

// bridge.py inside the app, for the server: reads the receiver XIAO's USB serial port and hands
// every line to PodReceiver (Feed), and writes cue commands straight back (directCueOut) - so
// the actor's PC runs one program, with no Python. Same wire format, untouched: see CLAUDE.md,
// "Pod transport and protocol".
//
// Finding the port: the Seeed boards present on USB (by vendor ID, from the registry), opened
// one at a time; the receiver is the one that sends "S,..." status lines (it does every second,
// pods or not). A pod plugged in to charge has the same USB ID but never sends those. Looks
// again whenever the port goes away or goes quiet. "-puerto COM7" on the command line skips the
// search. If the port is held by something else (bridge.py, the Arduino Serial Monitor) this
// keeps waiting and says so; PodReceiver's UDP still hears bridge.py meanwhile.
//
// Windows only (the server is a Windows .exe): Win32 through P/Invoke, since System.IO.Ports
// isn't in the .NET Standard profile this project builds with. Overlapped I/O, so a cue is
// written at once even while a read is waiting - a cue going out is the latency path.
public class SerialBridge : MonoBehaviour
{
    public enum BridgeState { Searching, Busy, Connected, Unsupported }

    public PodReceiver receiver;

    // For the server's window. Written by the bridge thread, read by anyone.
    public BridgeState State => state;
    public string PortName => portName;
    public string Detail => detail;
    public long LinesReceived => Interlocked.Read(ref linesReceived);
    public long CuesSent => Interlocked.Read(ref cuesSent);

    volatile BridgeState state = BridgeState.Searching;
    volatile string portName = "";
    volatile string detail = "";
    long linesReceived;
    long cuesSent;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    const int Baud = 115200;
    const double IdentifySeconds = 3.0;   // a receiver's S line comes every second
    const double QuietSeconds = 6.0;      // identified, then nothing: look again
    const int RetryMilliseconds = 1500;
    const double NotReceiverRetrySeconds = 20.0; // a port that wasn't the receiver is left alone this long
    static readonly string[] VendorIds = { "VID_2886", "VID_239A" }; // Seeed; Adafruit (the core's default)

    Thread thread;
    volatile bool running;
    readonly object ioLock = new object(); // the handle: writes and closing
    IntPtr handle = InvalidHandle;
    bool identified;
    IntPtr writeOverlapped, writeEvent, writeBuffer;
    string forcedPort;

    void Start()
    {
        if (receiver == null)
            receiver = GetComponent<PodReceiver>();
        if (receiver == null)
        {
            Debug.LogError("[Serial] no PodReceiver to feed.");
            enabled = false;
            return;
        }

        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-puerto" && args[i + 1].StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                forcedPort = args[i + 1].ToUpperInvariant();
        }

        writeEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        writeOverlapped = NewOverlapped(writeEvent);
        writeBuffer = Marshal.AllocHGlobal(WriteBufferSize);

        receiver.directCueOut = WriteLine;
        running = true;
        thread = new Thread(Run) { IsBackground = true, Name = "SerialBridge" };
        thread.Start();
        Debug.Log("[Serial] built-in bridge started" + (forcedPort != null ? $" (port {forcedPort} from the command line)" : ""));
    }

    void OnDestroy()
    {
        running = false;
        if (receiver != null && receiver.directCueOut == (Func<string, bool>)WriteLine)
            receiver.directCueOut = null;
        lock (ioLock)
        {
            if (handle != InvalidHandle)
                CancelIoEx(handle, IntPtr.Zero);
        }
        thread?.Join(1500);
        ClosePort();
        if (writeOverlapped != IntPtr.Zero) Marshal.FreeHGlobal(writeOverlapped);
        if (writeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(writeBuffer);
        if (writeEvent != IntPtr.Zero) CloseHandle(writeEvent);
        writeOverlapped = writeBuffer = writeEvent = IntPtr.Zero;
    }

    // --- the bridge thread ----------------------------------------------------------------

    void Run()
    {
        IntPtr readEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        IntPtr readOverlapped = NewOverlapped(readEvent);
        IntPtr readBuffer = Marshal.AllocHGlobal(ReadBufferSize);
        var managed = new byte[ReadBufferSize];
        var line = new StringBuilder(128);
        var notReceiverUntil = new Dictionary<string, DateTime>();
        string reportedOpenError = "";

        try
        {
            while (running)
            {
                List<string> candidates = forcedPort != null ? new List<string> { forcedPort } : SeeedPorts();
                if (candidates.Count == 0)
                {
                    Set(BridgeState.Searching, "", "No hay ningún receptor conectado por USB");
                    Sleep(RetryMilliseconds);
                    continue;
                }

                bool anyBusy = false;
                string busyPort = "";
                foreach (string candidate in candidates)
                {
                    if (!running)
                        break;
                    if (forcedPort == null && notReceiverUntil.TryGetValue(candidate, out DateTime until) && DateTime.UtcNow < until)
                        continue;
                    int error = OpenPort(candidate);
                    if (error != 0)
                    {
                        // 5 = access denied: another program has it open.
                        if (error == 5) { anyBusy = true; busyPort = candidate; }
                        if (reportedOpenError != candidate + error)
                        {
                            reportedOpenError = candidate + error;
                            Debug.Log($"[Serial] {candidate} couldn't be opened (Win32 error {error})");
                        }
                        continue;
                    }

                    if (state != BridgeState.Connected)
                        Set(BridgeState.Searching, candidate, $"Probando {candidate}…");
                    bool wasReceiver = ReadUntilLost(candidate, readOverlapped, readEvent, readBuffer, managed, line);
                    ClosePort();
                    if (wasReceiver)
                        notReceiverUntil.Clear();
                    else
                    {
                        if (!notReceiverUntil.ContainsKey(candidate))
                            Debug.Log($"[Serial] {candidate} opened, but it isn't the receiver (no status lines in {IdentifySeconds:F0} s) - a pod on USB, or another board");
                        notReceiverUntil[candidate] = DateTime.UtcNow.AddSeconds(NotReceiverRetrySeconds);
                    }
                }

                if (!running)
                    break;
                if (anyBusy)
                    Set(BridgeState.Busy, busyPort, $"{busyPort} está ocupado por otro programa (¿bridge.py o el Monitor Serie de Arduino?)");
                else
                    Set(BridgeState.Searching, "", "No encuentro el receptor: conéctalo por USB a esta computadora");
                Sleep(RetryMilliseconds);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[Serial] bridge thread stopped: {e}");
            Set(BridgeState.Searching, "", "El puente serie falló: " + e.Message);
        }
        finally
        {
            ClosePort();
            Marshal.FreeHGlobal(readOverlapped);
            Marshal.FreeHGlobal(readBuffer);
            CloseHandle(readEvent);
        }
    }

    // Reads the open port until it fails, goes quiet, or turns out not to be the receiver.
    // True if it was the receiver.
    bool ReadUntilLost(string name, IntPtr overlapped, IntPtr readEvent, IntPtr buffer, byte[] managed, StringBuilder line)
    {
        line.Clear();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double lastLine = 0;
        bool isReceiver = false;

        while (running)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (!isReceiver && now > IdentifySeconds)
                return false; // something else on that port (a pod charging, say)
            if (isReceiver && now - lastLine > QuietSeconds)
            {
                Debug.LogWarning($"[Serial] {name} went quiet, looking for the receiver again");
                Set(BridgeState.Searching, "", "El receptor dejó de responder, buscándolo…");
                return isReceiver;
            }

            if (!ReadFile(handle, buffer, ReadBufferSize, IntPtr.Zero, overlapped))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorIoPending)
                {
                    Lost(name, isReceiver, error);
                    return isReceiver;
                }
                uint wait = WaitTimeout;
                while (running && (wait = WaitForSingleObject(readEvent, 250)) == WaitTimeout) { }
                if (!running)
                {
                    CancelIoEx(handle, overlapped);
                    GetOverlappedResult(handle, overlapped, out _, true);
                    return isReceiver;
                }
                if (wait != 0)
                {
                    Lost(name, isReceiver, Marshal.GetLastWin32Error());
                    return isReceiver;
                }
            }

            if (!GetOverlappedResult(handle, overlapped, out uint read, false))
            {
                Lost(name, isReceiver, Marshal.GetLastWin32Error());
                return isReceiver;
            }
            if (read == 0)
                continue; // the read's own timeout: nothing arrived

            Marshal.Copy(buffer, managed, 0, (int)read);
            for (int i = 0; i < read; i++)
            {
                char c = (char)managed[i];
                if (c != '\n')
                {
                    if (c != '\r' && line.Length < 512)
                        line.Append(c);
                    continue;
                }

                string text = line.ToString();
                line.Clear();
                if (text.Length == 0)
                    continue;

                if (!isReceiver)
                {
                    if (!LooksLikeReceiver(text))
                        continue;
                    isReceiver = true;
                    lock (ioLock)
                        identified = true;
                    Set(BridgeState.Connected, name, $"Receptor en {name}");
                    Debug.Log($"[Serial] receiver found on {name}");
                }

                lastLine = clock.Elapsed.TotalSeconds;
                Interlocked.Increment(ref linesReceived);
                receiver.Feed(text);
            }
        }
        return isReceiver;
    }

    void Lost(string name, bool wasReceiver, int error)
    {
        if (!wasReceiver)
            return;
        Debug.LogWarning($"[Serial] {name} lost (error {error}), looking for the receiver again");
        Set(BridgeState.Searching, "", "Se desconectó el receptor, buscándolo…");
    }

    // The receiver's line formats: S (status), P (sample), H (health), R (connected / lost), E.
    static bool LooksLikeReceiver(string text) =>
        text.Length > 2 && text[1] == ',' && (text[0] == 'S' || text[0] == 'P' || text[0] == 'H' || text[0] == 'R');

    void Set(BridgeState newState, string port, string text)
    {
        portName = port;
        detail = text;
        state = newState;
    }

    void Sleep(int milliseconds)
    {
        for (int waited = 0; running && waited < milliseconds; waited += 100)
            Thread.Sleep(100);
    }

    // --- the port -------------------------------------------------------------------------

    // 0 = open and configured; otherwise the Win32 error.
    int OpenPort(string name)
    {
        IntPtr opened = CreateFileW(@"\\.\" + name, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (opened == InvalidHandle)
            return Marshal.GetLastWin32Error();

        // 115200 8N1, DTR and RTS on: the receiver's firmware waits for DTR before it prints
        // (while (!Serial)). The baud rate is set before anything else - these boards reboot
        // into their bootloader when a port open at 1200 baud is closed.
        var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf<DCB>() };
        if (!GetCommState(opened, ref dcb))
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle(opened);
            return error != 0 ? error : -1;
        }
        dcb.BaudRate = Baud;
        dcb.ByteSize = 8;
        dcb.Parity = 0;
        dcb.StopBits = 0;
        dcb.Flags = DcbBinary | DcbDtrEnable | DcbRtsEnable;
        var timeouts = new COMMTIMEOUTS
        {
            // A read returns as soon as anything arrives, or after a second with nothing.
            ReadIntervalTimeout = uint.MaxValue,
            ReadTotalTimeoutMultiplier = uint.MaxValue,
            ReadTotalTimeoutConstant = 1000,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = 500,
        };
        if (!SetCommState(opened, ref dcb) || !SetCommTimeouts(opened, ref timeouts))
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle(opened);
            return error != 0 ? error : -1;
        }
        EscapeCommFunction(opened, SetDtr);
        PurgeComm(opened, PurgeRxClear | PurgeTxClear);

        lock (ioLock)
        {
            handle = opened;
            identified = false;
        }
        return 0;
    }

    void ClosePort()
    {
        lock (ioLock)
        {
            if (handle == InvalidHandle)
                return;
            CloseHandle(handle);
            handle = InvalidHandle;
            identified = false;
        }
    }

    // PodReceiver.directCueOut: "C,<id>,<cue>,<param>" to the receiver. False = not sent (no
    // receiver on USB here), so the caller falls back to UDP.
    bool WriteLine(string text)
    {
        lock (ioLock)
        {
            if (handle == InvalidHandle || !identified || writeBuffer == IntPtr.Zero)
                return false;

            int length = Math.Min(text.Length, WriteBufferSize - 1);
            for (int i = 0; i < length; i++)
                Marshal.WriteByte(writeBuffer, i, (byte)text[i]);
            Marshal.WriteByte(writeBuffer, length, (byte)'\n');

            if (!WriteFile(handle, writeBuffer, (uint)(length + 1), IntPtr.Zero, writeOverlapped))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorIoPending)
                {
                    Debug.LogWarning($"[Serial] cue not written (error {error}): {text}");
                    return false;
                }
            }
            // A USB serial write finishes at once; the port's write timeout (500 ms) bounds it.
            if (!GetOverlappedResult(handle, writeOverlapped, out uint written, true) || written != length + 1)
            {
                Debug.LogWarning($"[Serial] cue not written (error {Marshal.GetLastWin32Error()}): {text}");
                return false;
            }
        }

        Interlocked.Increment(ref cuesSent);
        return true;
    }

    // --- which COM ports are Seeed boards, and present ---------------------------------------

    // The registry: every USB device of those vendors that ever got a COM port, kept only if
    // that port exists right now (HARDWARE\DEVICEMAP\SERIALCOMM lists the present ones).
    static List<string> SeeedPorts()
    {
        var result = new List<string>();
        HashSet<string> present = PresentPorts();
        if (present.Count == 0)
            return result;

        const string usbRoot = @"SYSTEM\CurrentControlSet\Enum\USB";
        foreach (string device in SubKeys(usbRoot))
        {
            bool vendor = false;
            foreach (string id in VendorIds)
                vendor |= device.StartsWith(id, StringComparison.OrdinalIgnoreCase);
            if (!vendor)
                continue;

            foreach (string instance in SubKeys(usbRoot + "\\" + device))
            {
                string port = ReadString(usbRoot + "\\" + device + "\\" + instance + @"\Device Parameters", "PortName");
                if (!string.IsNullOrEmpty(port) && present.Contains(port.ToUpperInvariant()) && !result.Contains(port.ToUpperInvariant()))
                    result.Add(port.ToUpperInvariant());
            }
        }
        return result;
    }

    static HashSet<string> PresentPorts()
    {
        var ports = new HashSet<string>();
        if (RegOpenKeyExW(HkeyLocalMachine, @"HARDWARE\DEVICEMAP\SERIALCOMM", 0, KeyRead, out IntPtr key) != 0)
            return ports;
        try
        {
            var name = new StringBuilder(512);
            var data = new byte[512];
            for (uint index = 0; ; index++)
            {
                uint nameLength = (uint)name.Capacity, dataLength = (uint)data.Length;
                if (RegEnumValueW(key, index, name, ref nameLength, IntPtr.Zero, out uint type, data, ref dataLength) != 0)
                    break;
                if (type == RegSz && dataLength >= 2)
                    ports.Add(Encoding.Unicode.GetString(data, 0, (int)dataLength).TrimEnd('\0').ToUpperInvariant());
            }
        }
        finally
        {
            RegCloseKey(key);
        }
        return ports;
    }

    static List<string> SubKeys(string path)
    {
        var keys = new List<string>();
        if (RegOpenKeyExW(HkeyLocalMachine, path, 0, KeyRead, out IntPtr key) != 0)
            return keys;
        try
        {
            var name = new StringBuilder(512);
            for (uint index = 0; ; index++)
            {
                uint length = (uint)name.Capacity;
                if (RegEnumKeyExW(key, index, name, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
                    break;
                keys.Add(name.ToString());
            }
        }
        finally
        {
            RegCloseKey(key);
        }
        return keys;
    }

    static string ReadString(string path, string valueName)
    {
        if (RegOpenKeyExW(HkeyLocalMachine, path, 0, KeyRead, out IntPtr key) != 0)
            return null;
        try
        {
            var data = new byte[512];
            uint length = (uint)data.Length;
            if (RegQueryValueExW(key, valueName, IntPtr.Zero, out uint type, data, ref length) != 0 || type != RegSz || length < 2)
                return null;
            return Encoding.Unicode.GetString(data, 0, (int)length).TrimEnd('\0');
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    // --- Win32 ----------------------------------------------------------------------------

    const int ReadBufferSize = 1024;
    const int WriteBufferSize = 96;
    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, OpenExisting = 3, FileFlagOverlapped = 0x40000000;
    const uint DcbBinary = 0x1, DcbDtrEnable = 0x10, DcbRtsEnable = 0x1000;
    const uint PurgeTxClear = 0x4, PurgeRxClear = 0x8, SetDtr = 5;
    const int ErrorIoPending = 997;
    const uint WaitTimeout = 258;
    const uint KeyRead = 0x20019, RegSz = 1;
    static readonly IntPtr InvalidHandle = new IntPtr(-1);
    static readonly IntPtr HkeyLocalMachine = new IntPtr(unchecked((int)0x80000002));

    [StructLayout(LayoutKind.Sequential)]
    struct DCB
    {
        public uint DCBlength, BaudRate, Flags;
        public ushort wReserved, XonLim, XoffLim;
        public byte ByteSize, Parity, StopBits;
        public sbyte XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct COMMTIMEOUTS
    {
        public uint ReadIntervalTimeout, ReadTotalTimeoutMultiplier, ReadTotalTimeoutConstant, WriteTotalTimeoutMultiplier, WriteTotalTimeoutConstant;
    }

    // OVERLAPPED in unmanaged memory (it must not move while an operation is pending):
    // Internal, InternalHigh (pointers), Offset, OffsetHigh (uint), hEvent (pointer).
    static IntPtr NewOverlapped(IntPtr eventHandle)
    {
        int size = IntPtr.Size * 3 + 8;
        IntPtr memory = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++)
            Marshal.WriteByte(memory, i, 0);
        Marshal.WriteIntPtr(memory, IntPtr.Size * 2 + 8, eventHandle);
        return memory;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetCommState(IntPtr handle, ref DCB dcb);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommState(IntPtr handle, ref DCB dcb);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommTimeouts(IntPtr handle, ref COMMTIMEOUTS timeouts);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool PurgeComm(IntPtr handle, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool EscapeCommFunction(IntPtr handle, uint function);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(IntPtr handle, IntPtr buffer, uint count, IntPtr read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(IntPtr handle, IntPtr buffer, uint count, IntPtr written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(IntPtr handle, IntPtr overlapped, out uint transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateEventW(IntPtr security, bool manualReset, bool initialState, IntPtr name);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegOpenKeyExW(IntPtr key, string subKey, uint options, uint access, out IntPtr result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegEnumKeyExW(IntPtr key, uint index, StringBuilder name, ref uint nameLength, IntPtr reserved, IntPtr cls, IntPtr classLength, IntPtr lastWrite);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegEnumValueW(IntPtr key, uint index, StringBuilder name, ref uint nameLength, IntPtr reserved, out uint type, byte[] data, ref uint dataLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegQueryValueExW(IntPtr key, string valueName, IntPtr reserved, out uint type, byte[] data, ref uint dataLength);
    [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr key);
#else
    void Start()
    {
        state = BridgeState.Unsupported;
        detail = "El puente serie integrado solo funciona en Windows";
    }
#endif
}
