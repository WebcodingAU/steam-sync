// Gaming Enjoyer's limiter: a small stereo look-ahead limiter that Equalizer APO loads as a VST2 plugin, straight after
// the EQ. An EQ boost can push loud peaks over full scale, where they'd clip at the output. A fixed preamp avoids that by
// turning everything down all the time; this turns the sound down only around a peak that would go over, and leaves
// everything else bit for bit as it was (2 ms later).
//
// How: each sample works out the gain it needs (need). The lowest need of the look-ahead window goes through a release
// (down at once, back up over RELEASE_SECONDS) and then a moving average as long as the look-ahead. Every value that
// average covers when a sample comes out of the delay line is at or below that sample's need, so the gain has already
// ramped down by the time it arrives and never overshoots. That ramp is what keeps it from clicking.
//
// Runs inside audiodg.exe, on its real-time thread: no locks, no allocation and no system calls while processing, and it
// keeps going whatever it's fed. The VST2 structures are written out here from their published layout, so no SDK is needed.

#include <math.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>
#include <sddl.h>

#define MAX_LOOKAHEAD 2048 // samples: 2 ms at up to 1 MHz, far past any audio device
#define LOOKAHEAD_SECONDS 0.002
#define RELEASE_SECONDS 0.15
#define CEILING 0.966f // -0.3 dBFS: a little room for the peaks between samples, which a DAC's filter can bring out

typedef struct AEffect AEffect;
typedef intptr_t (*AudioMaster)(AEffect *, int32_t, int32_t, intptr_t, void *, float);

// VST2's AEffect, as hosts lay it out on 64-bit Windows.
struct AEffect
{
    int32_t magic;
    intptr_t (*dispatcher)(AEffect *, int32_t opcode, int32_t index, intptr_t value, void *ptr, float opt);
    void (*process)(AEffect *, float **, float **, int32_t);
    void (*setParameter)(AEffect *, int32_t, float);
    float (*getParameter)(AEffect *, int32_t);
    int32_t numPrograms, numParams, numInputs, numOutputs, flags;
    intptr_t reserved1, reserved2;
    int32_t initialDelay, realQualities, offQualities;
    float ioRatio;
    void *object, *user;
    int32_t uniqueID, version;
    void (*processReplacing)(AEffect *, float **, float **, int32_t);
    void (*processDoubleReplacing)(AEffect *, double **, double **, int32_t);
    char future[56];
};

enum
{
    EffMagic = 0x56737450, // "VstP"
    FlagCanReplacing = 1 << 4,
    OpClose = 1,
    OpSetSampleRate = 10,
    OpMainsChanged = 12,
    OpGetPlugCategory = 35,
    OpGetEffectName = 45,
    OpGetVendorString = 47,
    OpGetProductString = 48,
    OpGetVendorVersion = 49,
    OpGetVstVersion = 58,
    CategoryMastering = 7,
    Version = 1,
};

// What the limiter has done, for the tray app's equalizer window: shared memory that every copy adds to (one per app's
// sound stream, as Equalizer APO runs per stream), once per block. Named after its layout's version, so a newer DLL never
// reads an older layout. Global when the audio engine creates it (a service may), else Local (Equalizer APO's Benchmark).
#define STATS_NAME L"GamingEnjoyerLimiter1"
#define STATS_MAGIC 0x734C4547 // "GELs"

typedef struct
{
    uint32_t magic;
    uint32_t streams;            // copies running now
    int64_t since;               // FILETIME (UTC) when the counting started
    int64_t frames;              // processed, all streams
    int64_t limited;             // frames turned down
    int64_t overFullScale;       // frames that were over full scale going in: these would have clipped
    int64_t dips;                // separate peaks it turned down (over the ceiling after 50 ms under it)
    int64_t lastDip;             // GetTickCount64 at the last frame it turned down
    int32_t lowestGain;          // the float bits of the lowest gain it used (1.0 = never)
} Stats;

