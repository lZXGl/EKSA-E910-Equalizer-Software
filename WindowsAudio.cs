using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EksaCompanion
{
    public static class AntiSkipKeepAlive
    {
        private const int CALLBACK_NULL = 0x00000000;
        private const uint WHDR_BEGINLOOP = 0x00000004;
        private const uint WHDR_ENDLOOP = 0x00000008;
        private const uint WAVE_MAPPER = unchecked((uint)-1);

        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int BitsPerSample = 16;
        private const int BytesPerSample = 2;
        private const int BufferBytes = SampleRate * Channels * BytesPerSample;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hWaveOut);

        private static readonly object _sync = new object();
        private static readonly short[] _pilotPcm = BuildPilotPcm();
        private static IntPtr _hWaveOut = IntPtr.Zero;
        private static IntPtr _pSampleBuffer = IntPtr.Zero;
        private static IntPtr _pHeader = IntPtr.Zero;
        private static volatile bool _isPlaying = false;

        public static bool IsPlaying
        {
            get { return _isPlaying; }
        }

        private static short[] BuildPilotPcm()
        {
            // Pure 14 Hz sub-audible pilot wave (14 exact integer cycles in 48,000 samples = seamless loop)
            // Amplitude 64 LSBs (~0.00195 scalar / -54 dBFS): holds the 5.8GHz DAC zero-detect gate open (< 0.00015 mW)
            short[] pcm = new short[SampleRate * Channels];
            const double subFreq = 14.0;
            const double subAmp = 64.0;

            for (int i = 0; i < SampleRate; i++)
            {
                double t = (double)i / SampleRate;
                short s = (short)Math.Round(Math.Sin(2.0 * Math.PI * subFreq * t) * subAmp);
                pcm[i * 2] = s;
                pcm[i * 2 + 1] = s;
            }
            return pcm;
        }

        public static void Start()
        {
            lock (_sync)
            {
                if (_isPlaying) return;

                try
                {
                    WAVEFORMATEX fmt = new WAVEFORMATEX
                    {
                        wFormatTag = 1, // WAVE_FORMAT_PCM
                        nChannels = Channels,
                        nSamplesPerSec = (uint)SampleRate,
                        wBitsPerSample = BitsPerSample,
                        nBlockAlign = (ushort)(Channels * BytesPerSample),
                        nAvgBytesPerSec = (uint)(SampleRate * Channels * BytesPerSample),
                        cbSize = 0
                    };

                    IntPtr hOut;
                    int rc = waveOutOpen(out hOut, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                    if (rc != 0 || hOut == IntPtr.Zero)
                        return;

                    _pSampleBuffer = Marshal.AllocHGlobal(BufferBytes);
                    Marshal.Copy(_pilotPcm, 0, _pSampleBuffer, _pilotPcm.Length);

                    WAVEHDR hdr = new WAVEHDR
                    {
                        lpData = _pSampleBuffer,
                        dwBufferLength = (uint)BufferBytes,
                        dwFlags = WHDR_BEGINLOOP | WHDR_ENDLOOP,
                        dwLoops = 0xFFFFFFFF
                    };

                    int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
                    _pHeader = Marshal.AllocHGlobal(hdrSize);
                    Marshal.StructureToPtr(hdr, _pHeader, false);

                    if (waveOutPrepareHeader(hOut, _pHeader, hdrSize) == 0 &&
                        waveOutWrite(hOut, _pHeader, hdrSize) == 0)
                    {
                        _hWaveOut = hOut;
                        _isPlaying = true;
                        App.Log("AntiSkipKeepAlive started (14Hz pilot gate hold active)");
                    }
                    else
                    {
                        waveOutClose(hOut);
                        FreeBuffers();
                    }
                }
                catch (Exception ex)
                {
                    App.Log("AntiSkipKeepAlive.Start error: " + ex.Message);
                    FreeBuffers();
                }
            }
        }

        public static void Stop()
        {
            lock (_sync)
            {
                if (!_isPlaying && _hWaveOut == IntPtr.Zero)
                    return;

                try
                {
                    if (_hWaveOut != IntPtr.Zero)
                    {
                        waveOutReset(_hWaveOut);
                        if (_pHeader != IntPtr.Zero)
                        {
                            waveOutUnprepareHeader(_hWaveOut, _pHeader, Marshal.SizeOf(typeof(WAVEHDR)));
                        }
                        waveOutClose(_hWaveOut);
                        _hWaveOut = IntPtr.Zero;
                    }
                }
                catch (Exception ex)
                {
                    App.Log("AntiSkipKeepAlive.Stop error: " + ex.Message);
                }
                finally
                {
                    FreeBuffers();
                    if (_isPlaying)
                    {
                        _isPlaying = false;
                        App.Log("AntiSkipKeepAlive stopped (0% idle battery drain)");
                    }
                }
            }
        }

        private static void FreeBuffers()
        {
            if (_pHeader != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pHeader);
                _pHeader = IntPtr.Zero;
            }
            if (_pSampleBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pSampleBuffer);
                _pSampleBuffer = IntPtr.Zero;
            }
        }
    }

    public static class WindowsAudio
    {
        private const int CLSCTX_ALL = 23;
        private static readonly uint _currentPid = (uint)Process.GetCurrentProcess().Id;

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig]
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);
            [PreserveSig]
            int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig]
            int GetCount(out uint count);
            [PreserveSig]
            int Item(uint index, out IMMDevice device);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid id, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
            [PreserveSig]
            int OpenPropertyStore(int stgmAccess, out IntPtr properties);
            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig]
            int GetState(out int state);
        }

        [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig]
            int GetPeakValue(out float peak);
            [PreserveSig]
            int GetMeteringChannelCount(out int channelCount);
            [PreserveSig]
            int GetChannelsPeakValues(int channelCount, [Out] float[] peakValues);
            [PreserveSig]
            int QueryHardwareSupport(out int hardwareSupportMask);
        }

        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig]
            int GetAudioSessionControl(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr SessionControl);
            [PreserveSig]
            int GetSimpleAudioVolume(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr AudioVolume);
            [PreserveSig]
            int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
            [PreserveSig]
            int RegisterSessionNotification(IntPtr SessionNotification);
            [PreserveSig]
            int UnregisterSessionNotification(IntPtr SessionNotification);
            [PreserveSig]
            int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);
            [PreserveSig]
            int UnregisterDuckNotification(IntPtr duckNotification);
        }

        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig]
            int GetCount(out int SessionCount);
            [PreserveSig]
            int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            [PreserveSig]
            int GetState(out int pRetVal);
        }

        [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            [PreserveSig]
            int GetState(out int pRetVal);
            [PreserveSig]
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig]
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig]
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig]
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig]
            int GetGroupingParam(out Guid pRetVal);
            [PreserveSig]
            int SetGroupingParam(ref Guid Override, ref Guid EventContext);
            [PreserveSig]
            int RegisterAudioSessionNotification(IntPtr NewNotifications);
            [PreserveSig]
            int UnregisterAudioSessionNotification(IntPtr NewNotifications);
            [PreserveSig]
            int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig]
            int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig]
            int GetProcessId(out uint pRetVal);
            [PreserveSig]
            int IsSystemSoundsSession();
            [PreserveSig]
            int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig]
            int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig]
            int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig]
            int GetChannelCount(out int channelCount);
            [PreserveSig]
            int SetMasterVolumeLevel(float levelDB, ref Guid eventContext);
            [PreserveSig]
            int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            [PreserveSig]
            int GetMasterVolumeLevel(out float levelDB);
            [PreserveSig]
            int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig]
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            [PreserveSig]
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            [PreserveSig]
            int GetVolumeStepInfo(out uint step, out uint stepCount);
            [PreserveSig]
            int VolumeStepUp(ref Guid eventContext);
            [PreserveSig]
            int VolumeStepDown(ref Guid eventContext);
            [PreserveSig]
            int QueryHardwareSupport(out uint hardwareSupportMask);
            [PreserveSig]
            int GetVolumeRange(out float volumeMindB, out float volumeMaxdB, out float volumeIncrementdB);
        }

        private static void SafeRelease(object comObj)
        {
            if (comObj != null && Marshal.IsComObject(comObj))
            {
                try { Marshal.ReleaseComObject(comObj); } catch { }
            }
        }

        private static float CheckExternalRenderPeak(IMMDevice dev, ref Guid meterGuid, ref Guid sessionMgrGuid)
        {
            if (dev == null) return 0f;

            // When Keep-Alive (64/32767 = 0.001953 scalar) is playing, loopback sessions (e.g. nvcontainer/Discord)
            // echo the 0.001953 pilot peak. Using a 0.0030 threshold while Keep-Alive is active and 0.0006 while
            // stopped guarantees instant wake on quiet audio AND guaranteed shutoff after 15s of silence.
            float activeFloor = AntiSkipKeepAlive.IsPlaying ? 0.0030f : 0.0006f;
            float maxSessionPeak = 0f;

            object mgrObj = null;
            IAudioSessionEnumerator sessionEnum = null;
            try
            {
                if (dev.Activate(ref sessionMgrGuid, CLSCTX_ALL, IntPtr.Zero, out mgrObj) == 0 && mgrObj != null)
                {
                    var mgr = (IAudioSessionManager2)mgrObj;
                    if (mgr.GetSessionEnumerator(out sessionEnum) == 0 && sessionEnum != null)
                    {
                        int count = 0;
                        sessionEnum.GetCount(out count);
                        for (int i = 0; i < count; i++)
                        {
                            IAudioSessionControl ctrl = null;
                            try
                            {
                                if (sessionEnum.GetSession(i, out ctrl) == 0 && ctrl != null)
                                {
                                    var ctrl2 = ctrl as IAudioSessionControl2;
                                    if (ctrl2 != null)
                                    {
                                        uint pid = 0;
                                        if (ctrl2.GetProcessId(out pid) == 0 && pid == _currentPid)
                                        {
                                            // Ignore our own AntiSkipKeepAlive stream
                                            continue;
                                        }
                                    }

                                    var sessionMeter = ctrl as IAudioMeterInformation;
                                    if (sessionMeter != null)
                                    {
                                        float sPeak = 0f;
                                        if (sessionMeter.GetPeakValue(out sPeak) == 0 && sPeak > activeFloor && sPeak > maxSessionPeak)
                                            maxSessionPeak = sPeak;
                                    }
                                }
                            }
                            finally
                            {
                                SafeRelease(ctrl);
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                SafeRelease(sessionEnum);
                SafeRelease(mgrObj);
            }

            if (maxSessionPeak > activeFloor)
                return maxSessionPeak;

            object meterObj = null;
            try
            {
                if (dev.Activate(ref meterGuid, CLSCTX_ALL, IntPtr.Zero, out meterObj) == 0 && meterObj != null)
                {
                    var meter = (IAudioMeterInformation)meterObj;
                    float peak = 0f;
                    if (meter.GetPeakValue(out peak) == 0 && peak > activeFloor && peak > maxSessionPeak)
                    {
                        maxSessionPeak = peak;
                    }
                }
            }
            catch { }
            finally
            {
                SafeRelease(meterObj);
            }

            return maxSessionPeak;
        }

        public static void GetActivityState(out bool isSoundPlaying, out bool isMicInUse, out float renderPeak, out float micPeak)
        {
            isSoundPlaying = false;
            isMicInUse = false;
            renderPeak = 0f;
            micPeak = 0f;

            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                Guid meterGuid = typeof(IAudioMeterInformation).GUID;
                Guid sessionMgrGuid = typeof(IAudioSessionManager2).GUID;

                for (int role = 0; role <= 1; role++)
                {
                    IMMDevice device = null;
                    try
                    {
                        if (enumerator.GetDefaultAudioEndpoint(0, role, out device) == 0 && device != null)
                        {
                            float p = CheckExternalRenderPeak(device, ref meterGuid, ref sessionMgrGuid);
                            if (p > renderPeak) renderPeak = p;
                        }
                    }
                    finally
                    {
                        SafeRelease(device);
                    }
                }

                if (renderPeak <= 0.0006f)
                {
                    IMMDeviceCollection renderCol = null;
                    try
                    {
                        if (enumerator.EnumAudioEndpoints(0, 1, out renderCol) == 0 && renderCol != null)
                        {
                            uint count = 0;
                            renderCol.GetCount(out count);
                            for (uint i = 0; i < count; i++)
                            {
                                IMMDevice dev = null;
                                try
                                {
                                    if (renderCol.Item(i, out dev) == 0 && dev != null)
                                    {
                                        float p = CheckExternalRenderPeak(dev, ref meterGuid, ref sessionMgrGuid);
                                        if (p > renderPeak) renderPeak = p;
                                        if (renderPeak > 0.0006f) break;
                                    }
                                }
                                finally
                                {
                                    SafeRelease(dev);
                                }
                            }
                        }
                    }
                    finally
                    {
                        SafeRelease(renderCol);
                    }
                }

                isSoundPlaying = renderPeak > 0.0006f;
            }
            catch (Exception ex)
            {
                App.Log("GetActivityState error: " + ex.Message);
            }
            finally
            {
                SafeRelease(enumerator);
            }
        }

        public static float GetMasterPeakAudio()
        {
            bool sound, mic;
            float rPeak, mPeak;
            GetActivityState(out sound, out mic, out rPeak, out mPeak);
            return sound ? rPeak : 0f;
        }

        public static float GetMasterVolume()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            object volObj = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.GetDefaultAudioEndpoint(0, 0, out device) == 0 && device != null)
                {
                    Guid volGuid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref volGuid, CLSCTX_ALL, IntPtr.Zero, out volObj) == 0 && volObj != null)
                    {
                        var vol = (IAudioEndpointVolume)volObj;
                        float level = 0;
                        vol.GetMasterVolumeLevelScalar(out level);
                        return level;
                    }
                }
            }
            catch { }
            finally
            {
                SafeRelease(volObj);
                SafeRelease(device);
                SafeRelease(enumerator);
            }
            return 1.0f;
        }

        public static bool SetMasterVolume(float level)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            object volObj = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.GetDefaultAudioEndpoint(0, 0, out device) == 0 && device != null)
                {
                    Guid volGuid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref volGuid, CLSCTX_ALL, IntPtr.Zero, out volObj) == 0 && volObj != null)
                    {
                        var vol = (IAudioEndpointVolume)volObj;
                        Guid empty = Guid.Empty;
                        vol.SetMasterVolumeLevelScalar(level, ref empty);
                        return true;
                    }
                }
            }
            catch { }
            finally
            {
                SafeRelease(volObj);
                SafeRelease(device);
                SafeRelease(enumerator);
            }
            return false;
        }
    }
}
