using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace W10MDashcam.Streaming
{
    /// <summary>
    /// A fake "file" that Windows' MP4 recorder writes into. Instead of storing the
    /// bytes, we hand them (in order) to the H.264 extractor, which pulls the encoded
    /// video frames out live. When recording stops the recorder seeks back to patch
    /// headers; those late rewrites are simply ignored.
    /// </summary>
    internal sealed class Mp4TapStream : IRandomAccessStream
    {
        private readonly Mp4H264Extractor _extractor;
        private readonly Action<string> _log;
        private readonly object _lock = new object();
        private readonly SortedDictionary<ulong, byte[]> _pending = new SortedDictionary<ulong, byte[]>();
        private ulong _position;
        private ulong _size;
        private ulong _sequentialEnd; // bytes already delivered to the extractor, in order
        private bool _warnedGap;

        // write-burst statistics (how Windows hands us the video)
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private long _lastWriteMs = -1, _statStartMs, _writes, _writeBytes, _maxGapMs, _maxWrite;

        public Mp4TapStream(Mp4H264Extractor extractor, Action<string> log)
        {
            _extractor = extractor;
            _log = log ?? (_ => { });
        }

        internal void WriteAt(ulong position, byte[] data)
        {
            lock (_lock)
            {
                long now = _clock.ElapsedMilliseconds;
                if (_lastWriteMs >= 0) _maxGapMs = Math.Max(_maxGapMs, now - _lastWriteMs);
                _lastWriteMs = now;
                _writes++; _writeBytes += data.Length; _maxWrite = Math.Max(_maxWrite, data.Length);
                if (now - _statStartMs >= 10000)
                {
                    _log(string.Format("Recorder writes (last {0:0.0}s): {1} writes, avg {2} KB, biggest {3} KB, longest pause {4} ms",
                        (now - _statStartMs) / 1000.0, _writes, _writes == 0 ? 0 : _writeBytes / _writes / 1024, _maxWrite / 1024, _maxGapMs));
                    _statStartMs = now; _writes = 0; _writeBytes = 0; _maxGapMs = 0; _maxWrite = 0;
                }

                ulong end = position + (ulong)data.Length;
                if (end > _size) _size = end;

                if (position == _sequentialEnd)
                {
                    Deliver(data, 0, data.Length);
                    DrainPending();
                }
                else if (position < _sequentialEnd)
                {
                    // Rewrite of something we already passed on (header patch). Pass on only new tail bytes.
                    if (end > _sequentialEnd)
                    {
                        int skip = (int)(_sequentialEnd - position);
                        Deliver(data, skip, data.Length - skip);
                        DrainPending();
                    }
                }
                else
                {
                    if (!_warnedGap)
                    {
                        _warnedGap = true;
                        _log("Note: recorder wrote out of order (gap at " + _sequentialEnd + ", write at " + position + ")");
                    }
                    _pending[position] = data;
                }
            }
        }

        private void Deliver(byte[] data, int offset, int count)
        {
            _extractor.Feed(data, offset, count);
            _sequentialEnd += (ulong)count;
        }

        private void DrainPending()
        {
            while (_pending.Count > 0)
            {
                ulong first = 0;
                foreach (var k in _pending.Keys) { first = k; break; }
                if (first > _sequentialEnd) return;
                byte[] chunk = _pending[first];
                _pending.Remove(first);
                ulong end = first + (ulong)chunk.Length;
                if (end > _sequentialEnd)
                {
                    int skip = (int)(_sequentialEnd - first);
                    Deliver(chunk, skip, chunk.Length - skip);
                }
            }
        }

        // ---- IRandomAccessStream ----

        public bool CanRead => true;
        public bool CanWrite => true;
        public ulong Position => _position;

        public ulong Size
        {
            get { return _size; }
            set { _size = value; }
        }

        public void Seek(ulong position) => _position = position;

        public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer)
        {
            byte[] data = buffer.ToArray();
            ulong at;
            lock (_lock) { at = _position; _position += (ulong)data.Length; }
            WriteAt(at, data);
            uint written = (uint)data.Length;
            return AsyncInfo.Run<uint, uint>((token, progress) => Task.FromResult(written));
        }

        public IAsyncOperation<bool> FlushAsync()
        {
            return AsyncInfo.Run(token => Task.FromResult(true));
        }

        public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options)
        {
            // The recorder should never read back. Report "end of stream".
            buffer.Length = 0;
            return AsyncInfo.Run<IBuffer, uint>((token, progress) => Task.FromResult(buffer));
        }

        public IOutputStream GetOutputStreamAt(ulong position) => new PositionedWriter(this, position);

        public IInputStream GetInputStreamAt(ulong position) => new EmptyInput();

        public IRandomAccessStream CloneStream()
        {
            throw new NotSupportedException("Mp4TapStream cannot be cloned");
        }

        public void Dispose() { }

        private sealed class PositionedWriter : IOutputStream
        {
            private readonly Mp4TapStream _owner;
            private ulong _pos;

            public PositionedWriter(Mp4TapStream owner, ulong pos)
            {
                _owner = owner;
                _pos = pos;
            }

            public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer)
            {
                byte[] data = buffer.ToArray();
                _owner.WriteAt(_pos, data);
                _pos += (ulong)data.Length;
                uint written = (uint)data.Length;
                return AsyncInfo.Run<uint, uint>((token, progress) => Task.FromResult(written));
            }

            public IAsyncOperation<bool> FlushAsync() => AsyncInfo.Run(token => Task.FromResult(true));

            public void Dispose() { }
        }

        private sealed class EmptyInput : IInputStream
        {
            public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options)
            {
                buffer.Length = 0;
                return AsyncInfo.Run<IBuffer, uint>((token, progress) => Task.FromResult(buffer));
            }

            public void Dispose() { }
        }
    }
}
