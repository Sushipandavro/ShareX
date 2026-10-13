#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.ScreenRecordingLib.Native;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ShareX.ScreenRecordingLib.Audio;

/// <summary>WASAPI shared-mode loopback and microphone capture, on a dedicated event-driven MTA thread.
/// Sources without a chosen device follow the Windows default device, including changes during recording.</summary>
internal sealed unsafe class WasapiCapture : IDisposable
{
    private sealed class Endpoint : IDisposable
    {
        public required ComPtr<IAudioClient3> Client;
        public required ComPtr<IAudioCaptureClient> Capture;
        public required AutoResetEvent Ready;
        public required string DeviceId;
        public bool Started;
        /// <summary>The first packet of a replacement endpoint repositions the mixer stream.</summary>
        public bool Replacement;
        public long NextPacketTime;
        public void Dispose()
        {
            if (Started) Client.Pointer->Stop();
            Capture.Dispose(); Client.Dispose(); Ready.Dispose();
        }
    }

    private sealed class Source
    {
        public required bool Loopback;
        public required int Index;
        public required float Gain;
        /// <summary>Null follows the Windows default device.</summary>
        public string? DeviceId;
        public Endpoint? Endpoint;
        public string? FailedDeviceId;
        public string Name => Loopback ? "System audio" : "Microphone";
    }

    private static readonly long DeviceCheckInterval = TimeSpan.FromMilliseconds(500).Ticks;
    private readonly RecordingOptions options;
    private readonly AudioMixer mixer;
    private readonly Action<string> diagnostic;
    private readonly Thread thread;
    private readonly ManualResetEvent stop = new(false);
    private readonly ManualResetEvent start = new(false);
    private readonly TaskCompletionSource prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? failure;
    private int disposed;
    public Exception? Failure => Volatile.Read(ref failure);

    public WasapiCapture(RecordingOptions options, AudioMixer mixer, Action<string> diagnostic)
    {
        this.options = options;
        this.mixer = mixer;
        this.diagnostic = diagnostic;
        thread = new Thread(Run) { IsBackground = true, Name = "ShareX WASAPI capture" };
        thread.Start();
        try { prepared.Task.GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    public void Start() => start.Set();

    /// <summary>Multimedia is the device Windows Settings shows as the default output and microphone.</summary>
    internal static IMMDevice GetDefaultDevice(IMMDeviceEnumerator enumerator, bool loopback)
    {
        try { return enumerator.GetDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture, Role.Multimedia); }
        catch (SharpGenException) when (!loopback) { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
    }

    private static string? GetDefaultDeviceId(IMMDeviceEnumerator enumerator, bool loopback)
    {
        try
        {
            using IMMDevice device = GetDefaultDevice(enumerator, loopback);
            return device.Id;
        }
        catch (SharpGenException) { return null; }
    }

    /// <summary>Null when Windows has no default device yet; FollowDevice attaches one when it appears.</summary>
    private Endpoint? OpenFirst(IMMDeviceEnumerator enumerator, Source source)
    {
        if (source.DeviceId != null)
        {
            try
            {
                using IMMDevice chosen = enumerator.GetDevice(source.DeviceId);
                return Open(chosen, source.Loopback);
            }
            catch (Exception ex) when (ex is SharpGenException or System.Runtime.InteropServices.COMException)
            {
                diagnostic($"{source.Name} device is unavailable; using the Windows default device. {ex.Message}");
                source.DeviceId = null;
            }
        }
        IMMDevice device;
        try { device = GetDefaultDevice(enumerator, source.Loopback); }
        catch (SharpGenException)
        {
            diagnostic($"{source.Name}: Windows has no default device; it is recorded once one is connected.");
            return null;
        }
        using (device) return Open(device, source.Loopback);
    }

    private static Endpoint Open(IMMDevice device, bool loopback)
    {
        ComPtr<IAudioClient3> client = ActivateClient(device);
        AutoResetEvent ready = new(false);
        try
        {
            // Ask Windows to perform channel mapping and high-quality resampling. No managed resampler or codec DLL.
            WAVEFORMATEX format = new()
            {
                wFormatTag = 3,
                nChannels = 2,
                nSamplesPerSec = 48_000,
                nAvgBytesPerSec = 384_000,
                nBlockAlign = 8,
                wBitsPerSample = 32,
                cbSize = 0
            };
            const uint eventCallback = 0x00040000, loopbackFlag = 0x00020000, autoConvert = 0x80000000, srcQuality = 0x08000000;
            uint flags = eventCallback | autoConvert | srcQuality | (loopback ? loopbackFlag : 0);
            bool initialized = false;
            if (!loopback)
            {
                uint defaultPeriod, fundamentalPeriod, minimumPeriod, maximumPeriod;
                if (client.Pointer->GetSharedModeEnginePeriod(&format, &defaultPeriod, &fundamentalPeriod, &minimumPeriod, &maximumPeriod).Succeeded)
                {
                    // IAudioClient3 accepts EVENTCALLBACK only, not AUTOCONVERTPCM or SRC_DEFAULT_QUALITY.
                    // Try the low period when this format is supported directly by the shared engine.
                    initialized = client.Pointer->InitializeSharedAudioStream(eventCallback, minimumPeriod, &format, null).Succeeded;
                    if (!initialized)
                    {
                        // Failed initialization can leave a client partially initialized. Retry on a
                        // fresh client so default-period Windows resampling also works for mono/44.1 kHz microphones.
                        client.Dispose();
                        client = ActivateClient(device);
                    }
                }
            }
            // Loopback and devices with a locked engine period use the supported default period (typically 10 ms).
            if (!initialized) client.Pointer->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, &format, null).ThrowOnFailure();
            client.Pointer->SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()).ThrowOnFailure();
            Guid iid = typeof(IAudioCaptureClient).GUID;
            void* rawCapture;
            client.Pointer->GetService(&iid, &rawCapture).ThrowOnFailure();
            return new() { Client = client, Capture = new((IAudioCaptureClient*)rawCapture), Ready = ready, DeviceId = device.Id };
        }
        catch { ready.Dispose(); client.Dispose(); throw; }
    }

