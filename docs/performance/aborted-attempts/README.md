# Aborted attempts of the final run (superseded, kept as evidence)

The results in the parent folder replace both attempts. Both were stopped by the low-memory safety stop, not by an error in the system under test.

| Attempt | What happened | Why it was redone |
| --- | --- | --- |
| attempt-1 | Direct stopped at `medium c=1` (available RAM 1009 MB < 1024 MB). Backend finished all 12 scenarios. | The sampler read `FreePhysicalMemory` (excludes the standby cache) and counted only `ollama*` processes, missing the model runner `llama-server.exe` (about 1.7 GB). Fixed, then every scenario was rerun. |
| attempt-2 | Direct finished short and medium (8 scenarios) and stopped in `long c=1`; Backend stopped in `short c=5` (available RAM 999 / 983 MB < 1024 MB). | Available RAM on this 16 GB laptop swings between about 1.3 and 1.9 GB while other applications run, so a hard 1,024 MB limit tripped on transient dips although commit charge was 28.5 of 36.6 GB and the pagefile only 2 of 21 GB. The script now waits up to 120 s for recovery before stopping, the limit is 512 MB, and the waits are recorded in the summary (`memory_pauses`). |

The latency, throughput and outcome columns of these attempts were not affected by the memory problems and agree with the final run (see the report, repeatability section). Only attempt-1 Backend is a complete run.
