ENTRY(audio_haptics_step)
SECTIONS
{
    .text 0x00100000 : { *(.text.audio_haptics_step) *(.text .text.*) }
}
