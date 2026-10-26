using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MeetingRecorder;

/// <summary>
/// Adds a sound track to a recorded video with Media Foundation: the H.264 video is copied as it is (no re-encoding)
/// and the sound is encoded to AAC. The video is recorded without sound so its sound can come from the separate audio
/// files, which follow device changes; see <see cref="RecordingFinisher"/>.
/// </summary>
/// <remarks>
/// Calls the COM interfaces through their vtables: the Media Foundation interfaces NAudio declares are internal, and
/// only a few methods are needed. Slot numbers follow mfobjects.h and mfreadwrite.h.
/// </remarks>
static unsafe class VideoSoundMuxer
{
    private const int MF_VERSION = 0x00020070;
    private const int FirstVideoStream = unchecked((int)0xFFFFFFFC);
    private const int AllStreams = unchecked((int)0xFFFFFFFE);
    private const int ReaderEndOfStream = 0x2;
    private const int ReaderStreamTick = 0x100;
    private const int AacBytesPerSecond = 16_000;
    private static readonly long ChunkTicks = TimeSpan.FromMilliseconds(100).Ticks;
    // Sound is written up to this far ahead of the video, so the file interleaves them.
    private static readonly long LeadTicks = TimeSpan.FromSeconds(1).Ticks;

    private static readonly Guid MF_TRANSCODE_CONTAINERTYPE = new("150FF23F-4ABC-478B-AC4F-E1916FBA1CCA");
    private static readonly Guid MFTranscodeContainerType_MPEG4 = new("DC6CD05D-B9D0-40EF-BD35-FA622C1AB28A");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");

    /// <summary>
    /// Writes <paramref name="outputPath"/>: the video of <paramref name="videoPath"/> with <paramref name="sound"/>
    /// (16-bit stereo, 48 kHz) as its sound, <paramref name="soundLength"/> long, starting with the first video frame.
    /// The sound may run past the last frame: a variable frame rate video gets no new frames while nothing changes.
    /// </summary>
    public static void Write(string videoPath, IWaveProvider sound, TimeSpan soundLength, string outputPath)
    {
        var format = sound.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 || format.Channels != 2)
            throw new ArgumentException("The sound must be 16-bit stereo PCM.", nameof(sound));

