#include <jni.h>
#include <aaudio/AAudio.h>
#include <android/log.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#define LOG_TAG "PhoneAudioBridge"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace {

constexpr uint8_t MSG_CONFIG = 0x01;
constexpr uint8_t MSG_PCM = 0x02;
constexpr uint8_t MSG_SET_TARGET_MS = 0x03;
constexpr uint8_t MSG_SET_GAIN = 0x04;
constexpr uint8_t MSG_PING = 0x05;
constexpr uint8_t MSG_STOP = 0x06;

constexpr uint8_t MSG_STATS = 0x81;
constexpr uint8_t MSG_PONG = 0x85;
constexpr uint8_t MSG_DISCONNECTED_BY_PHONE = 0x86;

constexpr uint32_t RING_CAP = 32768;
constexpr uint32_t RING_MASK = RING_CAP - 1;
constexpr const char* ABSTRACT_SOCK_NAME = "phoneaudiobridge";

inline uint16_t read_u16_le(const uint8_t* p) {
    return static_cast<uint16_t>(p[0]) | (static_cast<uint16_t>(p[1]) << 8);
}

inline uint32_t read_u32_le(const uint8_t* p) {
    return static_cast<uint32_t>(p[0]) |
           (static_cast<uint32_t>(p[1]) << 8) |
           (static_cast<uint32_t>(p[2]) << 16) |
           (static_cast<uint32_t>(p[3]) << 24);
}

inline int16_t read_i16_le(const uint8_t* p) {
    return static_cast<int16_t>(read_u16_le(p));
}

inline void write_u16_le(uint8_t* p, uint16_t v) {
    p[0] = static_cast<uint8_t>(v & 0xFF);
    p[1] = static_cast<uint8_t>((v >> 8) & 0xFF);
}

inline void write_u32_le(uint8_t* p, uint32_t v) {
    p[0] = static_cast<uint8_t>(v & 0xFF);
    p[1] = static_cast<uint8_t>((v >> 8) & 0xFF);
    p[2] = static_cast<uint8_t>((v >> 16) & 0xFF);
    p[3] = static_cast<uint8_t>((v >> 24) & 0xFF);
}

inline float hermite4(const float* arr, uint64_t idxInt, double f) {
    float y0 = arr[(idxInt - 1) & RING_MASK];
    float y1 = arr[idxInt & RING_MASK];
    float y2 = arr[(idxInt + 1) & RING_MASK];
    float y3 = arr[(idxInt + 2) & RING_MASK];
    float c0 = y1;
    float c1 = 0.5f * (y2 - y0);
    float c2 = y0 - 2.5f * y1 + 2.0f * y2 - 0.5f * y3;
    float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
    float ff = static_cast<float>(f);
    return ((c3 * ff + c2) * ff + c1) * ff + c0;
}

struct AudioEngineState {
    JavaVM* jvm = nullptr;
    jobject cbObj = nullptr;
    jmethodID midState = nullptr;
    jmethodID midStopped = nullptr;

    std::atomic<bool> running{false};
    std::atomic<bool> stopRequested{false};
    std::atomic<int> stopReason{0};
    std::atomic<int> disconnectEventReason{0};

    std::mutex lifecycleMtx;
    std::mutex writeMtx;
    std::thread netThread;

    int listenFd = -1;
    std::atomic<int> clientFd{-1};

    AAudioStream* aaudioStream = nullptr;
    std::atomic<uint32_t> aaudioSampleRate{48000};
    std::atomic<uint16_t> aaudioBurstFrames{256};
    std::atomic<uint16_t> aaudioBufFrames{512};
    std::atomic<uint16_t> aaudioFlags{0};
    std::atomic<int32_t> baseXruns{0};
    bool baselineCaptured = false;

    // Lock-free SPSC ring buffer (written by netThread, read by audioDataCallback)
    float ringL[RING_CAP]{};
    float ringR[RING_CAP]{};
    std::atomic<uint64_t> writePos{0};
    std::atomic<uint64_t> resetEpoch{0};
    std::atomic<uint32_t> srcSampleRate{48000};
    std::atomic<uint32_t> pktFrames{480};
    std::atomic<int> targetMs{10};
    std::atomic<float> targetGain{1.0f};
    std::atomic<uint32_t> seqLast{0};

    // Telemetry updated by audioDataCallback or netThread (atomics only)
    std::atomic<uint32_t> plcTotal{0};
    std::atomic<uint32_t> resyncTotal{0};
    std::atomic<uint16_t> lastBufTenthsMs{0};
    std::atomic<uint32_t> minAvailFramesWindow{0xFFFFFFFFu};
    std::atomic<uint16_t> pktGapMaxMsWindow{0};

