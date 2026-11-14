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
