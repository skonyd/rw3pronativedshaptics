/* Replay-only replacement for the stock TX tail branch at 0x2315C.
 * Copies one selected block into a mailbox, then resumes stock I2S enqueue.
 * These simulation addresses are not an allocation in controller RAM.
 */
.syntax unified
.thumb
.section .text.audio_haptics_tap, "ax", %progbits
.thumb_func
.global audio_haptics_tap
audio_haptics_tap:
    push {r0-r3, r4, lr}
    mrs r4, APSR
    ldr r0, =0x20024000
    ldr r1, [r0, #192]
    cbz r1, done
    cmp r3, #192
    bne done
    ldr r1, [r0, #196]
    cbnz r1, done
    str r2, [r0, #200]
    movs r1, #48
copy:
    ldr r3, [r2], #4
    str r3, [r0], #4
    subs r1, #1
    bne copy
    dmb
    movs r1, #1
    str r1, [r0, #4]
done:
    msr APSR_nzcvq, r4
    pop.w {r0-r3, r4, lr}
    ldr.w pc, resume
    .align 2
resume:
    .word 0x12f9d
    .ltorg

/* The foreground call at 0x2F5FA must still tail-call the stock Sensa
 * dispatcher. The simulation invokes the original call instruction.
 */
.section .text.audio_haptics_foreground, "ax", %progbits
.thumb_func
.global audio_haptics_foreground
audio_haptics_foreground:
    push {r0-r3, r4, lr}
    mrs r4, APSR
    ldr r0, =0x20024200
    ldr r1, [r0]
    cmp r1, #1
    beq publish
    ldr r0, =0x200240C0
    ldr r1, [r0, #4]
    cbz r1, dispatch
    ldr r1, =0x20000B2C
    ldrb r1, [r1]
    cbnz r1, dispatch
    bl audio_haptics_replay_worker
dispatch:
    msr APSR_nzcvq, r4
    pop.w {r0-r3, r4, lr}
    ldr.w pc, dispatcher
    .align 2
dispatcher:
    .word 0x2b539
publish:
    bl audio_haptics_publish_foreground
    msr APSR_nzcvq, r4
    pop.w {r0-r3, r4, lr}
    bx lr
    .ltorg

/* Called after the stock scatter loader. The private replay regions
 * are outside stock BSS and can retain data across a warm reset.
 */
.section .text.audio_haptics_boot, "ax", %progbits
.thumb_func
.global audio_haptics_boot
audio_haptics_boot:
    push {r1-r3, lr}
    movs r3, #0
    ldr r1, =0x20024000
    movs r2, #204
    bl clear_words
    ldr r1, =0x20021000
    movs r2, #32
    bl clear_words
    ldr r1, =0x20021100
    str r3, [r1]
    ldr r1, =0x20024200
    movs r2, #128
    bl clear_words
    pop.w {r1-r3, lr}
    ldr r0, =0x2ef69
    bx r0
clear_words:
    str r3, [r1], #4
    subs r2, #4
    bne clear_words
    bx lr
    .ltorg

/* Replay-only replacement for the accepted game's MOVS/STRB at 0x30F36.
 * It observes the original class-16 handler after its complete frame copy. */
.section .text.audio_haptics_game_notice, "ax", %progbits
.thumb_func
.global audio_haptics_game_notice
audio_haptics_game_notice:
    push {r1-r3, lr}
    ldr r1, =0x20024200
    ldr r2, [r1, #12]
    add.w r2, r2, #1
    str r2, [r1, #12]
    movs r2, #1
    str r2, [r1, #8]
    dmb
    movs r0, #1
    strb r0, [r7]
    pop {r1-r3, pc}
    .ltorg