    // Consumer-only state (only touched inside audioDataCallback!)
    uint64_t consumerEpoch = 0;
    uint64_t readIdx = 0;
    double readFrac = 0.0;
    bool primed = false;
    double avgAvail = 0.0;
    double integral = 0.0;
    double smoothedRate = 1.0;
    bool plcMode = false;
    int plcFrames = 0;
    uint64_t plcAnchor = 0;
    int plcStep = 1;
    float plcGain = 1.0f;
    int fadeLen = 48;
    int fadeRem = 0;
    float prevOutL = 0.0f;
    float prevOutR = 0.0f;
    float gainCur = 1.0f;
};

AudioEngineState g_engine;

void notifyJavaState(int state, const char* info) {
    JNIEnv* env = nullptr;
    bool attached = false;
    if (!g_engine.jvm || !g_engine.cbObj || !g_engine.midState) return;
    int getEnvRes = g_engine.jvm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6);
    if (getEnvRes == JNI_EDETACHED) {
        if (g_engine.jvm->AttachCurrentThread(&env, nullptr) != JNI_OK) return;
        attached = true;
    } else if (getEnvRes != JNI_OK) {
        return;
    }
    jstring jInfo = env->NewStringUTF(info ? info : "");
    env->CallVoidMethod(g_engine.cbObj, g_engine.midState, state, jInfo);
    env->DeleteLocalRef(jInfo);
    if (attached) g_engine.jvm->DetachCurrentThread();
}

void notifyJavaStopped(int reason) {
    JNIEnv* env = nullptr;
    bool attached = false;
    if (!g_engine.jvm || !g_engine.cbObj || !g_engine.midStopped) return;
    int getEnvRes = g_engine.jvm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6);
    if (getEnvRes == JNI_EDETACHED) {
        if (g_engine.jvm->AttachCurrentThread(&env, nullptr) != JNI_OK) return;
        attached = true;
    } else if (getEnvRes != JNI_OK) {
        return;
    }
    env->CallVoidMethod(g_engine.cbObj, g_engine.midStopped, reason);
    if (attached) g_engine.jvm->DetachCurrentThread();
}

bool sendFrameLocked(int fd, uint8_t type, const uint8_t* payload, uint16_t len) {
    if (fd < 0) return false;
    uint8_t hdr[3];
    hdr[0] = type;
    write_u16_le(hdr + 1, len);

    std::lock_guard<std::mutex> lock(g_engine.writeMtx);
    auto sendAll = [&](const uint8_t* data, size_t n) -> bool {
        size_t sent = 0;
        while (sent < n) {
            ssize_t r = ::send(fd, data + sent, n - sent, MSG_NOSIGNAL);
            if (r < 0) {
                if (errno == EINTR) continue;
                return false;
            }
            if (r == 0) return false;
            sent += static_cast<size_t>(r);
        }
        return true;
    };
    if (!sendAll(hdr, 3)) return false;
    if (len > 0 && payload != nullptr) {
        if (!sendAll(payload, len)) return false;
    }
    return true;
}