        Check(MFStartup(MF_VERSION, 0));
        IntPtr reader = 0, writer = 0, attributes = 0, videoType = 0, aacType = 0, pcmType = 0;
        try
        {
            Check(MFCreateSourceReaderFromURL(videoPath, 0, out reader));
            Check(Call(reader, 4, AllStreams, 0));
            Check(Call(reader, 4, FirstVideoStream, 1));
            Check(CallOut(reader, 6, FirstVideoStream, out videoType));

            Check(MFCreateAttributes(out attributes, 1));
            Check(SetGuid(attributes, MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4));
            Check(MFCreateSinkWriterFromURL(outputPath, 0, attributes, out writer));

            int videoStream, soundStream;
            Check(AddStream(writer, videoType, out videoStream));
            Check(SetInputMediaType(writer, videoStream, videoType));

            aacType = AudioType(MFAudioFormat_AAC, format, AacBytesPerSecond, blockAlign: 0);
            pcmType = AudioType(MFAudioFormat_PCM, format, format.AverageBytesPerSecond, format.BlockAlign);
            Check(AddStream(writer, aacType, out soundStream));
            Check(SetInputMediaType(writer, soundStream, pcmType));
            Check(((delegate* unmanaged<IntPtr, int>)Slot(writer, 5))(writer));

            long soundEnd = soundLength.Ticks;
            long soundWritten = 0;
            long? firstFrame = null;
            var buffer = new byte[(int)(format.AverageBytesPerSecond * (ChunkTicks / (double)TimeSpan.TicksPerSecond))];
            while (true)
            {
                int actualStream, flags;
                long time;
                IntPtr sample;
                Check(((delegate* unmanaged<IntPtr, int, int, int*, int*, long*, IntPtr*, int>)Slot(reader, 9))(
                    reader, FirstVideoStream, 0, &actualStream, &flags, &time, &sample));
                if (sample != 0)
                {
                    try
                    {
                        firstFrame ??= time;
                        long at = time - firstFrame.Value;
                        soundWritten = WriteSound(writer, soundStream, sound, buffer, soundWritten, Math.Min(soundEnd, at + LeadTicks));
                        Check(((delegate* unmanaged<IntPtr, long, int>)Slot(sample, 36))(sample, at));
                        Check(((delegate* unmanaged<IntPtr, int, IntPtr, int>)Slot(writer, 6))(writer, videoStream, sample));
                    }
                    finally
                    {
                        Release(sample);
                    }
                }
                else if ((flags & ReaderStreamTick) != 0 && firstFrame is { } first)
                {
                    Check(((delegate* unmanaged<IntPtr, int, long, int>)Slot(writer, 7))(writer, videoStream, time - first));
                }
                if ((flags & ReaderEndOfStream) != 0)
                    break;
            }
            WriteSound(writer, soundStream, sound, buffer, soundWritten, soundEnd);
            Check(((delegate* unmanaged<IntPtr, int>)Slot(writer, 11))(writer));
        }
        finally
        {
            Release(pcmType);
            Release(aacType);
            Release(videoType);
            Release(attributes);
            Release(writer);
            Release(reader);
            MFShutdown();
        }
    }

    /// <summary>Writes sound from <paramref name="written"/> up to <paramref name="until"/> (100 ns units); returns where it got to.</summary>
    private static long WriteSound(IntPtr writer, int stream, IWaveProvider sound, byte[] buffer, long written, long until)
    {
        var format = sound.WaveFormat;
        while (written < until)
        {
            long chunk = Math.Min(ChunkTicks, until - written);
            int bytes = (int)(chunk * format.AverageBytesPerSecond / TimeSpan.TicksPerSecond) / format.BlockAlign * format.BlockAlign;
            if (bytes == 0)
                break;
            int read = sound.Read(buffer.AsSpan(0, bytes));
            // Past the end of the audio files: silence, so the sound lasts as long as the recording.
            Array.Clear(buffer, read, bytes - read);
            long duration = bytes * (long)TimeSpan.TicksPerSecond / format.AverageBytesPerSecond;

            Check(MFCreateMemoryBuffer(bytes, out IntPtr media));
            IntPtr sample = 0;
            try
            {
                byte* data;
                int max, current;
                Check(((delegate* unmanaged<IntPtr, byte**, int*, int*, int>)Slot(media, 3))(media, &data, &max, &current));
                Marshal.Copy(buffer, 0, (IntPtr)data, bytes);
                Check(((delegate* unmanaged<IntPtr, int>)Slot(media, 4))(media));
                Check(((delegate* unmanaged<IntPtr, int, int>)Slot(media, 6))(media, bytes));
                Check(MFCreateSample(out sample));
                Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Slot(sample, 42))(sample, media));
                Check(((delegate* unmanaged<IntPtr, long, int>)Slot(sample, 36))(sample, written));
                Check(((delegate* unmanaged<IntPtr, long, int>)Slot(sample, 38))(sample, duration));
                Check(((delegate* unmanaged<IntPtr, int, IntPtr, int>)Slot(writer, 6))(writer, stream, sample));
            }
            finally
            {
                Release(sample);
                Release(media);
            }
            written += duration;
        }
        return written;
    }

    private static IntPtr AudioType(Guid subtype, WaveFormat format, int bytesPerSecond, int blockAlign)
    {
        Check(MFCreateMediaType(out IntPtr type));
        Check(SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Audio));
        Check(SetGuid(type, MF_MT_SUBTYPE, subtype));
        Check(SetUInt32(type, MF_MT_AUDIO_BITS_PER_SAMPLE, format.BitsPerSample));
        Check(SetUInt32(type, MF_MT_AUDIO_SAMPLES_PER_SECOND, format.SampleRate));
        Check(SetUInt32(type, MF_MT_AUDIO_NUM_CHANNELS, format.Channels));
        Check(SetUInt32(type, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bytesPerSecond));
        if (blockAlign > 0)
            Check(SetUInt32(type, MF_MT_AUDIO_BLOCK_ALIGNMENT, blockAlign));
        return type;
    }

    private static IntPtr Slot(IntPtr com, int index) => (*(IntPtr**)com)[index];

    private static int Call(IntPtr com, int slot, int a, int b) =>
        ((delegate* unmanaged<IntPtr, int, int, int>)Slot(com, slot))(com, a, b);

    private static int CallOut(IntPtr com, int slot, int a, out IntPtr result)
    {
        IntPtr value;
        int hr = ((delegate* unmanaged<IntPtr, int, IntPtr*, int>)Slot(com, slot))(com, a, &value);
        result = value;
        return hr;
    }

    // IMFAttributes::SetUINT32 and SetGUID.
    private static int SetUInt32(IntPtr attributes, Guid key, int value) =>
        ((delegate* unmanaged<IntPtr, Guid*, int, int>)Slot(attributes, 21))(attributes, &key, value);

    private static int SetGuid(IntPtr attributes, Guid key, Guid value) =>
        ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value);

    // IMFSinkWriter::AddStream and SetInputMediaType.
    private static int AddStream(IntPtr writer, IntPtr type, out int stream)
    {
        int index;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr, int*, int>)Slot(writer, 3))(writer, type, &index);
        stream = index;
        return hr;
    }

    private static int SetInputMediaType(IntPtr writer, int stream, IntPtr type) =>
        ((delegate* unmanaged<IntPtr, int, IntPtr, IntPtr, int>)Slot(writer, 4))(writer, stream, type, 0);

    private static void Release(IntPtr com)
    {
        if (com != 0)
            ((delegate* unmanaged<IntPtr, uint>)Slot(com, 2))(com);
    }

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFCreateAttributes(out IntPtr attributes, int initialSize);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IntPtr type);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateSample(out IntPtr sample);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMemoryBuffer(int maxLength, out IntPtr buffer);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IntPtr writer);
}
