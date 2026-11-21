/* Original replay-only Xbox Sensa publisher. Addresses are simulator
 * allocations, NOT a validated placement in a controller firmware image. */
#include "audio_haptics_core.h"

typedef struct {
    volatile uint32_t select, owns_audio, game_latched, game_generation;
    volatile uint32_t rearm, published, game_deferrals, busy_deferrals;
    volatile uint32_t length, unsupported_mode, silence_frames, encode_errors;
    volatile uint32_t period, last_tick, last_pcm_tick, prepared_length;
    uint8_t frame[64];
} publication;
_Static_assert(sizeof(publication) == 128, "Publication state ABI changed");

static publication *const pub = (publication *)0x20024200;
static volatile uint8_t *const pending = (volatile uint8_t *)0x20000B2C;
static volatile uint8_t *const stock_frame = (volatile uint8_t *)0x20000B34;
static volatile uint32_t *const mailbox = (volatile uint32_t *)0x200240C0;
static uint8_t *const levels = (uint8_t *)0x20021100;

static void put(uint8_t *out, uint32_t *position, uint32_t value, unsigned width)
{
    for (unsigned bit = width; bit; --bit) {
        uint32_t p = (*position)++;
        out[p >> 3] |= ((value >> (bit - 1)) & 1) << (7 - (p & 7));
    }
}

/* Return size, or zero on an invalid strength. The full 64-byte tail is
 * initialized; no level normalization, floating point, transient or trigger. */
unsigned audio_haptics_encode_frame(uint8_t out[64], const uint8_t input[4])
{
    for (unsigned i = 0; i < 64; ++i) out[i] = 0;
    for (unsigned i = 0; i < 4; ++i) if (input[i] > 63) return 0;
    uint32_t bit = 0;
    put(out, &bit, 0x3C, 7);
    for (unsigned side = 0; side < 2; ++side) {
        unsigned bands = 0;
        for (unsigned band = 0; band < 2; ++band) {
            uint32_t strength = input[side * 2 + band];
            if (!strength) continue;
            uint32_t pitch = band ? 55 : 17;
            put(out, &bit, 1, 1);
            put(out, &bit, strength, 6); put(out, &bit, pitch, 7);
            put(out, &bit, strength, 6); put(out, &bit, pitch, 7);
            ++bands;
        }
        if (bands < 2) put(out, &bit, 0, 1);
        put(out, &bit, 0, 1); /* no palm transients */
    }
    put(out, &bit, 0, 4); /* both trigger parts: no band, no transient */
    return (bit + 7) >> 3;
}

static void reset_dsp(void)
{
    audio_haptics_step((audio_haptics_state *)0x20021000, (const int16_t *)0x20024000, 0, levels);
}

void audio_haptics_publish_foreground(void)
{
    void (*const dispatch)(void) = (void (*)(void))0x2B539;
    uint32_t ready = mailbox[1];
    uint32_t now = *(volatile uint32_t *)0x200139F4;
    unsigned length = pub->prepared_length;
    /* Preparation is interruptible and only touches our private state. */
    uint32_t stale = (pub->owns_audio || length) && (uint32_t)(now - pub->last_pcm_tick) >= 250;
    if (!pub->game_latched && !*pending && !pub->rearm &&
        (ready || ((pub->owns_audio || length) && (!mailbox[0] || stale)))) {
        audio_haptics_replay_worker();
        length = audio_haptics_encode_frame(pub->frame, levels);
        if (!length) {
            ++pub->encode_errors;
            reset_dsp();
            length = audio_haptics_encode_frame(pub->frame, levels);
        }
        pub->prepared_length = length;
        if (ready) pub->last_pcm_tick = now;
    }
    uint32_t mask;
    __asm__ volatile ("mrs %0, primask\n cpsid i" : "=r"(mask) :: "memory");
    if (pub->rearm) {
        pub->rearm = 0;
        pub->game_latched = 0;
        pub->owns_audio = 0;
        mailbox[1] = 0; /* discard stale PCM; wait for a fresh producer */
        ready = 0;
        reset_dsp();
        length = 0;
        pub->prepared_length = 0;
    }
    if (*pending) pub->game_latched = 1; /* includes unknown stock producers */
    if (pub->game_latched) {
        pub->owns_audio = 0;
        pub->prepared_length = 0;
        ++pub->game_deferrals;
    } else if (*(volatile uint8_t *)0x20001057 != 0) {
        /* Xbox frame format only. No silent PC-mode conversion. */
        ++pub->unsupported_mode;
        mailbox[1] = 0;
        reset_dsp();
        pub->prepared_length = 0;
    } else if (*(volatile uint8_t *)0x20000EC4 || *(volatile uint8_t *)0x20000EC8) {
        ++pub->busy_deferrals;
    } else {
        uint32_t audible = levels[0] | levels[1] | levels[2] | levels[3];
        uint32_t enabled = mailbox[0];
        now = *(volatile uint32_t *)0x200139F4;
        uint32_t period = pub->period ? pub->period : 12;
        /* Disable/source loss must not wait for the normal publication period. */
        if ((length || pub->owns_audio) &&
            (!enabled || (uint32_t)(now - pub->last_pcm_tick) >= 250)) {
            reset_dsp();
            audible = 0;
            uint8_t zero[4] = {0};
            length = audio_haptics_encode_frame(pub->frame, zero);
        }
        uint32_t due = !pub->published || !audible || (uint32_t)(now - pub->last_tick) >= period;
        if (length && due && ((enabled && audible) || pub->owns_audio)) {
            pub->length = length;
            for (unsigned i = 0; i < 64; ++i) stock_frame[i] = pub->frame[i];
            __asm__ volatile ("dmb" ::: "memory");
            *pending = 1; /* the complete bytes precede the publication flag */
            pub->owns_audio = enabled && audible;
            pub->last_tick = now;
            ++pub->published;
            pub->prepared_length = 0;
            if (!audible) ++pub->silence_frames;
        } else if (length && !audible && !pub->owns_audio) {
            pub->prepared_length = 0;
        }
    }
    /* Keep maskable producers/timer consumers out until stock has decoded
     * the complete frame. Preserve the caller's original PRIMASK exactly. */
    dispatch();
    __asm__ volatile ("dmb\n msr primask, %0" :: "r"(mask) : "memory");
}