aaudio_data_callback_result_t audioDataCallback(
    AAudioStream* /*stream*/,
    void* /*userData*/,
    void* audioData,
    int32_t numFrames) {

    float* out = static_cast<float*>(audioData);
    uint64_t ep = g_engine.resetEpoch.load(std::memory_order_acquire);
    uint64_t wp = g_engine.writePos.load(std::memory_order_acquire);

    if (ep != g_engine.consumerEpoch) {
        g_engine.consumerEpoch = ep;
        g_engine.readIdx = wp;
        g_engine.readFrac = 0.0;
        g_engine.primed = false;
        g_engine.avgAvail = 0.0;
        g_engine.integral = 0.0;
        g_engine.plcMode = false;
        g_engine.plcFrames = 0;
        g_engine.fadeRem = 0;
        g_engine.prevOutL = 0.0f;
        g_engine.prevOutR = 0.0f;
    }

    uint32_t srcRate = g_engine.srcSampleRate.load(std::memory_order_relaxed);
    uint32_t outRate = g_engine.aaudioSampleRate.load(std::memory_order_relaxed);
    if (srcRate == 0) srcRate = 48000;
    if (outRate == 0) outRate = srcRate;

    uint32_t pf = g_engine.pktFrames.load(std::memory_order_relaxed);
    if (pf < 48) pf = 480;

    int tMs = g_engine.targetMs.load(std::memory_order_relaxed);
    if (tMs < 1) tMs = 1;
    if (tMs > 120) tMs = 120;

    double targetFrames = std::max(48.0, (static_cast<double>(tMs) * srcRate) / 1000.0);
    double primeFrames = targetFrames + (static_cast<double>(pf) * 0.5);

    if (!g_engine.primed) {
        uint64_t availInt = (wp >= g_engine.readIdx) ? (wp - g_engine.readIdx) : 0;
        if (static_cast<double>(availInt) >= primeFrames) {
            g_engine.primed = true;
            uint64_t back = static_cast<uint64_t>(primeFrames);
            g_engine.readIdx = (wp >= back) ? (wp - back) : 0;
            g_engine.readFrac = 0.0;
            g_engine.avgAvail = targetFrames;
            g_engine.integral = 0.0;
            g_engine.smoothedRate = static_cast<double>(srcRate) / static_cast<double>(outRate);
            g_engine.fadeLen = 48;
            g_engine.fadeRem = 48;
            g_engine.prevOutL = 0.0f;
            g_engine.prevOutR = 0.0f;
        } else {
            std::memset(out, 0, static_cast<size_t>(numFrames) * 2 * sizeof(float));
            uint16_t bTenths = static_cast<uint16_t>((availInt * 10000ULL) / srcRate);
            g_engine.lastBufTenthsMs.store(bTenths, std::memory_order_relaxed);
            return AAUDIO_CALLBACK_RESULT_CONTINUE;
        }
    }

    double availNow = (wp >= g_engine.readIdx)
        ? (static_cast<double>(wp - g_engine.readIdx) - g_engine.readFrac)
        : 0.0;
    if (availNow < 0.0) availNow = 0.0;

    int maxMs = std::max(tMs * 2 + 25, tMs + 40);
    double maxFrames = std::max((static_cast<double>(maxMs) * srcRate) / 1000.0,
                                targetFrames + srcRate * 0.030);
    if (availNow > maxFrames) {
        uint64_t back = static_cast<uint64_t>(primeFrames);
        g_engine.readIdx = (wp >= back) ? (wp - back) : 0;
        g_engine.readFrac = 0.0;
        g_engine.fadeLen = 48;
        g_engine.fadeRem = 48;
        g_engine.avgAvail = targetFrames;
        g_engine.integral = 0.0;
        g_engine.resyncTotal.fetch_add(1, std::memory_order_relaxed);
        availNow = primeFrames;
    }

    double availMid = std::max(0.0, availNow - static_cast<double>(numFrames) * 0.5);
    g_engine.avgAvail = g_engine.avgAvail * 0.94 + availMid * 0.06;

    double dt = static_cast<double>(numFrames) / static_cast<double>(outRate);
    double errSec = (g_engine.avgAvail - targetFrames) / static_cast<double>(srcRate);
    g_engine.integral = std::clamp(g_engine.integral + errSec * dt, -0.02, 0.02);

    double corr = std::clamp(errSec * 0.45 + g_engine.integral * 0.12, -0.015, 0.015);
    double targetRate = (static_cast<double>(srcRate) / static_cast<double>(outRate)) * (1.0 + corr);
    g_engine.smoothedRate = g_engine.smoothedRate * 0.88 + targetRate * 0.12;

    int maxPlcDecayFrames = static_cast<int>(srcRate * 0.028);
    int maxPlcResetFrames = static_cast<int>(srcRate * 0.085);
    float gainTarget = g_engine.targetGain.load(std::memory_order_relaxed);

    uint32_t minWindowFrames = 0xFFFFFFFFu;
    for (int32_t i = 0; i < numFrames; ++i) {
        g_engine.gainCur += (gainTarget - g_engine.gainCur) * 0.01f;
        wp = g_engine.writePos.load(std::memory_order_acquire);

        uint64_t availInt = (wp > g_engine.readIdx) ? (wp - g_engine.readIdx) : 0;
        uint32_t availU32 = static_cast<uint32_t>(std::min<uint64_t>(availInt, 0xFFFFFFFEu));
        if (availU32 < minWindowFrames) minWindowFrames = availU32;

        if (g_engine.readIdx + 2 <= wp) {
            if (g_engine.plcMode) {
                g_engine.plcMode = false;
                g_engine.plcFrames = 0;
                g_engine.fadeLen = 32;
                g_engine.fadeRem = 32;
            }
            float sL, sR;
            if (g_engine.readIdx + 3 <= wp) {
                sL = hermite4(g_engine.ringL, g_engine.readIdx, g_engine.readFrac);
                sR = hermite4(g_engine.ringR, g_engine.readIdx, g_engine.readFrac);
            } else {
                uint32_t i0 = static_cast<uint32_t>(g_engine.readIdx & RING_MASK);
                uint32_t i1 = static_cast<uint32_t>((g_engine.readIdx + 1) & RING_MASK);
                float f = static_cast<float>(g_engine.readFrac);
                sL = g_engine.ringL[i0] + (g_engine.ringL[i1] - g_engine.ringL[i0]) * f;
                sR = g_engine.ringR[i0] + (g_engine.ringR[i1] - g_engine.ringR[i0]) * f;
            }

            if (g_engine.fadeRem > 0) {
                double t = static_cast<double>(g_engine.fadeLen - g_engine.fadeRem) / static_cast<double>(g_engine.fadeLen);
                float w = static_cast<float>(0.5 - 0.5 * std::cos(3.14159265358979323846 * t));
                sL = g_engine.prevOutL * (1.0f - w) + sL * w;
                sR = g_engine.prevOutR * (1.0f - w) + sR * w;
                g_engine.fadeRem--;
            }

            out[i * 2] = sL * g_engine.gainCur;
            out[i * 2 + 1] = sR * g_engine.gainCur;
            g_engine.prevOutL = sL;
            g_engine.prevOutR = sR;

            g_engine.readFrac += g_engine.smoothedRate;
            uint64_t adv = static_cast<uint64_t>(g_engine.readFrac);
            if (adv > 0) {
                g_engine.readIdx += adv;
                g_engine.readFrac -= static_cast<double>(adv);
            }
        } else {
            minWindowFrames = 0;
            if (!g_engine.plcMode) {
                g_engine.plcMode = true;
                g_engine.plcFrames = 0;
                g_engine.plcAnchor = (wp > 0) ? (wp - 1) : 0;
                g_engine.plcStep = 1;
                g_engine.plcGain = 1.0f;
                g_engine.plcTotal.fetch_add(1, std::memory_order_relaxed);
            }
            g_engine.plcFrames++;
            if (g_engine.plcFrames > maxPlcResetFrames) {
                g_engine.primed = false;
                g_engine.plcMode = false;
                g_engine.readIdx = wp;
                g_engine.readFrac = 0.0;
                g_engine.resyncTotal.fetch_add(1, std::memory_order_relaxed);
                for (int32_t k = i; k < numFrames; ++k) {
                    g_engine.prevOutL *= 0.9f;
                    g_engine.prevOutR *= 0.9f;
                    out[k * 2] = g_engine.prevOutL * g_engine.gainCur;
                    out[k * 2 + 1] = g_engine.prevOutR * g_engine.gainCur;
                }
                break;
            } else if (g_engine.plcFrames > maxPlcDecayFrames) {
                g_engine.prevOutL *= 0.92f;
                g_engine.prevOutR *= 0.92f;
                out[i * 2] = g_engine.prevOutL * g_engine.gainCur;
                out[i * 2 + 1] = g_engine.prevOutR * g_engine.gainCur;
            } else {
                uint64_t mPos = (g_engine.plcAnchor >= static_cast<uint64_t>(g_engine.plcStep))
                    ? (g_engine.plcAnchor - static_cast<uint64_t>(g_engine.plcStep))
                    : 0;
                uint32_t mIdx = static_cast<uint32_t>(mPos & RING_MASK);
                g_engine.plcStep++;
                if (g_engine.plcStep >= 160) g_engine.plcStep = 1;
                g_engine.plcGain *= 0.993f;
                float mL = g_engine.ringL[mIdx] * g_engine.plcGain;
                float mR = g_engine.ringR[mIdx] * g_engine.plcGain;
                out[i * 2] = mL * g_engine.gainCur;
                out[i * 2 + 1] = mR * g_engine.gainCur;
                g_engine.prevOutL = mL;
                g_engine.prevOutR = mR;
            }
        }
    }

    wp = g_engine.writePos.load(std::memory_order_acquire);
    double remFrames = (wp >= g_engine.readIdx)
        ? (static_cast<double>(wp - g_engine.readIdx) - g_engine.readFrac)
        : 0.0;
    if (remFrames < 0.0) remFrames = 0.0;
    uint16_t bTenths = static_cast<uint16_t>(std::min(65535.0, (remFrames * 10000.0) / srcRate));
    g_engine.lastBufTenthsMs.store(bTenths, std::memory_order_relaxed);

    uint32_t prevMin = g_engine.minAvailFramesWindow.load(std::memory_order_relaxed);
    while (minWindowFrames < prevMin &&
           !g_engine.minAvailFramesWindow.compare_exchange_weak(
               prevMin, minWindowFrames, std::memory_order_relaxed)) {
    }

    return AAUDIO_CALLBACK_RESULT_CONTINUE;
}

