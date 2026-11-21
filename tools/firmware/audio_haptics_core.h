#ifndef AUDIO_HAPTICS_CORE_H
#define AUDIO_HAPTICS_CORE_H
#include <stdint.h>

typedef struct {
    int32_t slow[2];
    int32_t fast[2];
    uint32_t envelope[2][2];
} audio_haptics_state;

_Static_assert(sizeof(audio_haptics_state) == 32, "Replay DSP state ABI changed");
void audio_haptics_step(audio_haptics_state *, const int16_t *, uint32_t, uint8_t[4]);
void audio_haptics_replay_worker(void);
unsigned audio_haptics_encode_frame(uint8_t[64], const uint8_t[4]);
void audio_haptics_publish_foreground(void);
#endif
