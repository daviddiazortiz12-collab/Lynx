.arm
.balign 4

.section .text.svcControlService, "ax", %progbits
.global svcControlService
.type svcControlService, %function
.align 2
svcControlService:
    svc 0xB0
    bx lr