static Stats *openStats(HANDLE *mapping)
{
    // Read for the signed-in user (the tray app); everything for the accounts the audio engine runs as.
    SECURITY_ATTRIBUTES sa = { sizeof sa, NULL, FALSE };
    ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;NS)(A;;GA;;;BA)(A;;GA;;;OW)(A;;GR;;;IU)",
        SDDL_REVISION_1, &sa.lpSecurityDescriptor, NULL);
    HANDLE m = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, sizeof(Stats), L"Global\\" STATS_NAME);
    if (!m) m = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, sizeof(Stats), L"Local\\" STATS_NAME);
    BOOL created = m && GetLastError() != ERROR_ALREADY_EXISTS;
    if (sa.lpSecurityDescriptor) LocalFree(sa.lpSecurityDescriptor);
    if (!m) return NULL;
    Stats *s = MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Stats));
    if (!s) { CloseHandle(m); return NULL; }
    if (created)
    {
        FILETIME now;
        GetSystemTimeAsFileTime(&now);
        s->since = (int64_t)(((uint64_t)now.dwHighDateTime << 32) | now.dwLowDateTime);
        float one = 1;
        memcpy(&s->lowestGain, &one, sizeof one);
        InterlockedExchange((volatile LONG *)&s->magic, STATS_MAGIC);
    }
    InterlockedIncrement((volatile LONG *)&s->streams);
    *mapping = m;
    return s;
}

typedef struct
{
    AEffect effect;
    Stats *stats;                // null when the shared memory couldn't be made: the limiter works the same without it
    HANDLE mapping;
    int64_t under;               // samples since the sound was last over the ceiling, for counting separate peaks
    int apart;                   // how long under the ceiling (50 ms) makes the next one a new peak
    int look;                    // the look-ahead in samples: the delay, and the moving average's length
    int at;                      // where in delay and recent the next sample goes
    double release;              // the part of the way back to 1 the gain goes each sample
    double level;                // the gain after the release
    double sum;                  // of recent
    int64_t sample;              // samples seen
    int head, tail;              // the lowest needs in the window, oldest first, rising: head == tail when empty
    int64_t lowAt[MAX_LOOKAHEAD + 2];
    double low[MAX_LOOKAHEAD + 2];
    double recent[MAX_LOOKAHEAD];
    float delay[2][MAX_LOOKAHEAD];
} Limiter;

static void reset(Limiter *l)
{
    l->at = 0;
    l->level = 1;
    l->sum = l->look;
    l->sample = 0;
    l->head = l->tail = 0;
    for (int i = 0; i < l->look; i++) l->recent[i] = 1;
    memset(l->delay, 0, sizeof l->delay);
}

static void setRate(Limiter *l, double rate)
{
    if (!(rate >= 8000 && rate <= 1000000)) rate = 48000;
    int look = (int)ceil(rate * LOOKAHEAD_SECONDS);
    l->look = look < 1 ? 1 : look > MAX_LOOKAHEAD ? MAX_LOOKAHEAD : look;
    l->release = 1 - exp(-1 / (RELEASE_SECONDS * rate));
    l->apart = (int)(rate * 0.05);
    l->under = l->apart;
    l->effect.initialDelay = l->look;
    reset(l);
}

// A broken sample (NaN, infinity) must not get into the running sum, where it would stay.
static inline float clean(float x) { return isfinite(x) ? x : 0.0f; }

