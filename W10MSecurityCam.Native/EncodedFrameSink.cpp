#include "pch.h"
#include "EncodedFrameSink.h"

using namespace W10MSecurityCam::Native;
using namespace Microsoft::WRL;
using namespace Platform;

EncodedFrameSink::EncodedFrameSink(Windows::Media::MediaProperties::IMediaEncodingProperties^ videoProperties)
    : _hooked(false)
{
    Init(videoProperties, nullptr);
}

EncodedFrameSink::EncodedFrameSink(Windows::Media::MediaProperties::IMediaEncodingProperties^ videoProperties,
                                   Windows::Media::MediaProperties::IMediaEncodingProperties^ audioProperties)
    : _hooked(false)
{
    Init(videoProperties, audioProperties);
}

void EncodedFrameSink::Init(Windows::Media::MediaProperties::IMediaEncodingProperties^ videoProperties,
                            Windows::Media::MediaProperties::IMediaEncodingProperties^ audioProperties)
{
    MFStartup(MF_VERSION, MFSTARTUP_LITE);

    ComPtr<IMFMediaType> type, audioType;
    if (videoProperties != nullptr)
    {
        HRESULT hr = MFCreateMediaTypeFromProperties(reinterpret_cast<IUnknown*>(videoProperties), &type);
        if (FAILED(hr)) type = nullptr;
    }
    if (audioProperties != nullptr)
    {
        HRESULT hr = MFCreateMediaTypeFromProperties(reinterpret_cast<IUnknown*>(audioProperties), &audioType);
        if (FAILED(hr)) audioType = nullptr;
        if (audioType)
        {
            // The capture engine configures the AAC encoder's output with this type, and the Microsoft AAC
            // encoder insists on a complete one: 16-bit samples, one of its bitrates, raw AAC payload.
            // The encoder writes its own AudioSpecificConfig, so don't hand it a user-data blob to argue with.
            UINT32 v = 0;
            if (FAILED(audioType->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &v)) || v == 0)
                audioType->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            v = 0;
            if (FAILED(audioType->GetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, &v)) ||
                (v != 12000 && v != 16000 && v != 20000 && v != 24000))
                audioType->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 12000);
            audioType->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0);
            audioType->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
            audioType->DeleteItem(MF_MT_USER_DATA);
            audioType->DeleteItem(MF_MT_AUDIO_BLOCK_ALIGNMENT);
        }
    }

    HRESULT hr = MakeAndInitialize<EncodedMediaSink>(&_sink, type.Get(), audioType.Get());
    if (FAILED(hr)) throw Exception::CreateException(hr, L"Could not create the native media sink");
}

EncodedFrameSink::~EncodedFrameSink()
{
    if (_sink) _sink->ClearCallbacks();
    _sink = nullptr;
    MFShutdown();
}

Windows::Media::IMediaExtension^ EncodedFrameSink::MediaExtension::get()
{
    if (!_hooked)
    {
        _hooked = true;
        WeakReference weak(this);
        _sink->SetCallbacks(
            [weak](const BYTE* data, DWORD length, LONGLONG time, bool keyframe, LONGLONG queueDelay, LONGLONG deviceLatency)
            {
                auto self = weak.Resolve<EncodedFrameSink>();
                if (self != nullptr) self->RaiseFrame(data, length, time, keyframe, queueDelay, deviceLatency);
            },
            [weak](const wchar_t* message)
            {
                auto self = weak.Resolve<EncodedFrameSink>();
                if (self != nullptr) self->RaiseLog(message);
            },
            [weak](const BYTE* data, DWORD length, LONGLONG time)
            {
                auto self = weak.Resolve<EncodedFrameSink>();
                if (self != nullptr) self->RaiseAudio(data, length, time);
            });
    }
    // Hand out our own reference: reinterpret_cast to a ^ does not AddRef, but the ^ releases when done.
    ComPtr<ABI::Windows::Media::IMediaExtension> extension;
    _sink.As(&extension);
    return reinterpret_cast<Windows::Media::IMediaExtension^>(extension.Detach());
}

Array<uint8>^ EncodedFrameSink::GetSequenceHeader()
{
    auto header = _sink->GetSequenceHeader();
    auto result = ref new Array<uint8>((unsigned int)header.size());
    if (!header.empty()) memcpy(result->Data, header.data(), header.size());
    return result;
}

void EncodedFrameSink::RaiseFrame(const BYTE* data, DWORD length, LONGLONG sampleTime, bool keyframe, LONGLONG queueDelay, LONGLONG deviceLatency)
{
    auto copy = ref new Array<uint8>(const_cast<uint8*>(data), length);
    FrameReceived(copy, sampleTime, keyframe, queueDelay, deviceLatency);
}

Array<uint8>^ EncodedFrameSink::GetAudioConfig()
{
    auto config = _sink->GetAudioConfig();
    auto result = ref new Array<uint8>((unsigned int)config.size());
    if (!config.empty()) memcpy(result->Data, config.data(), config.size());
    return result;
}

void EncodedFrameSink::RaiseAudio(const BYTE* data, DWORD length, LONGLONG sampleTime)
{
    auto copy = ref new Array<uint8>(const_cast<uint8*>(data), length);
    AudioReceived(copy, sampleTime);
}

void EncodedFrameSink::RaiseLog(const wchar_t* message)
{
    LogMessage(ref new String(message));
}