void audioErrorCallback(
    AAudioStream* /*stream*/,
    void* /*userData*/,
    aaudio_result_t error) {
    LOGE("AAudio error callback: %s (%d)", AAudio_convertResultToText(error), error);
    if (error == AAUDIO_ERROR_DISCONNECTED) {
        g_engine.disconnectEventReason.store(1, std::memory_order_release);
    } else {
        g_engine.disconnectEventReason.store(3, std::memory_order_release);
    }
    g_engine.stopRequested.store(true, std::memory_order_release);
}

void closeAAudioStream() {
    if (g_engine.aaudioStream != nullptr) {
        AAudioStream_requestStop(g_engine.aaudioStream);
        AAudioStream_close(g_engine.aaudioStream);
        g_engine.aaudioStream = nullptr;
    }
    g_engine.baselineCaptured = false;
}

bool openAAudioStream(uint32_t sampleRate) {
    closeAAudioStream();

    AAudioStreamBuilder* builder = nullptr;
    aaudio_result_t res = AAudio_createStreamBuilder(&builder);
    if (res != AAUDIO_OK || builder == nullptr) {
        LOGE("AAudio_createStreamBuilder failed: %s", AAudio_convertResultToText(res));
        return false;
    }

    AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
    AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_FLOAT);
    AAudioStreamBuilder_setChannelCount(builder, 2);
    AAudioStreamBuilder_setSampleRate(builder, static_cast<int32_t>(sampleRate));
    AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
    AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_EXCLUSIVE);
    AAudioStreamBuilder_setUsage(builder, AAUDIO_USAGE_MEDIA);
    AAudioStreamBuilder_setContentType(builder, AAUDIO_CONTENT_TYPE_MUSIC);
    AAudioStreamBuilder_setDataCallback(builder, audioDataCallback, nullptr);
    AAudioStreamBuilder_setErrorCallback(builder, audioErrorCallback, nullptr);

    AAudioStream* stream = nullptr;
    res = AAudioStreamBuilder_openStream(builder, &stream);
    AAudioStreamBuilder_delete(builder);

    if (res != AAUDIO_OK || stream == nullptr) {
        LOGE("AAudioStreamBuilder_openStream failed: %s", AAudio_convertResultToText(res));
        return false;
    }

    int32_t burst = std::max<int32_t>(32, AAudioStream_getFramesPerBurst(stream));
    AAudioStream_setBufferSizeInFrames(stream, burst * 2);

    uint32_t actualRate = static_cast<uint32_t>(AAudioStream_getSampleRate(stream));
    int32_t actualBuf = AAudioStream_getBufferSizeInFrames(stream);
    aaudio_performance_mode_t perfMode = AAudioStream_getPerformanceMode(stream);
    aaudio_sharing_mode_t shareMode = AAudioStream_getSharingMode(stream);

    bool isMmap = false;
    using FnIsMMap = bool (*)(AAudioStream*);
    FnIsMMap fnIsMmap = reinterpret_cast<FnIsMMap>(dlsym(RTLD_DEFAULT, "AAudioStream_isMMapUsed"));
    if (fnIsMmap != nullptr) {
        isMmap = fnIsMmap(stream);
    }

    uint16_t flags = 0;
    if (perfMode == AAUDIO_PERFORMANCE_MODE_LOW_LATENCY) flags |= 1;
    if (shareMode == AAUDIO_SHARING_MODE_EXCLUSIVE) flags |= 2;
    if (isMmap) flags |= 4;

    g_engine.aaudioStream = stream;
    g_engine.aaudioSampleRate.store(actualRate, std::memory_order_relaxed);
    g_engine.aaudioBurstFrames.store(static_cast<uint16_t>(std::min(burst, 65535)), std::memory_order_relaxed);
    g_engine.aaudioBufFrames.store(static_cast<uint16_t>(std::min(std::max(actualBuf, 0), 65535)), std::memory_order_relaxed);
    g_engine.aaudioFlags.store(flags, std::memory_order_relaxed);
    g_engine.baseXruns.store(AAudioStream_getXRunCount(stream), std::memory_order_relaxed);
    g_engine.baselineCaptured = true;

    res = AAudioStream_requestStart(stream);
    if (res != AAUDIO_OK) {
        LOGE("AAudioStream_requestStart failed: %s", AAudio_convertResultToText(res));
        closeAAudioStream();
        return false;
    }

    LOGI("AAudio opened: rate=%u burst=%d buf=%d perf=%d share=%d mmap=%d",
         actualRate, burst, actualBuf, perfMode, shareMode, isMmap ? 1 : 0);
    return true;
}

