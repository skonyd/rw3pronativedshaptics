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
        . = 0x00100A00;
        KEEP(*(.text.audio_haptics_foreground))
        . = 0x00100B00;
        KEEP(*(.text.audio_haptics_boot))
        *(.text .text.*)
    }
}
