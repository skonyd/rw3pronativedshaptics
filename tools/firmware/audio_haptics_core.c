/* Original freestanding Cortex-M4 DSP. One call per scheduled 1 ms block.
 * The caller owns the zero-initialized state and applies the two palm bands.
 * No peripheral accesses or writes to stock firmware.
 */
#include "audio_haptics_core.h"

static uint32_t magnitude(int32_t sample)
{
    return sample < 0 ? (uint32_t)-sample : (uint32_t)sample;
}

void audio_haptics_step(audio_haptics_state *state, const int16_t *pcm,
                        uint32_t active, uint8_t output[4])
{
    if (!active) {
        *state = (audio_haptics_state){0};
        for (unsigned i = 0; i < 4; ++i)
            output[i] = 0;
        return;
    }

    uint32_t sum[2][2] = {{0}};
    for (unsigned frame = 0; frame < 48; ++frame) {
        for (unsigned channel = 0; channel < 2; ++channel) {
            int32_t sample = (int32_t)pcm[2 * frame + channel] * 256;
            state->slow[channel] += (sample - state->slow[channel]) / 128;
            state->fast[channel] += (sample - state->fast[channel]) / 32;
            sum[channel][0] += magnitude(state->slow[channel] / 256);
            sum[channel][1] += magnitude((state->fast[channel] - state->slow[channel]) / 256);
        }
    }

    for (unsigned channel = 0; channel < 2; ++channel) {
        for (unsigned band = 0; band < 2; ++band) {
            uint32_t level = sum[channel][band] / 48;
            uint32_t previous = state->envelope[channel][band];
            uint32_t envelope = level >= previous ? level : (7 * previous + level) / 8;
            state->envelope[channel][band] = envelope;
            uint32_t strength = envelope > 96 ? (envelope - 96) / 192 : 0;
            output[channel * 2 + band] = (uint8_t)(strength > 63 ? 63 : strength);
        }
    }
}

/* Replay-only worker. Called outside the DMA handler. Keep ready set
 * while reading a block so a producer interrupt cannot replace it.
 */
void audio_haptics_replay_worker(void)
{
    audio_haptics_state *const state = (audio_haptics_state *)0x20021000;
    uint8_t *const output = (uint8_t *)0x20021100;
    const int16_t *const pcm = (const int16_t *)0x20024000;
    volatile uint32_t *const mailbox = (volatile uint32_t *)0x200240C0;
    uint32_t ready = mailbox[1];
    __asm__ volatile ("dmb" ::: "memory");
    uint32_t active = ready && mailbox[0] && mailbox[2] != 0x200056EC;
    audio_haptics_step(state, pcm, active, output);
    __asm__ volatile ("dmb" ::: "memory");
    if (ready)
        mailbox[1] = 0;
}
