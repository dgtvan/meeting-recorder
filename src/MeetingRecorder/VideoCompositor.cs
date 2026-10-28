using System.Runtime.InteropServices;

namespace MeetingRecorder;

/// <summary>
/// Joins recorded videos into one with Media Foundation: from each cut on, the frames of that cut's video are shown,
/// scaled to fit one frame size with black bars. Used for a call in which the user shared their screen: the meeting
/// window's video, cut to the shared window or display while the share lasts and back. The result is re-encoded as
/// H.264 with a variable frame rate and no sound; <see cref="VideoSoundMuxer"/> adds the sound afterwards.
/// </summary>
/// <remarks>
/// Calls the COM interfaces through their vtables, like <see cref="VideoSoundMuxer"/>. Slot numbers follow
/// mfobjects.h and mfreadwrite.h.
/// </remarks>
static unsafe class VideoCompositor
{
    /// <summary>A video to take frames from; <paramref name="Offset"/> is where its first frame is on the output's clock (100 ns units).</summary>
    public sealed record Input(string Path, long Offset);

    /// <summary>From <paramref name="At"/> (100 ns units on the output's clock) on, the frames of input <paramref name="Input"/> are shown.</summary>
    public sealed record Cut(long At, int Input);

    private const int MF_VERSION = 0x00020070;
    private const int FirstVideoStream = unchecked((int)0xFFFFFFFC);
    private const int AllStreams = unchecked((int)0xFFFFFFFE);
    private const int ReaderEndOfStream = 0x2;
    private const int Framerate = 30;
    // The ceiling for quality-based rate control, in bits per pixel at the nominal frame rate.
    private const double MaxBitsPerPixel = 0.08;
    private const int RateControlQuality = 3;
    private const int H264ProfileMain = 77;
    private const int InterlaceProgressive = 2;
    private const int BlackCheckStep = 8;
    private const byte BlackLuma = 24;
    private static readonly long FrameTicks = TimeSpan.TicksPerSecond / Framerate;

    private static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid MF_TRANSCODE_CONTAINERTYPE = new("150FF23F-4ABC-478B-AC4F-E1916FBA1CCA");
    private static readonly Guid MFTranscodeContainerType_MPEG4 = new("DC6CD05D-B9D0-40EF-BD35-FA622C1AB28A");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");

    /// <summary>The frame size of a video, or null when it can't be read.</summary>
    public static Size? FrameSize(string path)
    {
        Check(MFStartup(MF_VERSION, 0));
        IntPtr reader = 0, type = 0;
        try
        {
            if (MFCreateSourceReaderFromURL(path, 0, out reader) < 0
                || ((delegate* unmanaged<IntPtr, int, IntPtr*, int>)Slot(reader, 6))(reader, FirstVideoStream, &type) < 0)
                return null;
            return GetSize(type);
        }
        finally
        {
            Release(type);
            Release(reader);
            MFShutdown();
        }
    }

