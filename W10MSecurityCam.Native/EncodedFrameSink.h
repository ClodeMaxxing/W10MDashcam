#pragma once
#include "pch.h"
#include "EncodedMediaSink.h"

namespace W10MSecurityCam
{
    namespace Native
    {
        /// Raised for every encoded H.264 sample (usually one video frame). sampleTime is in 100 ns units.
        /// queueDelay: wait in the native hand-off queue; deviceLatency: sensor capture -> encoder output. 100 ns units (-1 = unknown).
        public delegate void FrameHandler(const Platform::Array<uint8>^ data, int64 sampleTime, bool keyframe, int64 queueDelay, int64 deviceLatency);
        public delegate void NativeLogHandler(Platform::String^ message);
        /// Raised for every encoded AAC audio frame (raw, no ADTS header). sampleTime in 100 ns units, same clock as video.
        public delegate void AudioHandler(const Platform::Array<uint8>^ data, int64 sampleTime);

        /// The object the C# app uses: pass MediaExtension to MediaCapture.StartRecordToCustomSinkAsync.
        public ref class EncodedFrameSink sealed
        {
        public:
            EncodedFrameSink(Windows::Media::MediaProperties::IMediaEncodingProperties^ videoProperties);
            /// Video plus AAC audio (audioProperties may be null).
            EncodedFrameSink(Windows::Media::MediaProperties::IMediaEncodingProperties^ videoProperties,
                             Windows::Media::MediaProperties::IMediaEncodingProperties^ audioProperties);
            virtual ~EncodedFrameSink();

            property Windows::Media::IMediaExtension^ MediaExtension
            {
                Windows::Media::IMediaExtension^ get();
            }

            /// SPS/PPS from the encoder's output media type (may be empty).
            Platform::Array<uint8>^ GetSequenceHeader();
            /// AAC AudioSpecificConfig from the audio stream's media type (may be empty).
            Platform::Array<uint8>^ GetAudioConfig();

            event FrameHandler^ FrameReceived;
            event NativeLogHandler^ LogMessage;
            event AudioHandler^ AudioReceived;

        internal:
            void RaiseFrame(const BYTE* data, DWORD length, LONGLONG sampleTime, bool keyframe, LONGLONG queueDelay, LONGLONG deviceLatency);
            void RaiseLog(const wchar_t* message);
            void RaiseAudio(const BYTE* data, DWORD length, LONGLONG sampleTime);

        private:
            void Init(Windows::Media::MediaProperties::IMediaEncodingProperties^ videoProperties,
                      Windows::Media::MediaProperties::IMediaEncodingProperties^ audioProperties);
            Microsoft::WRL::ComPtr<EncodedMediaSink> _sink;
            bool _hooked;
        };
    }
}