static void processReplacing(AEffect *e, float **in, float **out, int32_t frames)
{
    Limiter *l = e->object;
    const int cap = MAX_LOOKAHEAD + 2;
    int64_t limited = 0, over = 0, dips = 0;
    double lowestGain = 1;
    for (int32_t i = 0; i < frames; i++)
    {
        float a = clean(in[0][i]), b = clean(in[1][i]);
        float peak = fmaxf(fabsf(a), fabsf(b));
        if (peak > 1.0f) over++;
        double need = peak > CEILING ? (double)CEILING / peak : 1.0;
        // A peak is sound going over the ceiling after 50 ms under it; the gain's slow return in between doesn't matter.
        if (need < 1.0)
        {
            if (l->under >= l->apart) dips++;
            l->under = 0;
        }
        else l->under++;

        // The lowest need of the last look + 1 samples: anything that isn't lower than this one can never be the lowest again.
        while (l->tail != l->head)
        {
            int last = (l->tail + cap - 1) % cap;
            if (l->low[last] < need) break;
            l->tail = last;
        }
        l->low[l->tail] = need;
        l->lowAt[l->tail] = l->sample;
        l->tail = (l->tail + 1) % cap;
        if (l->lowAt[l->head] < l->sample - l->look) l->head = (l->head + 1) % cap;
        double lowest = l->low[l->head];

        if (lowest <= l->level) l->level = lowest;
        else
        {
            l->level = fmin(l->level + (1 - l->level) * l->release, lowest);
            if (l->level > 0.999999) l->level = lowest; // done: from here on the sound passes through untouched
        }

        l->sum += l->level - l->recent[l->at];
        l->recent[l->at] = l->level;
        double gain = l->sum / l->look;

        float outA = l->delay[0][l->at], outB = l->delay[1][l->at];
        l->delay[0][l->at] = a;
        l->delay[1][l->at] = b;
        if (++l->at == l->look)
        {
            // Adding exactly once per trip round the buffer keeps rounding from building up, and lets the gain reach exactly 1.
            l->at = 0;
            l->sum = 0;
            for (int k = 0; k < l->look; k++) l->sum += l->recent[k];
        }
        l->sample++;

        if (gain < 1.0)
        {
            outA = (float)(outA * gain);
            outB = (float)(outB * gain);
            limited++;
            if (gain < lowestGain) lowestGain = gain;
        }
        // Rounding could leave a sample a hair over; it never gets past the ceiling.
        out[0][i] = fminf(fmaxf(outA, -CEILING), CEILING);
        out[1][i] = fminf(fmaxf(outB, -CEILING), CEILING);
    }

    // Once per block: a few atomic adds, no locks, so the audio thread never waits.
    Stats *s = l->stats;
    if (!s) return;
    InterlockedExchangeAdd64(&s->frames, frames);
    if (over) InterlockedExchangeAdd64(&s->overFullScale, over);
    if (dips) InterlockedExchangeAdd64(&s->dips, dips);
    if (!limited) return;
    InterlockedExchangeAdd64(&s->limited, limited);
    InterlockedExchange64(&s->lastDip, (int64_t)GetTickCount64());
    // Positive floats order like their bits, so the lowest gain is an integer minimum.
    float low = (float)lowestGain;
    LONG bits;
    memcpy(&bits, &low, sizeof bits);
    for (LONG seen = s->lowestGain; bits < seen;)
    {
        LONG was = InterlockedCompareExchange((volatile LONG *)&s->lowestGain, bits, seen);
        if (was == seen) break;
        seen = was;
    }
}

static intptr_t dispatcher(AEffect *e, int32_t opcode, int32_t index, intptr_t value, void *ptr, float opt)
{
    (void)index;
    Limiter *l = e->object;
    switch (opcode)
    {
        case OpClose:
            if (l->stats)
            {
                InterlockedDecrement((volatile LONG *)&l->stats->streams);
                UnmapViewOfFile(l->stats);
                CloseHandle(l->mapping);
            }
            free(l);
            return 1;
        case OpSetSampleRate: setRate(l, opt); return 1;
        case OpMainsChanged: if (value) reset(l); return 0;
        case OpGetPlugCategory: return CategoryMastering;
        case OpGetEffectName: case OpGetProductString: strcpy(ptr, "Gaming Enjoyer Limiter"); return 1;
        case OpGetVendorString: strcpy(ptr, "gamingenjoyer.com"); return 1;
        case OpGetVendorVersion: return Version;
        case OpGetVstVersion: return 2400;
        default: return 0;
    }
}

static void setParameter(AEffect *e, int32_t index, float value) { (void)e; (void)index; (void)value; }
static float getParameter(AEffect *e, int32_t index) { (void)e; (void)index; return 0; }

__declspec(dllexport) AEffect *VSTPluginMain(AudioMaster host)
{
    (void)host;
    Limiter *l = calloc(1, sizeof(Limiter));
    if (!l) return NULL;
    AEffect *e = &l->effect;
    e->magic = EffMagic;
    e->dispatcher = dispatcher;
    e->process = processReplacing; // only for hosts from before processReplacing, which this flags
    e->setParameter = setParameter;
    e->getParameter = getParameter;
    e->numInputs = e->numOutputs = 2;
    e->flags = FlagCanReplacing;
    e->ioRatio = 1;
    e->object = l;
    e->uniqueID = 0x47454C6D; // "GELm"
    e->version = Version;
    e->processReplacing = processReplacing;
    setRate(l, 48000);
    l->stats = openStats(&l->mapping);
    return e;
}