int bindAbstractSocket() {
    int fd = ::socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) return -1;

    struct sockaddr_un addr {};
    addr.sun_family = AF_UNIX;
    addr.sun_path[0] = '\0';
    size_t nameLen = std::strlen(ABSTRACT_SOCK_NAME);
    std::memcpy(addr.sun_path + 1, ABSTRACT_SOCK_NAME, nameLen);
    socklen_t sockLen = static_cast<socklen_t>(offsetof(struct sockaddr_un, sun_path) + 1 + nameLen);

    if (::bind(fd, reinterpret_cast<struct sockaddr*>(&addr), sockLen) < 0) {
        LOGE("bind localabstract failed: %s", strerror(errno));
        ::close(fd);
        return -1;
    }
    if (::listen(fd, 2) < 0) {
        LOGE("listen failed: %s", strerror(errno));
        ::close(fd);
        return -1;
    }
    return fd;
}

bool readExactPoll(int fd, uint8_t* dst, size_t n) {
    size_t got = 0;
    while (got < n) {
        if (g_engine.stopRequested.load(std::memory_order_acquire)) return false;
        struct pollfd pfd { fd, POLLIN, 0 };
        int pr = ::poll(&pfd, 1, 100);
        if (pr < 0) {
            if (errno == EINTR) continue;
            return false;
        }
        if (pr == 0) {
            // Check timed disconnect request even while idle
            continue;
        }
        if ((pfd.revents & (POLLERR | POLLHUP | POLLNVAL)) && !(pfd.revents & POLLIN)) {
            return false;
        }
        ssize_t r = ::recv(fd, dst + got, n - got, 0);
        if (r < 0) {
            if (errno == EINTR) continue;
            return false;
        }
        if (r == 0) return false;
        got += static_cast<size_t>(r);
    }
    return true;
}

