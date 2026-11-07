#!/usr/bin/env python3
"""Generate single-instruction 68000 test cases and solve them with the Unicorn emulator (M68000 model).

Usage: python3 gen_cases.py cases.txt [states_per_opcode] [seed]
Each line is consumed by `GenesisRecomp --verify cases.txt`.
"""
import ctypes, random, sys, zlib
from unicorn import *
import unicorn.m68k_const as k

out_path = sys.argv[1]
states = int(sys.argv[2]) if len(sys.argv) > 2 else 2
base_seed = int(sys.argv[3]) if len(sys.argv) > 3 else 1
start_op = int(sys.argv[4]) if len(sys.argv) > 4 else 0
rng = random.Random(base_seed * 100003 + start_op)
PROGRESS = out_path + ".progress"
import os
_bad = os.path.join(os.path.dirname(os.path.abspath(__file__)), "unicorn_bad_ops.txt")
BAD_OPS = {int(x, 16) for x in open(_bad).read().split()} if os.path.exists(_bad) else set()
CHUNK = 4000
END_OP = min(start_op + CHUNK, 0x10000)

RAM_BASE, RAM_SIZE, CODE = 0xFF0000, 0x10000, 0xFF0010
SCRATCH_CODE, SCRATCH_SLOT = 0xFF0040, 0x0FFE            # MOVE.W SR,($FF0FFE).l lives at SCRATCH_CODE
STORE_SR = b''.join(w.to_bytes(2, 'big') for w in (0x40F9, 0x00FF, 0x0FFE))
buf = (ctypes.c_char * RAM_SIZE)()

uc = Uc(UC_ARCH_M68K, UC_MODE_BIG_ENDIAN)
uc.ctl_set_cpu_model(k.UC_CPU_M68K_M68000)
# the same host buffer is visible at the 24-bit address and at its sign-extended alias (abs.W with a negative value)
uc.mem_map_ptr(RAM_BASE, RAM_SIZE, UC_PROT_ALL, ctypes.addressof(buf))
# (no alias mapping: aliased host memory crashes Unicorn after a few hundred cases)

D = [getattr(k, f"UC_M68K_REG_D{i}") for i in range(8)]
A = [getattr(k, f"UC_M68K_REG_A{i}") for i in range(8)]
SR, PC = k.UC_M68K_REG_SR, k.UC_M68K_REG_PC

def fill(seed):
    return bytes((((i * 2654435761 + seed * 40503) & 0xFFFFFFFF) >> 16) & 0xFF for i in range(RAM_SIZE))
FILLS = [fill(s) for s in range(64)]

def rand_d():
    return rng.randrange(-0x3000, 0x3000) & 0xFFFFFFFF if rng.random() < 0.5 else rng.getrandbits(32)

def rand_a():
    return rng.randrange(0xFF4000, 0xFFC000) & ~1

def rand_ext():
    r = rng.random()
    if r < 0.35: return rng.getrandbits(16)
    if r < 0.60: return rng.randrange(-0x40, 0x40) & 0xFFFF
    if r < 0.80: return 0x00FF
    return rng.randrange(0x4000, 0xBFFE) & ~1

n_ok = n_exc = n_skip = 0
with open(out_path, "a" if start_op else "w") as out:
    for op in range(start_op, END_OP):
        if 0x4848 <= op <= 0x484F or op in BAD_OPS:   # BKPT etc.: crash or hang Unicorn's native code
            continue
        with open(PROGRESS, "w") as pf: pf.write(str(op))   # so a native crash can be attributed to this opcode
        for _ in range(states):
            # bits 10-9 of an index extension word are a 68020 scale factor; a real 68000 ignores them but QEMU applies them
            code = [op] + [rand_ext() & ~0x0600 for _ in range(3)]
            seed = rng.randrange(64)
            d = [rand_d() for _ in range(8)]
            a = [rand_a() for _ in range(8)]
            sr = 0x2700 | rng.getrandbits(5)

            ctypes.memmove(buf, FILLS[seed], RAM_SIZE)
            # write the code through Unicorn itself so its translated-code cache is invalidated
            uc.mem_write(CODE, b"".join(w.to_bytes(2, "big") for w in code))
            uc.ctl_flush_tb()
            uc.reg_write(SR, sr)
            for i in range(8):
                uc.reg_write(D[i], d[i]); uc.reg_write(A[i], a[i])
            head = f"{{}} {code[0]:04X} {code[1]:04X} {code[2]:04X} {code[3]:04X} {seed:X} " \
                   + " ".join(f"{x:08X}" for x in d) + " " + " ".join(f"{x:08X}" for x in a) + f" {sr:04X}"
            try:
                uc.emu_start(CODE, 0, timeout=50000, count=1)
            except UcError as e:
                if e.errno == UC_ERR_EXCEPTION:
                    out.write(head.format("exc") + "\n"); n_exc += 1
                else:
                    n_skip += 1          # unmapped access: cannot compare
                continue
            res = [uc.reg_read(PC)] + [uc.reg_read(r) for r in D] + [uc.reg_read(r) for r in A]
            # Unicorn's reg_read(SR) returns stale lazy flags, so execute MOVE.W SR,<ram> to force QEMU to evaluate them
            keep = bytes(buf[0x40:0x48]) + bytes(buf[SCRATCH_SLOT:SCRATCH_SLOT + 2])
            uc.mem_write(SCRATCH_CODE, STORE_SR)
            uc.ctl_flush_tb()
            uc.emu_start(SCRATCH_CODE, 0, timeout=50000, count=1)
            sr_after = int.from_bytes(bytes(buf[SCRATCH_SLOT:SCRATCH_SLOT + 2]), "big")
            ctypes.memmove(ctypes.addressof(buf) + 0x40, keep[:8], 8)                 # undo the scratch writes
            ctypes.memmove(ctypes.addressof(buf) + SCRATCH_SLOT, keep[8:], 2)
            res.append(sr_after)
            crc = zlib.crc32(bytes(buf)) & 0xFFFFFFFF
            out.write(head.format("ok") + " -> " + " ".join(f"{x:08X}" for x in res[:17]) + f" {res[17]:04X} {crc:08X}\n")
            n_ok += 1
print(f"ok={n_ok} exception={n_exc} skipped(unmapped)={n_skip}")
if END_OP < 0x10000:
    sys.exit(3)        # chunk finished; the wrapper restarts after the last processed opcode