    /// <summary>
    /// Writes <paramref name="outputPath"/>, <paramref name="frameSize"/> large: the frames of each cut's input from
    /// that cut on. A cut starts with the input's latest frame up to then, so a still screen shows at once. The first
    /// cut should be at 0. <paramref name="quality"/> (0-100) is the encoder's quality, as for a recording.
    /// </summary>
    public static void Write(IReadOnlyList<Input> inputs, IReadOnlyList<Cut> cuts, Size frameSize, int quality, string outputPath)
    {
        var canvas = new Size(frameSize.Width & ~1, frameSize.Height & ~1);
        Check(MFStartup(MF_VERSION, 0));
        var sources = new List<Source>();
        IntPtr writer = 0;
        try
        {
            foreach (var input in inputs)
                sources.Add(Source.Open(input, canvas));
            writer = CreateWriter(outputPath, canvas, quality, out int stream);
            long written = -1;
            void Emit(Source source, long at)
            {
                at = Math.Max(at, written + 1);
                WriteFrame(writer, stream, source.Frame!, at);
                written = at;
            }

            var ordered = cuts.OrderBy(c => c.At).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var source = sources[ordered[i].Input];
                long start = ordered[i].At;
                long end = i + 1 < ordered.Count ? ordered[i + 1].At : long.MaxValue;
                while (source.Peek() is { } next && next <= start)
                    source.Take();
                if (source.HasFrame)
                    Emit(source, start);
                while (source.Peek() is { } time && time < end)
                {
                    source.Take();
                    Emit(source, time);
                }
            }
            Check(((delegate* unmanaged<IntPtr, int>)Slot(writer, 11))(writer));
        }
        finally
        {
            foreach (var source in sources)
                source.Dispose();
            Release(writer);
            MFShutdown();
        }
    }

    /// <summary>One input, decoded to NV12 at the size that fits the canvas, placed in the middle of a black canvas.</summary>
    private sealed class Source : IDisposable
    {
        private IntPtr _reader;
        private IntPtr _peeked;
        private long _peekedTime;
        private IntPtr _taken;
        private long? _first;
        private bool _ended;
        private long _offset;
        private Size _canvas;
        private Size _size;
        private Point _at;
        private int _stride;
        private byte[]? _frame;
        private bool _frameCurrent;
        private bool _drawn;

        public static Source Open(Input input, Size canvas)
        {
            var source = new Source { _offset = input.Offset, _canvas = canvas };
            IntPtr attributes = 0, type = 0, native = 0;
            try
            {
                Check(MFCreateAttributes(out attributes, 1));
                Check(SetUInt32(attributes, MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1));
                Check(MFCreateSourceReaderFromURL(input.Path, attributes, out source._reader));
                Check(Call(source._reader, 4, AllStreams, 0));
                Check(Call(source._reader, 4, FirstVideoStream, 1));
                Check(((delegate* unmanaged<IntPtr, int, IntPtr*, int>)Slot(source._reader, 6))(source._reader, FirstVideoStream, &native));
                var size = GetSize(native) ?? canvas;
                source._size = Fit(size, canvas);
                source._at = new Point((canvas.Width - source._size.Width) / 2 & ~1, (canvas.Height - source._size.Height) / 2 & ~1);

                type = VideoType(MFVideoFormat_NV12, source._size);
                Check(((delegate* unmanaged<IntPtr, int, IntPtr, IntPtr, int>)Slot(source._reader, 7))(source._reader, FirstVideoStream, 0, type));
                Release(type);
                type = 0;
                Check(((delegate* unmanaged<IntPtr, int, IntPtr*, int>)Slot(source._reader, 6))(source._reader, FirstVideoStream, &type));
                int stride;
                Guid strideKey = MF_MT_DEFAULT_STRIDE;
                source._stride = ((delegate* unmanaged<IntPtr, Guid*, int*, int>)Slot(type, 7))(type, &strideKey, &stride) >= 0
                    ? Math.Abs(stride) : source._size.Width;
                return source;
            }
            catch
            {
                source.Dispose();
                throw;
            }
            finally
            {
                Release(type);
                Release(native);
                Release(attributes);
            }
        }

        public bool HasFrame => _taken != 0 || _frame != null;

        /// <summary>The canvas with the latest taken frame.</summary>
        public byte[]? Frame
        {
            get
            {
                if (!_frameCurrent && _taken != 0)
                {
                    Draw(_taken);
                    _frameCurrent = true;
                }
                return _frame;
            }
        }

        /// <summary>The output time of the next frame, or null at the end.</summary>
        public long? Peek()
        {
            while (_peeked == 0 && !_ended)
            {
                int actualStream, flags;
                long time;
                IntPtr sample;
                Check(((delegate* unmanaged<IntPtr, int, int, int*, int*, long*, IntPtr*, int>)Slot(_reader, 9))(
                    _reader, FirstVideoStream, 0, &actualStream, &flags, &time, &sample));
                if (sample != 0)
                {
                    _first ??= time;
                    _peeked = sample;
                    _peekedTime = time - _first.Value + _offset;
                }
                if ((flags & ReaderEndOfStream) != 0)
                    _ended = true;
            }
            return _peeked != 0 ? _peekedTime : null;
        }

        /// <summary>Makes the peeked frame the latest one; it is only drawn when shown.</summary>
        public void Take()
        {
            Release(_taken);
            _taken = _peeked;
            _peeked = 0;
            _frameCurrent = false;
        }

        private void Draw(IntPtr sample)
        {
            if (_frame == null)
            {
                _frame = new byte[_canvas.Width * _canvas.Height * 3 / 2];
                Array.Fill(_frame, (byte)16, 0, _canvas.Width * _canvas.Height);
                Array.Fill(_frame, (byte)128, _canvas.Width * _canvas.Height, _canvas.Width * _canvas.Height / 2);
            }
            IntPtr buffer = 0;
            try
            {
                Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer));
                byte* data;
                int max, length;
                Check(((delegate* unmanaged<IntPtr, byte**, int*, int*, int>)Slot(buffer, 3))(buffer, &data, &max, &length));
                try
                {
                    // Decoders may pad the height (1080 to 1088); the chroma plane follows the padded luma plane.
                    int lumaRows = Math.Max(_size.Height, length * 2 / 3 / Math.Max(1, _stride));
                    if ((long)_stride * (lumaRows + _size.Height / 2) > length)
                        throw new InvalidDataException($"A decoded frame is {length} bytes, too small for {_size.Width}x{_size.Height}.");
                    // A minimized window records as black frames: the meeting app minimizes its meeting window as you
                    // pick what to share, seconds before the share starts. The last real frame stays instead.
                    if (_drawn && IsBlack(data))
                        return;
                    _drawn = true;
                    int width = _canvas.Width;
                    fixed (byte* frame = _frame)
                    {
                        for (int y = 0; y < _size.Height; y++)
                            Buffer.MemoryCopy(data + (long)y * _stride, frame + (long)(_at.Y + y) * width + _at.X, _size.Width, _size.Width);
                        byte* chroma = data + (long)_stride * lumaRows;
                        byte* canvasChroma = frame + (long)width * _canvas.Height;
                        for (int y = 0; y < _size.Height / 2; y++)
                            Buffer.MemoryCopy(chroma + (long)y * _stride, canvasChroma + (long)(_at.Y / 2 + y) * width + _at.X, _size.Width, _size.Width);
                    }
                }
                finally
                {
                    ((delegate* unmanaged<IntPtr, int>)Slot(buffer, 4))(buffer);
                }
            }
            finally
            {
                Release(buffer);
            }
        }

        /// <summary>True when every sampled pixel is black (luma at most a little above video black, 16).</summary>
        private bool IsBlack(byte* luma)
        {
            for (int y = 0; y < _size.Height; y += BlackCheckStep)
            {
                byte* row = luma + (long)y * _stride;
                for (int x = 0; x < _size.Width; x += BlackCheckStep)
                {
                    if (row[x] > BlackLuma)
                        return false;
                }
            }
            return true;
        }

        public void Dispose()
        {
            Release(_peeked);
            Release(_taken);
            Release(_reader);
            _peeked = _taken = _reader = 0;
        }
    }

    /// <summary>The largest size with the same aspect ratio that fits in <paramref name="canvas"/>, even in both directions.</summary>
    private static Size Fit(Size size, Size canvas)
    {
        double scale = Math.Min(canvas.Width / (double)size.Width, canvas.Height / (double)size.Height);
        int width = Math.Min(canvas.Width, (int)Math.Round(size.Width * scale)) & ~1;
        int height = Math.Min(canvas.Height, (int)Math.Round(size.Height * scale)) & ~1;
        return new Size(Math.Max(2, width), Math.Max(2, height));
    }

    private static IntPtr CreateWriter(string path, Size canvas, int quality, out int stream)
    {
        IntPtr attributes = 0, output = 0, input = 0, writer = 0, encoding = 0;
        try
        {
            Check(MFCreateAttributes(out attributes, 3));
            Check(SetGuid(attributes, MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4));
            Check(SetUInt32(attributes, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1));
            Check(SetUInt32(attributes, MF_SINK_WRITER_DISABLE_THROTTLING, 1));
            Check(MFCreateSinkWriterFromURL(path, 0, attributes, out writer));

            output = VideoType(MFVideoFormat_H264, canvas);
            Check(SetUInt32(output, MF_MT_AVG_BITRATE, (int)(canvas.Width * canvas.Height * Framerate * MaxBitsPerPixel)));
            Check(SetUInt32(output, MF_MT_MPEG2_PROFILE, H264ProfileMain));
            int index;
            Check(((delegate* unmanaged<IntPtr, IntPtr, int*, int>)Slot(writer, 3))(writer, output, &index));
            stream = index;
            input = VideoType(MFVideoFormat_NV12, canvas);
            // Quality-based like the recordings: a still screen then costs next to nothing.
            Check(MFCreateAttributes(out encoding, 2));
            Check(SetUInt32(encoding, CODECAPI_AVEncCommonRateControlMode, RateControlQuality));
            Check(SetUInt32(encoding, CODECAPI_AVEncCommonQuality, Math.Clamp(quality, 1, 100)));
            Check(((delegate* unmanaged<IntPtr, int, IntPtr, IntPtr, int>)Slot(writer, 4))(writer, index, input, encoding));
            Check(((delegate* unmanaged<IntPtr, int>)Slot(writer, 5))(writer));
            var result = writer;
            writer = 0;
            return result;
        }
        finally
        {
            Release(encoding);
            Release(input);
            Release(output);
            Release(attributes);
            Release(writer);
        }
    }

    private static void WriteFrame(IntPtr writer, int stream, byte[] frame, long time)
    {
        Check(MFCreateMemoryBuffer(frame.Length, out IntPtr media));
        IntPtr sample = 0;
        try
        {
            byte* data;
            int max, current;
            Check(((delegate* unmanaged<IntPtr, byte**, int*, int*, int>)Slot(media, 3))(media, &data, &max, &current));
            Marshal.Copy(frame, 0, (IntPtr)data, frame.Length);
            Check(((delegate* unmanaged<IntPtr, int>)Slot(media, 4))(media));
            Check(((delegate* unmanaged<IntPtr, int, int>)Slot(media, 6))(media, frame.Length));
            Check(MFCreateSample(out sample));
            Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Slot(sample, 42))(sample, media));
            Check(((delegate* unmanaged<IntPtr, long, int>)Slot(sample, 36))(sample, time));
            Check(((delegate* unmanaged<IntPtr, long, int>)Slot(sample, 38))(sample, FrameTicks));
            Check(((delegate* unmanaged<IntPtr, int, IntPtr, int>)Slot(writer, 6))(writer, stream, sample));
        }
        finally
        {
            Release(sample);
            Release(media);
        }
    }

    private static IntPtr VideoType(Guid subtype, Size size)
    {
        Check(MFCreateMediaType(out IntPtr type));
        Check(SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video));
        Check(SetGuid(type, MF_MT_SUBTYPE, subtype));
        Check(SetUInt64(type, MF_MT_FRAME_SIZE, ((long)size.Width << 32) | (uint)size.Height));
        Check(SetUInt64(type, MF_MT_FRAME_RATE, ((long)Framerate << 32) | 1));
        Check(SetUInt64(type, MF_MT_PIXEL_ASPECT_RATIO, (1L << 32) | 1));
        Check(SetUInt32(type, MF_MT_INTERLACE_MODE, InterlaceProgressive));
        return type;
    }

    private static Size? GetSize(IntPtr type)
    {
        long value;
        Guid key = MF_MT_FRAME_SIZE;
        if (((delegate* unmanaged<IntPtr, Guid*, long*, int>)Slot(type, 8))(type, &key, &value) < 0)
            return null;
        return new Size((int)(value >> 32), (int)(value & 0xFFFFFFFF));
    }

    private static IntPtr Slot(IntPtr com, int index) => (*(IntPtr**)com)[index];

    private static int Call(IntPtr com, int slot, int a, int b) =>
        ((delegate* unmanaged<IntPtr, int, int, int>)Slot(com, slot))(com, a, b);

    // IMFAttributes::SetUINT32, SetUINT64 and SetGUID.
    private static int SetUInt32(IntPtr attributes, Guid key, int value) =>
        ((delegate* unmanaged<IntPtr, Guid*, int, int>)Slot(attributes, 21))(attributes, &key, value);

    private static int SetUInt64(IntPtr attributes, Guid key, long value) =>
        ((delegate* unmanaged<IntPtr, Guid*, long, int>)Slot(attributes, 22))(attributes, &key, value);

    private static int SetGuid(IntPtr attributes, Guid key, Guid value) =>
        ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value);

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
