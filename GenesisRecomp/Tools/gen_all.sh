#!/bin/sh
# Generates the full case file, surviving opcodes that crash or hang Unicorn's native code.
# Usage: gen_all.sh cases.txt [states_per_opcode] [seed] [start_opcode]
OUT=$1; STATES=${2:-2}; SEED=${3:-1}; START=${4:-0}
[ "$START" = 0 ] && : > "$OUT.crash_ops"
while true; do
  timeout 90 python3 "$(dirname "$0")/gen_cases.py" "$OUT" "$STATES" "$SEED" "$START"
  RC=$?
  [ $RC = 0 ] && break
  LAST=$(cat "$OUT.progress")
  if [ $RC != 3 ]; then printf '%04X\n' "$LAST" >> "$OUT.crash_ops"; fi   # crash (139) or hang (124): skip that opcode
  START=$((LAST + 1))
done
echo "done; opcodes that crash or hang Unicorn: $(wc -l < "$OUT.crash_ops")"