void pushPcmToRing(const uint8_t* pcmBytes, size_t pcmByteCount) {
    uint32_t frames = static_cast<uint32_t>(pcmByteCount / 4);
    if (frames == 0 || frames > RING_CAP / 2) return;
    g_engine.pktFrames.store(frames, std::memory_order_relaxed);

    uint64_t wp = g_engine.writePos.load(std::memory_order_relaxed);
    for (uint32_t i = 0; i < frames; ++i) {
        int16_t iL = read_i16_le(pcmBytes + i * 4);
        int16_t iR = read_i16_le(pcmBytes + i * 4 + 2);
        uint32_t idx = static_cast<uint32_t>((wp + i) & RING_MASK);
        g_engine.ringL[idx] = static_cast<float>(iL) / 32768.0f;
        g_engine.ringR[idx] = static_cast<float>(iR) / 32768.0f;
    }
    g_engine.writePos.store(wp + frames, std::memory_order_release);
}

void resetStreamSession(uint32_t sampleRate, uint16_t targetMs, uint16_t gainPct) {
    g_engine.srcSampleRate.store(sampleRate, std::memory_order_relaxed);
    g_engine.targetMs.store(std::clamp<int>(targetMs, 1, 120), std::memory_order_relaxed);
    g_engine.targetGain.store(std::clamp<float>(gainPct / 100.0f, 0.0f, 1.5f), std::memory_order_relaxed);
    g_engine.plcTotal.store(0, std::memory_order_relaxed);
    g_engine.resyncTotal.store(0, std::memory_order_relaxed);
    g_engine.minAvailFramesWindow.store(0xFFFFFFFFu, std::memory_order_relaxed);
    g_engine.pktGapMaxMsWindow.store(0, std::memory_order_relaxed);
    g_engine.lastBufTenthsMs.store(0, std::memory_order_relaxed);
    g_engine.resetEpoch.fetch_add(1, std::memory_order_release);

    if (g_engine.aaudioStream == nullptr || g_engine.aaudioSampleRate.load(std::memory_order_relaxed) != sampleRate) {
        openAAudioStream(sampleRate);
    } else {
        g_engine.baseXruns.store(AAudioStream_getXRunCount(g_engine.aaudioStream), std::memory_order_relaxed);
        g_engine.baselineCaptured = true;
    }
}

void sendStatsPacket(int fd) {
    uint8_t buf[32];
    uint32_t srcRate = g_engine.srcSampleRate.load(std::memory_order_relaxed);
    if (srcRate == 0) srcRate = 48000;

    uint32_t minFr = g_engine.minAvailFramesWindow.exchange(0xFFFFFFFFu, std::memory_order_relaxed);
    uint16_t bufTenths = g_engine.lastBufTenthsMs.load(std::memory_order_relaxed);
    uint16_t minTenths = (minFr == 0xFFFFFFFFu)
        ? bufTenths
        : static_cast<uint16_t>(std::min<uint64_t>(65535ULL, (static_cast<uint64_t>(minFr) * 10000ULL) / srcRate));
    uint16_t gapMax = g_engine.pktGapMaxMsWindow.exchange(0, std::memory_order_relaxed);

    uint32_t xruns = 0;
    if (g_engine.aaudioStream != nullptr && g_engine.baselineCaptured) {
        int32_t curX = AAudioStream_getXRunCount(g_engine.aaudioStream);
        int32_t baseX = g_engine.baseXruns.load(std::memory_order_relaxed);
        if (curX > baseX) xruns = static_cast<uint32_t>(curX - baseX);
    }

    write_u32_le(buf + 0, g_engine.seqLast.load(std::memory_order_relaxed));
    write_u16_le(buf + 4, bufTenths);
    write_u16_le(buf + 6, minTenths);
    write_u16_le(buf + 8, gapMax);
    write_u16_le(buf + 10, g_engine.aaudioBurstFrames.load(std::memory_order_relaxed));
    write_u16_le(buf + 12, g_engine.aaudioBufFrames.load(std::memory_order_relaxed));
    write_u16_le(buf + 14, g_engine.aaudioFlags.load(std::memory_order_relaxed));
    write_u32_le(buf + 16, g_engine.plcTotal.load(std::memory_order_relaxed));
    write_u32_le(buf + 20, xruns);
    write_u32_le(buf + 24, g_engine.resyncTotal.load(std::memory_order_relaxed));
    write_u32_le(buf + 28, g_engine.aaudioSampleRate.load(std::memory_order_relaxed));

    sendFrameLocked(fd, MSG_STATS, buf, 32);
}