    private static ComPtr<IAudioClient3> ActivateClient(IMMDevice device)
    {
        Guid iid = typeof(IAudioClient3).GUID;
        device.Activate(iid, 1, null, out nint rawClient).CheckError(); // CLSCTX_INPROC_SERVER
        return new((IAudioClient3*)rawClient);
    }

    private static void Begin(Endpoint endpoint)
    {
        endpoint.Client.Pointer->Start().ThrowOnFailure();
        endpoint.Started = true;
    }

    /// <summary>WASAPI reports a removed, disabled or reconfigured endpoint with AUDCLNT_E_* errors.</summary>
    private static bool IsEndpointLost(Exception ex) => ((uint)ex.HResult & 0xFFFF0000u) == 0x88890000u;

    private void Run()
    {
        List<Source> sources = new();
        bool com = false;
        nint mmcss = 0;
        try
        {
            NativeMethods.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED).ThrowOnFailure();
            com = true;
            uint taskIndex = 0;
            mmcss = NativeMethods.AvSetMmThreadCharacteristics("Audio", ref taskIndex);
            using IMMDeviceEnumerator enumerator = new();
            if (options.CaptureSystemAudio)
                sources.Add(new() { Loopback = true, Index = 0, Gain = options.SystemAudioGain, DeviceId = NullIfEmpty(options.SystemAudioDeviceId) });
            if (options.CaptureMicrophone)
                sources.Add(new() { Loopback = false, Index = 1, Gain = options.MicrophoneGain, DeviceId = NullIfEmpty(options.MicrophoneDeviceId) });
            foreach (Source source in sources) source.Endpoint = OpenFirst(enumerator, source);
            prepared.TrySetResult();
            if (WaitHandle.WaitAny([stop, start]) == 0) return;
            foreach (Source source in sources) if (source.Endpoint != null) Begin(source.Endpoint);
            WaitHandle[] events = GetEvents(sources);
            long nextDeviceCheck = RecordingClock.Now + DeviceCheckInterval;
            while (WaitHandle.WaitAny(events, 100) != 0)
            {
                bool changed = false;
                foreach (Source source in sources) changed |= Drain(source);
                if (RecordingClock.Now >= nextDeviceCheck)
                {
                    foreach (Source source in sources) changed |= FollowDevice(enumerator, source);
                    nextDeviceCheck = RecordingClock.Now + DeviceCheckInterval;
                }
                if (changed) events = GetEvents(sources);
            }
            foreach (Source source in sources) Drain(source);
        }
        catch (Exception ex) { Volatile.Write(ref failure, ex); prepared.TrySetException(ex); }
        finally
        {
            foreach (Source source in sources) source.Endpoint?.Dispose();
            if (mmcss != 0) NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
            if (com) NativeMethods.CoUninitialize();
        }
    }

    private static string? NullIfEmpty(string? id) => string.IsNullOrWhiteSpace(id) ? null : id;

    private WaitHandle[] GetEvents(List<Source> sources) =>
        [stop, .. sources.Where(x => x.Endpoint != null).Select(x => x.Endpoint!.Ready)];

    /// <summary>Returns true when the source lost its endpoint and the wait handles must be rebuilt.</summary>
    private bool Drain(Source source)
    {
        if (source.Endpoint == null) return false;
        try
        {
            Drain(source, source.Endpoint);
            return false;
        }
        catch (Exception ex) when (IsEndpointLost(ex))
        {
            // A chosen device that disappears is replaced by the Windows default rather than ending the recording.
            diagnostic($"{source.Name} device stopped responding; switching to the Windows default device. {ex.Message}");
            source.Endpoint.Dispose();
            source.Endpoint = null;
            source.DeviceId = null;
            mixer.MarkDiscontinuity();
            return true;
        }
    }

    /// <summary>Moves the source to the device it should record, such as a new Windows default output.</summary>
    private bool FollowDevice(IMMDeviceEnumerator enumerator, Source source)
    {
        string? wanted = source.DeviceId ?? GetDefaultDeviceId(enumerator, source.Loopback);
        if (wanted == null || wanted == source.Endpoint?.DeviceId) return false;
        Endpoint? replacement = null;
        string name;
        try
        {
            using IMMDevice device = enumerator.GetDevice(wanted);
            name = device.FriendlyName;
            replacement = Open(device, source.Loopback);
            Begin(replacement);
        }
        catch (Exception ex) when (ex is SharpGenException or System.Runtime.InteropServices.COMException)
        {
            // Devices can be mid-transition. Retry on the next check; report a device only once.
            replacement?.Dispose();
            if (source.FailedDeviceId != wanted) diagnostic($"{source.Name} could not open the new device yet. {ex.Message}");
            source.FailedDeviceId = wanted;
            return false;
        }
        // Keep the last packets of the previous device before closing it.
        Drain(source);
        source.Endpoint?.Dispose();
        replacement.Replacement = true;
        source.Endpoint = replacement;
        source.FailedDeviceId = null;
        mixer.MarkDiscontinuity();
        diagnostic($"{source.Name} now records '{name}'.");
        return true;
    }

    private void Drain(Source source, Endpoint endpoint)
    {
        while (true)
        {
            uint available;
            endpoint.Capture.Pointer->GetNextPacketSize(&available).ThrowOnFailure();
            if (available == 0) return;
            byte* data;
            uint frames, flags;
            ulong position, timestamp;
            endpoint.Capture.Pointer->GetBuffer(&data, &frames, &flags, &position, &timestamp).ThrowOnFailure();
            if (frames == 0) return;
            try
            {
                bool discontinuity = (flags & 1) != 0 || endpoint.Replacement;
                endpoint.Replacement = false;
                if ((flags & 1) != 0) mixer.MarkDiscontinuity();
                long packetTime = (flags & 4) == 0 ? (long)timestamp : endpoint.NextPacketTime != 0 ? endpoint.NextPacketTime : RecordingClock.Now - frames * TimeSpan.TicksPerSecond / AudioMixer.SampleRate;
                if ((flags & 4) != 0) mixer.MarkDiscontinuity();
                endpoint.NextPacketTime = packetTime + frames * TimeSpan.TicksPerSecond / AudioMixer.SampleRate;
                // Silent packets still advance the sample clock; their buffer pointer must not be read.
                ReadOnlySpan<float> packet = (flags & 2) == 0 ? new(data, checked((int)frames * 2)) : ReadOnlySpan<float>.Empty;
                mixer.Write(source.Index, packetTime, checked((int)frames), packet, source.Gain, discontinuity);
            }
            finally { endpoint.Capture.Pointer->ReleaseBuffer(frames).ThrowOnFailure(); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Set();
        thread.Join();
        start.Dispose(); stop.Dispose();
    }
}
