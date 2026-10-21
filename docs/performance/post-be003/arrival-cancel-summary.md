### Random arrivals (Poisson), latency includes queue wait (ms)

| Profile | Rate req/s | Target | Arrived | Success | Unfinished | P50 | P95 | P99 | Max | P50 first quarter | P50 last quarter |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| short | 2.1 | backend | 117 | 117 | 0 | 296 | 589 | 716 | 761 | 341 | 329 |
| short | 2.1 | ollama | 117 | 117 | 0 | 280 | 514 | 592 | 729 | 249 | 328 |
| short | 3.8 | backend | 206 | 206 | 0 | 1,037 | 2,120 | 2,397 | 2,536 | 1,199 | 1,053 |
| short | 3.8 | ollama | 206 | 206 | 0 | 1,152 | 2,421 | 2,720 | 2,887 | 1,430 | 918 |
| medium | 0.37 | backend | 50 | 50 | 0 | 1,688 | 4,109 | 4,631 | 4,631 | 1,382 | 1,657 |
| medium | 0.37 | ollama | 50 | 50 | 0 | 2,036 | 4,974 | 5,300 | 5,300 | 2,081 | 1,850 |
| medium | 0.67 | backend | 84 | 84 | 0 | 8,738 | 13,650 | 14,786 | 14,786 | 4,303 | 11,660 |
| medium | 0.67 | ollama | 84 | 84 | 0 | 4,864 | 8,618 | 9,938 | 9,938 | 3,738 | 6,760 |
| medium | 0.89 | backend | 104 | 104 | 0 | 32,106 | 45,574 | 46,320 | 46,905 | 10,532 | 43,268 |
| medium | 0.89 | ollama | 104 | 104 | 0 | 23,442 | 35,033 | 36,932 | 37,876 | 7,924 | 32,418 |
| long | 0.17 | backend | 31 | 31 | 0 | 3,527 | 7,761 | 8,441 | 8,441 | 2,744 | 2,448 |
| long | 0.17 | ollama | 31 | 31 | 0 | 3,491 | 7,620 | 8,285 | 8,285 | 2,629 | 3,143 |
| long | 0.3 | backend | 59 | 59 | 0 | 7,470 | 12,472 | 13,400 | 13,400 | 7,392 | 8,288 |
| long | 0.3 | ollama | 59 | 59 | 0 | 8,108 | 14,728 | 16,182 | 16,182 | 9,086 | 8,197 |

P50 last quarter much higher than first quarter = the queue was still growing (arrival rate above capacity).

### Cancel while queued: time until the remaining requests finish (ms, median of repeats)

| Profile | Target | Burst | Cancelled | Repeats | No cancel (min-max) | With cancel (min-max) | Saved |
| --- | --- | ---: | ---: | ---: | --- | --- | ---: |
| medium | backend | 6 | 3 | 8 | 8,610 (7,568-9,847) | 4,532 (3,647-5,683) | 47% |
| medium | ollama | 6 | 3 | 8 | 8,066 (7,571-9,056) | 4,507 (3,398-5,376) | 44% |
| long | backend | 6 | 3 | 8 | 17,468 (14,026-20,522) | 9,789 (6,716-10,933) | 44% |
| long | ollama | 6 | 3 | 8 | 20,175 (13,975-23,111) | 9,609 (6,646-12,884) | 52% |