void networkLoop(uint32_t initialSampleRate) {
    if (!openAAudioStream(initialSampleRate)) {
        notifyJavaStopped(3);
        return;
    }

    char infoBuf[96];
    std::snprintf(
        infoBuf,
        sizeof(infoBuf),
        "AAudio %uHz burst %u",
        g_engine.aaudioSampleRate.load(std::memory_order_relaxed),
        g_engine.aaudioBurstFrames.load(std::memory_order_relaxed));
    notifyJavaState(1, infoBuf);

    std::vector<uint8_t> payload(65540);
    while (!g_engine.stopRequested.load(std::memory_order_acquire)) {
        struct pollfd pfd { g_engine.listenFd, POLLIN, 0 };
        int pr = ::poll(&pfd, 1, 100);
        if (pr < 0) {
            if (errno == EINTR) continue;
            break;
        }
        if (pr == 0) continue;

        int cfd = ::accept(g_engine.listenFd, nullptr, nullptr);
        if (cfd < 0) {
            if (errno == EINTR || errno == EAGAIN) continue;
            break;
        }
        g_engine.clientFd.store(cfd, std::memory_order_release);

        std::snprintf(
            infoBuf,
            sizeof(infoBuf),
            "AAudio %uHz · burst %u",
            g_engine.aaudioSampleRate.load(std::memory_order_relaxed),
            g_engine.aaudioBurstFrames.load(std::memory_order_relaxed));
        notifyJavaState(2, infoBuf);

        using Clock = std::chrono::steady_clock;
        auto lastPcmTime = Clock::time_point{};
        auto lastStatsTime = Clock::now();
        bool clientWantedStop = false;

        while (!g_engine.stopRequested.load(std::memory_order_acquire)) {
            struct pollfd cpfd { cfd, POLLIN, 0 };
            int cpr = ::poll(&cpfd, 1, 50);
            auto now = Clock::now();
            if (std::chrono::duration_cast<std::chrono::milliseconds>(now - lastStatsTime).count() >= 250) {
                lastStatsTime = now;
                sendStatsPacket(cfd);
            }

            if (cpr < 0) {
                if (errno == EINTR) continue;
                break;
            }
            if (cpr == 0) continue;

            uint8_t hdr[3];
            if (!readExactPoll(cfd, hdr, 3)) break;
            uint8_t msgType = hdr[0];
            uint16_t msgLen = read_u16_le(hdr + 1);
            if (msgLen > payload.size()) {
                payload.resize(msgLen);
            }
            if (msgLen > 0) {
                if (!readExactPoll(cfd, payload.data(), msgLen)) break;
            }

            if (msgType == MSG_CONFIG && msgLen >= 8) {
                uint32_t sr = read_u32_le(payload.data());
                uint16_t tMs = read_u16_le(payload.data() + 4);
                uint16_t gPct = read_u16_le(payload.data() + 6);
                resetStreamSession(sr, tMs, gPct);
                lastPcmTime = Clock::time_point{};
            } else if (msgType == MSG_PCM && msgLen >= 8) {
                uint32_t seq = read_u32_le(payload.data());
                g_engine.seqLast.store(seq, std::memory_order_relaxed);
                auto pcmNow = Clock::now();
                if (lastPcmTime.time_since_epoch().count() != 0) {
                    auto dtMs = std::chrono::duration_cast<std::chrono::milliseconds>(pcmNow - lastPcmTime).count();
                    uint16_t dtU16 = static_cast<uint16_t>(std::clamp<long long>(dtMs, 0, 65535));
                    uint16_t prevGap = g_engine.pktGapMaxMsWindow.load(std::memory_order_relaxed);
                    if (dtU16 > prevGap) {
                        g_engine.pktGapMaxMsWindow.store(dtU16, std::memory_order_relaxed);
                    }
                }
                lastPcmTime = pcmNow;
                pushPcmToRing(payload.data() + 4, msgLen - 4);
            } else if (msgType == MSG_SET_TARGET_MS && msgLen >= 2) {
                uint16_t tMs = read_u16_le(payload.data());
                g_engine.targetMs.store(std::clamp<int>(tMs, 1, 120), std::memory_order_relaxed);
            } else if (msgType == MSG_SET_GAIN && msgLen >= 2) {
                uint16_t gPct = read_u16_le(payload.data());
                g_engine.targetGain.store(std::clamp<float>(gPct / 100.0f, 0.0f, 1.5f), std::memory_order_relaxed);
            } else if (msgType == MSG_PING && msgLen == 8) {
                sendFrameLocked(cfd, MSG_PONG, payload.data(), 8);
            } else if (msgType == MSG_STOP) {
                clientWantedStop = true;
                g_engine.stopReason.store(0, std::memory_order_relaxed);
                g_engine.stopRequested.store(true, std::memory_order_release);
                break;
            }
        }

        int finalReason = g_engine.disconnectEventReason.load(std::memory_order_acquire);
        if (finalReason == 0) {
            finalReason = g_engine.stopReason.load(std::memory_order_relaxed);
        }
        if (finalReason > 0) {
            uint8_t rByte = static_cast<uint8_t>(finalReason);
            sendFrameLocked(cfd, MSG_DISCONNECTED_BY_PHONE, &rByte, 1);
        }

        g_engine.clientFd.store(-1, std::memory_order_release);
        ::close(cfd);

        if (clientWantedStop || g_engine.stopRequested.load(std::memory_order_acquire)) {
            break;
        }
        // PC disconnected TCP cleanly -> enter listening state reset
        g_engine.resetEpoch.fetch_add(1, std::memory_order_release);
        notifyJavaState(1, infoBuf);
    }

    closeAAudioStream();
    if (g_engine.listenFd >= 0) {
        ::close(g_engine.listenFd);
        g_engine.listenFd = -1;
    }

    bool hadActiveRun = g_engine.running.exchange(false);
    int notifyReason = g_engine.disconnectEventReason.load(std::memory_order_acquire);
    if (notifyReason == 0) notifyReason = g_engine.stopReason.load(std::memory_order_relaxed);
    if (hadActiveRun && notifyReason > 0) {
        notifyJavaStopped(notifyReason);
    } else if (hadActiveRun) {
        notifyJavaStopped(0);
    }
}

}  // namespace

