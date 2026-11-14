ENTRY(audio_haptics_step)
SECTIONS
{
    .text 0x00100000 :
    {
        *(.text.audio_haptics_step)
        . = 0x00100800;
        KEEP(*(.text.audio_haptics_tap))
        . = 0x00100900;
        KEEP(*(.text.audio_haptics_replay_worker))
        *(.text .text.*)
    }
}