extern "C" {

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* /*reserved*/) {
    g_engine.jvm = vm;
    return JNI_VERSION_1_6;
}

JNIEXPORT jboolean JNICALL
Java_com_antoniociorcero_phoneaudiobridge_NativeBridge_nativeStart(
    JNIEnv* env,
    jclass /*clazz*/,
    jobject callbackTarget,
    jint targetMs,
    jint sampleRate,
    jint gainPct) {
    std::lock_guard<std::mutex> lock(g_engine.lifecycleMtx);

    if (g_engine.running.load()) {
        g_engine.stopRequested.store(true);
        if (g_engine.netThread.joinable()) g_engine.netThread.join();
    }

    if (g_engine.cbObj != nullptr) {
        env->DeleteGlobalRef(g_engine.cbObj);
        g_engine.cbObj = nullptr;
    }
    if (callbackTarget != nullptr) {
        g_engine.cbObj = env->NewGlobalRef(callbackTarget);
        jclass cls = env->GetObjectClass(callbackTarget);
        g_engine.midState = env->GetMethodID(cls, "onNativeStateChanged", "(ILjava/lang/String;)V");
        g_engine.midStopped = env->GetMethodID(cls, "onNativeStopped", "(I)V");
        env->DeleteLocalRef(cls);
    }

    int lfd = bindAbstractSocket();
    if (lfd < 0) {
        return JNI_FALSE;
    }

    uint32_t sr = sampleRate > 8000 ? static_cast<uint32_t>(sampleRate) : 48000;
    g_engine.listenFd = lfd;
    g_engine.stopRequested.store(false);
    g_engine.stopReason.store(0);
    g_engine.disconnectEventReason.store(0);
    g_engine.writePos.store(0);
    g_engine.resetEpoch.fetch_add(1);
    g_engine.srcSampleRate.store(sr);
    g_engine.targetMs.store(std::clamp<int>(targetMs, 1, 120));
    g_engine.targetGain.store(std::clamp<float>(gainPct / 100.0f, 0.0f, 1.5f));
    g_engine.plcTotal.store(0);
    g_engine.resyncTotal.store(0);
    g_engine.running.store(true);

    g_engine.netThread = std::thread(networkLoop, sr);
    return JNI_TRUE;
}

JNIEXPORT void JNICALL
Java_com_antoniociorcero_phoneaudiobridge_NativeBridge_nativeStop(
    JNIEnv* env,
    jclass /*clazz*/,
    jint reason) {
    std::lock_guard<std::mutex> lock(g_engine.lifecycleMtx);
    if (!g_engine.running.load() && !g_engine.netThread.joinable()) {
        return;
    }

    g_engine.stopReason.store(reason, std::memory_order_relaxed);
    if (reason > 0 && g_engine.disconnectEventReason.load(std::memory_order_relaxed) == 0) {
        g_engine.disconnectEventReason.store(reason, std::memory_order_release);
    }

    int cfd = g_engine.clientFd.load(std::memory_order_acquire);
    if (cfd >= 0 && reason > 0) {
        uint8_t rByte = static_cast<uint8_t>(reason);
        sendFrameLocked(cfd, MSG_DISCONNECTED_BY_PHONE, &rByte, 1);
    }

    g_engine.running.store(false, std::memory_order_release);
    g_engine.stopRequested.store(true, std::memory_order_release);

    if (g_engine.netThread.joinable()) {
        g_engine.netThread.join();
    }

    if (g_engine.cbObj != nullptr && env != nullptr) {
        env->DeleteGlobalRef(g_engine.cbObj);
        g_engine.cbObj = nullptr;
    }
}

JNIEXPORT jboolean JNICALL
Java_com_antoniociorcero_phoneaudiobridge_NativeBridge_nativeIsRunning(
    JNIEnv* /*env*/,
    jclass /*clazz*/) {
    return g_engine.running.load() ? JNI_TRUE : JNI_FALSE;
}

}  // extern "C"
